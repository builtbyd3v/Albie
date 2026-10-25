using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Video;

/// <summary>
/// The movie screen above the desk. A VideoPlayer streams the hub's file (GET /movie/files/&lt;id&gt;.mp4, byte ranges)
/// into a RenderTexture on a near-borderless panel; sound comes from the screen. Point at it and the controls fade in:
/// title and close at the top; play / pause, back and forward 10 s, the scrubber, subtitles and volume at the bottom.
/// The hub keeps the state (movie.state): the player plays or pauses with it and seeks only when seek_rev changes;
/// it reports where it is every few seconds so the hub can pick up there next time. The sound dips while the pet
/// talks (pet.state speaking). Hub off: plays <see cref="mockFile"/> (default: the hub's own media folder, if it's
/// on this machine). Built at runtime (Desk/Prefabs/MoviePanel.prefab, a ForgePanel variant without a title bar).
/// </summary>
[RequireComponent(typeof(VideoPlayer), typeof(AudioSource))]
public class MoviePanel : MonoBehaviour
{
    [Tooltip("Hub off: the file to play. Empty = <repo>/hub/.data/media/<movie_id>.mp4.")]
    public string mockFile = "";
    [Range(0f, 1f)] public float duckTo = 0.3f;

    const float ReportEvery = 5f;
    static readonly Regex Tags = new Regex(@"\{[^}]*\}");

    struct Cue { public float start, end; public string text; }

    ForgePanel panel;
    PanelManipulator handles;
    VideoPlayer player;
    AudioSource sound;
    RenderTexture texture;
    RawImage picture;
    CanvasGroup controls;
    TMP_Text title, sub, placeholder, subtitles, position, remaining, duckText;
    DeskIcon playIcon;
    DeskTrack scrubber, volumeTrack;
    GameObject duckChip, bigPlay;
    ForgeSurface ccButton;
    DeskHover ccHover;

    MovieState state;
    string loadedUrl, subtitlesFor;
    readonly List<Cue> cues = new List<Cue>();
    int cue = -1, shownSecond = -1, seekRev = int.MinValue;
    bool prepared, showSubtitles, speaking;
    float reportAt, controlsShown;
    string assistant = "Albie";

    public void Bind(DeskExtras owner)
    {
        assistant = owner != null && !string.IsNullOrEmpty(owner.assistantName) ? owner.assistantName : "Albie";
        if (duckText != null) duckText.text = $"Quieter while {assistant} talks";
    }

    void Awake()
    {
        panel = GetComponent<ForgePanel>();
        panel.KeepAspect = true;
        sound = GetComponent<AudioSource>();
        sound.playOnAwake = false;
        sound.spatialBlend = 0.8f;  // from the screen, but loud enough wherever you sit
        sound.rolloffMode = AudioRolloffMode.Linear;
        sound.minDistance = 2f;
        sound.maxDistance = 30f;
        sound.dopplerLevel = 0f;
        player = GetComponent<VideoPlayer>();
        player.playOnAwake = false;
        player.source = VideoSource.Url;
        player.renderMode = VideoRenderMode.RenderTexture;
        player.audioOutputMode = VideoAudioOutputMode.AudioSource;
        player.controlledAudioTrackCount = 1;
        player.EnableAudioTrack(0, true);
        player.SetTargetAudioSource(0, sound);
        player.waitForFirstFrame = true;
        player.skipOnDrop = true;
        player.isLooping = false;
        Build();
    }

    void OnEnable()
    {
        ForgeEvents.OnPetState += OnPet;
        player.prepareCompleted += OnPrepared;
        player.loopPointReached += OnEnded;
        player.errorReceived += OnError;
        if (state != null) Show(state);
    }

    void OnDisable()
    {
        ForgeEvents.OnPetState -= OnPet;
        player.prepareCompleted -= OnPrepared;
        player.loopPointReached -= OnEnded;
        player.errorReceived -= OnError;
        if (prepared && player.isPlaying) Report(ended: false);
        player.Stop();
        prepared = false;
        loadedUrl = null;
    }

