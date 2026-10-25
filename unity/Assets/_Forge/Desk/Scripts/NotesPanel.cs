using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The user's notes (Bit keeps them: "add that to my notes"). Newest first, each with a ring to check it off, how long
/// ago, and a small × to remove it (Undo for a few seconds after, the hub keeps it). Notes are added by voice, so the
/// footer says who to ask (<see cref="DeskExtras.assistantName"/>). The header's − (with Face me / Straight) shrinks it
/// into a dot with the open count. Built at runtime under the panel's Content (Desk/Prefabs/NotesPanel.prefab).
/// </summary>
public class NotesPanel : MonoBehaviour
{
    const float RowGap = 8f, FooterHeight = 42f;
    static readonly Color RowRest = new Color(1f, 1f, 1f, 0.06f);
    static readonly Color RowLit = new Color(1f, 1f, 1f, 0.1f);
    static readonly Color TextColor = new Color(1f, 1f, 1f, 0.93f);
    static readonly Color DoneText = new Color(1f, 1f, 1f, 0.52f);

    class Row
    {
        public RectTransform root;
        public ForgeSurface check;
        public DeskIcon tick;
        public TMP_Text text, age;
        public DeskNote note;
    }

    readonly List<Row> rows = new List<Row>();
    ForgePanel panel;
    RectTransform list;
    GameObject emptyState, toast;
    TMP_Text emptyCaption, hint, toastText;
    NotesUpdated notes;
    string assistant = "Albie", toastFor;
    float toastUntil, agesAt;
    bool built;

    public void Bind(DeskExtras owner)
    {
        assistant = owner != null && !string.IsNullOrEmpty(owner.assistantName) ? owner.assistantName : "Albie";
        Build();
        hint.text = $"Ask {assistant} to add a note";
        emptyCaption.text = $"Ask {assistant} to write something down.";
    }

    void Awake() => Build();

    void Start()
    {
        var handles = GetComponent<PanelManipulator>();
        if (handles != null) handles.AddMinimize(() => DeskApi.SetWidget("notes", "minimized"));
    }

    public void Show(NotesUpdated value)
    {
        Build();
        notes = value ?? new NotesUpdated();
        int n = notes.notes.Count, done = 0;
        while (rows.Count < n) rows.Add(BuildRow());
        for (int i = 0; i < rows.Count; i++)
        {
            bool on = i < n;
            rows[i].root.gameObject.SetActive(on);
            if (!on) continue;
            Bind(rows[i], notes.notes[i]);
            if (notes.notes[i].done) done++;
        }
        emptyState.SetActive(n == 0);
        panel.Subtitle = n == 0 ? "Empty" : $"{n} {(n == 1 ? "note" : "notes")} · {done} done";

        // Just removed (here, or by Bit): Undo for a few seconds.
        var removed = notes.lastRemoved;
        if (removed != null && removed.id != toastFor)
        {
            toastFor = removed.id;
            toastText.text = $"Removed “{removed.text}”";
            toastUntil = Time.time + 5f;
        }
        else if (removed == null) toastUntil = 0f;
        ShowToast(Time.time < toastUntil);
    }

    void Update()
    {
        if (toast.activeSelf && Time.time >= toastUntil) ShowToast(false);
        // "2m" ages move on by themselves.
        if (notes != null && Time.time - agesAt > 30f)
        {
            agesAt = Time.time;
            for (int i = 0; i < rows.Count && i < notes.notes.Count; i++) rows[i].age.text = Age(notes.notes[i].createdAt);
        }
    }

    void ShowToast(bool on)
    {
        toast.SetActive(on);
        hint.transform.parent.gameObject.SetActive(!on);
    }

    void Bind(Row row, DeskNote note)
    {
        row.note = note;
        row.text.text = note.done ? $"<s>{note.text}</s>" : note.text;
        row.text.color = note.done ? DoneText : TextColor;
        row.age.text = Age(note.createdAt);
        row.check.color = note.done ? Color.white : new Color(1f, 1f, 1f, 0.02f);
        row.check.Outline = note.done ? 0f : 0.8f;
        row.check.Glow = note.done ? 0.45f : 0f;
        row.tick.gameObject.SetActive(note.done);
    }

