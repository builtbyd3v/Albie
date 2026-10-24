using System.Collections.Generic;
using Oculus.Interaction.Surfaces;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Round mode buttons lying on the desk near its front edge. Press one (poke, ray + pinch/trigger, or mouse)
/// to unfold that mode's workspace; press it again to fold it. Built from <see cref="Workspace.modes"/>.
/// Two grey buttons after them work on the open mode: Flat / Curve switches the layout, Reset puts its panels back.
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

    static readonly Color ToolColor = new Color(0.55f, 0.6f, 0.68f);

    readonly List<(string id, Image image, Color color)> buttons = new List<(string, Image, Color)>();
    readonly List<Button> tools = new List<Button>();
    TMP_Text layoutLabel;
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
        foreach (var t in tools) Destroy(t.gameObject);
        buttons.Clear();
        tools.Clear();
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

        layoutLabel = AddTool("Dock layout", workspace.ToggleLayout);
        AddTool("Dock reset", workspace.ResetLayout).text = "Reset";
        workspace.layoutChanged.RemoveListener(ShowLayout);
        workspace.layoutChanged.AddListener(ShowLayout);
        ShowLayout(workspace.layout);
        SetActiveMode(workspace.ActiveMode);

        int n = Mathf.Max(1, buttons.Count + tools.Count);
        ((RectTransform)transform).sizeDelta = new Vector2(n * ButtonMm + (n - 1) * SpacingMm + PaddingMm * 2f, HeightMm);
        SyncHitArea();
    }

    // The Interaction SDK only lets rays and pokes hit inside its clippers' boxes, which the prefab saved at its
    // original size. Fit them to the dock, or the outer buttons can't be pressed in XR (the mouse doesn't care).
    void SyncHitArea()
    {
        Vector2 size = ((RectTransform)transform).sizeDelta;
        foreach (var clipper in GetComponentsInChildren<BoundsClipper>(true))
            clipper.Size = new Vector3(size.x, size.y, clipper.Size.z);
    }

    public void SetActiveMode(string modeId)
    {
        foreach (var b in buttons)
        {
            bool active = b.id == modeId;
            b.image.color = active ? b.color : Dim(b.color);
            b.image.transform.localScale = Vector3.one * (active ? 1.15f : 1f);
        }
        // Layout and Reset only mean something while a mode is open.
        foreach (var t in tools)
        {
            t.interactable = modeId != null;
            t.image.color = modeId != null ? ToolColor : Dim(ToolColor);
        }
    }

    TMP_Text AddTool(string name, UnityEngine.Events.UnityAction onClick)
    {
        var button = Instantiate(buttonTemplate, row);
        button.gameObject.SetActive(true);
        button.name = name;
        button.onClick.AddListener(onClick);
        tools.Add(button);
        var label = button.GetComponentInChildren<TMP_Text>();
        label.fontSize = 16f;
        return label;
    }

    // The button says what pressing it does.
    void ShowLayout(WorkspaceLayout layout)
    {
        if (layoutLabel != null) layoutLabel.text = layout == WorkspaceLayout.Curved ? "Flat" : "Curve";
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