    void Start() => handles = GetComponent<PanelManipulator>();

    void OnDestroy()
    {
        if (texture != null) texture.Release();
    }

    void OnPet(PetState pet) => speaking = pet != null && pet.mode == "speaking";

    // ---------------------------------------------------------------- the hub's state

    public void Show(MovieState value)
    {
        state = value;
        if (state == null || state.movie == null || !isActiveAndEnabled) return;
        var movie = state.movie;
        title.text = movie.title;
        sub.text = (movie.year.HasValue ? movie.year + " · " : "") + (movie.durationS.HasValue ? Length(movie.durationS.Value) : "");
        string url = Source(movie);
        if (url == null)
        {
            placeholder.text = "The movie streams from the hub.\nStart the hub to watch it.";
            placeholder.gameObject.SetActive(true);
            return;
        }
        if (url != loadedUrl)
        {
            loadedUrl = url;
            prepared = false;
            seekRev = int.MinValue;
            player.url = url;
            player.Prepare();
            placeholder.text = "Loading…";
            placeholder.gameObject.SetActive(true);
            LoadSubtitles(movie);
        }
        if (prepared) Apply();
    }

    // Play or pause with the hub; jump only when it says so (seek_rev), otherwise the player keeps time.
    void Apply()
    {
        if (state.seekRev != seekRev)
        {
            seekRev = state.seekRev;
            player.time = state.positionS;
            cue = -1;
        }
        bool play = state.status == "playing";
        if (play && !player.isPlaying) player.Play();
        else if (!play && player.isPlaying) player.Pause();
        playIcon.Kind = play ? DeskIcon.Shape.Pause : DeskIcon.Shape.Play;
        bigPlay.SetActive(!play);
        if (!volumeTrack.Dragging) volumeTrack.Value = state.volume;
    }

    string Source(MovieInfo movie)
    {
        if (ForgeNet.Status == ForgeNetStatus.Live) return ForgeNet.Url(movie.url);
        string path = string.IsNullOrEmpty(mockFile)
            ? Path.Combine(Application.dataPath, "..", "..", "hub", ".data", "media", movie.movieId + ".mp4")
            : Path.IsPathRooted(mockFile) ? mockFile : Path.Combine(Application.dataPath, "..", mockFile);
        return File.Exists(path) ? Path.GetFullPath(path) : null;
    }

    void OnPrepared(VideoPlayer vp)
    {
        // The picture at the video's own size (at most 2048 wide, the Quest limit), the panel at its shape.
        int w = (int)vp.width, h = (int)vp.height;
        if (w > 2048)
        {
            h = Mathf.RoundToInt(h * 2048f / w);
            w = 2048;
        }
        if (texture == null || texture.width != w || texture.height != h)
        {
            if (texture != null) texture.Release();
            texture = new RenderTexture(Mathf.Max(16, w), Mathf.Max(16, h), 0) { name = "Movie" };
        }
        player.targetTexture = texture;
        picture.texture = texture;
        if (w > 0 && h > 0)
        {
            Vector2 size = panel.SizeMeters;
            panel.SizeMeters = new Vector2(size.x, size.x * h / w);
        }
        prepared = true;
        placeholder.gameObject.SetActive(false);
        if (state != null) Apply();
    }

    void OnEnded(VideoPlayer vp) => Report(ended: true);

    void OnError(VideoPlayer vp, string message)
    {
        placeholder.text = "Couldn't play the movie:\n" + message;
        placeholder.gameObject.SetActive(true);
    }

    void Report(bool ended)
    {
        reportAt = Time.time;
        DeskApi.MovieReport((float)player.time, (float)player.length, ended);
    }

    // ---------------------------------------------------------------- per frame

