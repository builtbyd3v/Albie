# Messages module (Unity)

Slack on the desk: a Slack-like panel in MSG mode, a "New for you" list beside it, and the arrival digest + Albie's
Send / Cancel card that keep working while MSG mode is closed. Hub side: `hub/messaging/` (Socket Mode, no tunnel);
spec: `PROJECT_CONTEXT.md` 7, 8.7 and 8.8; event shapes mirrored in `Core/Net/ForgeEvents.cs` (`NotifyMessage`,
`SlackChannel` / `NotifyChannels`, `NotifyDraft`, `NotifySent`, `NotifyDraftCancelled`, `NotifyDigest`).
Live hub only. There is no mock mode: run the hub with the Slack tokens in `hub/.env` and test against the real workspace;
without a hub the panels show "Hub offline" and connect by themselves when it appears.

## What it does

- **Slack panel** (`SlackPanel.prefab`, panel id `messages`, slot Main), laid out like the Slack app in its dark
  aubergine theme, simplified. Sidebar: the workspace's name, **Channels** A-Z (`#`, or a lock for private ones) and
  **Direct messages** (an initials avatar; a group DM shows how many people), unread ones bold, a count pill for unread
  DMs and for mentions, the open one lit. Conversation: a rounded dark pane with the header (`#` name or the person,
  topic), the messages oldest -> newest as Slack rows (`SlackRow`: rounded-square avatar, bold name, "4:15 PM", the
  text; follow-ups from the same person within 5 minutes grouped under one name, their time in the gutter on hover),
  day dividers ("Today", "Yesterday", "Wednesday, October 22nd"), a red **New** line above what was unread when the
  conversation opened, "N replies" in link blue, "replied in a thread" on a reply, "(edited)". Slack markup renders
  Slack's way: `<@U..>` as @name (yours yellow-highlighted, others blue), `#channel` and links blue, `*bold*`,
  `_italic_`, `~strike~`, inline code and code blocks. Opening a conversation GETs its history (cached per conversation
  for the session) and POSTs `/messages/channels/{id}/seen`; new messages in the open conversation are appended and
  marked seen; the list follows new messages unless you scrolled up, and always follows your own. Point at a message: it
  lights up and **Ask Albie** floats at its top right (`messages.focus`).
- **Composer**, Slack's message box: a rounded field with a grey edge ("Message #general"), **Ask Albie** inside it on
  the left and the **Send** arrow on the right (green once there is text). What you type becomes a draft
  (`POST /messages/drafts {channel, text}`) that is sent at once (`POST .../send`), since you wrote it; the message shows
  in the conversation as soon as the hub answers with its ts (`SlackStore.AddOwn`), and the hub's `notify.message` for it
  (and `notify.sent`) are no-ops then. A draft from Albie (`notify.draft`) lands in the field as editable text, the panel
  switches to its conversation, and a banner above the box says "**Albie drafted this** for #x. Edit it, then Send." with
  **Cancel**; Send `PATCH`es the text first when you changed it (`SlackNet.Patch`, see Notes), Cancel
  `POST`s `.../cancel`. The composer also closes the draft on `notify.sent` / `notify.draft_cancelled`, so "send it" to Albie
  works. **Ask Albie** sends `messages.focus` for the conversation's latest message (Albie reads it aloud and offers to answer).
  On the Quest the field opens the system keyboard when poked; on desktop the keyboard types into it (Enter sends).
- **Mentions panel** (`MentionsPanel.prefab`, panel id `messages-mentions`, slot Left). "New for you": unread DMs and
  mentions across conversations as cards, newest first, 8 at most plus "+N more". Tapping a card opens that conversation
  in the Slack panel; the card clears once the conversation is seen. Replaces the old `MessagesPanel`.
- **Arrival digest.** The pet's `ArrivalDirector` POSTs `/messages/digest` once per sit (`DigestTrigger.RequestDigest()`);
  the hub answers with `notify.digest` and a `pet.say` line Albie speaks. With MSG mode closed, a floating `MentionsPanel`
  (`floating = true`) shows the digest's cards (4 at most, "+N more") beside Albie at the desk's front-right and folds after
  20 s, when MSG mode opens, or when you leave the desk. Tapping a card opens MSG mode on that conversation.
