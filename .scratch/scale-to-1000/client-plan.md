> **Scope cut by the owner, 2026-09-12.** "It must support live video, and file browsing/viewing.
> Do not make it too advanced, this is just a demo client for the backend, and it is the backend
> that is important here."
>
> So C2 (the wall of simultaneous players), C3 (virtualisation for a thousand tiles) and the map in
> C4 are **out**. The client demonstrates the backend; it is not the monitoring product. What stays
> is the smallest set that keeps it working and shows what the backend now does:
>
> 1. **The token on every gRPC call.** Not a feature: the new interceptor guards `ListLive`, so
>    without this the client's stream list fails against any configured server. It is broken today.
> 2. **The classification marking**, wherever imagery is shown. A domain requirement, not polish.
> 3. **Health at a glance** on the existing tiles, from the figures already on the listing.
> 4. **KLV as text** for the stream being watched.
>
> Detection waits for the worker tier to exist. Everything below is kept as the record of what a
> real monitoring client would need, and is explicitly not the plan for this one.

# Plan: the desktop client at a thousand streams

## Destination

One WPF window against a cluster carrying a thousand live streams. A few of them are watched all
the time, on a wall of live players that survives the user clicking around. The rest are rows in a
grid that says, without a number on every tile, which of them is broken, which is marked what, and
opens any of them on a click. The explorer, the player, the server switcher and the upload flow
stay as they are.

Fixed by the repository owner:

- "Some monitored manually, the rest available if the user wants to see them." Monitored is a
  thing the user declares, not a tab they happen to have open.
- The target standard is STANAG 4609 Ed. 5, so every stream is expected to carry the MISB ST 0902
  minimum metadata set, and inside it the ST 0102 security local set. **Imagery is never shown
  without its classification marking.** Not a details-panel row: on the tile, on the wall, on the
  player, in fullscreen.
- **The desktop client stays gRPC.** REST is the surface for AI agents and command-line tools.
  Every live feature the client needs arrives on `protos/documents.proto`, and the service-side
  list of those additions is part of this plan.
- Nothing under `src/` changes for this document. It is a plan.

## What is wrong today, in one line each

| Ceiling | Cause | Fixed by |
| --- | --- | --- |
| Five hundred preview fetches a second at a thousand streams | `RefreshLiveAsync` refetches every tile's JPEG on every two-second tick, visible or not | Phase C3 |
| Five hundred JPEG decodes a second on the UI thread | `LoadBitmap` runs after the `await`, so on the dispatcher, at full resolution | Phase C3 |
| A thousand `Image` controls, none virtualised | `WrapPanel` as the items panel; a `ListBox` only virtualises with a virtualizing panel | Phase C3 |
| Nine property-change notifications per stream per tick | `DocumentItem.Apply` raises everything whether or not it moved | Phase C3 |
| A thousand "Live stream started" status lines on connect | `SetStatus` per new stream | Phase C3 |
| In a cluster, most tiles show a "VID" icon, not a picture | gRPC `DownloadLivePreview` deliberately does not proxy to the owner; only the REST route does | Service item 3, Phase C3 |
| A stream can be a fifth delivered and look fine | `packetsLost` / `packetsDropped` exist on `LiveStream` and on `GET /api/live`, and are not on the proto | Service item 1, Phase C0 |
| No marking anywhere | The client predates KLV | Service item 2, Phase C1 |
| One player, one host, hard-wired | `_player`, `PlayerHost`, the transport controls, the position timer and the shortcuts all assume exactly one | Phase C2 |
| Watching is a side-effect of selection | Click another tile and the stream you were watching is gone | Phase C2 |

None of these is a defect at ten streams, which is what the client was built for. All of them are
at a thousand.

## What is kept, and why this is not a rewrite

Say it plainly, because a plan this long reads as one otherwise.

- **The explorer.** Documents tab, name search, type and age filters through the collection view,
  sorted insertion, thumbnails over the change feed, optimistic upload tiles, delete with rollback.
  All of it works and none of it is touched. The documents grid stays a `WrapPanel`: a document
  list of a few thousand is a different problem from a thousand tiles that each repaint every two
  seconds, and the README already says why filtering is done in memory.