    void Update()
    {
        if (state == null) return;
        float dt = Time.deltaTime;
        // The sound: the hub's volume, dipped while the pet talks.
        float target = state.volume * (speaking ? duckTo : 1f);
        sound.volume = Mathf.MoveTowards(sound.volume, target, dt * 1.5f);
        if (duckChip.activeSelf != speaking) duckChip.SetActive(speaking);

        // Controls while pointed at (or paused), fading.
        bool show = (handles != null && handles.PointedAt) || state.status != "playing" || scrubber.Dragging || volumeTrack.Dragging;
        controlsShown = show ? 1.2f : controlsShown - dt;
        float alpha = Mathf.MoveTowards(controls.alpha, controlsShown > 0f ? 1f : 0f, dt * 5f);
        if (alpha != controls.alpha) controls.alpha = alpha;
        // Pointed at, they take presses at once, before they've faded in.
        bool live = controlsShown > 0f || alpha > 0.05f;
        if (controls.blocksRaycasts != live) controls.blocksRaycasts = live;

        if (!prepared) return;
        float time = (float)player.time, length = (float)player.length;
        int second = Mathf.FloorToInt(time);
        if (second != shownSecond)
        {
            shownSecond = second;
            position.text = DeskUi.Clock(time, roundUp: false);
            remaining.text = "-" + DeskUi.Clock(Mathf.Max(0f, length - time), roundUp: false);
        }
        if (!scrubber.Dragging && length > 0f) scrubber.Value = time / length;
        UpdateSubtitles(time);
        if (player.isPlaying && Time.time - reportAt > ReportEvery) Report(ended: false);
    }

    // ---------------------------------------------------------------- subtitles

    void LoadSubtitles(MovieInfo movie)
    {
        cues.Clear();
        cue = -1;
        subtitlesFor = movie.movieId;
        if (string.IsNullOrEmpty(movie.subtitlesUrl)) return;
        if (ForgeNet.Status == ForgeNetStatus.Live)
        {
            string id = movie.movieId;
            ForgeNet.Get(movie.subtitlesUrl, text => { if (subtitlesFor == id) ParseSrt(text); }, _ => { });
        }
        else
        {
            string path = loadedUrl != null ? Path.ChangeExtension(loadedUrl, null) + ".en.srt" : null;
            if (path != null && File.Exists(path)) ParseSrt(File.ReadAllText(path));
        }
    }

    // SubRip: blocks of an index, "00:00:55,870 --> 00:00:57,538" and the lines. Position tags like {\an8} go.
    void ParseSrt(string text)
    {
        cues.Clear();
        foreach (string block in Regex.Split(text.Replace("\r", ""), "\n\\s*\n"))
        {
            string[] lines = block.Trim().Split('\n');
            int timing = lines.Length > 1 && lines[1].Contains("-->") ? 1 : lines.Length > 0 && lines[0].Contains("-->") ? 0 : -1;
            if (timing < 0) continue;
            string[] parts = lines[timing].Split(new[] { "-->" }, System.StringSplitOptions.None);
            if (parts.Length != 2 || !TryTime(parts[0], out float start) || !TryTime(parts[1], out float end)) continue;
            string body = Tags.Replace(string.Join("\n", lines, timing + 1, lines.Length - timing - 1), "").Trim();
            if (body.Length > 0) cues.Add(new Cue { start = start, end = end, text = body });
        }
        cues.Sort((a, b) => a.start.CompareTo(b.start));
    }

