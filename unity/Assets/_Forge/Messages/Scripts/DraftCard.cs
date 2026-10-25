using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// "Send this?" card (DraftCard.prefab, a ForgePanel variant, panel id <c>messages-draft</c>). Shows a reply Bit wrote
/// but has NOT sent (notify.draft, PROJECT_CONTEXT 7 / 8.8): who it goes to, what it answers, the exact text, and
/// Send / Cancel. Send -> POST /messages/drafts/{id}/send (the hub posts it under the user's own name);
/// Cancel -> POST /messages/drafts/{id}/cancel. The card closes itself on notify.sent / notify.draft_cancelled for
/// its draft, so saying "send it" to Bit (the messages_confirm tool) or a newer draft (reason superseded) dismisses it
/// too. Spawned and placed by <see cref="DraftPresenter"/> only while the Slack panel is closed (open, the draft goes to
/// its composer instead). Buttons are uGUI Buttons (SlackButton pills), so mouse (DesktopRig) and Interaction SDK
/// ray/poke (wired on the ForgePanel prefab) both work. Look: the module's translucent slate card inside the graphite frame.
/// </summary>
public class DraftCard : MonoBehaviour
{
    /// <summary>Panel size (m); the builder and the presenter's docking math both use it.</summary>
    public static readonly Vector2 SizeMeters = new Vector2(0.42f, 0.26f);
    const float LingerSeconds = 0.8f;
    static readonly char[] LineBreaks = { '\n', '\r' };

    [SerializeField] ForgeSurface sheet;   // translucent slate card inside the panel (text stays opaque)
    [SerializeField] TMP_Text header;      // "#forge-team" / "DM to Hudson"
    [SerializeField] TMP_Text replying;    // "Replying to Hudson: is the build ready?"
    [SerializeField] TMP_Text body;        // the exact text that will be sent
    [SerializeField] TMP_Text status;      // "Sending…", "Sent", "Cancelled", errors
    [SerializeField] Button send;
    [SerializeField] Button cancel;
    TMP_Text files;                        // the draft's attachments (see ShowFiles); null until a draft has some

    public NotifyDraft Draft { get; private set; }
    /// <summary>Sent or cancelled; the card is on its way out.</summary>
    public bool Closing { get; private set; }
    /// <summary>What to destroy when the card closes (the presenter's holder); defaults to this object.</summary>
    public GameObject Root { get; set; }

    void Awake()
    {
        send.onClick.AddListener(Send);
        cancel.onClick.AddListener(Cancel);
    }

    void OnEnable()
    {
        ForgeEvents.OnNotifySent += OnSent;
        ForgeEvents.OnNotifyDraftCancelled += OnCancelled;
    }

    void OnDisable()
    {
        ForgeEvents.OnNotifySent -= OnSent;
        ForgeEvents.OnNotifyDraftCancelled -= OnCancelled;
    }

    public void Bind(NotifyDraft d)
    {
        Draft = d;
        Closing = false;
        NotifyMessage to = d.replyTo;
        // The hub names a DM draft after the person (channels stay bare), so the kind comes from the conversation list
        // (by channel id) or the message being answered, never from the name.
        var conversation = SlackStore.Find(d.channel);
        bool dm = conversation != null ? SlackText.IsDmKind(conversation.kind)
            : (to != null && to.isDm) || d.channelName == "dm" || d.channelName == "group-dm";
        if (dm)
            header.text = !string.IsNullOrEmpty(d.channelName) && d.channelName != "dm" && d.channelName != "group-dm" ? $"DM to {d.channelName}"
                : to != null && !string.IsNullOrEmpty(to.userName) ? $"DM to {to.userName}" : "Direct message";
        else
            header.text = string.IsNullOrEmpty(d.channelName) ? "Slack" : $"#{d.channelName.TrimStart('#')}";

        bool hasReply = to != null && !string.IsNullOrEmpty(to.text);
        replying.gameObject.SetActive(hasReply);
        if (hasReply) replying.text = $"Replying to {SlackText.SenderName(to)}: {FirstLine(to.text)}";

        body.text = d.text ?? "";
        ShowFiles(SlackText.Attachments(d.attachments));
        status.text = d.edited ? "Edited by Albie" : "Albie wrote this";
        SetButtons(true);
    }

    // The draft's files (Addendum H), one line above the buttons: "4 files · STEP, STL, Drawing PDF, Parts list · 1.2 MB".
    // Made here rather than in the prefab, which predates attachments; the body gives up the line's height while it shows.
    const float FilesBottom = 40f, FilesH = 18f, BodyInset = 50f;

    void ShowFiles(string line)
    {
        if (files == null && line.Length > 0)
        {
            files = SlackUi.Text(body.transform.parent, "Files", SlackUi.Small, SlackUi.AccentA(0.95f));
            files.overflowMode = TextOverflowModes.Ellipsis;
            var rt = files.rectTransform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = new Vector2(1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(14f, FilesBottom);
            rt.offsetMax = new Vector2(-14f, FilesBottom + FilesH);
        }
        if (files == null) return;
        files.text = line;
        files.gameObject.SetActive(line.Length > 0);
        body.rectTransform.offsetMin = new Vector2(body.rectTransform.offsetMin.x,
            line.Length > 0 ? FilesBottom + FilesH + 4f : BodyInset);
    }

    public void Send()
    {
        if (Draft == null || Closing) return;
        SetButtons(false);
        status.text = "Sending…";
        ForgeNet.Post($"/messages/drafts/{Draft.draftId}/send", null, null, Fail);  // confirmation arrives as notify.sent
    }

    public void Cancel()
    {
        if (Draft == null || Closing) return;
        SetButtons(false);
        status.text = "Cancelling…";
        ForgeNet.Post($"/messages/drafts/{Draft.draftId}/cancel", null, null, Fail);  // arrives as notify.draft_cancelled
    }

    void Fail(string error)
    {
        if (Closing) return;
        Debug.LogWarning($"[DraftCard] {error}");
        status.text = "Hub didn't answer, try again";
        SetButtons(true);
    }

    void OnSent(NotifySent s)
    {
        if (Draft == null || Closing) return;
        if (!string.IsNullOrEmpty(s.draftId) && s.draftId != Draft.draftId) return;  // no draft_id = the open draft
        Close("Sent");
    }

    void OnCancelled(NotifyDraftCancelled c)
    {
        if (Draft == null || Closing || c.draftId != Draft.draftId) return;
        Close(c.reason == "superseded" ? "Replaced" : c.reason == "expired" ? "Expired" : "Cancelled");
    }

    void Close(string label)
    {
        Closing = true;
        SetButtons(false);
        status.text = label;
        StartCoroutine(CloseLater());
    }

    IEnumerator CloseLater()
    {
        yield return new WaitForSeconds(LingerSeconds);
        DigestTrigger.FoldOut(this, Root != null ? Root : gameObject);
    }

    void SetButtons(bool on)
    {
        send.interactable = on;
        cancel.interactable = on;
    }

    static string FirstLine(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        int cut = text.IndexOfAny(LineBreaks);
        return cut < 0 ? text : text.Substring(0, cut);
    }
}
