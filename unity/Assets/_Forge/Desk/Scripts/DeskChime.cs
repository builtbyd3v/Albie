using UnityEngine;

/// <summary>
/// The timers' chime: a soft two-note bell (E6 then B5, each with two quiet overtones) built in code, no sound file;
/// the same one the mockup plays. Repeats every <see cref="interval"/> seconds while a timer rings, for at most
/// <see cref="maxRing"/> seconds after the last one started ringing (then the pill keeps glowing, quietly).
/// </summary>
[RequireComponent(typeof(AudioSource))]
public class DeskChime : MonoBehaviour
{
    public float interval = 2.4f;
    public float maxRing = 60f;
    [Range(0f, 1f)] public float volume = 0.6f;

    const int Rate = 48000;
    static AudioClip clip;

    AudioSource source;
    bool ringing;
    float startedAt, nextAt;

    void Awake()
    {
        source = GetComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0f;
        if (clip == null) clip = Build();
    }

    /// <summary>Ring (or stop). <paramref name="restart"/>: a new timer started ringing, so ring for another minute.</summary>
    public void SetRinging(bool on, bool restart = false)
    {
        if (on && (!ringing || restart))
        {
            startedAt = Time.time;
            nextAt = Mathf.Min(nextAt, Time.time);
            if (!ringing) nextAt = Time.time;
        }
        ringing = on;
    }

    void Update()
    {
        if (!ringing || Time.time - startedAt > maxRing || Time.time < nextAt) return;
        source.PlayOneShot(clip, volume);
        nextAt = Time.time + interval;
    }

    static AudioClip Build()
    {
        // (frequency Hz, start s) per note; (multiple, gain) per partial.
        float[,] notes = { { 1318.5f, 0f }, { 987.8f, 0.34f } };
        float[,] partials = { { 1f, 0.14f }, { 2.76f, 0.03f }, { 5.4f, 0.01f } };
        int length = Mathf.CeilToInt(Rate * 2.3f);
        var data = new float[length];
        for (int n = 0; n < notes.GetLength(0); n++)
        {
            int start = Mathf.RoundToInt(notes[n, 1] * Rate);
            for (int p = 0; p < partials.GetLength(0); p++)
            {
                float f = notes[n, 0] * partials[p, 0], gain = partials[p, 1];
                float decay = 1.7f / Mathf.Pow(partials[p, 0], 0.35f);  // higher partials fade sooner, like a bell
                for (int i = start; i < length; i++)
                {
                    float t = (i - start) / (float)Rate;
                    if (t > decay + 0.05f) break;
                    float attack = Mathf.Clamp01(t / 0.012f);
                    data[i] += gain * attack * Mathf.Exp(-6.9f * t / decay) * Mathf.Sin(2f * Mathf.PI * f * t);
                }
            }
        }
        float peak = 0f;
        foreach (float v in data) peak = Mathf.Max(peak, Mathf.Abs(v));
        if (peak > 0f)
            for (int i = 0; i < length; i++) data[i] *= 0.9f / peak;
        var result = AudioClip.Create("Desk chime", length, 1, Rate, false);
        result.SetData(data, 0);
        return result;
    }
}
