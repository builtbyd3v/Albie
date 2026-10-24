using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>One parameter in CadParamsPanel: a stepped slider, or one button per option (nose shape).</summary>
public class CadParamRow : MonoBehaviour
{
    [SerializeField] TMP_Text label;
    [SerializeField] TMP_Text valueText;
    [SerializeField] Slider slider;
    [SerializeField] RectTransform options;
    [SerializeField] Button optionTemplate;

    static readonly Color OptionOn = new Color(0.25f, 0.65f, 1f, 1f);
    static readonly Color OptionOff = new Color(1f, 1f, 1f, 0.12f);

    CadParam param;
    Action<string, object> commit;
    double step = 1;
    float committedSliderValue;
    readonly List<(Button button, string option)> optionButtons = new List<(Button, string)>();

    public string Name => param?.name;

    public void Bind(CadParam p, Action<string, object> onCommit)
    {
        param = p;
        commit = onCommit;
        label.text = p.label;
        slider.gameObject.SetActive(!p.IsOptions);
        options.gameObject.SetActive(p.IsOptions);
        if (p.IsOptions)
        {
            foreach (var option in p.options)
            {
                var button = Instantiate(optionTemplate, options);
                button.gameObject.SetActive(true);
                button.GetComponentInChildren<TMP_Text>().text = option;
                string chosen = option;
                button.onClick.AddListener(() => { if (param.value as string != chosen) commit(param.name, chosen); });
                optionButtons.Add((button, option));
            }
        }
        else
        {
            // Slider units are steps, so dragging snaps to the hub's step size.
            step = p.step.HasValue && p.step.Value > 0 ? p.step.Value : 1;
            slider.wholeNumbers = true;
            slider.minValue = (float)Math.Round((p.min ?? 0) / step);
            slider.maxValue = (float)Math.Round((p.max ?? 100) / step);
            slider.onValueChanged.AddListener(_ => ShowValue());
            slider.GetComponent<CadSliderRelease>().released += Commit;
        }
        SetValue(p);
    }

    /// <summary>Show the hub's value (after a regen, or to snap back after a refused change).</summary>
    public void SetValue(CadParam p)
    {
        param = p;
        if (p.IsOptions)
        {
            foreach (var (button, option) in optionButtons)
                button.targetGraphic.color = option == p.value as string ? OptionOn : OptionOff;
            valueText.text = "";
            return;
        }
        committedSliderValue = (float)Math.Round(p.Number / step);
        slider.SetValueWithoutNotify(committedSliderValue);
        ShowValue();
    }

    void Commit()
    {
        if (Mathf.Approximately(slider.value, committedSliderValue)) return;
        double v = slider.value * step;
        bool integer = param.value is long || param.value is int;
        commit(param.name, integer ? (object)(long)Math.Round(v) : v);
    }

    void ShowValue()
    {
        double v = slider.value * step;
        valueText.text = string.IsNullOrEmpty(param.unit) ? $"{v:0.##}" : $"{v:0.##} {param.unit}";
    }
}
