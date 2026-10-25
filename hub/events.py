"""WebSocket event bus (`/ws`) and payload models. Mirror of ForgeEvents.cs.

Every message is {"type": ..., "payload": {...}}. Models so far: CAD, ui.highlight, the VS Code push event,
the Slack notify.* events (PROJECT_CONTEXT 7 + 8.8), screen.status (the screen-bridge supervisor, hub/screens;
docs/PET_AGENT_CONTRACT.md Addendum F), the desk extras (ui.widgets, timers.updated, notes.updated, movie.state),
and Albie driving the views: ui.mode, cad.action, draft attachments (Addendum H).
The ML and pet models from section 7 still belong to their owners; add them next to these.
"""
from __future__ import annotations

import asyncio
import logging
from collections.abc import Callable, Iterable
from typing import Any, Literal

from fastapi import WebSocket, WebSocketDisconnect
from pydantic import BaseModel, ConfigDict, Field

log = logging.getLogger("forge.events")


class CadMetric(BaseModel):
    id: str
    label: str
    value: float
    unit: str = ""
    display: str = ""                 # value + unit, formatted by the hub
    status: str = "ok"                # ok | warn | bad
    min: float | None = None          # min and max: draw it as a gauge
    max: float | None = None
    bands: list[dict[str, Any]] = []  # gauge colours, ascending: [{to, status}]
    note: str = ""                    # one word for the gauge's chip (e.g. "stable")


# What Unity draws on and around the model (CAD_PLAN section 5, round 4; hub/cad/scene.py). Positions are in the
# model frame: the GLB's glTF scene space (metres, Y up, origin at the part's origin, like cg_gltf); directions are
# unit vectors in it. Unity (glTFast flips X): [x, y, z] -> new Vector3(-x, y, z) under CadModel.Root.

class CadMarker(BaseModel):
    id: str                           # "cg" on every design; "cp", "thrust", "input", "output", ...
    label: str
    kind: str = ""                    # cg | cp | thrust | input | output | ...: picks the marker's style
    pos: list[float]


class CadDimension(BaseModel):
    """A callout: value_mm measured from `from` to `to` (points on the model); draw the line offset_mm (design mm)
    along offset_dir, with extension lines back to the points."""
    model_config = ConfigDict(validate_by_name=True, validate_by_alias=True, serialize_by_alias=True)
    id: str
    label: str
    value_mm: float
    from_: list[float] = Field(alias="from")
    to: list[float]
    offset_dir: list[float] | None = None
    offset_mm: float | None = None


class CadExplodePart(BaseModel):
    id: str                           # a leaf part (CadPart.id); every leaf part has one
    dir: list[float]                  # unit
    dist_mm: float                    # at design size; 0 = stays put
    order: int = 0                    # stagger: 0 moves first


class CadExplode(BaseModel):
    axis: str = "auto"                # stack | radial | shaft | auto
    parts: list[CadExplodePart] = []


class CadMassItem(BaseModel):
    id: str                           # assembly / part id, or a key for a group or a point mass
    label: str
    mass_g: float
    share: float = 0.0                # of analysis.mass_g
    part_ids: list[str] = []          # the parts to highlight; [] for a point mass (motor, parachute)


class CadAxis(BaseModel):
    origin: list[float]
    dir: list[float]


class CadSpinPart(BaseModel):
    model_config = ConfigDict(extra="allow")
    id: str
    origin: list[float]               # a point on the spin axis
    dir: list[float]                  # unit
    rpm: float                        # signed: + is counter-clockwise seen from the tip of dir (right-hand rule)
    spin: str = ""                    # ccw | cw (seen from the tip of dir) | none
    rel_speed: float | None = None    # spin: speed relative to the input, signed
    motor_id: str | None = None       # hover: the motor under this prop
    radius_mm: float | None = None    # design mm: a prop's swept radius, a gear's pitch radius, a shaft's radius


