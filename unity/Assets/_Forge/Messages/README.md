# Messages module (Unity)

Bit's Slack inbox on the desk. Hub side: `hub/messaging/` (Socket Mode, no tunnel); spec: `PROJECT_CONTEXT.md` 8.7 and 8.8;
event shapes: section 7 (`notify.message`, `notify.draft`, `notify.sent`, `notify.draft_cancelled`, `notify.digest`,
mirrored in `Core/Net/ForgeEvents.cs`).
Live hub only. There is no mock mode: run the hub with the Slack tokens in `hub/.env` and test against the real workspace.

## What it does

- **Arrival digest.** `DigestTrigger` watches `DeskPresence`. When you sit down it POSTs `/messages/digest` once; the hub
  answers with `notify.digest` and a `pet.say` line ("You have 3 new messages, 1 mentions you."), which Bit speaks.
  If MSG mode is closed, a floating copy of the panel pops up front-right of the desk, beside Bit, and folds after 20 s
  (or when you open MSG mode, or leave the desk).
- **Cards.** `MessagesPanel` shows the newest 4 messages as `MessageCard`s, older ones as "+N more" in the footer.
  Card = sender initial bubble, name + `#channel` (or `DM`), first line of text, relative time; mentions get an orange strip.
  Translucent at rest (frame alpha 0.8, text opaque). Hover lifts the card 1 cm, makes it opaque and expands it to the full
  text. Click or poke = **Reply with Bit**: one agent turn with the message as context; Bit asks what to say and drafts it.
- **Confirm card (sending).** Nothing leaves without you. You dictate, Bit phrases it, the hub emits `notify.draft`, and
  `DraftPresenter` pops a `DraftCard`: recipient (`#channel` or `DM to Hudson`), what it answers, the exact text, **Send**
  and **Cancel**. Send -> `POST /messages/drafts/{id}/send` (posted under *your* Slack name); Cancel -> `POST /messages/drafts/{id}/cancel`.
  The card closes itself on `notify.sent` / `notify.draft_cancelled` for its draft, so saying "send it" to Bit (the hub's
  `messages_confirm` tool) or a newer draft (reason `superseded`) dismisses it too. One card at a time. It docks under the
  open Messages panel when MSG mode is open, otherwise it floats beside Bit at the front-right of the desk (a little closer
  than the digest stack); with no desk in the scene it sits 0.6 m in front of the camera.
- Live `notify.message` while seated pushes a card in at the top; `notify.sent` flashes "Sent to #channel" in the footer.

## Files

| File | What |
|---|---|
| `Scripts/MessagesPanel.cs` | Panel logic: digest / message / sent events, `GET /messages/latest` on open, 4-card cap, reply. |
| `Scripts/MessageCard.cs` | One message: binding, relative time, hover lift/expand, click. |
| `Scripts/DigestTrigger.cs` | Sit-down digest request + floating panel. Self-installs into any scene with a `DeskPresence`. Also the shared `SpawnAtDesk` / `PopIn` / `FoldOut` helpers. |
| `Scripts/DraftCard.cs` | The Send / Cancel card: binding, the two POSTs, closes on `notify.sent` / `notify.draft_cancelled`. |
| `Scripts/DraftPresenter.cs` | Shows one `DraftCard` per `notify.draft`, docked under the open panel or floating at the desk. Self-installs like `DigestTrigger`. |
| `Editor/MessagesPrefabBuilder.cs` | **Forge > Messages > Build Prefabs** builds the two prefabs below. |
| `Resources/MessagesPanel.prefab` | ForgePanel variant, panel id `messages`, 0.42 x 0.52 m. Built, not hand-edited. |
| `Resources/MessageCard.prefab` | The card, nested in the panel as `CardTemplate`. Built, not hand-edited. |
| `Resources/DraftCard.prefab` | ForgePanel variant, panel id `messages-draft`, 0.42 x 0.26 m, translucent sheet + Send / Cancel buttons. Built, not hand-edited. |

The prefabs live in `Resources/` (not `Prefabs/`) so `DigestTrigger` can `Resources.Load` the panel for the floating copy
without a scene reference. `Resources` and the `.prefab` files are created by the builder; commit them (and their `.meta`s).

## Slotting it into Main (Hudson)

`Workspace.DefaultModes()` already has the `messages` mode (MSG button) with one item `("messages", "Messages", Right)`.
Assign `Resources/MessagesPanel.prefab` to that item's `prefab` (or add `("messages", "Assets/_Forge/Messages/Resources/MessagesPanel.prefab")`
to `MainSceneBuilder.ModulePrefabs`). The panel's `ForgePanel` is the prefab root, so move/resize handles work.
`DigestTrigger` and `DraftPresenter` need nothing in the scene: they add themselves at load when a `DeskPresence` exists
(placing them by hand is fine too).

## Test live

1. `hub/`: tokens in `.env` (`SLACK_BOT_TOKEN`, `SLACK_APP_TOKEN`, `SLACK_USER_TOKEN`), `uvicorn main:app --host 0.0.0.0 --port 8000`.
2. Unity: **Forge > Messages > Build Prefabs** once (and after script changes to the UI layout). Play `Main`.
3. From your phone, DM the bot or post in `#forge-team`, then walk to the desk and sit (desktop: `E`). Bit says the digest
   line and the floating cards appear. Press **MSG** on the desk dock for the full panel.
4. Hover a card (crosshair or hand ray): it lifts and shows the whole message. Click it: Bit asks what to reply, drafts it,
   and the Send / Cancel card appears under the panel. Press **Send** (or tell Bit "send it"): the message lands in Slack
   under your name and the card says "Sent" and folds away. Cancel, or a new draft, closes it too.
5. While seated, send another message: it slides in at the top as `notify.message`.

## Notes

- Everything goes through `ForgeNet` (events + REST); the module opens no sockets of its own.
- No `Update` loops. Hover and fold animations are `ForgeTween` coroutines; the relative-time labels refresh every 30 s.
- First import generates `.meta` files for the new folder, scripts and prefabs: commit them with the prefabs.
