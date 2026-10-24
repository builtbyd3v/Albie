using TMPro;

/// <summary>
/// Text that changes every frame (telemetry: altitude, rpm, time) without allocating: built in a char buffer, numbers
/// with thousands commas or fixed decimals (TextMeshPro's SetText formats can't do commas), and handed to TextMeshPro
/// only when it differs from what's showing, so an unchanged number doesn't re-mesh. Rich-text tags pass through.
/// </summary>
public sealed class CadLiveText
{
    readonly TMP_Text text;
    char[] buffer = new char[128];
    char[] shown = new char[128];
    int length, shownLength = -1;

    public TMP_Text Text => text;

    public CadLiveText(TMP_Text target) => text = target;

    public CadLiveText Begin()
    {
        length = 0;
        return this;
    }

    public CadLiveText Add(string s)
    {
        if (s == null) return this;
        for (int i = 0; i < s.Length; i++) Put(s[i]);
        return this;
    }

    public CadLiveText Add(char c)
    {
        Put(c);
        return this;
    }

    /// <summary>A whole number, "3,000" with <paramref name="commas"/>; a minus sign is the typographic one.</summary>
    public CadLiveText Int(long v, bool commas = true)
    {
        if (v < 0)
        {
            Put('−');
            v = -v;
        }
        int start = length, digits = 0;
        do
        {
            if (commas && digits > 0 && digits % 3 == 0) Put(',');
            Put((char)('0' + (int)(v % 10)));
            v /= 10;
            digits++;
        } while (v > 0);
        Reverse(start, length - 1);
        return this;
    }

    /// <summary>A number with <paramref name="decimals"/> places (rounded), "73.5".</summary>
    public CadLiveText Fixed(float v, int decimals)
    {
        if (decimals <= 0) return Int((long)System.Math.Round(v), false);
        long scale = 1;
        for (int i = 0; i < decimals; i++) scale *= 10;
        long n = (long)System.Math.Round(System.Math.Abs(v) * scale);
        if (v < 0 && n > 0) Put('−');
        Int(n / scale, false);
        Put('.');
        long frac = n % scale;
        for (long d = scale / 10; d > 0; d /= 10)
        {
            Put((char)('0' + (int)(frac / d)));
            frac %= d;
        }
        return this;
    }

    /// <summary>Show it, if it changed.</summary>
    public void Apply()
    {
        if (text == null) return;
        bool same = length == shownLength;
        for (int i = 0; same && i < length; i++) same = buffer[i] == shown[i];
        if (same) return;
        if (shown.Length < buffer.Length) shown = new char[buffer.Length];
        System.Array.Copy(buffer, shown, length);
        shownLength = length;
        text.SetCharArray(buffer, 0, length);
    }

    /// <summary>Forget what's showing (the text was set some other way).</summary>
    public void Invalidate() => shownLength = -1;

    void Put(char c)
    {
        if (length == buffer.Length) System.Array.Resize(ref buffer, buffer.Length * 2);
        buffer[length++] = c;
    }

    void Reverse(int a, int b)
    {
        while (a < b)
        {
            char t = buffer[a];
            buffer[a++] = buffer[b];
            buffer[b--] = t;
        }
    }
}
