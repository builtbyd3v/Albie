using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Everything the desk panels change goes through here. Hub live: a POST (hub README "Desk"), and the hub answers with
/// the event that redraws the panels. Hub off (mocks): the same change made on a local copy of the hub's state and
/// raised as that event (<see cref="ForgeNet.Emit"/>), so the sandbox and desktop mode work without it; timers even
/// ring. <see cref="DeskExtras"/> keeps the copy in step with what arrives (<see cref="Seen"/>).
/// </summary>
public static class DeskApi
{
    static UiWidgets widgets = new UiWidgets();
    static readonly List<DeskTimer> timers = new List<DeskTimer>();
    static float timersAt;                      // realtimeSinceStartup when `timers` was true
    static NotesUpdated notes = new NotesUpdated();
    static MovieState movie = new MovieState();
    static float movieAt;
    static int mockIds;

    static bool Live => ForgeNet.Status == ForgeNetStatus.Live;
    static float Now => Time.realtimeSinceStartup;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics()
    {
        widgets = new UiWidgets();
        timers.Clear();
        notes = new NotesUpdated();
        movie = new MovieState();
    }

    // ---------------------------------------------------------------- the local copy

    public static void Seen(UiWidgets value) => widgets = value ?? new UiWidgets();

    public static void Seen(TimersUpdated value)
    {
        timers.Clear();
        if (value?.timers != null)
            foreach (var t in value.timers) timers.Add(Copy(t));
        timersAt = Now;
    }

    public static void Seen(NotesUpdated value) => notes = value ?? new NotesUpdated();

    public static void Seen(MovieState value)
    {
        movie = value ?? new MovieState();
        movieAt = Now;
    }

    // ---------------------------------------------------------------- widgets

    /// <summary>Open, minimize or close one: "timer", "notes" or "movie". Closing the timers stops them all.</summary>
    public static void SetWidget(string widget, string state)
    {
        if (Live)
        {
            ForgeNet.Post("/ui/widgets", new Dictionary<string, string> { [widget] = state });
            return;
        }
        if (widget == "timer" && state == "closed" && timers.Count > 0)
        {
            timers.Clear();
            EmitTimers();
        }
        if (widget == "movie" && state == "closed" && movie.status != "closed")
        {
            movie.positionS = MoviePosition();
            movie.status = "closed";
            EmitMovie();
        }
        bool hasContent = widget == "timer" ? timers.Count > 0 : widget == "movie" ? movie.status != "closed" : true;
        if (state != "closed" && !hasContent) return;
        if (widget == "timer") widgets.timer = state;
        else if (widget == "notes") widgets.notes = state;
        else if (widget == "movie") widgets.movie = state;
        ForgeNet.Emit(ForgeEvents.UiWidgetsType, widgets);
    }

    // ---------------------------------------------------------------- timers

    public static void TimerPause(string id) => Timer(id, "pause", t =>
    {
        if (t.status != "running") return false;
        t.status = "paused";
        return true;
    });

    public static void TimerResume(string id) => Timer(id, "resume", t =>
    {
        if (t.status != "paused") return false;
        t.status = "running";
        return true;
    });

    /// <summary>More time; on a timer that rang it runs again for that long.</summary>
    public static void TimerAdd(string id, float minutes) => Timer(id, "add", t =>
    {
        float add = minutes * 60f;
        if (t.status == "ringing")
        {
            t.status = "running";
            t.remainingS = add;
        }
        else t.remainingS = Mathf.Max(1f, t.remainingS + add);
        t.durationS = Mathf.Max(t.remainingS, t.durationS + add);
        return true;
    }, new { minutes });

    /// <summary>Cancel it, or turn off its ring.</summary>
    public static void TimerStop(string id)
    {
        if (Live)
        {
            ForgeNet.Post($"/timers/{id}/stop", null);
            return;
        }
        Advance();
        if (timers.RemoveAll(t => t.timerId == id) == 0) return;
        EmitTimers();
    }

