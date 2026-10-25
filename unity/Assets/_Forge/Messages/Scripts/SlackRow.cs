using System;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// One message in the Slack panel, laid out like Slack (1 canvas unit = 1 mm): a rounded-square avatar with initials,
/// the name in bold with the time after it ("4:15 PM"), the text below, "N replies" in link blue under a thread. A
/// follow-up from the same person within five minutes is a <see cref="Continuation"/>: just the text, its time showing
/// in the gutter while pointed at. Pointed at, the row lights up and a small "Ask Albie" toolbar floats at its top right
/// (<see cref="AskAlbie"/>: Albie reads the message aloud and offers to answer). Built in code by
/// <see cref="Create"/> and pooled by <see cref="SlackPanel"/>, which measures (<see cref="Measure"/>) and places it
/// (<see cref="Layout"/>) only when data or the panel's size change. Hover arrives as uGUI pointer events from the
/// mouse (DesktopRig) and Interaction SDK rays / pokes alike; a drag passes through to the scroll list.
/// </summary>
public class SlackRow : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    public const float PadL = 16f, AvatarSize = 32f, TextX = 58f, PadR = 18f;
    const float NameY = 6f, NameH = 19f, BodyY = 26f, ContY = 3f, BottomPad = 6f, RepliesH = 20f, MinLine = 17f;
    const float NameSize = 13.5f, BodySize = 13f, TimeSize = 10.5f, AskH = 24f;

    public NotifyMessage Message { get; private set; }
    /// <summary>A follow-up: no avatar or name, the text right under the previous message.</summary>
    public bool Continuation { get; private set; }
    public RectTransform Rect => rect;
    /// <summary>The row's "Ask Albie" was pressed.</summary>
    public event Action<SlackRow> AskAlbie;

    RectTransform rect;
    ForgeSurface hover, avatar;
    TextMeshProUGUI initials, nameText, timeText, gutterTime, body, replies;
    SlackButton ask;
    string shown;           // the body as shown (rendered text plus "(edited)")
    float width = -1f, measured;
    bool hovered;

    public static SlackRow Create(Transform parent)
    {
        var rt = SlackUi.Rect(parent, "Message");
        var hit = rt.gameObject.AddComponent<SlackHitArea>();  // takes the pointer: hover, and a drag scrolls the list
        hit.raycastTarget = true;
        var row = rt.gameObject.AddComponent<SlackRow>();
        row.Build();
        return row;
    }

    void Build()
    {
        rect = (RectTransform)transform;
        hover = SlackUi.Surface(transform, "Hover", SlackUi.SkPaneHover, 4f);
        SlackUi.Stretch(hover.rectTransform, 0f, 0f, 0f, 0f);
        SlackUi.Show(hover, false);

        avatar = SlackUi.Surface(transform, "Avatar", SlackUi.BubbleColors[0], 7f);
        initials = SlackUi.Text(avatar.transform, "Initials", 12f, Color.white, bold: true, align: TextAlignmentOptions.Center);
        SlackUi.Stretch(initials.rectTransform, 0f, 0f, 0f, 0f);

        nameText = SlackUi.Text(transform, "Name", NameSize, SlackUi.SkName, bold: true, align: TextAlignmentOptions.BottomLeft);
        nameText.overflowMode = TextOverflowModes.Ellipsis;
        timeText = SlackUi.Text(transform, "Time", TimeSize, SlackUi.SkMuted, align: TextAlignmentOptions.BottomLeft);
        timeText.overflowMode = TextOverflowModes.Ellipsis;
        gutterTime = SlackUi.Text(transform, "GutterTime", 9.5f, SlackUi.SkMuted, align: TextAlignmentOptions.Right);
        SlackUi.Show(gutterTime, false);
        body = SlackUi.Text(transform, "Body", BodySize, SlackUi.SkText, wrap: true, align: TextAlignmentOptions.TopLeft);
        body.lineSpacing = 4f;
        replies = SlackUi.Text(transform, "Replies", 11.5f, SlackUi.SkLink, bold: true);

        ask = SlackButton.Create(transform, "AskAlbie", SlackButton.Toolbar, 6f, () => AskAlbie?.Invoke(this));
        var sparkle = SlackUi.Icon(ask.transform, "Sparkle", "sparkle", ForgeTheme.Messages);
        SlackUi.Place(sparkle.rectTransform, 8f, 5f, 14f, 14f);
        var label = SlackUi.Text(ask.transform, "Label", 11.5f, SlackUi.SkText, bold: true);
        label.text = "Ask Albie";
        float lw = SlackUi.Width(label, label.text);
        SlackUi.Place(label.rectTransform, 27f, 0f, lw + 2f, AskH);
        ask.Rect.sizeDelta = new Vector2(27f + lw + 10f, AskH);
        SlackUi.Show(ask, false);
    }

    /// <summary>
    /// Show <paramref name="m"/>; <paramref name="rendered"/> is its text as TMP rich text (<see cref="SlackText.RenderSlack"/>).
    /// Binding the same message the same way again is a no-op, so pooled rows refill cheaply.
    /// </summary>
    public void Bind(NotifyMessage m, string rendered, bool continuation)
    {
        string text = m.edited ? rendered + " <size=78%><color=#ABABAD>(edited)</color></size>" : rendered;
        if (Message == m && Continuation == continuation && shown == text) return;
        Message = m;
        Continuation = continuation;
        shown = text;
        width = -1f;  // re-measure

        string name = SlackText.SenderName(m);
        SlackUi.Show(avatar, !continuation);
        SlackUi.Show(nameText, !continuation);
        SlackUi.Show(timeText, !continuation);
        if (!continuation)
        {
            initials.text = SlackText.Initials(m);
            avatar.color = SlackUi.BubbleColor(name);  // by name, like the sidebar's DM avatars (they only know the name)
            nameText.text = name;
            timeText.text = SlackText.IsThreadReply(m) ? SlackText.ClockTime(m.ts) + "  ·  replied in a thread" : SlackText.ClockTime(m.ts);
        }
        gutterTime.text = SlackText.ShortClockTime(m.ts);
        body.text = text;
        bool thread = m.replyCount.HasValue && m.replyCount.Value > 0;
        SlackUi.Show(replies, thread);
        if (thread) replies.text = m.replyCount.Value == 1 ? "1 reply" : m.replyCount.Value + " replies";
        SetHover(false);
    }

    /// <summary>Height (mm) at <paramref name="w"/> mm wide; measured once per bind and width.</summary>
    public float Measure(float w)
    {
        if (Mathf.Approximately(w, width)) return measured;
        width = w;
        float textW = Mathf.Max(20f, w - TextX - PadR);
        float h = (Continuation ? ContY : BodyY) + Mathf.Max(MinLine, SlackUi.Height(body, shown, textW)) + BottomPad;
        if (replies.gameObject.activeSelf) h += RepliesH;
        measured = Continuation ? h : Mathf.Max(NameY + AvatarSize + BottomPad, h);
        return measured;
    }

    /// <summary>Place the children for a row <paramref name="w"/> x <paramref name="h"/> (the owner set the row's rect).</summary>
    public void Layout(float w, float h)
    {
        float textW = Mathf.Max(20f, w - TextX - PadR);
        float bodyY = Continuation ? ContY : BodyY;
        if (!Continuation)
        {
            SlackUi.Place(avatar.rectTransform, PadL, NameY + 1f, AvatarSize, AvatarSize);
            float nameW = Mathf.Min(SlackUi.Width(nameText, nameText.text) + 2f, textW * 0.6f);
            SlackUi.Place(nameText.rectTransform, TextX, NameY, nameW, NameH);
            SlackUi.Place(timeText.rectTransform, TextX + nameW + 8f, NameY - 1f, Mathf.Max(20f, textW - nameW - 8f), NameH);
        }
        float bodyH = Mathf.Max(MinLine, h - bodyY - BottomPad - (replies.gameObject.activeSelf ? RepliesH : 0f));
        SlackUi.Place(body.rectTransform, TextX, bodyY, textW, bodyH);
        if (replies.gameObject.activeSelf) SlackUi.Place(replies.rectTransform, TextX, bodyY + bodyH + 2f, textW, RepliesH - 4f);
        SlackUi.Place(gutterTime.rectTransform, 2f, ContY, TextX - 10f, MinLine);
        Vector2 s = ask.Rect.sizeDelta;
        SlackUi.PlaceRight(ask.Rect, 14f, -AskH * 0.4f, s.x, s.y);  // floats over the row's top edge, like Slack's toolbar
    }

    public void OnPointerEnter(PointerEventData eventData) => SetHover(true);
    public void OnPointerExit(PointerEventData eventData) => SetHover(false);

    void OnDisable() => SetHover(false);

    void SetHover(bool on)
    {
        if (hover == null) return;
        hovered = on && Message != null;
        SlackUi.Show(hover, hovered);
        SlackUi.Show(ask, hovered);
        SlackUi.Show(gutterTime, hovered && Continuation);
    }
}
