using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;

/// <summary>
/// The design panel's look and building blocks (CAD UX spec section 2; boards HANDOFF_UI2/cad/CadPanels.dc.html and
/// CadExperience.dc.html): colours, the type scale and small factories for the UI CadParamsPanel builds at runtime.
/// 1 canvas unit = 1 mm. Rects are placed from their parent's top left with y down, as on the boards, so a number here
/// is the board's px. Pivots sit at the centre, so a press can dip a button about its middle.
/// </summary>
public static class CadUi
{
    // ---- Colours: graphite and white light; the CAD hue only as an accent; status colours from CadLook ----

    public static Color Hue => ForgeTheme.CAD;                        // #FF8A3D
    public static readonly Color Dark = new Color32(0x1C, 0x1E, 0x21, 255);   // text on a white pill
    public static readonly Color OkText = Hex(0x7BF0AE), WarnText = Hex(0xFFD27F), BadText = Hex(0xFF9A90);
    public static readonly Color CgText = Hex(0x9CC6FF), CpText = Hex(0xFF9A90);
    public static readonly Color HubLive = Hex(0x34D17A), HubOff = Hex(0xFFB52E);
    public static readonly Color Peach = Hex(0xFFB98A);               // "was 300", the design glyph
    public static readonly Color PeachLight = Hex(0xFFCFAE);          // a changed value
    public static readonly Color PhaseText = Hex(0xFFD2B3);           // telemetry phase pill
    public static readonly Color ActiveText = Hex(0xFFD8BD);          // an active footer button (Assemble, Back to desk)
    public static readonly Color BusyText = Hex(0xFFC9A4);            // "Rebuilding with ..."
    public static readonly Color BannerTitle = Hex(0xFFC0B9);
    public static readonly Color Ember = Hex(0xFFF1E4);               // the flown part of the flight curve
    public static readonly Color MenuFill = new Color32(30, 33, 38, 252);
    public static readonly Color ToastFill = new Color32(15, 17, 21, 230);
    public static readonly Color Hole = new Color32(14, 17, 23, 255); // a bore, the CG disc's ground

    public static Color White(float a) => new Color(1f, 1f, 1f, a);
    public static Color HueA(float a) => Alpha(Hue, a);
    public static Color Alpha(Color c, float a) => new Color(c.r, c.g, c.b, a);
    public static Color Status(string s) => CadLook.StatusColor(s);
    public static Color StatusText(string s) => s == "bad" ? BadText : s == "warn" ? WarnText : OkText;

    // ---- Type (mm). Nothing under 10: at 0.8 m, 10 mm is about 0.7 deg ----

    public const float Headline = 28f, Digits = 40f, Title = 17f, TileValue = 16.5f, Value = 14f;
    public const float Body = 12.5f, Small = 11.5f, Overline = 11f, Unit = 10.5f, Tick = 10f;

    // Inter's own metrics (per em): CSS centres a line's glyph box in its line height; TMP stacks lines at 1.21 em.
    public const float InterLine = 1.2102f, InterAscent = 0.96875f;

    // ---- Rects ----

    public static RectTransform Rect(Transform parent, string name)
    {
        var rt = (RectTransform)new GameObject(name, typeof(RectTransform)).transform;
        rt.SetParent(parent, false);
        return rt;
    }

    /// <summary>x, y from the parent's top left (y down), w x h.</summary>
    public static void Place(RectTransform rt, float x, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(x + w * 0.5f, -(y + h * 0.5f));
    }

    /// <summary>Hung from the parent's top right: <paramref name="right"/> from its right edge to the rect's.</summary>
    public static void PlaceRight(RectTransform rt, float right, float y, float w, float h)
    {
        rt.anchorMin = rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.sizeDelta = new Vector2(w, h);
        rt.anchoredPosition = new Vector2(-(right + w * 0.5f), -(y + h * 0.5f));
    }

