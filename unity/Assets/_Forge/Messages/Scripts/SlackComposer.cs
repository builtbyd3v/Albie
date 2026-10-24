using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// <summary>
/// The Slack panel's composer: a multi-line TMP_InputField on the module's card fill, Send, Ask Bit and (for a draft Bit
/// wrote) Cancel, plus a caption line ("Bit wrote this", "Sending…", errors). Built at runtime under the panel's Content
/// by <see cref="SlackPanel"/>, which lays it out and ticks it.
/// Sending: text you typed becomes a draft (POST /messages/drafts with the open conversation) that is sent at once
/// (POST .../send), since you wrote it yourself. A draft from Bit (notify.draft) lands in the field as editable text with
/// Send / Cancel; Send PATCHes the text first when you changed it. Nothing leaves without a Send.
/// Keyboard: on the Quest TMP_InputField opens the system keyboard when poked; on desktop the keyboard types into it.
/// The Interaction SDK's PointableCanvasModule never sends OnUpdateSelected (that is where TMP reads key events), so
/// <see cref="Tick"/> sends it while the field is focused; <see cref="ForgeInput.TextFieldFocused"/> mirrors the focus
/// so other key handlers can stand down.
/// </summary>
public class SlackComposer
{
    public const float Height = 96f;
    const float FieldH = 54f, PillRow = 30f, Gap = 8f, StatusSeconds = 2.5f;

    readonly SlackPanel owner;
    readonly RectTransform root, fieldRect;
    readonly ForgeSurface fieldFill;
    readonly TMP_InputField field;
    readonly TextMeshProUGUI text, placeholder, caption;
    readonly SlackButton send, askBit, cancel;
    BaseEventData updateEvent;

    SlackChannel channel;
    string draftId, draftText, draftChannel;   // the Bit draft in the field, if any
    string ownText, ownChannel, ownDraftId;    // the draft we created ourselves (its notify.draft must not land in the field)
    string closedDraftId;                      // the last Bit draft sent or cancelled here: late echoes of it are ignored
    bool sending, focused;
    float statusTimer;
    string statusText;

    public bool HasDraft => draftId != null;
    public RectTransform Root => root;

    public SlackComposer(Transform parent, SlackPanel owner)
    {
        this.owner = owner;
        root = SlackUi.Rect(parent, "Composer");

        fieldRect = SlackUi.Rect(root, "Field");
        fieldFill = SlackUi.Card(fieldRect, SlackUi.RowCorner, raycast: true);
        var area = SlackUi.Rect(fieldRect, "TextArea");
        SlackUi.Stretch(area, 12f, 8f, 12f, 8f);
        area.gameObject.AddComponent<RectMask2D>();
        text = SlackUi.Text(area, "Text", SlackUi.Body, SlackUi.TextColor, align: TextAlignmentOptions.TopLeft, wrap: true);
        text.richText = false;
        SlackUi.Stretch(text.rectTransform, 0f, 0f, 0f, 0f);
        placeholder = SlackUi.Text(area, "Placeholder", SlackUi.Body, SlackUi.Subtext, align: TextAlignmentOptions.TopLeft, wrap: true);
        placeholder.text = "Message";
        SlackUi.Stretch(placeholder.rectTransform, 0f, 0f, 0f, 0f);

        field = fieldRect.gameObject.AddComponent<TMP_InputField>();
        field.targetGraphic = fieldFill;
        field.transition = Selectable.Transition.None;
        field.navigation = new Navigation { mode = Navigation.Mode.None };
        field.textViewport = area;
        field.textComponent = text;
        field.placeholder = placeholder;
        field.lineType = TMP_InputField.LineType.MultiLineSubmit;  // Enter sends; the Quest keyboard's Done too
        if (ForgeTheme.Font != null) field.fontAsset = ForgeTheme.Font;
        field.pointSize = SlackUi.Body;
        field.richText = false;
        field.customCaretColor = true;
        field.caretColor = Color.white;
        field.caretWidth = 1;
        field.selectionColor = SlackUi.AccentA(0.35f);
        field.restoreOriginalTextOnEscape = false;
        field.resetOnDeActivation = false;
        field.characterLimit = 4000;
        field.onSubmit.AddListener(_ => Send());

        caption = SlackUi.Text(root, "Caption", SlackUi.Small, SlackUi.Subtext);
        caption.overflowMode = TextOverflowModes.Ellipsis;
        cancel = SlackButton.CreatePill(root, "Cancel", SlackButton.Pill, Cancel);
        askBit = SlackButton.CreatePill(root, "Ask Bit", SlackButton.Pill, AskBit);
        send = SlackButton.CreatePill(root, "Send", SlackButton.Primary, Send);
        SlackUi.Show(cancel, false);
        RefreshButtons();
    }