- **The player and its controls.** `StartPlayback`, the scrub bar, skip, volume, speed,
  fullscreen, the keyboard map, `OnOpenCompleted` saying why a stream would not open. The wall adds
  players; it does not replace this one.
- **`FlyleafEngine`.** `EnsureStarted` and `TuneFor` are right and stay. `TuneFor` is per-player
  already, which is what makes the wall cheap.
- **The server switcher and `ServerList`.** Untouched, unless the token question below is closed.
- **`DocumentsApi`, all of it.** One channel, one generated client, one contract file compiled
  by both sides. Every new live call is one more method in the same class, in the same style as
  `ListLiveAsync` and `RecordLiveAsync`, including the `Unimplemented` catch that lets the client
  run against an older server.
- **Streams and documents in separate tabs.** Still right. What changes is what a stream tile
  fetches and when.
- **Polling the live list.** Two seconds, one call, whole list. At a thousand streams that is one
  response of roughly 150 KB every two seconds, which is nothing, and the health figures are per
  heartbeat anyway. There is no reason to build a push feed for streams. What has to stop is the
  thousand *other* calls each tick makes.

## The decision, made: gRPC, and what it costs and buys

The first draft of this plan asked whether the client should speak REST for the live surface,
because that is where the health figures, the cross-replica preview, KLV and detection were all
landing, and recommended it. **The owner decided against, and the reason is a real one:** one
connection, one surface, one contract file the server and the client both compile so the two can
never drift, and the desktop client stays exactly what the README says it is. REST is for agents
and the command line. A client with two transports is a client with two ways to be wrong about
the same stream, and a token on one of them and not the other, which is precisely the state the
service is in today (see below).

What it costs is that **every phase below has a service-side item in front of it**, and the
client cannot start any of them until its item has landed, because the client compiles the same
`documents.proto`. The items are listed in phase order under "Service-side gRPC work, in order".
Most are a field or two on `LiveStreamMessage`; three are new RPCs. None is large, but they are
sequential with the client work rather than parallel to it, and the estimates below say so.

### The token, checked rather than assumed

The REST live routes are guarded: `LiveStreamsController.Guard` refuses anything without a
matching `X-Storage-Token` when `Live__Token` is set, and the README says the token is "required
in X-Storage-Token on every API call". **The gRPC live RPCs are not guarded at all.**
`DocumentsGrpcService` has no token parameter, no metadata read and no interceptor; its
`RequireLive()` checks only `Live__Enabled`, and `Program.cs` maps the service with no
authorization policy. `SnapshotLive`, `RecordLive` and `StopLiveRecording` are reachable by anyone
who can reach the gRPC port.

So under gRPC the client needs no token today, and the service asks for none. That is a service
gap, not a client one, and it is not this plan's to fix; it is written down here because the
README claims otherwise. If the owner closes it, the shape is a server interceptor reading an
`x-storage-token` metadata entry and refusing the live RPCs without it, and on the client a
`Token` on `ServerEntry`, a field in the server box, and the header on every call from
`DocumentsApi`. A couple of hours each side, and it should land before the wall (C2) rather than
after, since that is the first phase that makes the client a permanent presence on the port.

## Order

```
C0  Health on the tiles that exist        service: half a day; client: a day, after it
C1  The marking                           first-class; blocks on the KLV route and its proto fields
C2  Monitored, and the wall               a spike on multiple players, then the feature; no service work
C3  The grid at a thousand                virtualisation, preview on visibility; needs preview proxying
C4  KLV as text                           ST 0902 panel for the stream being watched; new RPC; no map
C5  Detection                             toggle and overlay; two new RPCs against an assumed shape
C6  Retention in the explorer             one field on ProviderResponse
```

