# IMESSAGE_PLAN: iMessage in the Messages panel

> Owner: Adrian. Started 2026-10-24 from `hub/messaging/` on `feat/bit-in-main`.
> Companion to `PROJECT_CONTEXT.md` sections 8.7 / 8.8 (Slack). Adrian authorized the additive provider/source fields and Bit tool routing on 2026-10-24.

## 1. The constraint

Apple has no iMessage API. The only practical path is the **Mac signed into the Apple ID**: read `~/Library/Messages/chat.db` (SQLite) and send by driving Messages.app with AppleScript (`osascript`).

- **The demo hub runs on a Mac signed into the Apple ID**, so iMessage is on there.
- **On Windows (or any non-Mac) the hub works exactly as today, Slack only.** iMessage is simply not included: the hub checks `sys.platform == "darwin"` and that `chat.db` is readable; if not, `IMessageService` never starts, `/health` reports `imessage: unavailable (not macOS)` / `(no Full Disk Access)`, the panel shows no iMessage section, and nothing errors. The `imessage/` package is only imported on macOS.
- macOS permissions for whatever launches `uvicorn` (Terminal / iTerm / VS Code): **Full Disk Access** (read `chat.db`) and **Automation → Messages** (send).
- Not chosen: BlueBubbles (REST/WS server, bigger install, some features need SIP off). `chat.db` + `osascript` has no extra moving parts.

## 2. What the user sees

- The Messages panel's channel list gets an **iMessage** section next to Slack: every iMessage chat, newest activity first. No allowlist, everything is visible; you scroll.
- Opening a chat shows its history like a Slack conversation (blue-tinted cards, phone-style initials). Scrolling up pages older messages.
- Bit can draft a text exactly like a Slack reply: "Mom asks if you're coming to dinner, want me to answer?" → draft card with the exact text and **Send iMessage** / Cancel. Only Send or "yes, send it" posts it.
- The sit-down digest merges both: "3 Slack messages and a text from Mom". `IMESSAGE_DIGEST=false` keeps texts out of the spoken digest (the panel still shows everything), so a private text isn't read aloud on stage.

## 3. Contract additions (additive; authorized)

- `source: "slack" | "imessage"` on `SlackMessage`, `ChannelSummary`, `Draft`; default `"slack"`, so existing clients are unaffected. It tells Unity which provider a message/chat/draft came from (tint, list section, Send label). The hub itself routes on the id prefix; without the field Unity would have to check `channel.StartsWith("im:")`.
- iMessage conversation ids are `im:<chat.guid>`. `kind` reuses `dm` / `group-dm`.
- `ts` keeps the Slack `"seconds.micro"` string format, converted from chat.db's `date` (nanoseconds since 2001-01-01), so sorting and `before=` paging are unchanged.
- No new endpoints, no new WS events: `/messages/channels`, `/messages/history`, `/messages/drafts*`, `/messages/digest`, `notify.*` all serve both sources.

## 4. Hub layout

```
hub/messaging/
  drafts.py            <- NEW: draft bookkeeping lifted out of SlackService (_take/_expire/_clear_draft/_open)
  hub.py               <- NEW: MessagingHub, routes by id prefix (im: -> iMessage, else Slack),
                          merges channel lists, history lookups and the digest
  imessage/            <- imported only on macOS
    settings.py        IMESSAGE_ENABLED (default on when darwin), IMESSAGE_DB, IMESSAGE_POLL_MS,
                       IMESSAGE_DIGEST, IMESSAGE_RECENT_DAYS
    db.py              read-only chat.db queries (sqlite `?mode=ro`), attributedBody decoding
    contacts.py        handle (+1555... / email) -> display name, from the AddressBook sqlite
    sender.py          bounded `imsg` CLI send to a local chat id, with outgoing-row confirmation
    service.py         IMessageService: start/stop/health/history/channel_summaries/send, same surface the router uses
```

The router's `enabled` dependency returns `MessagingHub` instead of `SlackService`. The only real refactor is `drafts.py`: posting is the one per-source step, so draft state is shared.

## 5. Reading

- **Poll** every ~1 s for `message.ROWID > last_rowid`, joined through `chat_message_join` -> `chat` -> `handle`. Simpler than watching `chat.db-wal`, fast enough.
- **Text:** on recent macOS `message.text` is often NULL and the body lives in `attributedBody` (a typedstream blob). The decoder must handle it or messages show blank.
- **Mapping:** `is_from_me` -> `is_me`; `date_edited` -> `edited`; `thread_originator_guid` -> `thread_ts` (read only); attachments -> placeholder text (`[photo]`, `[file]`); tapbacks (`associated_message_type` 2000-2005) dropped.
- **`mentions_me`:** true when the user's name appears in a group chat. DMs count as unread, not mentions (same as Slack DMs).
- **Channel list:** all chats, sorted by last message. Startup loads chats active in the last `IMESSAGE_RECENT_DAYS` (~30); older ones load as the list scrolls. Names come from Contacts; group chats use `chat.display_name` or the members, comma-joined.
- **Read state:** the hub's `MessageStore` seen-tracking is the source of truth. AppleScript can't mark messages read in Messages.app, so a text read in the headset stays unread on the phone.

## 6. Sending

