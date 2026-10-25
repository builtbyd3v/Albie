using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The Slack panel's composer, laid out like Slack's message box: a rounded field with a grey edge ("Message #general"),
/// a toolbar inside it (Ask Albie on the left, the green Send arrow on the right, grey while there is nothing to send),
/// a status line under it ("Sending…", "Sent", errors) and, for a draft Albie wrote, a banner above it ("Albie drafted
/// this", Cancel). Built at runtime under the panel's Content by <see cref="SlackPanel"/>, which lays it out and ticks it.
/// Sending: text you typed becomes a draft (POST /messages/drafts with the open conversation) that is sent at once
/// (POST .../send), since you wrote it yourself. A draft from Albie (notify.draft) lands in the field as editable text
/// with Send / Cancel; Send PATCHes the text first when you changed it, and its files (a CAD design's STEP, STL, drawing,
/// parts list) show on the banner and go up with it. Albie rewording the open draft (messages_edit_draft) replaces the
/// text in the field. Nothing leaves without a Send. What was sent goes into the conversation at once
/// (<see cref="SlackStore.AddOwn"/>), without waiting for Slack. On the iMessage panel the box is Messages' rounded grey
/// field with a blue Send.
/// Keyboard: on the Quest TMP_InputField opens the system keyboard when poked; on desktop the keyboard types into it.
/// The Interaction SDK's PointableCanvasModule never sends OnUpdateSelected (that is where TMP reads key events), so
/// <see cref="Tick"/> sends it while the field is focused; <see cref="ForgeInput.TextFieldFocused"/> mirrors the focus
/// so other key handlers can stand down.
/// </summary>
public class SlackComposer
{
    const float BoxH = 84f, FieldTop = 9f, FieldH = 40f, ToolH = 26f, BannerH = 30f, FilesH = 18f, CaptionH = 18f, Gap = 6f;
    const float Corner = 9f, Edge = 1.2f, SendW = 34f, StatusSeconds = 2.5f, TextSize = 13f;

    /// <summary>Height (mm) the composer takes: the box and status line, plus the banner while Albie's draft is in it.</summary>
    public float Height => (HasDraft ? BannerHeight + Gap : 0f) + BoxH + CaptionH;

    // The banner grows by a line while the draft carries files.
    float BannerHeight => BannerH + (filesLine.Length > 0 ? FilesH : 0f);

    readonly SlackPanel owner;
    readonly RectTransform root, box, fieldRect, banner, bannerText;
    readonly ForgeSurface edge, fill, bannerFill;
    readonly TMP_InputField field;
    readonly TextMeshProUGUI text, placeholder, caption, bannerLabel, files;
    readonly Image sendIcon;
    readonly bool imessage;                    // on the iMessage panel: Messages' look
    string filesLine = "";                     // the Albie draft's attachments in one line (on the banner), "" for none
    readonly SlackButton send, askBit, cancel;
    BaseEventData updateEvent;

    SlackChannel channel;
    string draftId, draftText, draftChannel, draftThread, draftName;  // the Albie draft in the field, if any
    string ownText, ownChannel, ownDraftId;    // the draft we created ourselves (its notify.draft must not land in the field)
    string closedDraftId;                      // the last Albie draft sent or cancelled here: late echoes of it are ignored
    bool sending, focused, canSend;
    float statusTimer;
    string statusText;

    public bool HasDraft => draftId != null;
    public RectTransform Root => root;