C0 first because it is the cheapest fix to the worst defect: the service can say a stream is
healthy while delivering a fifth of it, and the figures that say otherwise are already computed.
C1 next because the owner ranks it ahead of anything decorative, and it needs the same list-row
plumbing as C0. C2 before C3 because the wall is what the people who use this all day look at,
the multi-player question has to be answered by running it, and it is the one phase with no
service dependency, so it can run while the proto items for C3 and C4 are being built. C3 is the
largest piece and the only one that is actually about a thousand. C4 and C5 are per-watched-stream
features and work as well on a grid of ten as of a thousand, so they follow. C6 is small.

C1 and C4 depend on the KLV work, which is being built in parallel and is landing as a REST route
first. What the client needs from it in proto form is stated under "Service-side gRPC work" so the
KLV agent can read it in one place.

---

## Service-side gRPC work, in order

Every item is in `protos/documents.proto` plus its mapping in `DocumentsGrpcService.cs`; the
client regenerates on build because the `.csproj` links the same file. Numbers continue from
`LiveStreamMessage`'s field 13 and `ProviderResponse`'s field 3.

| # | For | What | Shape | Size |
| --- | --- | --- | --- | --- |
| 1 | C0 | `packets_lost`, `packets_dropped` on `LiveStreamMessage` | Two `int32` fields, two lines in `ListLive` | An hour, plus a test asserting they round-trip |
| 2 | C1 | `has_klv`, `last_klv_at`, and the ST 0102 marking on `LiveStreamMessage`: `classification` as an enum, the marking text, `classifying_country`, `releasing_instructions`, `caveats` | Fields on an existing message, mapped from whatever the KLV agent puts on `LiveStream` | Half a day once `LiveStream` carries them; nothing until then |
| 3 | C3 | `DownloadLivePreview` proxies to the owning replica, as the REST route does through `LivePeerProxy` | No proto change; a branch in the existing RPC | Half a day. Without it a cluster's tiles are icons whatever the client does |
| 4 | C4 | `GetLiveKlv(LiveStreamName) returns (LiveKlvMessage)`: the ST 0902 minimum set as typed fields, the ST 0102 set, the packet's timestamp, and the raw packet as `bytes` | **New RPC**, new message | A day, mirroring the REST route's body |
| 5 | C5 | `SetLiveDetection(SetLiveDetectionRequest) returns (LiveStreamMessage)` with `enabled` and a rate; a `detection` sub-message on `LiveStreamMessage` (enabled, rate, worker); `GetLiveDetections(LiveStreamName) returns (LiveDetectionsMessage)` with class, score, normalised box and frame timestamp per detection | **Two new RPCs**, one new field | Unknowable until Phase 8 has a wire format; the shape here is the client's assumption |
| 6 | Optional | `CreateManualLive` and `DropLive`, if the client is to create pulled streams and end streams | **Two new RPCs** | Not needed by any phase in this plan. Listed because the REST surface has them and a client that stays gRPC-only can never reach them otherwise |
| 7 | C6 | `retention_enabled`, `retention_max_age_days` on `ProviderResponse` | Two fields, mapped from `RetentionOptions` | An hour |

Items 1 and 3 can be done today by anyone. Item 2 waits on the KLV agent's `LiveStream` fields.
Item 4 waits on the KLV route. Item 5 waits on Phase 8. Item 7 waits on nothing.

---

## Phase C0: Health on the tiles that exist

**Service side first: item 1, about half a day including the test.** The client cannot compile
against fields that are not in the proto, so this is a half-day the client waits, not a half-day
alongside. Client side, a day. Call it a day and a half, sequential.

### Build

- `packets_lost` and `packets_dropped` reach the tile through `DocumentItem.Apply`.
- **One mark per tile, no number.** A 3 px bar along the bottom of the preview frame, the full
  width of the tile: green when both figures are zero, amber when only `packets_dropped` moved,
  red when `packets_lost` moved, grey when the stream is interrupted. It reads across a grid of a
  thousand at a glance because it is colour and position, not text. `Startable == false` and
  `CeilingBinding` fold into amber: they already exist as warnings and are the same kind of "look
  at this, it is not dead".