- **Send / Cancel card** (`DraftCard.prefab`). With the Slack panel closed, `notify.draft` pops a `DraftCard` beside Albie
  (or in front of the camera without a desk): recipient, what it answers, the exact text, Send and Cancel. Sent, it says
  "Sent to #x" in green for ~2 s before it folds; the message is in that conversation when you open it. With the panel
  open, `DraftPresenter` hands the draft to the composer instead (and a floating card moves into the composer when the
  panel opens). A draft with files (`NotifyDraft.attachments`, e.g. a CAD design's STEP / STL / drawing / parts list) shows
  them as one line on the card and on the composer's "Albie drafted this" banner; the hub uploads them with the text on Send.
- **Albie opens a conversation** ("open my chat with Adrian", "pull up #general": the hub's `messages_open` tool, or
  `POST /messages/open {channel}`). `SlackRemote` (self-installing, like `DraftPresenter`) takes the hub's
  `messages.open`, shows that conversation on its provider's panel
  (`SlackPanel.ForChannel`: Slack, or the iMessage panel for an `im:` chat), opening MSG mode when it is closed (like tapping a Mentions
  card), and answers `messages.opened {ok, reason?}` in the same frame (`not_at_desk` away from the desk), so Albie says
  what really happened. A DM the hub just opened gets a row at once (`SlackStore.Provisional`).

## Look

Hudson's graphite `ForgePanel` frame (64 mm header with the MSG badge, title and a subtitle such as
`#forge-team · 3 unread`; Content insets 18 / 16 / 78 mm). Inside it, the Slack panel is Slack's dark theme
(`SlackUi.Sk*`): an aubergine window and sidebar (#3C1C40, the open conversation #74467E, count pills #9E63AB), the
conversation on #1A1D21 with #D1D2D3 text, #ABABAD times, #1D9BD1 links, #F2C744 for your mentions, #E01E5A for the New
line, #007A5A for Send. Icons (send, lock, caret, sparkle) are white SVGs in `Resources/SlackIcons`, imported as sprites
and tinted. Bold text is Inter with most of TMP's faux-bold spacing taken back (`SlackUi.SetBold`, `SlackText.BoldOpen`):
there is one Inter weight. The Mentions cards and the Send / Cancel card keep the module's translucent slate
(`SlackUi.CardFill`) and accent `ForgeTheme.Messages` (#A77BFF). Inter loads the way Core does (`ForgeTheme.Font`,
Core/Resources/ForgeLook); TMP's default font is the fallback. One canvas per panel.

## Files

| File | What |
|---|---|
| `Scripts/SlackPanel.cs` | The Slack panel: sidebar, conversation (dividers, New line, rows), composer; `SlackPanel.Active`, `Open(channelId)`, `FocusBit`. UI built at runtime under Content. |
| `Scripts/SlackRow.cs` | One message as a Slack row (avatar, name + time, text, replies; grouped follow-ups; hover + Ask Albie). Built in code, pooled. |
| `Scripts/SlackRemote.cs` | Albie's `messages.open`: opens MSG mode on a conversation, answers `messages.opened`. Self-installs. |
| `Resources/SlackIcons/*.svg` | send, lock, caret, sparkle (white, imported as sprites). |
| `Scripts/SlackComposer.cs` | The composer (field, Send / Ask Albie / Cancel, draft state, the OnUpdateSelected drive for typing). |
| `Scripts/MentionsPanel.cs` | "New for you" + the floating digest copy. |
| `Scripts/MessageCard.cs` | One message on a slate card (`MessageCard.prefab`): Inbox and Thread styles, `Measure` / `Layout`, hover, click. |
| `Scripts/SlackStore.cs` | Static model: channels (`notify.channels`, GET), per-conversation history cache, user names, "me", the workspace's name, recent messages; `MarkSeen`, `LoadHistory`, `AddOwn` (a sent message, at once), `Provisional`. |
| `Scripts/SlackData.cs` | REST DTOs (`SlackHistory`, `SlackMe`, `SlackOpenRequest`, ...), `SlackText` (ts maths, clock times and day labels, initials, one '#' per channel name, Slack markup -> TMP rich text in the accent or Slack's look), `SlackNet.Patch`. |
| `Scripts/SlackUi.cs`, `SlackButton.cs`, `SlackHitArea.cs` | Colours, type scale and factories (cards, pills, scroll lists), the hover button, the invisible hit area. |
| `Scripts/ForgeInput.cs` | `ForgeInput.TextFieldFocused`: true while the composer has the keyboard (for DesktopRig / pet key handlers to check). |
| `Scripts/DigestTrigger.cs` | `RequestDigest()` + the floating digest; shared `SpawnAtDesk` / `FaceUser` / `PopIn` / `FoldOut` (the Pet module calls them). |
| `Scripts/DraftPresenter.cs` | Routes `notify.draft`: composer when the Slack panel is open, else a floating `DraftCard`. Self-installs. |
| `Scripts/DraftCard.cs` | The Send / Cancel card. |
| `Scripts/MessagesDeskZone.cs` | Marker + desk-frame `Footprint` the pet keeps clear of while cards float (unchanged). |
| `Editor/MessagesPrefabBuilder.cs` | **Forge > Messages > Build Prefabs** builds the four prefabs below (and deletes the old MessagesPanel.prefab). |
| `Resources/SlackPanel.prefab`, `IMessagePanel.prefab` (panel id `messages-imessage`), `MentionsPanel.prefab`, `MessageCard.prefab`, `DraftCard.prefab` | Built, not hand-edited; commit them with their `.meta`s. |
| `Scripts/MessagesLayout.cs` | Adds the iMessage panel to MSG mode at runtime (center, Slack moves right) only when the hub's `/health` reports `imessage.available`; otherwise MSG is unchanged. No scene edit. |

## Slotting it into Main (Hudson)

`Workspace.DefaultModes()` has the `messages` mode with `("messages", "Slack", Main)` and `("messages-mentions", "Mentions", Left)`;
`MainSceneBuilder.ModulePrefabs` maps them to `Resources/SlackPanel.prefab` and `Resources/MentionsPanel.prefab`.
Run **Forge > Messages > Build Prefabs**, then **Forge > Build Main Scene**. `DigestTrigger` and `DraftPresenter` need
nothing in the scene: they add themselves at load when a `DeskPresence` exists.

## Test live

1. `hub/`: tokens in `.env`, `uvicorn main:app --host 0.0.0.0 --port 8000`.
2. Unity: **Forge > Messages > Build Prefabs** once (and after script changes to the prefabs), **Forge > Build Main Scene**. Play `Main`.
3. Sit at the desk (desktop: `E`). Albie says the digest line; the floating "New for you" cards appear beside him. Tap one: MSG
   mode opens on that conversation. Or press **MSG** on the dock.
4. Click conversations on the left; the unread pill clears. Scroll the list by dragging it (mouse or hand ray); "Load
   earlier" at the top when there is more. Type in the field, Enter or **Send**: the message appears in Slack under your
   name and comes back as your own (tinted) row.
5. From your phone, post in a channel or DM Bit: the row lands at the bottom, the Mentions panel lists it, the pill counts.
6. Say to Albie "reply that I'm on it": his draft lands in the composer with Cancel; edit it, Send: it shows in the
   conversation right away. **Ask Albie** reads the newest message aloud. Close MSG mode and ask for another reply: the
   Send / Cancel card floats beside Albie instead; Send says "Sent to ...", and the message is there when you open MSG.
7. "Albie, open my chat with Adrian" (or `POST /messages/open {"channel": "Adrian"}` on the hub): MSG mode opens on that
   DM. Standing away from the desk, Albie says to sit down first.

## Notes

- Everything goes through `ForgeNet` (events + REST) except `PATCH /messages/drafts/{id}`, which ForgeNet lacks:
  `SlackNet.Patch` runs the same `UnityWebRequest` shape on `ForgeNet.Url(path)` from the ForgeNet instance.
- Typing on desktop: Main's only input module is the Interaction SDK's `PointableCanvasModule`, which never sends
  `OnUpdateSelected` (where TMP reads key events), so `SlackComposer.Tick` sends it while the field is focused.
- `ForgeInput.TextFieldFocused` is set while the field has focus. DesktopRig (WASD, Shift, E) and the pet's key handlers
  don't check it yet: flagged for Core / Pet.
- Quest keyboard: TMP_InputField opens `TouchScreenKeyboard` itself; the Meta project config needs "Requires System
  Keyboard" on (flagged for Core).
- No `Update` work beyond the composer's focus check: lists rebuild only when data or the panel size changes, rows and
  cards are pooled, rendered rich text is cached per message, relative times refresh every 30 s.
- First import generates `.meta` files for the new scripts and prefabs: commit them with the prefabs.