    public SlackComposer(Transform parent, SlackPanel owner)
    {
        this.owner = owner;
        imessage = owner.IsIMessagePanel;
        root = SlackUi.Rect(parent, "Composer");

        // Albie's draft banner (shown only with a draft).
        banner = SlackUi.Rect(root, "Banner");
        bannerFill = SlackUi.Surface(banner, "Fill", SlackUi.AccentA(0.16f), 8f);
        SlackUi.Stretch(bannerFill.rectTransform, 0f, 0f, 0f, 0f);
        var sparkle = SlackUi.Icon(banner, "Sparkle", "sparkle", SlackUi.Accent);
        SlackUi.Place(sparkle.rectTransform, 10f, (BannerH - 15f) * 0.5f, 15f, 15f);
        bannerLabel = SlackUi.Text(banner, "Label", 12f, SlackUi.SkText);
        bannerLabel.overflowMode = TextOverflowModes.Ellipsis;
        bannerText = bannerLabel.rectTransform;
        files = SlackUi.Text(banner, "Files", 11f, SlackUi.Accent);
        files.overflowMode = TextOverflowModes.Ellipsis;
        SlackUi.Show(files, false);
        cancel = SlackButton.CreatePill(banner, "Cancel", SlackButton.Ghost, Cancel, 22f, 11.5f);
        SlackUi.Show(banner, false);

        // The box: a grey edge around the field colour, the text area and the toolbar inside.
        box = SlackUi.Rect(root, "Box");
        float corner = imessage ? 22f : Corner;
        edge = SlackUi.Surface(box, "Edge", imessage ? SlackUi.White(0.16f) : SlackUi.SkBorder, corner);
        SlackUi.Stretch(edge.rectTransform, 0f, 0f, 0f, 0f);
        fill = SlackUi.Surface(box, "Fill", imessage ? SlackUi.IMessageComposer : SlackUi.SkField, corner - Edge);
        SlackUi.Stretch(fill.rectTransform, Edge, Edge, Edge, Edge);

        fieldRect = SlackUi.Rect(box, "Field");
        var hit = fieldRect.gameObject.AddComponent<SlackHitArea>();  // a press anywhere on the field focuses it
        hit.raycastTarget = true;
        var area = SlackUi.Rect(fieldRect, "TextArea");
        SlackUi.Stretch(area, 0f, 0f, 0f, 0f);
        area.gameObject.AddComponent<RectMask2D>();
        text = SlackUi.Text(area, "Text", TextSize, SlackUi.SkName, align: TextAlignmentOptions.TopLeft, wrap: true);
        text.richText = false;
        SlackUi.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        placeholder = SlackUi.Text(area, "Placeholder", TextSize, SlackUi.SkMuted, align: TextAlignmentOptions.TopLeft, wrap: true);
        placeholder.text = imessage ? "iMessage" : "Message";
        placeholder.overflowMode = TextOverflowModes.Ellipsis;
        SlackUi.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);

        field = fieldRect.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = hit;
        field.transition = Selectable.Transition.None;
        field.navigation = new Navigation { mode = Navigation.Mode.None };
        field.textViewport = area;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.lineType = TMP_InputField.LineType.MultiLineSubmit;  // Enter sends; the Quest keyboard's Done too
        if (ForgeTheme.Font != null) field.fontAsset = ForgeTheme.Font;
        field.pointSize = TextSize;
        field.richText = false;
        field.customCaretColor = true;
        field.caretColor = Color.white;
        field.caretWidth = 1;
        field.selectionColor = new Color(SlackUi.SkLink.r, SlackUi.SkLink.g, SlackUi.SkLink.b, 0.35f);
        field.restoreOriginalTextOnEscape = false;
        field.resetOnDeActivation = false;
        field.characterLimit = 4000;
        field.onSubmit.AddListener(_ => Send());
        field.onValueChanged.AddListener(_ => RefreshButtons());

        // Toolbar inside the box.
        askBit = SlackButton.Create(box, "AskAlbie", SlackButton.Ghost, 6f, AskBit);
        var askIcon = SlackUi.Icon(askBit.transform, "Sparkle", "sparkle", SlackUi.Accent);
        SlackUi.Place(askIcon.rectTransform, 7f, (ToolH - 13f) * 0.5f, 13f, 13f);
        var askLabel = SlackUi.Text(askBit.transform, "Label", 11.5f, SlackUi.SkMuted, bold: true);
        askLabel.text = "Ask Albie";
        float aw = SlackUi.Width(askLabel, askLabel.text);
        SlackUi.Place(askLabel.rectTransform, 25f, 0f, aw + 2f, ToolH);
        askBit.Rect.sizeDelta = new Vector2(25f + aw + 9f, ToolH);