- **Hold red for three beats.** The figures are per heartbeat, so a single beat with loss would
  otherwise blink once and be gone before anyone looked. The hold is a heuristic with a ceiling:
  `ponytail: three beats, make it a setting if operators disagree`. The numbers themselves are never
  held; only the colour is.
- The tooltip and the `Details` line carry the numbers: `Live  312 lost  40 dropped  1.2 MB`.
- The metadata panel gains `Lost (last beat)` and `Dropped (last beat)` rows, and the preview
  title says `(degraded)` beside `(live from pod-3)`.
- Amber and red must not be the only difference. The bar gets a one-letter glyph at its left end
  (`L`, `D`) for anyone who cannot tell the colours apart, and the tooltip always has the words.
- The bar draws whether or not the tile has a picture, so C0 does not wait on item 3.

### Done when

The baseline's 250-stream overload case is run against this client. Today it shows 250 tiles that
look fine. After this phase the degraded ones are red and an operator names them from the grid
without opening anything.

---

## Phase C1: The marking

STANAG 4609 makes the ST 0902 minimum set mandatory, and the ST 0102 security local set is part
of it. A client that shows the picture and not the marking is wrong in this domain, so this is a
requirement with its own done-when and it precedes anything decorative.

### What is assumed about the wire

The KLV agent is decoding the ST 0902 set now, into a REST route first. This phase assumes:

- `LiveStream` gains `hasKlv`, a last-KLV-packet time, and the ST 0102 fields, and item 2 puts
  them on `LiveStreamMessage`. **The marking has to be on the list.** A thousand tiles cannot
  each make a KLV call to find out what to print, and a tile that shows a picture without a
  marking is the exact failure this phase exists to prevent. This is the one thing the client
  plan asks of the KLV agent beyond the route itself.
- The full ST 0102 text for the banner comes from the same fields; item 4's `GetLiveKlv` is not
  needed for the marking, only for C4.

### Build