class CadDemo(BaseModel):
    """What the demo button does. kind: launch (fly the samples from GET `source`), hover, spin, none."""
    model_config = ConfigDict(extra="allow")
    kind: str = "none"
    label: str = ""                   # the button: Fly | Hover | Run
    note: str = ""                    # one plain sentence
    source: str | None = None         # launch: "/cad/flight" (add ?v=version)
    axis: CadAxis | None = None       # launch / lift direction, or the input shaft
    values: dict[str, float] = {}     # numbers for the animation (hover_rpm, ratio, ...)
    spin_parts: list[CadSpinPart] = []
    readouts: list[CadMetric] = []
    assumptions: list[str] = []
    blocked: str | None = None        # why it can't run (button disabled, reason as its tooltip), else null
    reveal_ids: list[str] = []        # parts to lift / turn to glass while it runs and on the stage (the gearbox lid)


class CadVerdict(BaseModel):
    """The panel's answer ("Will it fly?"), from the design's metrics."""
    status: str = "ok"                # ok | warn | bad (the metrics' vocabulary)
    headline: str = ""                # a word or two: Stable, Props collide, Runs clean
    sentence: str = ""                # one plain sentence with the numbers
    metric: str | None = None         # the id of the metric it's about


class CadConstant(BaseModel):
    """A fixed input the design assumes (an info row): the rocket's motor, the drone's power system."""
    label: str
    value: str
    note: str = ""


class CadAnalysis(BaseModel):
    mass_g: float
    cg_gltf: list[float]
    size_mm: list[float] | None = None
    part_count: int | None = None
    metrics: list[CadMetric] = []     # what the panel shows for any design; the rocket's margin is one of them
    length_mm: float | None = None
    diameter_mm: float | None = None
    cg_mm: float | None = None        # from the nose tip
    cp_mm: float | None = None        # from the nose tip
    margin_cal: float | None = None
    stable: bool | None = None
    stability: str | None = None      # unstable | marginal | stable | overstable
    cna_per_rad: float | None = None
    motor: str | None = None
    markers_gltf: dict[str, list[float]] | None = None
    breakdown: list[dict[str, Any]] = []
    assumptions: list[str] = []
    markers: list[CadMarker] = []     # round 4: every design (CG first); see CadMarker
    dimensions: list[CadDimension] = []
    explode: CadExplode | None = None
    mass_breakdown: list[CadMassItem] = []
    demo: CadDemo | None = None
    verdict: CadVerdict | None = None
    constants: list[CadConstant] = []


class CadModelUpdated(BaseModel):
    version: int
    part: str = ""                    # design id: hub/cad/parts/<part>.py
    title: str = ""
    params: dict[str, Any]
    previous_version: int | None = None
    changed_ids: list[str] = []
    removed_ids: list[str] = []
    source: str = "api"               # api | pet | preset | script | undo | reset | open | startup
    analysis: CadAnalysis | None = None
    label: str = ""                   # the version in words: Bit's request, a preset, "Saved rocket.py", a change


class CadRegenFailed(BaseModel):
    source: str
    kind: str                         # params | build | timeout | crash
    error: str
    error_line: int | None = None     # line in the part script, when the script raised
    kept_version: int | None = None


class CadView(BaseModel):
    explode: bool | None = None
    focus_id: str | None = None
    focus_ids: list[str] = []         # proposed: several parts lit at once (all the fins); focus_id is the first of
                                      # them, so a build without focus_ids still lights one


class CadAction(BaseModel):
    """Bit pressing a button in the CAD view (docs/PET_AGENT_CONTRACT.md Addendum H). One-off, not sticky: a client
    that connects later must not replay it. Unity applies it once the model of `version` is on screen."""
    action: Literal["expand", "demo", "markers", "dimensions", "tab", "reassemble", "take_off", "put_back"]
    on: bool | None = None            # expand (Inspect: the model big in front of the user) / demo (Fly, Hover, Run) /
                                      # markers / dimensions: true = turn it on, false = off
    tab: str | None = None            # tab: overview | parameters | analysis | history (the design panel)
    part_id: str | None = None        # proposed, take_off: the part Albie takes off and hands you (taken already: the
                                      # next one of its group, FIN_2 after FIN_1); put_back: that part, none = the last
    part: str = ""                    # the design it is for
    version: int | None = None        # the version it is for: wait until that version is loaded
    seq: int = 0                      # increasing per hub run: a CadSession that starts late applies the last one once


