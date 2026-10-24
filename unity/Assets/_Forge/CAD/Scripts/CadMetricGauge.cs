using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// One gauge metric on CadParamsPanel (a metric with min and max): "Static margin 1.57 cal", a chip with its
/// note ("STABLE"), and a bar coloured by its bands with a needle at the value.
/// </summary>
public class CadMetricGauge : MonoBehaviour
{
    [SerializeField] TMP_Text label;
    [SerializeField] Image chip;
    [SerializeField] TMP_Text chipText;
    [SerializeField] RectTransform bar;
    [SerializeField] RectTransform needle;

    public static readonly Color Bad = new Color(0.93f, 0.3f, 0.27f);
    public static readonly Color Warn = new Color(0.98f, 0.7f, 0.2f);
    public static readonly Color Ok = new Color(0.3f, 0.8f, 0.45f);

    readonly List<Image> bands = new List<Image>();

    public static Color StatusColor(string status) => status == "bad" ? Bad : status == "warn" ? Warn : Ok;

    public void Show(CadMetric m)
    {
        label.text = $"{m.label} <b>{m.display}</b>";
        chip.gameObject.SetActive(!string.IsNullOrEmpty(m.note));
        chip.color = StatusColor(m.status);
        chipText.text = m.note?.ToUpperInvariant();

        float min = m.min.Value, span = m.max.Value - min;
        int count = Mathf.Max(1, m.bands.Count);
        while (bands.Count < count)
        {
            var band = new GameObject("Band", typeof(RectTransform), typeof(Image)).GetComponent<Image>();
            band.raycastTarget = false;
            band.transform.SetParent(bar, false);
            bands.Add(band);
        }
        float from = 0f;
        for (int i = 0; i < bands.Count; i++)
        {
            bool used = i < count;
            bands[i].gameObject.SetActive(used);
            if (!used) continue;
            // No bands: one bar in the metric's own status colour.
            float to = m.bands.Count == 0 ? 1f : Mathf.Clamp01((m.bands[i].to - min) / span);
            var rt = bands[i].rectTransform;
            rt.anchorMin = new Vector2(from, 0f);
            rt.anchorMax = new Vector2(to, 1f);
            rt.offsetMin = rt.offsetMax = Vector2.zero;
            bands[i].color = StatusColor(m.bands.Count == 0 ? m.status : m.bands[i].status);
            from = to;
        }
        needle.SetAsLastSibling();
        float x = Mathf.Clamp01((m.value - min) / span);
        needle.anchorMin = needle.anchorMax = new Vector2(x, 0.5f);
    }
}