        send = SlackButton.Create(box, "Send", SlackButton.SendOff, 6f, Send);
        sendIcon = SlackUi.Icon(send.transform, "Arrow", "send", SlackUi.SkMuted);
        SlackUi.Place(sendIcon.rectTransform, (SendW - 15f) * 0.5f, (ToolH - 15f) * 0.5f, 15f, 15f);

        caption = SlackUi.Text(root, "Caption", 10.5f, SlackUi.SkMuted, align: TextAlignmentOptions.Right);
        caption.overflowMode = TextOverflowModes.Ellipsis;
        RefreshButtons();
    }

    /// <summary>Place the composer at (x, y) in its parent, <paramref name="w"/> wide, <see cref="Height"/> tall.</summary>
    public void Layout(float x, float y, float w)
    {
        SlackUi.Place(root, x, y, w, Height);
        float by = 0f;
        if (HasDraft)
        {
            SlackUi.Place(banner, 0f, 0f, w, BannerHeight);
            Vector2 c = cancel.Rect.sizeDelta;
            SlackUi.PlaceRight(cancel.Rect, 6f, (BannerH - c.y) * 0.5f, c.x, c.y);
            SlackUi.Place(bannerText, 32f, 0f, Mathf.Max(20f, w - 32f - c.x - 16f), BannerH);
            SlackUi.Place(files.rectTransform, 32f, BannerH - 6f, Mathf.Max(20f, w - 44f), FilesH);
            by = BannerHeight + Gap;
        }
        SlackUi.Place(box, 0f, by, w, BoxH);
        SlackUi.Place(fieldRect, 14f, FieldTop, w - 28f, FieldH);
        float ty = BoxH - ToolH - 6f;
        Vector2 a = askBit.Rect.sizeDelta;
        SlackUi.Place(askBit.Rect, 6f, ty, a.x, ToolH);
        SlackUi.PlaceRight(send.Rect, 8f, ty, SendW, ToolH);
        SlackUi.Place(caption.rectTransform, 4f, by + BoxH + 2f, w - 8f, CaptionH - 3f);
    }

    /// <summary>The open conversation (what Send posts to and Ask Albie reads from).</summary>
    public void SetChannel(SlackChannel c)
    {
        channel = c;
        placeholder.text = imessage ? "iMessage" : c == null ? "Pick a conversation" : "Message " + SlackText.DisplayName(c);
        RefreshButtons();
    }

    public void Tick(float dt)
    {
        bool f = field != null && field.isFocused;
        if (f != focused)
        {
            focused = f;
            ForgeInput.TextFieldFocused = f;
            if (!imessage) edge.color = f ? SlackUi.SkBorderFocus : SlackUi.SkBorder;
        }
        if (f)
        {
            // PointableCanvasModule (Main's only input module) never calls the selected object's OnUpdateSelected, where
            // TMP_InputField pops its key events; drive it like StandaloneInputModule would. Harmless when another module
            // already does (the queue is then empty).
            var events = EventSystem.current;
            if (events != null)
            {
                if (updateEvent == null) updateEvent = new BaseEventData(events);
                updateEvent.Reset();
                ExecuteEvents.Execute(field.gameObject, updateEvent, ExecuteEvents.updateSelectedHandler);
            }
        }
        if (statusTimer > 0f)
        {
            statusTimer -= dt;
            if (statusTimer <= 0f) { statusText = null; RefreshCaption(); }
        }
    }

    public void Disabled()
    {
        if (focused) { focused = false; ForgeInput.TextFieldFocused = false; }
        if (field != null && field.isFocused) field.DeactivateInputField();
    }

    // ---- Sending ----

    void Send()
    {
        if (sending) return;
        string body = field.text != null ? field.text.Trim() : "";
        if (body.Length == 0) { Status("Nothing to send yet"); return; }
        if (ForgeNet.Status != ForgeNetStatus.Live) { Status("Hub offline"); return; }
        string target = draftId != null ? draftChannel : channel?.id;
        if (string.IsNullOrEmpty(target)) { Status("Pick a conversation first"); return; }

        sending = true;
        RefreshButtons();
        Status("Sending…", sticky: true);
        if (draftId != null)
        {
            string id = draftId, thread = draftThread;
            if (body != draftText)
                SlackNet.Patch<NotifyDraft>($"/messages/drafts/{id}", new JObject { ["text"] = body }, _ => { draftText = body; PostSend(id, target, body, thread); }, Fail);
            else PostSend(id, target, body, thread);
        }
        else
        {
            ownText = body;
            ownChannel = target;
            ownDraftId = null;
            ForgeNet.Post<NotifyDraft>("/messages/drafts", new JObject { ["channel"] = target, ["text"] = body }, d =>
            {
                if (d == null || string.IsNullOrEmpty(d.draftId)) { Fail("no draft id"); return; }
                ownDraftId = d.draftId;
                PostSend(d.draftId, target, body, null);
            }, Fail);
        }
    }

    // The hub answers with the post's ts: the message goes into the conversation right away (notify.sent follows).
    void PostSend(string id, string target, string body, string thread) =>
        ForgeNet.Post<SlackSendReply>($"/messages/drafts/{id}/send", null, reply =>
        {
            if (reply != null) SlackStore.AddOwn(target, body, reply.ts, thread);
            Sent();
        }, Fail);

    void Sent()
    {
        if (!sending && draftId == null) return;
        sending = false;
        field.SetTextWithoutNotify("");
        ClearDraft();
        // ownText / ownChannel / ownDraftId stay until the next Send: the hub's notify.draft for a typed message can
        // land after its send completed, and must still be recognised as ours.
        Status("Sent");
        RefreshButtons();
    }

    void Fail(string error)
    {
        Debug.LogWarning($"[SlackComposer] {error}");
        sending = false;
        Status(error != null && error.Contains("404") ? "That draft is gone (sent, cancelled or expired)" : "Hub didn't answer, try again");
        RefreshButtons();
    }

    void Cancel()
    {
        if (draftId == null) return;
        string id = draftId;
        ForgeNet.Post($"/messages/drafts/{id}/cancel", null, null, Fail);  // notify.draft_cancelled follows
        field.SetTextWithoutNotify("");
        ClearDraft();
        Status("Cancelled");
        RefreshButtons();
    }

    /// <summary>"Ask Albie": messages.focus on the conversation's latest message, so Albie reads it aloud and offers to answer.</summary>
    void AskBit()
    {
        var m = owner.LatestMessage();
        if (m == null || string.IsNullOrEmpty(m.ts)) { Status("Nothing here for Albie to read yet"); return; }
        if (!owner.FocusBit(m)) Status("Hub offline");
        else Status("Albie is reading it…");
    }

    // ---- Hub events (routed by SlackPanel) ----

    /// <summary>notify.draft: Albie's draft lands in the field (the panel switches to its conversation); our own drafts are skipped.</summary>
    public void ReceiveDraft(NotifyDraft d)
    {
        if (d == null || string.IsNullOrEmpty(d.draftId)) return;
        if (d.draftId == draftId)
        {
            if (!d.edited) return;
            // Our own PATCH echoed back (the field already says it), or Albie reworded it ("rephrase this",
            // messages_edit_draft): then the field takes his words. Mid-send the field is locked and the echo is ours.
            if (!sending && d.text != null && d.text != (field.text ?? ""))
            {
                field.SetTextWithoutNotify(d.text);
                Status("Albie reworded it");
            }
            draftText = d.text;
            SetFiles(d);
            return;
        }
        if (d.draftId == closedDraftId) return;  // a late echo of a draft already sent or cancelled here
        if (d.draftId == ownDraftId || (ownText != null && d.text == ownText && d.channel == ownChannel)) return;  // typed here, on its way out
        draftId = d.draftId;
        draftText = d.text ?? "";
        draftChannel = d.channel;
        draftThread = d.threadTs;
        draftName = d.channelName;
        sending = false;
        field.SetTextWithoutNotify(draftText);
        SetFiles(d);
        SlackUi.Show(banner, true);
        statusText = null;
        RefreshCaption();
        RefreshButtons();
        if (!string.IsNullOrEmpty(d.channel)) owner.Open(d.channel);
        owner.MarkLayoutDirty();
    }

    public void OnSent(NotifySent s)
    {
        if (s == null) return;
        bool ours = (draftId != null && (string.IsNullOrEmpty(s.draftId) || s.draftId == draftId))
                    || (ownDraftId != null && s.draftId == ownDraftId)
                    || (sending && draftId == null && ownText != null && s.text == ownText);
        if (ours) Sent();
    }

    public void OnDraftCancelled(NotifyDraftCancelled c)
    {
        if (c == null || draftId == null || c.draftId != draftId) return;
        field.SetTextWithoutNotify("");
        ClearDraft();
        sending = false;
        Status(c.reason == "superseded" ? "Replaced by a newer draft" : c.reason == "expired" ? "Draft expired" : "Cancelled");
        RefreshButtons();
    }

    /// <summary>A short line under the box (the panel's message toolbar reports through it).</summary>
    public void ShowStatus(string s) => Status(s);

    // ---- View ----

    /// <summary>The files line for Albie's draft (its attachments go up with the text on Send); hidden without any.</summary>
    void SetFiles(NotifyDraft d)
    {
        string line = d != null ? SlackText.Attachments(d.attachments) : "";
        if (line == filesLine) return;
        filesLine = line;
        files.text = line;
        SlackUi.Show(files, line.Length > 0);
        owner.MarkLayoutDirty();  // the banner grows or shrinks by the line
    }

    void ClearDraft()
    {
        if (draftId != null) closedDraftId = draftId;
        draftId = draftText = draftChannel = draftThread = draftName = null;
        SetFiles(null);
        SlackUi.Show(banner, false);
        RefreshCaption();
        owner.MarkLayoutDirty();
    }

    void Status(string s, bool sticky = false)
    {
        statusText = s;
        statusTimer = sticky ? 0f : StatusSeconds;
        RefreshCaption();
    }

    void RefreshCaption()
    {
        caption.text = statusText ?? "";
        if (draftId != null)
        {
            var c = SlackStore.Find(draftChannel);
            string to = c != null ? SlackText.DisplayName(c) : string.IsNullOrEmpty(draftName) ? "Slack" : draftName;
            bannerLabel.text = SlackText.BoldOpen + "Albie drafted this" + SlackText.BoldClose + " for " + to + ". Edit it, then Send.";
        }
    }

    void RefreshButtons()
    {
        bool live = ForgeNet.Status == ForgeNetStatus.Live;
        bool can = live && !sending && (channel != null || draftId != null) && !string.IsNullOrWhiteSpace(field.text);
        if (can != canSend)
        {
            canSend = can;
            send.SetLook(can ? (imessage ? SlackButton.IMessagePrimary : SlackButton.SendOn) : SlackButton.SendOff);
            sendIcon.color = can ? Color.white : SlackUi.SkMuted;
        }
        askBit.Interactable = live && channel != null;
        cancel.Interactable = live && !sending;
        field.interactable = !sending;
    }

    /// <summary>The hub came or went: buttons follow.</summary>
    public void OnStatus() => RefreshButtons();
}
