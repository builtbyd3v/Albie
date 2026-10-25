using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// DeskSandbox only: what Bit would do, on the number keys (hub live or off). 1 a 25 second tea timer, 2 an hour of
/// focus, 3 a note, 4 the notes open / closed, 5 the notes to a dot, 6 the movie on / off, 0 every timer off.
/// </summary>
public class DeskSandboxKeys : MonoBehaviour
{
    static readonly string[] Notes =
    {
        "Ask Adrian about the gator's jump timing", "Push the demo build by 3", "Re-record the fallback ML run",
        "Check the movie volume on the Quest", "Try the timer dot in the simulator",
    };

    int next;
    MovieState movie;
    UiWidgets widgets = new UiWidgets();

    void OnEnable()
    {
        ForgeEvents.OnMovieState += OnMovie;
        ForgeEvents.OnUiWidgets += OnWidgets;
    }

    void OnDisable()
    {
        ForgeEvents.OnMovieState -= OnMovie;
        ForgeEvents.OnUiWidgets -= OnWidgets;
    }

    void OnMovie(MovieState value) => movie = value;
    void OnWidgets(UiWidgets value) => widgets = value ?? new UiWidgets();

    void Update()
    {
        var k = Keyboard.current;
        if (k == null) return;
        if (k.digit1Key.wasPressedThisFrame) DeskApi.TimerStart(25f, "tea");
        if (k.digit2Key.wasPressedThisFrame) DeskApi.TimerStart(3600f, "");
        if (k.digit3Key.wasPressedThisFrame) DeskApi.NoteAdd(Notes[next++ % Notes.Length]);
        if (k.digit4Key.wasPressedThisFrame) DeskApi.SetWidget("notes", widgets.notes == "closed" ? "open" : "closed");
        if (k.digit5Key.wasPressedThisFrame) DeskApi.SetWidget("notes", "minimized");
        if (k.digit6Key.wasPressedThisFrame)
        {
            if (movie != null && movie.status != "closed") DeskApi.MovieClose();
            else DeskApi.MoviePlay(movie?.movie ?? MockMovie());
        }
        if (k.digit0Key.wasPressedThisFrame) DeskApi.SetWidget("timer", "closed");
    }

    // Hub off: the movie the hub would list, from Resources/Mocks/movie.json.
    static MovieInfo MockMovie()
    {
        var asset = Resources.Load<TextAsset>("Mocks/movie");
        return asset != null ? Newtonsoft.Json.JsonConvert.DeserializeObject<MovieState>(asset.text)?.movie : null;
    }
}