    /// <summary>The parent's width less <paramref name="left"/> and <paramref name="right"/>, <paramref name="y"/> from its top.</summary>
    public static void Band(RectTransform rt, float y, float h, float left = 0f, float right = 0f)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(1f, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, -(y + h));
        rt.offsetMax = new Vector2(-right, -y);
    }

    /// <summary>A share of the parent's width (<paramref name="x0"/>..<paramref name="x1"/> of 1), less padding each side.</summary>
    public static void Column(RectTransform rt, float x0, float x1, float padLeft, float padRight, float y, float h)
    {
        rt.anchorMin = new Vector2(x0, 1f);
        rt.anchorMax = new Vector2(x1, 1f);
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(padLeft, -(y + h));
        rt.offsetMax = new Vector2(-padRight, -y);
    }

    /// <summary>Fills the parent less insets.</summary>
    public static void Stretch(RectTransform rt, float left, float top, float right, float bottom)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.pivot = new Vector2(0.5f, 0.5f);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    /// <summary>
    /// Columns that share the parent's width by <paramref name="flex"/> with <paramref name="gap"/> between them (CSS
    /// flex: the footer's 1 / 1.3 / 1.3 / 1.15, the tiles' four equal ones). Hidden rects (inactive) take no room.
    /// </summary>
    public static void Share(IList<RectTransform> rects, IList<float> flex, float gap, float y, float h)
    {
        float total = 0f;
        int shown = 0;
        for (int i = 0; i < rects.Count; i++)
            if (rects[i].gameObject.activeSelf) { total += flex[i]; shown++; }
        if (shown == 0 || total <= 0f) return;
        float at = 0f;
        int k = 0;
        for (int i = 0; i < rects.Count; i++)
        {
            if (!rects[i].gameObject.activeSelf) continue;
            float x0 = at / total, x1 = (at + flex[i]) / total;
            Column(rects[i], x0, x1, k == 0 ? 0f : gap * 0.5f, k == shown - 1 ? 0f : gap * 0.5f, y, h);
            at += flex[i];
            k++;
        }
    }

    /// <summary>Lays sized rects out left to right, wrapping at <paramref name="width"/>; the height used.</summary>
    public static float Flow(IList<RectTransform> rects, float width, float gapX, float gapY, float y0)
    {
        float x = 0f, y = y0, row = 0f;
        for (int i = 0; i < rects.Count; i++)
        {
            var rt = rects[i];
            if (!rt.gameObject.activeSelf) continue;
            Vector2 size = rt.sizeDelta;
            if (x > 0f && x + size.x > width + 0.01f)
            {
                x = 0f;
                y += row + gapY;
                row = 0f;
            }
            Place(rt, x, y, size.x, size.y);
            x += size.x + gapX;
            row = Mathf.Max(row, size.y);
        }
        return row > 0f ? y + row - y0 : 0f;
    }

    // ---- Text ----

    public static TextMeshProUGUI Text(Transform parent, string name, float size, Color color, bool bold = false,
        TextAlignmentOptions align = TextAlignmentOptions.Left, bool wrap = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var t = go.AddComponent<TextMeshProUGUI>();
        if (ForgeTheme.Font != null) t.font = ForgeTheme.Font;
        t.fontSize = size;
        t.color = color;
        t.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
        t.alignment = align;
        t.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
        // Overflow, not Ellipsis: TMP hides a single line outright when its box is a hair shorter than the font's
        // line height (a 28 mm headline in a 31 mm box), and the layout sizes boxes to the board, not the font.
        t.overflowMode = TextOverflowModes.Overflow;
        t.richText = true;
        t.raycastTarget = false;
        t.text = "";
        return t;
    }

    /// <summary>Overline: 11 mm caps, +0.12 em, 50% (section headings, "WILL IT FLY?").</summary>
    public static TextMeshProUGUI OverlineText(Transform parent, string name)
    {
        var t = Text(parent, name, Overline, White(0.5f), bold: true);
        t.characterSpacing = 12f;
        t.fontStyle |= FontStyles.UpperCase;
        return t;
    }

    /// <summary>CSS line height for wrapped text: TMP's extra line spacing (em / 100) over Inter's own 1.21.</summary>
    public static void LineHeight(TMP_Text t, float ratio) => t.lineSpacing = (ratio - InterLine) * 100f;

    /// <summary>A text's width on one line.</summary>
    public static float Width(TMP_Text t, string s) => string.IsNullOrEmpty(s) ? 0f : t.GetPreferredValues(s).x;

    /// <summary>A wrapped text's height at <paramref name="width"/>.</summary>
    public static float Height(TMP_Text t, string s, float width) =>
        string.IsNullOrEmpty(s) ? 0f : t.GetPreferredValues(s, Mathf.Max(1f, width), 0f).y;

    // ---- Surfaces ----

    /// <summary>A rounded flat fill (Forge/PanelSurface) with an optional 1 mm outline in its own colour at
    /// <paramref name="outline"/> alpha: the boards' "background + inset 1px outline" in one graphic.</summary>
    public static ForgeSurface Surface(Transform parent, string name, Color color, float corner, float outline = 0f)
    {
        var rt = Rect(parent, name);
        var s = rt.gameObject.AddComponent<ForgeSurface>();
        s.Style = ForgeSurface.Kind.Fill;
        s.Corner = corner;
        s.Outline = outline;
        s.color = color;
        s.raycastTarget = false;
        return s;
    }

    public static ForgeSurface Pill(Transform parent, string name, Color color, float glow = 0f)
    {
        var s = Surface(parent, name, color, 0f);
        s.Pill = true;
        s.Glow = glow;
        return s;
    }

    public static CadShape Shape(Transform parent, string name, System.Action<CadShape> paint)
    {
        var rt = Rect(parent, name);
        var s = rt.gameObject.AddComponent<CadShape>();
        s.raycastTarget = false;
        s.paint = paint;
        return s;
    }

    public static CadHitArea Hit(Transform parent, string name)
    {
        var rt = Rect(parent, name);
        return rt.gameObject.AddComponent<CadHitArea>();
    }

    public static CanvasGroup Group(GameObject go)
    {
        var g = go.GetComponent<CanvasGroup>();
        return g != null ? g : go.AddComponent<CanvasGroup>();
    }

    public static void Show(Component c, bool on)
    {
        if (c != null && c.gameObject.activeSelf != on) c.gameObject.SetActive(on);
    }

    // ---- Words and numbers (the hub's, for Inter's glyphs) ----

    static CultureInfo Inv => CultureInfo.InvariantCulture;

    /// <summary>Hub text made safe for Inter's static atlas: no squares, divide signs or diameter marks.</summary>
    public static string Clean(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOf('²') >= 0) s = s.Replace("in²", "sq in").Replace("mm²", "sq mm").Replace("²", "2");
        if (s.IndexOf('³') >= 0) s = s.Replace("³", "3");
        if (s.IndexOf('÷') >= 0) s = s.Replace("÷", "/");
        if (s.IndexOf('Ø') >= 0) s = s.Replace("Ø", "dia. ");
        return s.Replace(" N m", " N·m");
    }

    /// <summary>A value by its step: "300", "1.5", "0.75"; thousands get commas ("1,300").</summary>
    public static string Number(double v, double? step)
    {
        int dec = 0;
        if (step.HasValue && step.Value > 0 && step.Value < 1)
        {
            string st = step.Value.ToString("0.########", Inv);
            int dot = st.IndexOf('.');
            dec = dot >= 0 ? Mathf.Min(2, st.Length - dot - 1) : 0;
        }
        string s = Mathf.Abs((float)v) >= 1000f ? v.ToString(dec > 0 ? "#,0." + new string('0', dec) : "#,0", Inv)
            : v.ToString(dec > 0 ? "0." + new string('0', dec) : "0", Inv);
        if (dec == 2 && s.EndsWith("0") && s.IndexOf('.') >= 0) s = s.Substring(0, s.Length - 1);
        return s;
    }

    /// <summary>A parameter's value in words: a number by its step, or an option capitalised ("Ogive").</summary>
    public static string ParamValue(CadParam p, object v)
    {
        if (v == null) return "";
        if (v is string str) return Capital(str);
        try { return Number(System.Convert.ToDouble(v, Inv), p?.step); }
        catch (System.FormatException) { return v.ToString(); }
        catch (System.InvalidCastException) { return v.ToString(); }
    }

    public static string Capital(string s) =>
        string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s.Substring(1);

    /// <summary>"3,000".</summary>
    public static string Thousands(double v) => v.ToString("#,0", Inv);

    public static string F(double v, string format) => v.ToString(format, Inv);

    /// <summary>A metric's display split for a tile or dial: "14.1 : 1" -> ("14.1", ": 1"), "1725 g" -> ("1.72", "kg").</summary>
    public static void SplitDisplay(CadMetric m, out string value, out string unit)
    {
        string d = Clean(m?.display ?? "");
        if (m != null && m.unit == "g" && m.value >= 1000f)
        {
            value = (m.value / 1000f).ToString("0.00", Inv);
            unit = "kg";
            return;
        }
        int space = d.IndexOf(' ');
        value = space > 0 ? d.Substring(0, space) : d;
        unit = space > 0 ? d.Substring(space + 1) : "";
        if (value.StartsWith("-")) value = "−" + value.Substring(1);
    }

    /// <summary>"4 min ago" from the hub's unix seconds (its clock; close enough on one network).</summary>
    public static string Ago(double? createdAt)
    {
        if (!createdAt.HasValue || createdAt.Value <= 0) return null;
        double s = System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0 - createdAt.Value;
        if (s < 45) return "just now";
        if (s < 3600) return Mathf.Max(1, Mathf.RoundToInt((float)(s / 60))) + " min ago";
        if (s < 86400) return Mathf.RoundToInt((float)(s / 3600)) + " h ago";
        return Mathf.RoundToInt((float)(s / 86400)) + " d ago";
    }

    /// <summary>How a version was made, for the History and Last change lines.</summary>
    public static string Source(string source)
    {
        switch (source)
        {
            case "pet": return "by Bit";
            case "preset": return "preset";
            case "startup":
            case "open": return "opened";
            case "api": return "slider";
            case "script": return "script saved";
            case "undo": return "undo";
            case "reset": return "reset";
            case "mock": return "stored";
            default: return string.IsNullOrEmpty(source) ? "" : source;
        }
    }

    static Color Hex(int rgb) => new Color32((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, 255);
}
