using TMPro;
using UnityEngine;

/// <summary>
/// The Forge look in one place: light drawn on the desk (dock, volume bases) and graphite panels with white pills.
/// Colors, sizes (mm, 1 canvas unit = 1 mm) and timings shared by <see cref="DeskDock"/>, <see cref="ForgePanel"/>,
/// <see cref="PanelManipulator"/> and <see cref="VolumeManipulator"/>. Assets (the Inter font, the light materials)
/// load from Core/Resources/ForgeLook, built by Forge > Build Look Assets.
/// </summary>
public static class ForgeTheme
{
    // Mode hues: only a faint tint of these shows (HueStrength); the light itself is white.
    public static readonly Color ML = Hex(0x4D8DFF);
    public static readonly Color CAD = Hex(0xFF8A3D);
    public static readonly Color Code = Hex(0x22CF86);
    public static readonly Color Messages = Hex(0xA77BFF);
    public static readonly Color Tool = Hex(0xCFE0FF);
    public const float HueStrength = 0.10f;

    // Hub status light.
    public static readonly Color HubLive = Hex(0x4BE38A), HubLiveGlow = Hex(0x22C46A);
    public static readonly Color HubMock = Hex(0xFFC04D), HubMockGlow = Hex(0xFFB52E);
    public static readonly Color HubConnecting = Hex(0xA3A9B3), HubConnectingGlow = Hex(0x7C838F);

    // Desk dock (mm).
    public const float DockModeRing = 72f, DockToolRing = 59f, DockGap = 23f, DockPadding = 25f, DockHeight = 115f;
    public const float DockCorner = 28f, DockDivider = 46f, DockRise = 25f, DockModeLabel = 15.5f, DockToolLabel = 10.7f;
    public const float DockTilt = 22f;           // how far a raised ring turns toward the head (deg)
    public const float DockNearRange = 110f;     // pointer distance at which a ring starts to brighten (mm)
    public const float DockNudgeRing = 44f, DockNudgeLabel = 22f;  // the ‹ › rings at the ends that slide the dock

    // Timings (s).
    public const float HoverFade = 0.3f, PressTime = 0.12f, LiftTime = 0.6f, HeadSmoothing = 0.35f, SelectFade = 0.45f;
    // Dock entrance: the frame slides in along the front edge, then the rings plop on one by one.
    public const float DockSlideTime = 0.6f, DockPlopTime = 0.25f, DockPlopStagger = 0.08f;
    public const float DockNudgeTime = 0.3f;  // ‹ / ›: one step along the desk

    // Motion (s). A mode unfolds out of the dock ring that was pressed and folds back into it; Flat / Curve and Reset
    // spring the items to their new places. Items go centre out (Main, the pair beside it, the outer pair, Above) and
    // fold outermost first; switching modes, the new one starts SwitchDelay after the old one starts folding.
    public const float UnfoldTime = 0.55f, UnfoldStagger = 0.06f, FoldTime = 0.3f, FoldStagger = 0.04f, SwitchDelay = 0.1f;
    public const float LayoutTime = 0.45f, LayoutStagger = 0.03f;
    public const float UnfoldFade = 0.4f;    // share of the unfold over which a panel fades in
    public const float UnfoldScale = 0.04f;  // a panel's scale as it leaves (and goes back into) the ring

    // Graphite panels.
    public static readonly Color PanelTop = new Color32(33, 36, 42, 245);
    public static readonly Color PanelBottom = new Color32(19, 21, 25, 245);
    public static readonly Color PanelText = new Color(1f, 1f, 1f, 0.95f);
    public static readonly Color PanelSubtext = new Color(1f, 1f, 1f, 0.5f);
    public static readonly Color Hairline = new Color(1f, 1f, 1f, 0.07f);
    public const float PanelCorner = 26f, PanelHeader = 64f, PanelInset = 18f;

    // Horizon-style pills: dim when off, near-white with dark text when on.
    public static readonly Color PillOff = new Color(1f, 1f, 1f, 0.10f);
    public static readonly Color PillOffHover = new Color(1f, 1f, 1f, 0.18f);
    public static readonly Color PillOffText = new Color(1f, 1f, 1f, 0.85f);
    public static readonly Color PillOn = new Color(1f, 1f, 1f, 0.92f);
    public static readonly Color PillOnText = Hex(0x1C1E21);
    public const float PillHeight = 30f, PillText = 13f;

    // Handles: white bars that glow brighter while pointed at or held.
    public static readonly Color HandleRest = new Color(1f, 1f, 1f, 0.62f);
    public static readonly Color HandleLit = new Color(1f, 1f, 1f, 1f);

    /// <summary>Mode hue at the given strength multiple (1 = the faint tint, 2.2 = the halo).</summary>
    public static Color Tint(Color hue, float alpha) => new Color(hue.r, hue.g, hue.b, Mathf.Clamp01(alpha));

    // ---- Assets ----

    static TMP_FontAsset font;
    static Material glowText, frame, pool, column, ring, panel;

    /// <summary>Inter (SIL OFL). Core text only; modules keep their own fonts.</summary>
    public static TMP_FontAsset Font => font != null ? font : font = Load<TMP_FontAsset>("Inter SDF");
    /// <summary>Inter with a soft white glow, for text drawn as light (dock labels, volume labels).</summary>
    public static Material GlowText => glowText != null ? glowText : glowText = Load<Material>("Inter Glow");
    public static Material FrameLight => frame != null ? frame : frame = Load<Material>("NeonFrame");
    public static Material PoolLight => pool != null ? pool : pool = Load<Material>("LightPool");
    public static Material ColumnLight => column != null ? column : column = Load<Material>("NeonColumn");
    public static Material RingLight => ring != null ? ring : ring = Load<Material>("NeonRing");
    /// <summary>UI material for graphite panel backgrounds (rounded, gradient, rim light).</summary>
    public static Material PanelSurface => panel != null ? panel : panel = Load<Material>("PanelSurface");

    /// <summary>A mode's white dock icon (Resources/ForgeLook/DockIcons/{modeId}.svg, imported as a sprite), or null.</summary>
    public static Sprite DockIcon(string modeId) => Resources.Load<Sprite>("ForgeLook/DockIcons/" + modeId);

    static T Load<T>(string name) where T : Object
    {
        var asset = Resources.Load<T>("ForgeLook/" + name);
        if (asset == null) Debug.LogWarning($"[ForgeTheme] Missing Resources/ForgeLook/{name}. Run Forge > Build Look Assets.");
        return asset;
    }

    static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);

    // A unit quad (-0.5..0.5, facing -Z like a canvas) shared by every light layer; scale its transform to size it.
    static Mesh quad;
    public static Mesh Quad
    {
        get
        {
            if (quad != null) return quad;
            quad = new Mesh { name = "ForgeQuad" };
            quad.SetVertices(new[] { new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f) });
            quad.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f) });
            quad.SetColors(new[] { Color.white, Color.white, Color.white, Color.white });
            quad.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            quad.RecalculateBounds();
            return quad;
        }
    }
}
