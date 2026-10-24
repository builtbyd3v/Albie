/// <summary>
/// Shared input flags for modules that read raw keys. <see cref="TextFieldFocused"/> is true while a text field (the
/// Slack composer) has the keyboard, so typed letters are text, not commands: DesktopRig's WASD / E / Shift and the pet's
/// key handlers should skip their keys while it is set (flagged for Core; the Pet side already does the same with
/// TypedSpeechToText.AnyListening). Lives in Messages until Core has a hook of its own.
/// </summary>
public static class ForgeInput
{
    /// <summary>A TMP_InputField in a Forge panel is focused and taking keyboard input.</summary>
    public static bool TextFieldFocused { get; set; }

    [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetStatics() => TextFieldFocused = false;
}