    static bool TryTime(string s, out float seconds)
    {
        seconds = 0f;
        string[] hms = s.Trim().Replace(',', '.').Split(':');
        if (hms.Length != 3) return false;
        if (!int.TryParse(hms[0], out int h) || !int.TryParse(hms[1], out int m)) return false;
        if (!float.TryParse(hms[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sec)) return false;
        seconds = h * 3600f + m * 60f + sec;
        return true;
    }

    void UpdateSubtitles(float time)
    {
        if (!showSubtitles || cues.Count == 0)
        {
            if (subtitles.text.Length > 0) subtitles.text = "";
            return;
        }
        // Usually the same cue or the next one; after a seek, search from the start.
        if (cue < 0 || cue >= cues.Count || time < cues[cue].start - 0.5f) cue = 0;
        while (cue + 1 < cues.Count && cues[cue + 1].start <= time) cue++;
        string line = time >= cues[cue].start && time <= cues[cue].end ? cues[cue].text : "";
        if (line != subtitles.text) subtitles.text = line;
    }

    // ---------------------------------------------------------------- building

    void Build()
    {
        var content = panel.Content;
        var stale = content.Find("Placeholder");
        if (stale != null) stale.gameObject.SetActive(false);

        picture = DeskUi.Rect("Picture", content).gameObject.AddComponent<RawImage>();
        DeskUi.Stretch(picture.rectTransform, 0f, 0f, 0f, 0f);
        picture.color = Color.white;
        picture.texture = Texture2D.blackTexture;

        placeholder = DeskUi.Text("Placeholder", content, 22f, FontStyles.Normal, DeskUi.Muted, TextAlignmentOptions.Center);
        DeskUi.Stretch(placeholder.rectTransform, 40f, 40f, 40f, 40f);
        placeholder.textWrappingMode = TextWrappingModes.Normal;
        placeholder.text = "Loading…";

        subtitles = DeskUi.Text("Subtitles", content, 40f, FontStyles.Normal, Color.white, TextAlignmentOptions.Bottom);
        DeskUi.Stretch(subtitles.rectTransform, 80f, 156f, 80f, 0f);
        subtitles.textWrappingMode = TextWrappingModes.Normal;
        subtitles.outlineWidth = 0.18f;
        subtitles.outlineColor = new Color32(0, 0, 0, 200);
        subtitles.text = "";

        bigPlay = DeskUi.Surface("Paused", content, new Color(1f, 1f, 1f, 0.18f), pill: true, glow: 0.4f).gameObject;
        ((RectTransform)bigPlay.transform).sizeDelta = Vector2.one * 120f;
        var bigIcon = DeskUi.Rect("Icon", bigPlay.transform).gameObject.AddComponent<DeskIcon>();
        bigIcon.Kind = DeskIcon.Shape.Play;
        bigIcon.raycastTarget = false;
        bigIcon.rectTransform.sizeDelta = Vector2.one * 52f;
        bigPlay.SetActive(false);

        duckChip = DeskUi.Surface("Ducked", content, new Color(0.08f, 0.09f, 0.1f, 0.86f), pill: true).gameObject;
        var duckRect = (RectTransform)duckChip.transform;
        duckRect.anchorMin = duckRect.anchorMax = new Vector2(1f, 0f);
        duckRect.pivot = new Vector2(1f, 0f);
        duckRect.anchoredPosition = new Vector2(-40f, 156f);
        duckRect.sizeDelta = new Vector2(370f, 56f);
        duckText = DeskUi.Text("Text", duckChip.transform, 22f, FontStyles.Normal, Color.white, TextAlignmentOptions.Center);
        DeskUi.Stretch(duckText.rectTransform, 12f, 0f, 12f, 0f);
        duckText.text = $"Quieter while {assistant} talks";
        duckChip.SetActive(false);

        // The controls: a shade top and bottom, faded in while pointed at.
        var overlay = DeskUi.Rect("Controls", content);
        DeskUi.Stretch(overlay, 0f, 0f, 0f, 0f);
        controls = overlay.gameObject.AddComponent<CanvasGroup>();
        controls.alpha = 0f;
        controls.blocksRaycasts = false;
        var shadeTop = DeskUi.Surface("ShadeTop", overlay, new Color(0f, 0f, 0f, 0.45f), pill: false, corner: 18f);
        Edge(shadeTop.rectTransform, top: true, 150f);
        var shadeBottom = DeskUi.Surface("ShadeBottom", overlay, new Color(0f, 0f, 0f, 0.55f), pill: false, corner: 18f);
        Edge(shadeBottom.rectTransform, top: false, 144f);

        title = DeskUi.Text("Title", overlay, 40f, FontStyles.Bold, Color.white, TextAlignmentOptions.TopLeft);
        Corner(title.rectTransform, new Vector2(0f, 1f), new Vector2(44f, -32f), new Vector2(900f, 52f));
        sub = DeskUi.Text("Sub", overlay, 24f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.75f), TextAlignmentOptions.TopLeft);
        Corner(sub.rectTransform, new Vector2(0f, 1f), new Vector2(44f, -86f), new Vector2(900f, 34f));
        var close = DeskUi.RoundButton("Close", overlay, 76f, DeskIcon.Shape.Close, DeskApi.MovieClose, out var closeIcon);
        closeIcon.rectTransform.sizeDelta = Vector2.one * 28f;
        Corner((RectTransform)close.transform, new Vector2(1f, 1f), new Vector2(-32f, -32f), Vector2.one * 76f);

        // Bottom row, left to right.
        float x = 40f;
        var play = DeskUi.RoundButton("Play", overlay, 100f, DeskIcon.Shape.Pause, () =>
        {
            if (state == null) return;
            if (state.status == "playing")
            {
                Report(ended: false);
                DeskApi.MoviePause();
            }
            else DeskApi.MovieResume();
        }, out playIcon);
        var playSurface = play.GetComponent<ForgeSurface>();
        playSurface.color = new Color(1f, 1f, 1f, 0.94f);
        var playHover = play.GetComponent<DeskHover>();
        playHover.rest = playSurface.color;
        playHover.lit = Color.white;
        playIcon.color = new Color(0.07f, 0.07f, 0.08f);
        playIcon.rectTransform.sizeDelta = Vector2.one * 38f;
        x = Row((RectTransform)play.transform, x, 100f);
        var back = DeskUi.PillButton("Back", overlay, "−10 s", 76f, 23f, primary: false, () => Skip(-10f), out _);
        x = Row((RectTransform)back.transform, x + 18f, ((RectTransform)back.transform).sizeDelta.x);
        var forward = DeskUi.PillButton("Forward", overlay, "+10 s", 76f, 23f, primary: false, () => Skip(10f), out _);
        x = Row((RectTransform)forward.transform, x + 12f, ((RectTransform)forward.transform).sizeDelta.x);
        position = DeskUi.Text("Position", overlay, 26f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.92f), TextAlignmentOptions.Right);
        x = Row(position.rectTransform, x + 22f, 140f);