class VscodeEvent(BaseModel):
    """Pushed by the Forge Bit VS Code extension so Bit can speak up on its own."""
    kind: Literal["errors", "command_finished"]
    file: str | None = None           # workspace-relative
    line: int | None = None           # 1-based, first error
    message: str | None = None        # first error
    count: int | None = None          # errors in the saved file; 0 = just fixed
    command: str | None = None        # command_finished: the command line
    exit_code: int | None = None


SCREEN_STATUS = "screen.status"


class ScreenStatus(BaseModel):
    """State of the screen bridge (screen-bridge/start.js, supervised by hub/screens). Sticky on /ws.

    starting: the child was (re)spawned and is bringing up signaling + Chrome; sharing: Chrome is capturing
    `source`; stopped: disabled, or the hub is shutting down; error: the child died and restarts are exhausted.
    """
    state: Literal["starting", "sharing", "stopped", "error"]
    source: str | None = None         # what Chrome shares ("| gatorhack-dubz", "Entire screen")
    detail: str | None = None         # last status line ("signaling: connected", "video: connected h264", an error)


class UiMode(BaseModel):
    """hub -> clients: Bit opens a workspace view (Addendum H). Unity answers with workspace.mode {mode, reason?}."""
    mode: str                         # cad | code | ml | messages; "" closes the open view


class UiHighlight(BaseModel):
    panel: str
    item_ids: list[str] | None = None
    class_idx: int | None = None


class SlackMessage(BaseModel):
    """One Slack message as the hub saw it. Payload of notify.message; also what /messages/* return."""
    channel: str
    channel_name: str
    is_dm: bool = False               # a DM to the user (channel_name is "dm")
    user: str
    user_name: str
    text: str
    ts: str                           # Slack message ts; reply thread key
    thread_ts: str | None = None
    mentions_me: bool = False         # a DM, or the text contains <@the user's id>; never for the user's own posts
    reply_count: int | None = None    # replies in its thread (from conversations.history; null on live events)
    edited: bool | None = None        # Slack marked it edited
    is_me: bool | None = None         # the user's own message: shown in history, skipped by digest / unread
    user_initials: str | None = None  # two letters for the avatar bubble
    source: Literal["slack", "imessage"] = "slack"


NotifyMessage = SlackMessage          # same name as the C# DTO


class ChannelSummary(BaseModel):
    """One row of the Slack panel's channel list (GET /messages/channels, notify.channels)."""
    id: str
    name: str                         # "#general"; a DM: the other person's display name; a group DM: names, comma-joined
    kind: Literal["channel", "private", "dm", "group-dm"]
    unread: int = 0                   # inbox messages since the channel was last seen (the user's own never count)
    mentions: int = 0                 # of those, DMs and <@me>
    last_ts: str | None = None
    last_text: str | None = None
    topic: str | None = None
    is_default: bool = False          # SLACK_DEFAULT_CHANNEL
    source: Literal["slack", "imessage"] = "slack"


class NotifyChannels(BaseModel):
    """The whole channel list, sorted: DMs / group DMs with unread first, then by last_ts desc. Sticky on /ws."""
    channels: list[ChannelSummary] = []


class Attachment(BaseModel):
    """A file on a draft (Addendum H). The hub keeps the file; clients only see what to show on the card."""
    id: str
    name: str                         # file name as it lands in Slack: rocket_v12.step
    title: str = ""                   # what it is, in words: "CAD model (STEP)"
    kind: str = "file"                # step | stl | drawing | parts | file: picks the chip's icon
    bytes: int = 0
    source: str = ""                  # where it came from: "cad rocket v12"
    path: str = Field(default="", exclude=True)  # on the hub's disk; never serialized