- **Every surface that shows a picture shows the marking.** Four surfaces today, plus the ones C2
  adds:
  - The tile: a chip in the top-right corner, opposite the LIVE badge, carrying the short form
    (`U`, `R`, `C`, `S`, `TS`, or the deployment's own scheme) on the classification's colour.
    Comes from the list, so it costs nothing per tile.
  - The single player: a banner across the top of the video area, full width, the full marking
    text (`SECRET // REL NATO`, whatever the set says) on the classification's colour.
  - Every wall player (C2): the same banner, smaller.
  - Fullscreen. See the trap below.
- **Absence is shown, never blank.** A stream with no KLV, or KLV without an ST 0102 set, shows
  `UNMARKED` on a warning colour on every one of those surfaces. It is not an error state and it
  is not hidden: a plain camera with no metadata is exactly the picture that must not be mistaken
  for an unclassified one. `has_klv == false` is `UNMARKED`; `has_klv == true` with no security
  set is also `UNMARKED`; a last-KLV time older than a few seconds is `UNMARKED (stale)`, because
  a marking that stopped arriving is not a marking. An older server that does not send the fields
  at all reads as `UNMARKED` too, which is proto3's default doing the right thing for once.
- **Colours are a table, not a rule.** Marking colours are a convention of the deployment, not of
  the standard, so they live in one small map from classification to brush, with the text always
  present and the colour never the only signal.
- **Fullscreen is the trap.** `FlyleafHost` renders in its own Surface window, with an Overlay
  window on top of it; that is how FlyleafLib gets round WPF airspace. Anything drawn in the main
  window over the host is not over the video in fullscreen, because the host's own windows are
  what go fullscreen. The banner therefore has to live in the host's overlay, not beside the host.
  Whether the overlay follows the surface into fullscreen is the thing to verify on the first day
  of this phase. If it does not, `ToggleFullScreenOnDoubleClick` and the `F` key are disabled
  for a stream that is not `UNCLASSIFIED`, and that is stated in the UI rather than silently.
- **Snapshots and recordings inherit the question.** A document made from a marked stream is
  itself marked imagery. `DocumentMessage.metadata` is an ordered list of key/value pairs already,
  so if the service writes the marking there when it takes the snapshot or starts the recording,
  the client can show a chip on the document tile with no proto change at all. Whether the service
  writes it is a service decision. Until it does, the explorer shows nothing on documents, and this
  plan says so rather than pretending the problem stops at live.

### Done when

- A stream with an ST 0102 set shows its marking on its tile, on the single player, on every wall
  player, and in fullscreen, or fullscreen is refused for it.
- A stream without one shows `UNMARKED` on the same surfaces.
- Running against a server without item 2 leaves no surface blank: every tile reads `UNMARKED`.
- Someone who cannot distinguish the colours can still read every marking.

---

## Phase C2: Monitored, and the wall

No service work. This is the phase to run while items 2, 3 and 4 are being built.

### What `FlyleafEngine.cs` and the package say about several players at once

Read for this plan, because multiple simultaneous players is the thing FlyleafLib had to be
checked for.

- **Nothing in `FlyleafEngine.cs` assumes one player.** `EnsureStarted` starts the engine once
  per process, which is what the library requires; `TuneFor` takes a `Player` and writes to that
  player's own `Config.Demuxer` and `Config.Decoder`, so each player on the wall is tuned
  independently and a stored recording in the main pane does not untune a live player beside it.
- **The library is built for it.** `Engine.Players` is documented as "List of active Players";
  `EngineConfig.UIRefresh`, which the client already sets, "activates Master Thread to monitor all
  the players"; and `FlyleafSharedOverlay` exists as "Shared Overlay on top of multiple
  FlyleafHosts". Several hosts in one window is a supported layout, not a trick.
- **The single-player assumption is entirely in `MainWindow`.** One `_player`, one `PlayerHost`,
  one `OpenCompleted` handler, one `_positionTimer`, and transport controls, shortcuts and
  fullscreen that reach for `_player` and `PlayerHost` by name. That is a `MainWindow` refactor,
  not an engine one.
- **What has not been measured, and the spike measures:**
  - Decode cost per player. `VideoConfig.VideoAcceleration` is the D3D11 hardware decode
    switch. The client never sets it, so the wall runs on whatever the library defaults to. Set it
    explicitly, then measure four, six and nine 1080p H.264 players on the target laptop. A
    software decode of 1080p is roughly a core each; a wall of nine on four cores does not work
    and the spike has to say so with a number.
  - Join cost. Each wall player is its own SRT caller to the consumption port, and each pays the
    one-second probe `TuneFor` sets, in parallel. Nine players joining together is nine
    connections through their owners; README says viewers funnel through the owner, so a wall of
    nine streams from one pod is nine relays off that pod. Fine at nine; the spike confirms the
    wall comes up in about the single-player time, not nine times it.
  - Audio and keys. Every host has `KeyBindings="Both"` and every player has audio. On the wall,
    all but the focused player are muted and the wall hosts have no key bindings, or `Space` on
    the window pauses whichever host thinks it has focus.
  - The overlay in each host, for the C1 banner and later the C5 boxes.

### Build

- **Monitored is a set of names, per server, on disk.** `%APPDATA%\StorageDemoClient\` beside
  `servers.json`, keyed by server address. A star on the tile, a context-menu item, and a key on
  the selected tile toggle it. Nothing on the service knows about it; it is this user's list.
- **The wall is where monitored streams play.** A third tab beside Streams and Documents, a
  uniform grid of `FlyleafHost`s, one per monitored stream, each with its C1 banner and its C0
  health bar drawn in the host's overlay. Clicking a wall player makes it the focused one: audio
  unmuted, transport controls (the existing `PlayerControls` border, now bound to "the focused
  player" rather than `_player`) act on it. Selecting a stream in the Streams tab still opens the
  single player in the main pane as today; that path is unchanged.
- **The wall survives the rest of the window.** Switching tabs, selecting documents and opening a
  recording in the main pane do not stop wall players. Only unmonitoring a stream, the stream
  ending, or the server changing does. A wall player whose stream is interrupted stays, dimmed,
  with the last frame, exactly as the tile does; the stream resuming reconnects it.
- **A ceiling with a number.** `ponytail: wall capped at 6 players by default, from the spike's
  measurement; a setting, not a constant, because the target laptop is not the only machine`.
  Monitoring a seventh is refused with a sentence, not silently ignored.
- **Monitored tiles sort first** in the Streams grid and their previews are not fetched: they are
  playing, and a JPEG of something already on screen is waste.

### Done when

- The spike's numbers are written down: CPU at four, six and nine players on the target machine,
  with hardware decode on and off, and time from opening the wall to all players showing a frame.
- Six streams are monitored, the user opens a six-hour recording in the main pane and scrubs it,
  switches to Documents, uploads a file, and comes back to six live players that never stopped.
- Closing the client and reopening it against the same server brings the same six back.
- Every wall player shows its marking.

---

## Phase C3: The grid at a thousand

The arithmetic first, because it is what the phase is answering. A thousand tiles, each refetching
a JPEG every two seconds, is five hundred requests a second from one client. Under the current
`DownloadLivePreview` most of them return NOT_FOUND in a cluster anyway, since only the owning
replica answers; once item 3 makes the RPC proxy the way the REST route does, each miss becomes a
pod-to-pod request, so the client would be generating five hundred cross-pod requests a second
against a service whose receive thread is the thing it is short of. Then each JPEG that does
arrive is decoded on the dispatcher at full size. It does not matter how good the virtualisation
is if that stays.

**Item 3 goes first.** Without it the grid in a cluster is icons, and a grid of a thousand icons
is not worth virtualising.

### Build

**The list.** One `ListLive` every two seconds, as now. Diff it against a `Dictionary` of tiles
by name instead of `FirstOrDefault` per stream; `Apply` compares before raising and raises only
what moved, which at a thousand streams is usually `Packets`, `Bytes` and the health pair. The
`SetStatus` per started stream becomes one line, `418 streams on air`, and a per-stream line only
when the count changed by one or two. `_streams.Insert(0, ...)` newest-first stays; it is a list
insert, not a sort.

**Virtualisation.** The `ListBox` is virtualising already; the `WrapPanel` is what stops it.
Three options, in the order to try them:

1. The `VirtualizingWrapPanel` NuGet package. One assembly, MIT, drop-in replacement for the
   panel, keeps the tile look. One dependency is cheaper than the next two options.
2. A `VirtualizingStackPanel` of rows, each row a fixed number of tiles chunked in the view
   model. Native, no dependency, breaks on resize unless re-chunked.
3. Give up the grid for streams and make the Streams tab a virtualised list: one row per stream
   with the health bar, the marking chip, the name, the owner and a small preview. Honest fallback:
   at a thousand items a list is arguably the better shape anyway, and it is what every other
   thing that lists a thousand of something looks like.

Pick 1; fall back to 3, not 2. The Documents tab keeps its `WrapPanel`.

**Preview on visibility.** With a virtualising panel in recycling mode, the only tile containers
that exist are the visible ones plus a small cache. The container's `Loaded` and `Unloaded` events
are therefore the visibility signal, and WPF gives them for free. A tile fetches its preview when
its container loads and every two seconds while it stays loaded; it stops when the container
unloads. At three tiles across the sidebar and eight rows visible that is about twenty-four
previews every two seconds, twelve a second, whatever the list length. A tile nobody has scrolled
to fetches nothing but its row in the list. Each preview is one server-streaming `DownloadLivePreview`
call, which is what it is today; a dozen a second on one HTTP/2 channel is nothing.

**The picture stays when the tile scrolls away.** It is not refreshed, but it is not dropped
either. With `DecodePixelWidth` set to the tile's width, a decoded preview is about 30 KB; a
thousand of them is 30 MB, and an LRU to save 30 MB is not worth its own bugs. `ponytail: keep
every preview ever fetched; add an LRU if the grid goes to ten thousand`. A tile that scrolls back
shows its last picture immediately and refreshes on the next tick, which is better than a flash of
icon.

**Decode off the dispatcher.** `LoadBitmap` already freezes the bitmap; move the decode into
`Task.Run` with `DecodePixelWidth`, and assign on the dispatcher. That is the change that keeps
the window responsive; the fetch count is what keeps the service responsive.

**Sort and filter for streams.** The Documents tab's search box moves up so both tabs have one,
and the Streams tab gets a `Problems only` checkbox that filters to amber and red. Both run through
the collection view exactly as the document filters do. Monitored first, then degraded, then name;
"newest first" was right at ten streams and is noise at a thousand.

**What is not built.** No push feed for the live list, no incremental list RPC, no server-side
paging. A 150 KB poll every two seconds is fine and everything else is cheaper on the client.

### Done when

- The load rig's thousand-stream case, or the largest it will run, against this client: the
  window scrolls without stutter, the dispatcher is under ten percent between ticks, and the
  service sees about a dozen preview requests a second from the client whatever the list length.
- Against two replicas, a tile owned by the other replica shows a picture.
- A tile scrolled into view shows a picture within one tick.
- `Problems only` with the baseline's 250 overloaded streams shows the degraded ones and nothing
  else.
- Memory for the client after an hour on a thousand streams is flat, measured, not assumed.

---

## Phase C4: KLV as text

For the stream being watched, the ST 0902 minimum set, shown as text. No map in this phase.
Needs item 4, the `GetLiveKlv` RPC, which mirrors the REST route the KLV agent is building.

### Build

- A `Sensor` group in the metadata panel under the player, and a small version of it on the
  focused wall player's overlay. Rows, in the order the set lists them: timestamp, mission id,
  platform heading, pitch and roll, sensor latitude, longitude and altitude, horizontal and
  vertical field of view, sensor relative azimuth, elevation and roll, slant range, frame centre
  latitude, longitude and elevation, and the UAS LS version. The security set is C1's banner, not
  a row here.
- Polled with `GetLiveKlv` once a second for the selected stream and for each wall player, six or
  seven calls a second at most. It is a per-watched-stream fetch; nothing about it scales with
  the grid.
- The raw packet the RPC returns goes behind a `Raw` expander as hex, for the day a field is
  wrong and someone needs to see what the sensor actually sent.
- Position and frame centre get a `Copy` beside them as `lat, lon`, because pasting into whatever
  map the operator already has open is the map for now.

### Why no map

A map is a tile source, a projection, an offline story for a network that will not reach one, and
a dependency the client does not have. It is a project. Sensor position and frame centre are the
two fields it would use, they are shown as text and copyable here, and when the map is wanted the
values are already in the view model. Deciding to build it is separate from this plan.

### Done when

A stream carrying ST 0902 shows every field in the set updating once a second under the player,
and the same fields for a wall player when it is focused. A stream without KLV shows the group
collapsed with `No KLV on this stream`, not an empty panel. Against a server without item 4 the
group is absent, through the same `Unimplemented` catch `ListLiveAsync` uses.

---

## Phase C5: Detection

Phase 8 is planned, not built, and its wire format does not exist. What can be planned is the
client side of turning it on and seeing results, with the assumptions written down so they can be
struck through when the real shape lands. Item 5 is the proto form of those assumptions.

### What is assumed

- **The toggle** is `SetLiveDetection` with the stream name, `enabled`, and the per-stream
  detection rate Phase 8 says is a setting. The same shape as `RecordLive`: a person and a
  detector are one caller.
- **The state** comes back on `LiveStreamMessage` as a `detection` sub-message (enabled, rate,
  worker), so the list says which streams are on without another call per tile.
- **The results** come from `GetLiveDetections` per stream, returning the most recent set: for
  each detection a class, a score, a box in normalised frame coordinates, and the presentation
  timestamp of the frame it was found in. Whether that is a unary call or a server stream is
  unknown; the client polls at the detection rate until told otherwise, and a server-streaming
  version is a one-method change in `DocumentsApi` later.

### Build

- A `Detect` toggle button beside `Record` and `Snapshot`, enabled for a live stream, reading
  `Detecting` while on. A `DET` mark in the tile's `Details` line, from the list.
- **Boxes over the picture**, drawn in the host's overlay so they survive fullscreen, as
  rectangles in normalised coordinates scaled to the video rectangle the host reports. Class and
  score as a label. For the selected stream and for the focused wall player only; never on tiles.
- **The boxes will trail the picture, and the plan says so.** A viewer joins at the live edge and
  sits behind it by SRT latency plus the player's buffer; a worker sees the same stream on a
  different path with different latency. Without the frame's timestamp in the results, and the
  player's clock to match it against, a box is drawn "as of now" and lags a moving object by the
  difference, which will be visible. With the timestamp, the client holds a short queue and draws
  the set whose timestamp is nearest the player's current time. That alignment is the part that
  cannot be designed until the wire format exists, and it is the part that decides whether the
  overlay is useful or merely present. The first cut ships without it and is labelled as such.

### What cannot be planned yet

The wire format, whether results are pushed or polled, what a detection carries beyond a box, and
whether the worker will draw boxes into the preview JPEG itself, which would give every tile
detections for free and is worth raising with Phase 8 before the client draws anything.

### Done when

Turning detection on for a stream survives a client restart, because the state is the service's;
boxes appear over the picture for that stream in the main pane, on the wall and in fullscreen; and
the lag between a box and the object it is on is measured and written down against the assumed
format, so the alignment work has a number to beat.

---

## Phase C6: Retention in the explorer

Documents produced by live streaming expire after `Retention__MaxAgeDays`. The explorer could say
when, and cannot today: nothing on `DocumentMessage` or `ProviderResponse` exposes the age
ceiling or a per-document expiry. Item 7 is two fields on `ProviderResponse`, which the client
already calls on connect.

### Build

From `retention_max_age_days` and `CreatedAt` the client computes `expires in 3 days` on the
tile's `Details` line and in the metadata panel, for documents whose metadata marks them as a
recording or a snapshot, which is the mark retention itself looks for. A document uploaded by hand
shows nothing, because retention leaves it alone.

A per-document expiry field would be more accurate and is not worth asking for until per-stream
overrides exist, which Phase 10 says nothing has asked for.

### Done when

A recording older than the configured age minus a day shows `expires tomorrow`; an uploaded file
of any age shows nothing; with retention off, or against a server without item 7, nothing shows
anywhere.

---

## Documentation, as each phase lands

- `README.md`, "The desktop client": the wall, monitored streams, the marking, the health bar
  and what its colours mean.
- `README.md`, "Watching a stream": the paragraph describing the tiles is rewritten for
  visibility-gated previews.
- `README.md`, "Running it": the sentence claiming the token is required on every API call is
  corrected to say REST, or the gRPC interceptor lands and it becomes true.
- `README.md`, "API": the new RPCs as they land, in the existing list.
- This file: the assumptions in C1, C4 and C5 struck through as the proto items land.

## Acceptance, end to end

Against the rig, at the largest stream count it will hold, two replicas, with six streams
monitored and the baseline's overload case induced on one pod:

| Claim | Measurement | Target |
| --- | --- | --- |
| A degraded stream is visible without opening it | Overload one pod; count red tiles | Every stream on that pod red within three beats, no others |
| No imagery without a marking | Walk every surface with a marked stream and an unmarked one | Marking or `UNMARKED` on tile, player, wall, fullscreen; never blank |
| The wall does not depend on the rest of the window | Six monitored, open a recording, upload, switch tabs, come back | Six players never stopped |
| The client does not load the service | Count preview requests at the service from one client | About a dozen a second, independent of list length |
| A cluster's tiles show pictures | Two replicas, tiles owned by each | Every visible tile has a picture within one tick |
| The window stays usable at a thousand | Scroll the grid end to end | No stutter; dispatcher under ten percent between ticks |
| KLV is readable | Select a stream with ST 0902 | Every field in the set, updating once a second |
| An older server still works | Point the client at a build without items 1 to 7 | Tiles unmarked, no health bar, no KLV group, no crash |