    /// <summary>A new timer (the sandbox's keys; Bit starts them through the hub).</summary>
    public static void TimerStart(float seconds, string label)
    {
        if (Live)
        {
            ForgeNet.Post("/timers", new { seconds, label });
            return;
        }
        Advance();
        string name = string.IsNullOrEmpty(label) ? "Focus" : label;
        timers.Add(new DeskTimer { timerId = "m" + (++mockIds), label = name, status = "running", durationS = seconds, remainingS = seconds });
        EmitTimers();
    }

    static void Timer(string id, string action, System.Func<DeskTimer, bool> change, object body = null)
    {
        if (Live)
        {
            ForgeNet.Post($"/timers/{id}/{action}", body);
            return;
        }
        Advance();
        var timer = timers.Find(t => t.timerId == id);
        if (timer == null || !change(timer)) return;
        EmitTimers();
    }

    /// <summary>Hub off: ring the timers whose time is up, as the hub would. Call every frame.</summary>
    public static void Tick()
    {
        if (Live || ForgeNet.Status != ForgeNetStatus.Mock || timers.Count == 0) return;
        float elapsed = Now - timersAt;
        DeskTimer rang = null;
        foreach (var t in timers)
            if (t.status == "running" && t.remainingS - elapsed <= 0f) rang = t;
        if (rang == null) return;
        Advance();
        rang.status = "ringing";
        rang.remainingS = 0f;
        EmitTimers();
        string line = rang.label.ToLowerInvariant() == "focus"
            ? $"Time's up! That's {DeskUi.Human(rang.durationS)} of focus."
            : $"Time's up! Your {rang.label} timer is done.";
        ForgeNet.Emit(ForgeEvents.PetSayType, new PetSay { petId = "bit", text = line });
        if (widgets.timer == "minimized") SetWidget("timer", "open");
    }

    // Count the running timers down to now.
    static void Advance()
    {
        float elapsed = Now - timersAt;
        foreach (var t in timers)
            if (t.status == "running") t.remainingS = Mathf.Max(0f, t.remainingS - elapsed);
        timersAt = Now;
    }

    // As the hub sends them: ringing first, then by time left; the widget closes with the last one and opens with
    // the first.
    static void EmitTimers()
    {
        timers.Sort((a, b) =>
        {
            int ra = a.status == "ringing" ? 0 : 1, rb = b.status == "ringing" ? 0 : 1;
            return ra != rb ? ra.CompareTo(rb) : a.remainingS.CompareTo(b.remainingS);
        });
        var payload = new TimersUpdated();
        foreach (var t in timers) payload.timers.Add(Copy(t));
        ForgeNet.Emit(ForgeEvents.TimersUpdatedType, payload);
        string want = timers.Count == 0 ? "closed" : widgets.timer == "closed" ? "open" : widgets.timer;
        if (want != widgets.timer)
        {
            widgets.timer = want;
            ForgeNet.Emit(ForgeEvents.UiWidgetsType, widgets);
        }
    }

    static DeskTimer Copy(DeskTimer t) =>
        new DeskTimer { timerId = t.timerId, label = t.label, status = t.status, durationS = t.durationS, remainingS = t.remainingS };

    // ---------------------------------------------------------------- notes

    public static void NoteDone(string id, bool done)
    {
        if (Live)
        {
            ForgeNet.Post($"/notes/{id}/edit", new { done });
            return;
        }
        var note = notes.notes.Find(n => n.id == id);
        if (note == null) return;
        note.done = done;
        ForgeNet.Emit(ForgeEvents.NotesUpdatedType, notes);
    }

    public static void NoteRemove(string id)
    {
        if (Live)
        {
            ForgeNet.Post($"/notes/{id}/remove", null);
            return;
        }
        var note = notes.notes.Find(n => n.id == id);
        if (note == null) return;
        notes.notes.Remove(note);
        notes.lastRemoved = note;
        ForgeNet.Emit(ForgeEvents.NotesUpdatedType, notes);
    }