        // From the right: volume, its icon, subtitles, time left; the scrubber takes the rest.
        float r = 44f;
        volumeTrack = Track("Volume", overlay, 190f, 12f, out var volumeRect);
        r = RowRight(volumeRect, r, 190f);
        volumeTrack.Committed += v => DeskApi.MovieVolume(v);
        volumeTrack.Changed += v => { if (state != null) state.volume = v; };
        var speaker = DeskUi.Rect("Speaker", overlay).gameObject.AddComponent<DeskIcon>();
        speaker.Kind = DeskIcon.Shape.Speaker;
        speaker.raycastTarget = false;
        r = RowRight(speaker.rectTransform, r + 18f, 38f);
        var cc = DeskUi.PillButton("Subtitles", overlay, "CC", 66f, 22f, primary: false, ToggleSubtitles, out var ccLabel);
        ccLabel.fontStyle = FontStyles.Bold;
        ccButton = cc.GetComponent<ForgeSurface>();
        ccHover = cc.GetComponent<DeskHover>();
        r = RowRight((RectTransform)cc.transform, r + 28f, ((RectTransform)cc.transform).sizeDelta.x);
        remaining = DeskUi.Text("Remaining", overlay, 26f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.92f), TextAlignmentOptions.Left);
        r = RowRight(remaining.rectTransform, r + 22f, 150f);

        scrubber = Track("Scrubber", overlay, 0f, 14f, out var scrubRect);
        scrubRect.anchorMin = new Vector2(0f, 0f);
        scrubRect.anchorMax = new Vector2(1f, 0f);
        scrubRect.pivot = new Vector2(0.5f, 0.5f);
        scrubRect.offsetMin = new Vector2(x + 24f, 38f);
        scrubRect.offsetMax = new Vector2(-(r + 24f), 114f);
        scrubber.Committed += f =>
        {
            float length = (float)player.length;
            if (length <= 0f) return;
            player.time = f * length;  // at once; the hub's seek_rev follows
            DeskApi.MovieSeek(f * length);
        };
    }

    void Skip(float seconds)
    {
        if (prepared) player.time = Mathf.Clamp((float)player.time + seconds, 0f, (float)player.length);
        DeskApi.MovieSkip(seconds);
    }

    void ToggleSubtitles()
    {
        showSubtitles = !showSubtitles;
        Color rest = showSubtitles ? ForgeTheme.PillOn : DeskUi.ButtonRest;
        ccHover.SetRest(rest);
        ccButton.GetComponentInChildren<TMP_Text>().color = showSubtitles ? ForgeTheme.PillOnText : ForgeTheme.PillOffText;
    }

    // A track 76 mm tall to aim at, its bar `bar` mm thick, with a fill and a knob.
    DeskTrack Track(string name, RectTransform parent, float width, float bar, out RectTransform rect)
    {
        var hit = DeskUi.Rect(name, parent).gameObject.AddComponent<Image>();
        hit.color = Color.clear;
        rect = hit.rectTransform;
        rect.sizeDelta = new Vector2(width, 76f);
        var groove = DeskUi.Surface("Groove", rect, new Color(1f, 1f, 1f, 0.26f), pill: true);
        groove.rectTransform.anchorMin = new Vector2(0f, 0.5f);
        groove.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        groove.rectTransform.sizeDelta = new Vector2(0f, bar);
        var fill = DeskUi.Surface("Fill", groove.rectTransform, Color.white, pill: true, glow: 0.4f);
        fill.rectTransform.anchorMin = Vector2.zero;
        fill.rectTransform.anchorMax = new Vector2(0f, 1f);
        fill.rectTransform.offsetMin = fill.rectTransform.offsetMax = Vector2.zero;
        var knob = DeskUi.Surface("Knob", groove.rectTransform, Color.white, pill: true);
        knob.rectTransform.sizeDelta = Vector2.one * (bar + 14f);
        var track = hit.gameObject.AddComponent<DeskTrack>();
        track.fill = fill.rectTransform;
        track.knob = knob.rectTransform;
        return track;
    }

    static void Edge(RectTransform rt, bool top, float height)
    {
        rt.anchorMin = new Vector2(0f, top ? 1f : 0f);
        rt.anchorMax = new Vector2(1f, top ? 1f : 0f);
        rt.pivot = new Vector2(0.5f, top ? 1f : 0f);
        rt.offsetMin = new Vector2(0f, top ? -height : 0f);
        rt.offsetMax = new Vector2(0f, top ? 0f : height);
    }

    static void Corner(RectTransform rt, Vector2 anchor, Vector2 position, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = rt.pivot = anchor;
        rt.anchoredPosition = position;
        rt.sizeDelta = size;
    }

    // Along the bottom row from the left: returns the x after it.
    static float Row(RectTransform rt, float x, float width)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
        rt.pivot = new Vector2(0f, 0.5f);
        rt.anchoredPosition = new Vector2(x, 76f);
        rt.sizeDelta = new Vector2(width, Mathf.Max(rt.sizeDelta.y, 30f));
        return x + width;
    }

    // Along the bottom row from the right: returns the distance from the right edge after it.
    static float RowRight(RectTransform rt, float fromRight, float width)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(1f, 0.5f);
        rt.anchoredPosition = new Vector2(-fromRight, 76f);
        rt.sizeDelta = new Vector2(width, Mathf.Max(rt.sizeDelta.y, 30f));
        return fromRight + width;
    }

    static string Length(float seconds)
    {
        int minutes = Mathf.RoundToInt(seconds / 60f);
        return minutes >= 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";
    }
}
