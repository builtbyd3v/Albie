# Messages module (Unity)

Bit's Slack inbox on the desk. Hub side: `hub/messaging/` (Socket Mode, no tunnel); spec: `PROJECT_CONTEXT.md` 8.7 and 8.8;
event shapes: section 7 (`notify.message`, `notify.sent`, `notify.digest`, mirrored in `Core/Net/ForgeEvents.cs`).
Live hub only. There is no mock mode: run the hub with the Slack tokens in `hub/.env` and test against the real workspace.

## What it does

- **Arrival digest.** `DigestTrigger` watches `DeskPresence`. When you sit down it POSTs `/messages/digest` once; the hub
  answers with `notify.digest` and a `pet.say` line ("You have 3 new messages, 1 mentions you."), which Bit speaks.
  If MSG mode is closed, a floating copy of the panel pops up front-right of the desk, beside Bit, and folds after 20 s
  (or when you open MSG mode, or leave the desk).
- **Cards.** `MessagesPanel` shows the newest 4 messages as `MessageCard`s, older ones as "+N more" in the footer.
  Card = sender initial bubble, name + `#channel` (or `DM`), first line of text, relative time; mentions get an orange strip.
  Translucent at rest (frame alpha 0.8, text opaque). Hover lifts the card 1 cm, makes it opaque and expands it to the full
  text. Click or poke = **Reply with Bit**: one agent turn with the message as context; the hub's `messages_send` tool reads
  the reply back and posts it in-thread only after you confirm.
- Live `notify.message` while seated pushes a card in at the top; `notify.sent` flashes "Sent to #channel" in the footer.

## Files

| File | What |
|---|---|
| `Scripts/MessagesPanel.cs` | Panel logic: digest / message / sent events, `GET /messages/latest` on open, 4-card cap, reply. |
| `Scripts/MessageCard.cs` | One message: binding, relative time, hover lift/expand, click. |
| `Scripts/DigestTrigger.cs` | Sit-down digest request + floating panel. Self-installs into any scene with a `DeskPresence`. |
| `Editor/MessagesPrefabBuilder.cs` | **Forge > Messages > Build Prefabs** builds the two prefabs below. |
| `Resources/MessagesPanel.prefab` | ForgePanel variant, panel id `messages`, 0.42 x 0.52 m. Built, not hand-edited. |
| `Resources/MessageCard.prefab` | The card, nested in the panel as `CardTemplate`. Built, not hand-edited. |

The prefabs live in `Resources/` (not `Prefabs/`) so `DigestTrigger` can `Resources.Load` the panel for the floating copy
without a scene reference. `Resources` and the `.prefab` files are created by the builder; commit them (and their `.meta`s).

## Slotting it into Main (Hudson)

`Workspace.DefaultModes()` already has the `messages` mode (MSG button) with one item `("messages", "Messages", Right)`.
Assign `Resources/MessagesPanel.prefab` to that item's `prefab` (or add `("messages", "Assets/_Forge/Messages/Resources/MessagesPanel.prefab")`
to `MainSceneBuilder.ModulePrefabs`). The panel's `ForgePanel` is the prefab root, so move/resize handles work.
`DigestTrigger` needs nothing in the scene: it adds itself at load when a `DeskPresence` exists (placing one by hand is fine too).

## Test live

1. `hub/`: tokens in `.env` (`SLACK_BOT_TOKEN`, `SLACK_APP_TOKEN`, `SLACK_USER_ID`), `uvicorn main:app --host 0.0.0.0 --port 8000`.
2. Unity: **Forge > Messages > Build Prefabs** once (and after script changes to the UI layout). Play `Main`.
3. From your phone, DM the bot or post in `#forge-team`, then walk to the desk and sit (desktop: `E`). Bit says the digest
   line and the floating cards appear. Press **MSG** on the desk dock for the full panel.
4. Hover a card (crosshair or hand ray): it lifts and shows the whole message. Click it: Bit asks what to reply, then sends.
5. While seated, send another message: it slides in at the top as `notify.message`.

## Notes

- Everything goes through `ForgeNet` (events + REST); the module opens no sockets of its own.
- No `Update` loops. Hover and fold animations are `ForgeTween` coroutines; the relative-time labels refresh every 30 s.
- First import generates `.meta` files for the new folder, scripts and prefabs: commit them with the prefabs.
