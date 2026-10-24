using UnityEngine;

/// <summary>
/// Bit's desk extras on top of any mode: the focus timers (pills above the screens), the notes (beside the open panels)
/// and the movie (a big screen above the desk). The hub decides what shows (ui.widgets) and owns the data
/// (timers.updated, notes.updated, movie.state); this turns that into panels and dots through
/// <see cref="WorkspaceExtras"/>, and the panels' buttons go back through <see cref="DeskApi"/>. Minimized, the timers
/// are one dot showing the one that ends soonest (with how many there are), the notes a dot with the open count.
/// Hub off: <see cref="DeskApi"/> stands in for it (mocks: Resources/Mocks/timers.json etc. and the sandbox's events).
/// </summary>
public class DeskExtras : MonoBehaviour
{
    public WorkspaceExtras host;
    public GameObject timersPrefab, notesPrefab, moviePrefab;
    public PanelOrb orbPrefab;
    public DeskChime chime;
    [Tooltip("What the panels call the pet (\"Ask Bit to add a note\"). Rename the pet here.")]
    public string assistantName = "Albie";

    UiWidgets widgets = new UiWidgets();
    TimersUpdated timers = new TimersUpdated();
    NotesUpdated notes = new NotesUpdated();
    MovieState movie = new MovieState();
    float timersAt;
    TimerStack timerStack;
    NotesPanel notesPanel;
    MoviePanel moviePanel;
    int shownCount = -1, shownSecond = -1, ringingCount;
    bool fetched;

    void OnEnable()
    {
        ForgeEvents.OnUiWidgets += OnWidgets;
        ForgeEvents.OnTimersUpdated += OnTimers;
        ForgeEvents.OnNotesUpdated += OnNotes;
        ForgeEvents.OnMovieState += OnMovie;
        ForgeNet.StatusChanged += OnStatus;
        if (host != null) host.Requested += OnRequested;
    }

    void OnDisable()
    {
        ForgeEvents.OnUiWidgets -= OnWidgets;
        ForgeEvents.OnTimersUpdated -= OnTimers;
        ForgeEvents.OnNotesUpdated -= OnNotes;
        ForgeEvents.OnMovieState -= OnMovie;
        ForgeNet.StatusChanged -= OnStatus;
        if (host != null) host.Requested -= OnRequested;
        if (chime != null) chime.SetRinging(false);
    }

    void Start() => OnStatus(ForgeNet.Status);

    // Connected (or in mocks): read the current state. Live, the hub's replay brings it too; the GETs cover mocks.
    void OnStatus(ForgeNetStatus status)
    {
        if (status == ForgeNetStatus.Connecting) return;
        if (status == ForgeNetStatus.Mock && fetched) return;
        fetched = true;
        ForgeNet.Get<TimersUpdated>("/timers", OnTimers, _ => { });
        ForgeNet.Get<NotesUpdated>("/notes", OnNotes, _ => { });
        ForgeNet.Get<MovieState>("/movie", OnMovie, _ => { });
        ForgeNet.Get<UiWidgets>("/ui/widgets", OnWidgets, _ => { });
    }

    void OnRequested(string id, ExtraState state) =>
        DeskApi.SetWidget(id, state == ExtraState.Open ? "open" : state == ExtraState.Minimized ? "minimized" : "closed");

    // ---------------------------------------------------------------- the hub's state

    void OnWidgets(UiWidgets value)
    {
        widgets = value ?? new UiWidgets();
        DeskApi.Seen(widgets);
        Apply();
    }

    void OnTimers(TimersUpdated value)
    {
        timers = value ?? new TimersUpdated();
        timersAt = Time.realtimeSinceStartup;
        DeskApi.Seen(timers);
        if (timerStack != null) timerStack.Show(timers.timers, timersAt);
        shownSecond = -1;
        UpdateRinging();
        if (timers.timers.Count != shownCount)
        {
            shownCount = timers.timers.Count;
            host.Relayout();  // the stack grew or shrank: the movie clears it
        }
    }

    void OnNotes(NotesUpdated value)
    {
        notes = value ?? new NotesUpdated();
        DeskApi.Seen(notes);
        if (notesPanel != null) notesPanel.Show(notes);
        UpdateNotesOrb();
    }

    void OnMovie(MovieState value)
    {
        movie = value ?? new MovieState();
        DeskApi.Seen(movie);
        if (moviePanel != null) moviePanel.Show(movie);
    }

    void Apply()
    {
        var go = host.Set("timer", State(widgets.timer), timersPrefab, ExtraAnchor.Top, orbPrefab, movable: false);
        var stack = go != null ? go.GetComponent<TimerStack>() : null;
        if (stack != timerStack)
        {
            timerStack = stack;
            if (stack != null) stack.Show(timers.timers, timersAt);
        }

        go = host.Set("notes", State(widgets.notes), notesPrefab, ExtraAnchor.Beside, orbPrefab, movable: true);
        var panel = go != null ? go.GetComponent<NotesPanel>() : null;
        if (panel != notesPanel)
        {
            notesPanel = panel;
            if (panel != null)
            {
                panel.Bind(this);
                panel.Show(notes);
            }
        }

        go = host.Set("movie", State(widgets.movie), moviePrefab, ExtraAnchor.Cinema, null, movable: true);
        var screen = go != null ? go.GetComponent<MoviePanel>() : null;
        if (screen != moviePanel)
        {
            moviePanel = screen;
            if (screen != null)
            {
                screen.Bind(this);
                screen.Show(movie);
            }
        }
        shownSecond = -1;
        UpdateNotesOrb();
    }

    static ExtraState State(string value) =>
        value == "open" ? ExtraState.Open : value == "minimized" ? ExtraState.Minimized : ExtraState.Closed;

    // ---------------------------------------------------------------- dots, chime

    void Update()
    {
        DeskApi.Tick();
        UpdateTimerOrb();
    }

    // The timers' dot shows the soonest one: minutes left (seconds in the last minute) and its rim draining.
    void UpdateTimerOrb()
    {
        var orb = host.Orb("timer");
        if (orb == null || timers.timers.Count == 0) return;
        DeskTimer t = timers.timers[0];
        float left = t.status == "running" ? Mathf.Max(0f, t.remainingS - (Time.realtimeSinceStartup - timersAt)) : t.remainingS;
        bool ringing = t.status == "ringing";
        orb.SetProgress(ringing ? 1f : t.durationS > 0f ? Mathf.Clamp01(left / t.durationS) : 0f);
        int second = Mathf.CeilToInt(left - 0.001f);
        if (second == shownSecond) return;
        shownSecond = second;
        orb.SetRinging(ringing);
        if (ringing) orb.SetText("0", "DONE");
        else if (left >= 60f) orb.SetText(Mathf.CeilToInt(left / 60f - 0.001f).ToString(), t.status == "paused" ? "PAUSED" : "MIN");
        else orb.SetText(second.ToString(), "SEC");
        orb.SetBadge(timers.timers.Count > 1 ? timers.timers.Count.ToString() : null);
    }

    void UpdateNotesOrb()
    {
        var orb = host.Orb("notes");
        if (orb == null) return;
        orb.SetGlyph();
        int open = 0;
        foreach (var n in notes.notes) if (!n.done) open++;
        orb.SetBadge(open > 0 ? open.ToString() : null);
    }

    // Chime while any timer rings; another one starting to ring rings for another minute.
    void UpdateRinging()
    {
        int ringing = 0;
        foreach (var t in timers.timers) if (t.status == "ringing") ringing++;
        if (chime != null) chime.SetRinging(ringing > 0, restart: ringing > ringingCount);
        ringingCount = ringing;
    }
}