    public static void NoteUndo()
    {
        if (Live)
        {
            ForgeNet.Post("/notes/undo", null);
            return;
        }
        if (notes.lastRemoved == null) return;
        notes.notes.Add(notes.lastRemoved);
        notes.notes.Sort((a, b) => b.createdAt.CompareTo(a.createdAt));
        notes.lastRemoved = null;
        ForgeNet.Emit(ForgeEvents.NotesUpdatedType, notes);
    }

    /// <summary>A new note (the sandbox's keys; Bit writes them through the hub).</summary>
    public static void NoteAdd(string text)
    {
        if (Live)
        {
            ForgeNet.Post("/notes", new { text });
            return;
        }
        double now = (System.DateTime.UtcNow - new System.DateTime(1970, 1, 1)).TotalSeconds;
        notes.notes.Insert(0, new DeskNote { id = "m" + (++mockIds), text = text, createdAt = now, updatedAt = now });
        ForgeNet.Emit(ForgeEvents.NotesUpdatedType, notes);
        if (widgets.notes == "closed") SetWidget("notes", "open");
    }

    // ---------------------------------------------------------------- movie

    public static void MoviePlay(MovieInfo info)
    {
        if (Live)
        {
            ForgeNet.Post("/movie/play", new { movie_id = info?.movieId });
            return;
        }
        if (info != null && (movie.movie == null || movie.movie.movieId != info.movieId))
        {
            movie.movie = info;
            movie.positionS = 0f;
            movie.seekRev++;
        }
        if (movie.movie == null) return;
        if (movie.status == "closed") movie.seekRev++;  // back where it was
        MovieSet("playing");
    }

    public static void MoviePause() => MovieAction("/movie/pause", null, () => MovieSet("paused"));
    public static void MovieResume() => MovieAction("/movie/resume", null, () => MovieSet("playing"));
    public static void MovieClose() => MovieAction("/movie/close", null, () =>
    {
        MovieSet("closed");
        SetWidget("movie", "closed");
    });

    public static void MovieSeek(float position) => MovieAction("/movie/seek", new { position_s = position }, () => Jump(position));
    public static void MovieSkip(float seconds) => MovieAction("/movie/seek", new { delta_s = seconds }, () => Jump(MoviePosition() + seconds));

    public static void MovieVolume(float volume) => MovieAction("/movie/volume", new { volume = Mathf.Clamp01(volume) }, () =>
    {
        movie.positionS = MoviePosition();
        movieAt = Now;
        movie.volume = Mathf.Clamp01(volume);
        EmitMovie();
    });

    /// <summary>The player says where it is (every few seconds, on pause, at the end). No event back.</summary>
    public static void MovieReport(float position, float duration, bool ended)
    {
        if (Live)
        {
            ForgeNet.Post("/movie/position", new { position_s = Mathf.Max(0f, position), duration_s = duration > 0f ? duration : (float?)null, ended });
            return;
        }
        if (movie.status == "closed") return;
        movie.positionS = position;
        movieAt = Now;
        if (ended && movie.status == "playing") MovieSet("paused");
    }

    static void MovieAction(string path, object body, System.Action local)
    {
        if (Live)
        {
            ForgeNet.Post(path, body);
            return;
        }
        if (movie.movie == null || (movie.status == "closed" && path != "/movie/resume")) return;
        local();
    }

    static void MovieSet(string status)
    {
        movie.positionS = MoviePosition();
        movieAt = Now;
        movie.status = status;
        EmitMovie();
        if (status != "closed" && widgets.movie == "closed") SetWidget("movie", "open");
    }

    static void Jump(float position)
    {
        float length = movie.movie?.durationS ?? float.MaxValue;
        movie.positionS = Mathf.Clamp(position, 0f, length);
        movieAt = Now;
        movie.seekRev++;
        EmitMovie();
    }

    static float MoviePosition() => movie.positionS + (movie.status == "playing" ? Now - movieAt : 0f);

    static void EmitMovie() => ForgeNet.Emit(ForgeEvents.MovieStateType, movie);
}
