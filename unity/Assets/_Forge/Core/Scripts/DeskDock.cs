using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Round mode buttons lying on the desk near its front edge. Press one (poke, ray + pinch/trigger, or mouse)
/// to unfold that mode's workspace; press it again to fold it. Built from <see cref="Workspace.modes"/>.
/// </summary>
[RequireComponent(typeof(Canvas))]
public class DeskDock : MonoBehaviour
{
    [SerializeField] RectTransform row;
    [SerializeField] Button buttonTemplate;
    [SerializeField] CanvasGroup group;
    [Tooltip("Hub connection light: green live, amber mock data, grey connecting.")]
    [SerializeField] Image statusDot;

    [Tooltip("Distance from the desk's front edge to the row of buttons (m).")]
    public float inset = 0.09f;

    const float ButtonMm = 72f, SpacingMm = 22f, PaddingMm = 18f, HeightMm = 110f;

    readonly List<(string id, Image image, Color color)> buttons = new List<(string, Image, Color)>();
    Coroutine anim;

    public bool IsShown { get; private set; }

    void OnEnable()
    {
        ForgeNet.StatusChanged += ShowStatus;
        ShowStatus(ForgeNet.Status);
    }

    void OnDisable() => ForgeNet.StatusChanged -= ShowStatus;

    void ShowStatus(ForgeNetStatus status)
    {
        if (statusDot == null) return;
        statusDot.color = status == ForgeNetStatus.Live ? new Color(0.3f, 0.9f, 0.4f)
                        : status == ForgeNetStatus.Mock ? new Color(1f, 0.7f, 0.2f)
                        : new Color(0.6f, 0.6f, 0.6f);
    }

    public void Build(Workspace workspace)
    {
        foreach (var b in buttons) Destroy(b.image.gameObject);
        buttons.Clear();
        buttonTemplate.gameObject.SetActive(false);

        foreach (var mode in workspace.modes)
        {
            var button = Instantiate(buttonTemplate, row);
            button.gameObject.SetActive(true);
            button.name = $"Mode {mode.id}";
            button.GetComponentInChildren<TMP_Text>().text = mode.label;
            var image = button.GetComponent<Image>();
            image.color = Dim(mode.color);
            string id = mode.id;
            button.onClick.AddListener(() => workspace.Toggle(id));
            buttons.Add((id, image, mode.color));
        }

        int n = Mathf.Max(1, buttons.Count);
        ((RectTransform)transform).sizeDelta = new Vector2(n * ButtonMm + (n - 1) * SpacingMm + PaddingMm * 2f, HeightMm);
    }

    public void SetActiveMode(string modeId)
    {
        foreach (var b in buttons)
        {
            bool active = b.id == modeId;
            b.image.color = active ? b.color : Dim(b.color);
            b.image.transform.localScale = Vector3.one * (active ? 1.15f : 1f);
        }
    }

    /// <summary>Lay the dock flat on the desk, centered along the front edge, readable from the seat.</summary>
    public void Show(Transform deskFrame, float deskWidth, float deskDepth)
    {
        transform.SetParent(deskFrame, false);
        transform.localPosition = new Vector3(0f, 0.004f, Mathf.Min(inset, deskDepth * 0.5f));
        transform.localRotation = Quaternion.LookRotation(Vector3.down, Vector3.forward);
        float width = ((RectTransform)transform).sizeDelta.x * ForgePanel.MetersPerUnit;
        float fit = Mathf.Min(1f, (deskWidth - 0.04f) / Mathf.Max(width, 0.01f));
        Vector3 scale = Vector3.one * ForgePanel.MetersPerUnit * Mathf.Max(0.5f, fit);

        IsShown = true;
        gameObject.SetActive(true);
        Animate(scale * 0.6f, scale, 0f, 1f, 0.3f, null);
    }

    public void Hide(bool immediate = false)
    {
        IsShown = false;
        if (!gameObject.activeInHierarchy) return;
        if (immediate)
        {
            gameObject.SetActive(false);
            return;
        }
        Vector3 scale = transform.localScale;
        Animate(scale, scale * 0.6f, group.alpha, 0f, 0.2f, () => gameObject.SetActive(false));
    }

    void Animate(Vector3 fromScale, Vector3 toScale, float fromAlpha, float toAlpha, float duration, System.Action done)
    {
        if (anim != null) StopCoroutine(anim);
        group.interactable = toAlpha > 0f;
        group.blocksRaycasts = toAlpha > 0f;
        anim = StartCoroutine(ForgeTween.Run(duration, 0f, k =>
        {
            float e = ForgeTween.EaseOutCubic(k);
            transform.localScale = Vector3.LerpUnclamped(fromScale, toScale, e);
            group.alpha = Mathf.Lerp(fromAlpha, toAlpha, e);
        }, done));
    }

    static Color Dim(Color c) => new Color(c.r * 0.55f, c.g * 0.55f, c.b * 0.55f, 0.85f);
}