class Draft(BaseModel):
    """A reply Bit wrote but has NOT sent. Payload of notify.draft; Unity shows it as a Send / Cancel card.

    Nothing reaches Slack until POST /messages/drafts/{id}/send (the card's Send button) or the
    messages_confirm tool ("yes, send it"). Posting uses the user's own token, so it lands under their name.
    """
    draft_id: str
    channel: str
    channel_name: str
    text: str
    thread_ts: str | None = None
    reply_to: SlackMessage | None = None
    edited: bool = False              # the user changed the text (PATCH /messages/drafts/{id}); same draft_id re-emitted
    attachments: list[Attachment] = []  # files uploaded with the message when it is sent (Addendum H); [] = text only
    source: Literal["slack", "imessage"] = "slack"


class NotifySent(BaseModel):
    draft_id: str | None = None
    channel: str
    channel_name: str
    text: str
    ts: str
    source: Literal["slack", "imessage"] = "slack"


class NotifyDraftCancelled(BaseModel):
    draft_id: str
    reason: str = "cancelled"         # cancelled | superseded | expired


NO_NEW_MESSAGES = "You have no new messages today."


def digest_line(count: int, mentions: int) -> str:
    """The one sentence Bit says on sit-down (the greeting card's headline too). Never empty: the zero case is
    NO_NEW_MESSAGES, so the wording lives here and nowhere in Unity."""
    if count <= 0:
        return NO_NEW_MESSAGES
    msgs = "1 new message" if count == 1 else f"{count} new messages"
    if mentions <= 0:
        return f"You have {msgs}."
    if count == 1:
        return f"You have {msgs}, and it mentions you."
    if mentions >= count:
        who = "both" if count == 2 else "all of them"
        return f"You have {msgs}, {who} mention you."
    verb = "mentions" if mentions == 1 else "mention"
    return f"You have {msgs}, {mentions} {verb} you."


class NotifyDigest(BaseModel):
    """Unread summary sent when the user sits down: counts, the spoken line (never empty), newest messages (capped)."""
    count: int
    mentions: int
    line: str
    messages: list[SlackMessage] = []

    @classmethod
    def build(cls, messages: list[SlackMessage], cap: int = 4) -> "NotifyDigest":
        newest_first = sorted(messages, key=lambda m: float(m.ts), reverse=True)
        mentions = sum(1 for m in messages if m.mentions_me)
        return cls(
            count=len(messages),
            mentions=mentions,
            line=digest_line(len(messages), mentions),
            messages=newest_first[:cap],
        )


# Desk extras (hub/desk): the focus timer, notes and the movie screen. Bit adds them on top of any mode and they stay
# open across mode switches. Proposed contract additions: hub/README "Desk (timer, notes, movie)".

WidgetState = Literal["open", "minimized", "closed"]


class UiWidgets(BaseModel):
    """Which desk extras are showing. Always the whole map, so a client that connects later gets the layout."""
    timer: WidgetState = "closed"     # the timers: minimized = one dot showing the one that ends soonest
    notes: WidgetState = "closed"     # minimized = the notes dot
    movie: WidgetState = "closed"


class TimerState(BaseModel):
    """One focus timer. Unity counts down from remaining_s when the event arrives (the Quest's clock isn't the
    laptop's); the hub sends the list again on every change and when one rings. Ringing lasts until it's stopped."""
    timer_id: str
    label: str                        # "Focus" unless the user named it; unique ("tea 2")
    status: Literal["running", "paused", "ringing"]
    duration_s: float                 # the whole run, including time added
    remaining_s: float


class TimersUpdated(BaseModel):
    """Every timer: ringing first, then by time left. The first is the one the minimized dot shows. Empty = none."""
    timers: list[TimerState] = []


