using UnityEngine;

/// <summary>
/// The design panel's icons, ported from the boards' 24-unit SVGs (stroked, round caps and joins): the design glyphs
/// (rocket, drone, gears, box), the footer's (undo, expand, back, explode, assemble, play, stop), the title's chevron
/// and the banner's warning. Each draws into a <see cref="CadShape"/> at (x, y), <paramref name="size"/> mm square.
/// </summary>
public static class CadIcons
{
    /// <summary>The design's glyph by its hub name (rocket | drone | gears | box); anything else is a box.</summary>
    public static void Glyph(CadShape s, string glyph, float x, float y, float size, Color c)
    {
        float k = size / 24f, w = 1.8f * k;
        s.At(x, y, k);
        switch (glyph)
        {
            case "rocket":
                s.Begin().M(12f, 2f).C(15f, 4.5f, 16.5f, 8f, 16.5f, 12.5f).L(16.5f, 17f).L(7.5f, 17f).L(7.5f, 12.5f)
                    .C(7.5f, 8f, 9f, 4.5f, 12f, 2f).Z()
                    .M(7.5f, 13f).L(5f, 16f).L(5f, 19f).L(7.5f, 17f)
                    .M(16.5f, 13f).L(19f, 16f).L(19f, 19f).L(16.5f, 17f)
                    .M(10.5f, 20.5f).L(13.5f, 20.5f)
                    .Circle(12f, 9f, 1.6f).Stroke(w, c);
                break;
            case "drone":
            case "quad":
                s.Begin().Circle(5.5f, 5.5f, 3f).Circle(18.5f, 5.5f, 3f).Circle(5.5f, 18.5f, 3f).Circle(18.5f, 18.5f, 3f)
                    .M(7.6f, 7.6f).L(10f, 10f).M(16.4f, 7.6f).L(14f, 10f).M(7.6f, 16.4f).L(10f, 14f).M(16.4f, 16.4f).L(14f, 14f)
                    .RoundRect(10f, 10f, 4f, 4f, 1f).Stroke(w, c);
                break;
            case "gears":
            case "gear":
                s.Begin().Circle(9f, 13f, 4.2f).Circle(9f, 13f, 1.4f)
                    .M(9f, 6.3f).L(9f, 7.9f).M(9f, 18.1f).L(9f, 19.7f).M(2.3f, 13f).L(3.9f, 13f).M(14.1f, 13f).L(15.7f, 13f)
                    .M(4.3f, 8.3f).L(5.4f, 9.4f).M(12.6f, 16.6f).L(13.7f, 17.7f).M(4.3f, 17.7f).L(5.4f, 16.6f).M(12.6f, 9.4f).L(13.7f, 8.3f)
                    .Circle(18f, 6f, 2.6f)
                    .M(18f, 2.2f).L(18f, 3.4f).M(18f, 8.6f).L(18f, 9.8f).M(14.2f, 6f).L(15.4f, 6f).M(20.6f, 6f).L(21.8f, 6f)
                    .Stroke(w, c);
                break;
            default:
                s.Begin().M(12f, 2.5f).L(20.5f, 7f).L(20.5f, 17f).L(12f, 21.5f).L(3.5f, 17f).L(3.5f, 7f).Z()
                    .M(3.5f, 7f).L(12f, 11.5f).L(20.5f, 7f).M(12f, 11.5f).L(12f, 21.5f).Stroke(w, c);
                break;
        }
        s.At(0f, 0f);
    }

    public static void Undo(CadShape s, Color c) => Footer(s, c, 0);
    public static void Expand(CadShape s, Color c) => Footer(s, c, 1);
    public static void Back(CadShape s, Color c) => Footer(s, c, 2);
    public static void Explode(CadShape s, Color c) => Footer(s, c, 3);
    public static void Assemble(CadShape s, Color c) => Footer(s, c, 4);

    /// <summary>Fly / Hover / Run: a filled play triangle (14 mm in the 16 mm box).</summary>
    public static void Play(CadShape s, Color c)
    {
        float k = 14f / 24f, o = (s.Width - 14f) * 0.5f;
        s.At(o, (s.Height - 14f) * 0.5f, k);
        s.Begin().M(7f, 4.5f).L(7f, 19.5f).L(19f, 12f).Z().Fill(c);
        s.At(0f, 0f);
    }

    /// <summary>Stop: a filled rounded square (13 mm).</summary>
    public static void Stop(CadShape s, Color c)
    {
        float k = 13f / 24f;
        s.At((s.Width - 13f) * 0.5f, (s.Height - 13f) * 0.5f, k);
        s.Begin().RoundRect(5f, 5f, 14f, 14f, 2.5f).Fill(c);
        s.At(0f, 0f);
    }

    // The footer's stroked icons, 16 mm, stroke 2 units.
    static void Footer(CadShape s, Color c, int which)
    {
        float size = Mathf.Min(s.Width, s.Height), k = size / 24f, w = 2f * k;
        s.At((s.Width - size) * 0.5f, (s.Height - size) * 0.5f, k);
        s.Begin();
        switch (which)
        {
            case 0:  // undo: a hook arrow
                s.M(9f, 14f).L(4f, 9f).L(9f, 4f)
                    .M(4f, 9f).L(14.5f, 9f).Arc(14.5f, 14.5f, 5.5f, Mathf.PI * 0.5f, -Mathf.PI * 0.5f).L(11f, 20f);
                break;
            case 1:  // expand: corners out
                s.M(14f, 4f).L(20f, 4f).L(20f, 10f).M(10f, 20f).L(4f, 20f).L(4f, 14f)
                    .M(20f, 4f).L(13.5f, 10.5f).M(4f, 20f).L(10.5f, 13.5f);
                break;
            case 2:  // back to desk: corners in
                s.M(20f, 10f).L(14f, 10f).L(14f, 4f).M(4f, 14f).L(10f, 14f).L(10f, 20f)
                    .M(14f, 10f).L(20.5f, 3.5f).M(10f, 14f).L(3.5f, 20.5f);
                break;
            case 3:  // explode: a small square with rays
                s.RoundRect(9f, 9f, 6f, 6f, 1.2f).M(12f, 2f).L(12f, 6f).M(12f, 18f).L(12f, 22f).M(2f, 12f).L(6f, 12f).M(18f, 12f).L(22f, 12f);
                break;
            default: // assemble: a bigger square, shorter rays
                s.RoundRect(8f, 8f, 8f, 8f, 1.5f).M(12f, 2f).L(12f, 5f).M(12f, 19f).L(12f, 22f).M(2f, 12f).L(5f, 12f).M(19f, 12f).L(22f, 12f);
                break;
        }
        s.Stroke(w, c);
        s.At(0f, 0f);
    }

    /// <summary>The title's chevron (a down "v"), 12 mm, stroke 2.6 units.</summary>
    public static void Chevron(CadShape s, float x, float y, float size, Color c)
    {
        float k = size / 24f;
        s.At(x, y, k);
        s.Begin().M(6f, 9f).L(12f, 15f).L(18f, 9f).Stroke(2.6f * k, c);
        s.At(0f, 0f);
    }

    /// <summary>The failed-build banner's warning triangle, 16 mm.</summary>
    public static void Warning(CadShape s, float x, float y, float size, Color c)
    {
        float k = size / 24f;
        s.At(x, y, k);
        s.Begin().M(12f, 3f).L(2.5f, 20f).L(21.5f, 20f).Z().M(12f, 10f).L(12f, 14.5f).Stroke(2f * k, c);
        s.Disc(12f, 17.3f, 1.1f, c);
        s.At(0f, 0f);
    }
}
