# Messages module (Unity)

Slack on the desk: a Slack-like panel in MSG mode, a "New for you" list beside it, and the arrival digest + Albie's
Send / Cancel card that keep working while MSG mode is closed. Hub side: `hub/messaging/` (Socket Mode, no tunnel);
spec: `PROJECT_CONTEXT.md` 7, 8.7 and 8.8; event shapes mirrored in `Core/Net/ForgeEvents.cs` (`NotifyMessage`,
`SlackChannel` / `NotifyChannels`, `NotifyDraft`, `NotifySent`, `NotifyDraftCancelled`, `NotifyDigest`).
Live hub only. There is no mock mode: run the hub with the Slack tokens in `hub/.env` and test against the real workspace;
without a hub the panels show "Hub offline" and connect by themselves when it appears.

## What it does

- **Slack panel** (`SlackPanel.prefab`, panel id `messages`, slot Main). Left: conversations grouped Channels / Direct
  messages, unread count pill, accent dot when something mentions you, bold when unread, the open one lit. Right: the
  conversation's name and topic, its messages oldest -> newest (initials bubble, name, relative time, text with `<@U..>`
  rendered as @name and your own mentions in the accent, "N replies", your own messages inset and tinted), "Load earlier"
  when the hub has more, and the composer. Opening a conversation GETs its history (cached per conversation for the
  session) and POSTs `/messages/channels/{id}/seen`; new messages in the open conversation are appended and marked seen;
  the list follows new messages unless you scrolled up. Tapping a message = **Ask Albie about it** (`messages.focus`).
- **Composer.** A multi-line Inter `TMP_InputField` + **Send** + **Ask Albie**. What you type becomes a draft
  (`POST /messages/drafts {channel, text}`) that is sent at once (`POST .../send`), since you wrote it. A draft from Albie
  (`notify.draft`) lands in the field as editable text, the panel switches to its conversation, **Cancel** appears and the
  caption says "Albie wrote this"; Send `PATCH`es the text first when you changed it (`SlackNet.Patch`, see Notes), Cancel
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
  (or in front of the camera without a desk): recipient, what it answers, the exact text, Send and Cancel. With the panel
  open, `DraftPresenter` hands the draft to the composer instead (and a floating card moves into the composer when the
  panel opens).

## Look

Hudson's graphite `ForgePanel` frame (64 mm header with the MSG badge, title and a subtitle such as
`#forge-team · 3 unread`; Content insets 18 / 16 / 78 mm). Inside it the module keeps its own card fill for message rows,
conversation rows, the composer field and the Mentions cards: the slightly lighter translucent slate
(`SlackUi.CardFill`, 0.12 / 0.15 / 0.21 at 0.8 alpha) with a soft shadow and 10-14 mm corners, opaque Inter text in
`ForgeTheme.PanelText` / `PanelSubtext`, accent `ForgeTheme.Messages` (#A77BFF) for the Send pill, unread pills, mention
dots and strips. Inter loads the way Core does (`ForgeTheme.Font`, Core/Resources/ForgeLook); TMP's default font is the
fallback. Hover: Inbox cards lift 1 cm and go opaque; list rows and buttons brighten. One canvas per panel.

## Files

| File | What |
|---|---|
| `Scripts/SlackPanel.cs` | The Slack panel: conversation list, message list, composer; `SlackPanel.Active`, `Open(channelId)`, `FocusBit`. UI built at runtime under Content. |
| `Scripts/SlackComposer.cs` | The composer (field, Send / Ask Albie / Cancel, draft state, the OnUpdateSelected drive for typing). |
| `Scripts/MentionsPanel.cs` | "New for you" + the floating digest copy. |
| `Scripts/MessageCard.cs` | One message on a slate card (`MessageCard.prefab`): Inbox and Thread styles, `Measure` / `Layout`, hover, click. |
| `Scripts/SlackStore.cs` | Static model: channels (`notify.channels`, GET), per-conversation history cache, user names, "me", recent messages; `MarkSeen`, `LoadHistory`. |
| `Scripts/SlackData.cs` | REST DTOs (`SlackHistory`, `SlackMe`, ...), `SlackText` (ts maths, relative time, initials, Slack markup -> TMP rich text), `SlackNet.Patch`. |
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
6. Say to Albie "reply that I'm on it": his draft lands in the composer with Cancel; edit it, Send. **Ask Albie** reads the newest
   message aloud. Close MSG mode and ask for another reply: the Send / Cancel card floats beside Albie instead.

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