class Note(BaseModel):
    id: str
    text: str
    done: bool = False
    created_at: float                 # unix seconds
    updated_at: float


class NotesUpdated(BaseModel):
    """Every note, newest first. last_removed is what Undo brings back (null when there's nothing to undo)."""
    notes: list[Note] = []
    last_removed: Note | None = None


class MovieInfo(BaseModel):
    movie_id: str                     # file name in .data/media without .mp4
    title: str
    year: int | None = None
    duration_s: float | None = None
    url: str                          # hub path for ForgeNet.Url: /movie/files/<id>.mp4 (byte ranges, so seeking works)
    subtitles_url: str | None = None  # /movie/files/<id>.en.srt when there is one


class MovieState(BaseModel):
    """The movie screen. Unity's VideoPlayer keeps time and reports it (POST /movie/position); the hub estimates
    position_s in between. A deliberate jump (seek, skip, play from the start) bumps seek_rev: Unity seeks to
    position_s when seek_rev changes and otherwise leaves the player alone."""
    status: Literal["closed", "playing", "paused"] = "closed"
    movie: MovieInfo | None = None
    position_s: float = 0.0
    volume: float = 0.8               # 0..1
    seek_rev: int = 0


def _body(payload: BaseModel | dict[str, Any]) -> dict[str, Any]:
    return payload.model_dump() if isinstance(payload, BaseModel) else payload


class EventBus:
    STICKY = frozenset({"cad.model_updated", "cad.view"})  # default set replayed to clients that connect later

    def __init__(self, sticky: Iterable[str] | None = None) -> None:
        self._clients: set[WebSocket] = set()
        self._last: dict[str, dict[str, Any]] = {}
        self.sticky: set[str] = set(self.STICKY if sticky is None else sticky)
        self._current: dict[str, Callable[[], BaseModel | dict[str, Any]]] = {}

    def make_sticky(self, type_: str) -> None:
        """Replay the last `type_` payload to late joiners too (modules register their own, e.g. screen.status)."""
        self.sticky.add(type_)

    def sticky_fresh(self, type_: str, current: Callable[[], BaseModel | dict[str, Any]]) -> None:
        """Replay `type_` to clients that connect later from `current()`, not from the last payload sent: for state
        that moves on its own, like the timers' remaining time or the movie's position."""
        self._current[type_] = current

    def latest(self, prefix: str = "") -> dict[str, dict[str, Any]]:
        """Last sticky payload per type: lets a client without a WebSocket poll for state."""
        out = {t: m["payload"] for t, m in self._last.items() if t.startswith(prefix)}
        out.update({t: _body(current()) for t, current in self._current.items() if t.startswith(prefix)})
        return out

    async def publish(self, type_: str, payload: BaseModel | dict[str, Any]) -> None:
        body = _body(payload)
        message = {"type": type_, "payload": body}
        if type_ in self.sticky:
            self._last[type_] = message
        dead = []
        for ws in list(self._clients):
            try:
                await ws.send_json(message)
            except Exception:  # noqa: BLE001 - a dropped client must not break the publisher
                dead.append(ws)
        self._clients.difference_update(dead)

    async def attach(self, ws: WebSocket) -> None:
        """Start publishing to an accepted socket (replays sticky state). Someone else owns its receive loop."""
        self._clients.add(ws)
        for message in list(self._last.values()):
            await ws.send_json(message)
        for type_, current in list(self._current.items()):
            await ws.send_json({"type": type_, "payload": _body(current())})

    def detach(self, ws: WebSocket) -> None:
        self._clients.discard(ws)

    async def serve(self, ws: WebSocket) -> None:
        await ws.accept()
        try:
            await self.attach(ws)
            while True:
                await ws.receive_text()  # clients don't publish yet; keep the socket open
        except (WebSocketDisconnect, asyncio.CancelledError):
            pass
        finally:
            self.detach(ws)