    static string Age(double createdAt)
    {
        double now = (DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds;
        double s = Math.Max(0, now - createdAt);
        if (s < 60) return "now";
        if (s < 3600) return $"{(int)(s / 60)}m";
        if (s < 86400) return $"{(int)(s / 3600)}h";
        return $"{(int)(s / 86400)}d";
    }

    // ---------------------------------------------------------------- building

    void Build()
    {
        if (built) return;
        built = true;
        panel = GetComponent<ForgePanel>();
        var content = panel.Content;
        var placeholder = content.Find("Placeholder");
        if (placeholder != null) placeholder.gameObject.SetActive(false);

        // The list scrolls above the footer.
        var viewport = DeskUi.Rect("Viewport", content);
        DeskUi.Stretch(viewport, 0f, FooterHeight + 10f, 0f, 0f);
        viewport.gameObject.AddComponent<RectMask2D>();
        var catcher = viewport.gameObject.AddComponent<Image>();  // the empty space between rows scrolls too
        catcher.color = Color.clear;
        list = DeskUi.Rect("List", viewport);
        list.anchorMin = new Vector2(0f, 1f);
        list.anchorMax = new Vector2(1f, 1f);
        list.pivot = new Vector2(0.5f, 1f);
        list.sizeDelta = Vector2.zero;
        var layout = list.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = RowGap;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;
        list.gameObject.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var scroll = viewport.gameObject.AddComponent<ScrollRect>();
        scroll.viewport = viewport;
        scroll.content = list;
        scroll.horizontal = false;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;

        emptyState = DeskUi.Rect("Empty", content).gameObject;
        DeskUi.Stretch((RectTransform)emptyState.transform, 10f, FooterHeight + 10f, 10f, 0f);
        var heading = DeskUi.Text("Heading", emptyState.transform, 15f, FontStyles.Bold, new Color(1f, 1f, 1f, 0.8f), TextAlignmentOptions.Bottom);
        DeskUi.Stretch(heading.rectTransform, 0f, 0f, 0f, 0f);
        heading.rectTransform.anchorMin = new Vector2(0f, 0.5f);
        heading.rectTransform.offsetMin = new Vector2(0f, 4f);
        heading.text = "No notes yet.";
        emptyCaption = DeskUi.Text("Caption", emptyState.transform, 12.5f, FontStyles.Normal, DeskUi.Muted, TextAlignmentOptions.Top);
        DeskUi.Stretch(emptyCaption.rectTransform, 0f, 0f, 0f, 0f);
        emptyCaption.rectTransform.anchorMax = new Vector2(1f, 0.5f);
        emptyCaption.rectTransform.offsetMax = new Vector2(0f, -4f);
        emptyCaption.textWrappingMode = TextWrappingModes.Normal;
        emptyState.SetActive(false);

        // Footer: who adds notes (they're spoken), or for a few seconds after a removal, Undo.
        var hintPill = DeskUi.Surface("Hint", content, new Color(1f, 1f, 1f, 0.08f), pill: true);
        Footer(hintPill.rectTransform);
        hint = DeskUi.Text("Text", hintPill.transform, 13f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.8f), TextAlignmentOptions.Center);
        DeskUi.Stretch(hint.rectTransform, 12f, 0f, 12f, 0f);
        hint.text = $"Ask {assistant} to add a note";

        var toastSurface = DeskUi.Surface("Toast", content, new Color(1f, 1f, 1f, 0.95f), pill: true);
        Footer(toastSurface.rectTransform);
        toastSurface.raycastTarget = true;
        toast = toastSurface.gameObject;
        toastText = DeskUi.Text("Text", toast.transform, 13f, FontStyles.Normal, ForgeTheme.PillOnText, TextAlignmentOptions.Left);
        DeskUi.Stretch(toastText.rectTransform, 16f, 0f, 90f, 0f);
        var undo = DeskUi.PillButton("Undo", toast.transform, "Undo", 32f, 13f, primary: false, DeskApi.NoteUndo, out var undoLabel);
        var undoRect = (RectTransform)undo.transform;
        undoRect.anchorMin = undoRect.anchorMax = new Vector2(1f, 0.5f);
        undoRect.pivot = new Vector2(1f, 0.5f);
        undoRect.anchoredPosition = new Vector2(-5f, 0f);
        var undoSurface = undo.GetComponent<ForgeSurface>();
        undoSurface.color = ForgeTheme.PillOnText;
        var undoHover = undo.GetComponent<DeskHover>();
        undoHover.rest = ForgeTheme.PillOnText;
        undoHover.lit = Color.black;
        undoLabel.color = Color.white;
        undoLabel.fontStyle = FontStyles.Bold;
        toast.SetActive(false);
    }