    /// <summary>Place the composer at (x, y) in its parent, <paramref name="w"/> wide, <see cref="Height"/> tall.</summary>
    public void Layout(float x, float y, float w)
    {
        SlackUi.Place(root, x, y, w, Height);
        SlackUi.Place(fieldRect, 0f, 0f, w, FieldH);
        float py = FieldH + Gap, right = 0f;
        Vector2 s = send.Rect.sizeDelta;
        SlackUi.PlaceRight(send.Rect, right, py, s.x, PillRow);
        right += s.x + 8f;
        s = askBit.Rect.sizeDelta;
        SlackUi.PlaceRight(askBit.Rect, right, py, s.x, PillRow);
        right += s.x + 8f;
        if (cancel.gameObject.activeSelf)
        {
            s = cancel.Rect.sizeDelta;
            SlackUi.PlaceRight(cancel.Rect, right, py, s.x, PillRow);
            right += s.x + 8f;
        }
        SlackUi.Place(caption.rectTransform, 4f, py + 7f, Mathf.Max(20f, w - right - 8f), 16f);
    }

    /// <summary>The open conversation (what Send posts to and Ask Bit reads from).</summary>
    public void SetChannel(SlackChannel c)
    {
        channel = c;
        placeholder.text = c == null ? "Pick a conversation" : "Message " + SlackText.DisplayName(c);
        RefreshButtons();
    }

    public void Tick(float dt)
    {
        bool f = field != null && field.isFocused;
        if (f != focused)
        {
            focused = f;
            ForgeInput.TextFieldFocused = f;
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
            string id = draftId;
            if (body != draftText)
                SlackNet.Patch<NotifyDraft>($"/messages/drafts/{id}", new JObject { ["text"] = body }, _ => { draftText = body; PostSend(id); }, Fail);
            else PostSend(id);
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
                PostSend(d.draftId);
            }, Fail);
        }
    }

    void PostSend(string id) =>
        ForgeNet.Post<SlackSendReply>($"/messages/drafts/{id}/send", null, _ => Sent(), Fail);  // notify.sent follows

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
        Status("Hub didn't answer, try again");
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

    /// <summary>"Ask Bit": messages.focus on the conversation's latest message, so Bit reads it aloud and offers to answer.</summary>
    void AskBit()
    {
        var m = owner.LatestMessage();
        if (m == null || string.IsNullOrEmpty(m.ts)) { Status("Nothing here for Bit to read yet"); return; }
        if (!owner.FocusBit(m)) Status("Hub offline");
        else Status("Bit is reading it…");
    }

    // ---- Hub events (routed by SlackPanel) ----

    /// <summary>notify.draft: Bit's draft lands in the field (the panel switches to its conversation); our own drafts are skipped.</summary>
    public void ReceiveDraft(NotifyDraft d)
    {
        if (d == null || string.IsNullOrEmpty(d.draftId)) return;
        if (d.draftId == draftId)
        {
            if (d.edited) draftText = d.text;  // our PATCH echoed back
            return;
        }
        if (d.draftId == closedDraftId) return;  // a late echo of a draft already sent or cancelled here
        if (d.draftId == ownDraftId || (ownText != null && d.text == ownText && d.channel == ownChannel)) return;  // typed here, on its way out
        draftId = d.draftId;
        draftText = d.text ?? "";
        draftChannel = d.channel;
        sending = false;
        field.SetTextWithoutNotify(draftText);
        SlackUi.Show(cancel, true);
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

    // ---- View ----

    void ClearDraft()
    {
        if (draftId != null) closedDraftId = draftId;
        draftId = draftText = draftChannel = null;
        SlackUi.Show(cancel, false);
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
        if (statusText != null) caption.text = statusText;
        else if (draftId != null) caption.text = "Bit wrote this · edit it, then Send";
        else caption.text = "";
    }

    void RefreshButtons()
    {
        bool live = ForgeNet.Status == ForgeNetStatus.Live;
        send.Interactable = live && !sending && (channel != null || draftId != null);
        askBit.Interactable = live && channel != null;
        cancel.Interactable = live && !sending;
        field.interactable = !sending;
    }

    /// <summary>The hub came or went: buttons follow.</summary>
    public void OnStatus() => RefreshButtons();
}