Same rule as Slack: Bit only writes a draft; only Send or "yes, send it" posts.

- `sender.py`: use the `imsg` CLI to send to the local chat row id. This wraps Messages automation and verifies the matching outgoing row; macOS Tahoe 26 can report false AppleScript success for ghost rows, so direct `osascript` success is not sufficient. At startup, install missing `imsg` using Homebrew (`brew install steipete/tap/imsg`); if Homebrew is missing or installation fails, show actionable setup instructions. The first send may prompt for Automation access to Messages. `IMESSAGE_CLI` can point to a non-default executable.
- **Confirm the send.** The `imsg` CLI performs its own outgoing-row verification; the hub then confirms a matching new `is_from_me` row in the intended chat before emitting `notify.sent` with its real `ts`. No matching row means an uncertain outcome, never a fake success.
- `thread_ts` is ignored (AppleScript can't send inline replies); the draft card still shows `reply_to`.
- `messages_send` resolves names ("Mom") across both sources: a draft replying to a message uses that message's source; if both sources match a name, Bit asks. New optional arg `via: "slack" | "imessage"`.

## 7. Unity layout

- MSG mode has three adjacent panels: mentions and unread notifications on the left, iMessage in the center, and Slack on the right.
- Each conversation panel filters to its provider. Selecting a notification opens its conversation in the matching panel.
- Bit drafts are routed to the matching panel's composer; if that panel is closed, the floating Send / Cancel card remains available.
- Digest line is built hub-side in `MessagingHub.digest`; Unity just speaks/shows it.

## 8. Phases

1. **Read and digest:** implemented in the macOS service and shared message APIs.
2. **Send:** source-aware drafts require confirmation and are sent through `imsg`; a live Mac send check remains.
3. **Unity layout:** implemented as left notifications, center iMessage and right Slack panels; a live editor/headset view check remains.

### Implementation status (2026-10-24)

- Started the macOS-only database reader and wired its lifecycle and `/health` status
  into `hub/main.py`. Non-macOS startup does not import the iMessage package and reports `unavailable (not macOS)`.
- The reader opens `chat.db` read-only, introspects optional schema columns, converts Cocoa timestamps, and polls for new
  rows into the existing in-memory message store and `notify.message` event. The Bit messaging tools now read across
  both sources and route iMessage drafts through `messages_send` / `messages_confirm` with the same confirmation gate.
- `/messages/channels`, `/messages/history`, `/messages/channels/{id}/seen`, `/messages/focus`, and draft update/send/cancel
  now route iMessage IDs; sticky `notify.channels` combines both providers. The digest merges providers and honors
  `IMESSAGE_DIGEST=false` without marking excluded texts as read.
- Sending uses the `imsg` CLI rather than invoking AppleScript directly: current macOS 26 behavior can report
  AppleScript success for a message that was not actually joined to the target chat. The code detects the executable
  and version in `/health`; startup now installs the CLI through Homebrew if it is missing. Actual sending still needs
  a live confirmation smoke check.
- Added the additive `source` field to message, channel, draft and sent payload models. REST channel/history endpoints
  and Bit's `messages_read_latest`, `messages_list_channels`, `messages_read_channel`, `messages_send` and
  `messages_confirm` now include or route iMessage. Drafts still require the existing explicit confirmation; sends
  route to the `imsg` CLI and require an outgoing database row before `notify.sent` is emitted.
- The local Mac is on macOS 26.0.1. Startup installs `imsg` through Homebrew when missing. Full Disk Access cannot be
  granted programmatically: `/health` and the startup log explain that the user must enable it for the app running
  the hub under System Settings > Privacy & Security > Full Disk Access, then restart. The first send may request
  Automation permission. A live send smoke check remains outstanding.
- MSG mode now places unread mentions/notifications at left, iMessage center, Slack right. Provider-specific views,
  draft routing, notification click-through, the message prefab and the serialized main scene are updated together.
- Startup currently ingests up to 1,000 rows from the configured recent-days window; reliable attributedBody
  decoding, thread-parent mapping and older-chat discovery/paging beyond the current 1,000-row cap remain. Contact labels are supplied from
  `imsg chats --json` when CLI and Contacts access are available. The fallback attributedBody text extraction is
  best-effort and still needs real fixture validation.

**Testing:** no mock mode. The real check is a live smoke script: text the Mac from a second phone, see it arrive on `/ws`, send a draft back and see it on the phone. Unit tests may build a tiny fixture `chat.db` in the test (a small stub, not a runtime mode). On a non-Mac, a test asserts the hub starts Slack-only with `imessage: unavailable`.

## 9. Risks

- **`attributedBody` decoding** is the most likely thing to break.
- **Group sends** via AppleScript have been flaky on recent macOS. Test early; DM-only send is an acceptable fallback (group chats stay readable).
- **Messages.app must be running**; `sender.py` launches it.
- **Privacy:** every chat is visible to the panel and to Bit's tools. `IMESSAGE_DIGEST=false` is the stage switch for the spoken digest.

## 10. Open

- Choose the demo default for `IMESSAGE_DIGEST`.
- Validate `attributedBody` decoding against real local messages without exposing message contents in logs.
- Finish Unity iMessage source styling and run the live send smoke test after installing `imsg`.