    static void Footer(RectTransform rt)
    {
        rt.anchorMin = new Vector2(0f, 0f);
        rt.anchorMax = new Vector2(1f, 0f);
        rt.pivot = new Vector2(0.5f, 0f);
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = new Vector2(0f, FooterHeight);
    }

    Row BuildRow()
    {
        var row = new Row();
        var bg = DeskUi.Surface("Note", list, RowRest, pill: false, corner: 12f);
        bg.raycastTarget = true;
        row.root = bg.rectTransform;
        var hover = bg.gameObject.AddComponent<DeskHover>();
        hover.target = bg;
        hover.rest = RowRest;
        hover.lit = RowLit;
        var layout = bg.gameObject.AddComponent<HorizontalLayoutGroup>();
        layout.padding = new RectOffset(12, 10, 10, 10);
        layout.spacing = 12f;
        layout.childAlignment = TextAnchor.MiddleLeft;
        layout.childControlWidth = layout.childControlHeight = true;
        layout.childForceExpandWidth = layout.childForceExpandHeight = false;
        bg.gameObject.AddComponent<LayoutElement>().minHeight = 46f;

        // The check ring: an outline, filled white with a dark tick once done.
        var check = DeskUi.Surface("Check", bg.transform, Color.clear, pill: true);
        check.raycastTarget = true;
        Fixed(check.gameObject, 22f, 22f);
        row.check = check;
        var checkButton = check.gameObject.AddComponent<Button>();
        checkButton.targetGraphic = check;
        checkButton.transition = Selectable.Transition.None;
        checkButton.navigation = new Navigation { mode = Navigation.Mode.None };
        checkButton.onClick.AddListener(() =>
        {
            if (row.note == null) return;
            row.note.done = !row.note.done;  // shows at once; the hub's list confirms it
            Bind(row, row.note);
            DeskApi.NoteDone(row.note.id, row.note.done);
        });
        row.tick = DeskUi.Rect("Tick", check.transform).gameObject.AddComponent<DeskIcon>();
        row.tick.Kind = DeskIcon.Shape.Check;
        row.tick.color = ForgeTheme.PillOnText;
        row.tick.raycastTarget = false;
        row.tick.rectTransform.sizeDelta = Vector2.one * 13f;

        row.text = DeskUi.Text("Text", bg.transform, 13.5f, FontStyles.Normal, TextColor, TextAlignmentOptions.Left);
        row.text.textWrappingMode = TextWrappingModes.Normal;
        row.text.overflowMode = TextOverflowModes.Overflow;
        row.text.lineSpacing = -4f;
        row.text.gameObject.AddComponent<LayoutElement>().flexibleWidth = 1f;

        row.age = DeskUi.Text("Age", bg.transform, 11f, FontStyles.Normal, new Color(1f, 1f, 1f, 0.55f), TextAlignmentOptions.Right);
        Fixed(row.age.gameObject, 30f, 16f);

        var remove = DeskUi.RoundButton("Remove", bg.transform, 26f, DeskIcon.Shape.Close, () =>
        {
            if (row.note != null) DeskApi.NoteRemove(row.note.id);
        }, out var x);
        x.rectTransform.sizeDelta = Vector2.one * 10f;
        Fixed(remove.gameObject, 26f, 26f);
        return row;
    }

    static void Fixed(GameObject go, float width, float height)
    {
        var e = go.AddComponent<LayoutElement>();
        e.minWidth = e.preferredWidth = width;
        e.minHeight = e.preferredHeight = height;
    }
}
