using System.Collections;
using Newtonsoft.Json.Linq;
using UnityEngine;

/// <summary>
/// Plays a scripted list of hub events through <see cref="ForgeNet"/> while it's in mock mode, so a module can be
/// built with the hub off. The sequence is a JSON array:
/// <code>[ { "at": 0.5, "type": "cad.model_updated", "payload": { "version": 1, ... } }, ... ]</code>
/// <c>at</c> is seconds after mock mode starts. Put one in your sandbox scene with your module's sequence file.
/// </summary>
public class ForgeMockPlayer : MonoBehaviour
{
    public TextAsset sequence;
    public bool loop;
    [Tooltip("Also play when the hub is live (for demos without the backend's cooperation).")]
    public bool playWhenLive;

    Coroutine playing;

    void OnEnable() => ForgeNet.StatusChanged += OnStatus;
    void OnDisable() => ForgeNet.StatusChanged -= OnStatus;

    void Start() => OnStatus(ForgeNet.Status);

    void OnStatus(ForgeNetStatus status)
    {
        bool shouldPlay = status == ForgeNetStatus.Mock || (playWhenLive && status == ForgeNetStatus.Live);
        if (shouldPlay && playing == null) playing = StartCoroutine(Play());
        else if (!shouldPlay && playing != null)
        {
            StopCoroutine(playing);
            playing = null;
        }
    }

    IEnumerator Play()
    {
        if (sequence == null) yield break;
        JArray events;
        try { events = JArray.Parse(sequence.text); }
        catch (System.Exception e)
        {
            Debug.LogError($"[ForgeMockPlayer] {sequence.name} isn't a JSON array of events: {e.Message}");
            yield break;
        }

        do
        {
            float start = Time.time;
            foreach (var e in events)
            {
                float at = e.Value<float?>("at") ?? 0f;
                while (Time.time - start < at) yield return null;
                ForgeNet.Emit(e.Value<string>("type"), e["payload"]);
            }
            yield return null;
        } while (loop);
        playing = null;
    }
}
