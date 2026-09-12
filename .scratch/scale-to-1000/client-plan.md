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
- Nothing under `src/` changes for this document. It is a plan.

## What is wrong today, in one line each

| Ceiling | Cause | Fixed by |
| --- | --- | --- |
| Five hundred preview fetches a second at a thousand streams | `RefreshLiveAsync` refetches every tile's JPEG on every two-second tick, visible or not | Phase C3 |
| Five hundred JPEG decodes a second on the UI thread | `LoadBitmap` runs after the `await`, so on the dispatcher, at full resolution | Phase C3 |
| A thousand `Image` controls, none virtualised | `WrapPanel` as the items panel; a `ListBox` only virtualises with a virtualizing panel | Phase C3 |
| Nine property-change notifications per stream per tick | `DocumentItem.Apply` raises everything whether or not it moved | Phase C3 |
| A thousand "Live stream started" status lines on connect | `SetStatus` per new stream | Phase C3 |
| In a cluster, most tiles show a "VID" icon, not a picture | gRPC `DownloadLivePreview` deliberately does not proxy to the owner; only the REST route does | The decision below |
| A stream can be a fifth delivered and look fine | `packetsLost` / `packetsDropped` exist on `LiveStream` and on `GET /api/live`, and are not on the proto | Phase C0, after the decision |
| No marking anywhere | The client predates KLV | Phase C1 |
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
- **The server switcher and `ServerList`.** It gains one field. See the decision.
- **`DocumentsApi` for documents.** List, get, download, upload, delete, watch, thumbnails: gRPC,
  as documented. Nothing here changes.
- **Streams and documents in separate tabs.** Still right. What changes is what a stream tile
  fetches and when.
- **Polling the live list.** Two seconds, one call, whole list. At a thousand streams that is one
  response of roughly 150 KB every two seconds, which is nothing, and the health figures are per
  heartbeat anyway. There is no reason to build a push feed for streams. What has to stop is the
  thousand *other* calls each tick makes.

## The decision before any of this starts

**The client is gRPC-only by design, and the live surface has moved to REST.** `DocumentsApi.cs`
says it in its first comment: "gRPC only: no REST call is made from here, the REST surface exists
for curl and Swagger." That was true and good when live streaming was five RPCs. It is no longer
where the service is putting things:

| The client needs | gRPC `ListLive` / `DownloadLivePreview` | REST `/api/live` |
| --- | --- | --- |
| `packetsLost`, `packetsDropped` (Phase 9) | Not on the proto, not mapped in `DocumentsGrpcService.ListLive` | Yes: `LiveStream` is serialised as-is |
| A preview for a stream another replica owns | No, on purpose: "the REST endpoint proxies across replicas; this one deliberately does not" | Yes |
| KLV, ST 0902 fields, the ST 0102 marking | Nothing planned | `GET /api/live/klv/{name}`, being built now |
| "has KLV", last KLV packet time on the list | Would need proto fields and mapping | Free, the moment `LiveStream` gains them |
| The detection toggle (Phase 8) | Nothing planned | Will be REST; the controller's own comment says the live control plane is "REST rather than gRPC on purpose" |
| Manual streams, drop a stream | No | Yes |

Two ways out, and the owner picks:

**A. Stay gRPC-only and extend the proto each time.** Two `int32` fields on `LiveStreamMessage`
and two lines of mapping gets the health figures today, an hour of work. But it is an hour per
feature for the life of the client, and the client is then always one proto change behind the two
agents building KLV and detection as REST. It also leaves the cross-replica preview gap unless
`DownloadLivePreview` is taught to proxy, which the service chose not to do.

**B. The client speaks REST for the live surface and keeps gRPC for documents.** `HttpClient` and
`System.Text.Json`, both in the box. And the DTO already exists: the client references
`StorageDemo.Core` today for content types, and `StorageDemo.Core.Streaming.LiveStream` is the
record the REST route serialises. `GET /api/live` deserialises straight into
`LiveStatusResponse(Transports, Streams)` with no client-side mirror to keep in step. What it costs
is one real thing: the REST live routes are guarded by `X-Storage-Token`, and the client has no
notion of a token. `ServerEntry` gains an optional `Token`, the server box gets a field for it, and
the client sends it on live calls. That is also simply correct: today the client talks to the live
control plane without one because gRPC does not check.

The recommendation is B. It is smaller over any horizon longer than a week, and it is the only
option that makes the tiles show pictures in a cluster. It reverses a documented principle, so it
is the owner's call, and **nothing below starts until it is made**: Phase C0 is "a day" under B
and "an hour plus a proto change" under A, and everything from C1 on assumes B.

Under B, `DocumentsApi` keeps its name and its gRPC half; a `LiveApi` beside it holds the REST
half, and the comment at the top of `DocumentsApi` is rewritten to say which surface is which and
why.

## Order

```
C0  Health on the tiles that exist        a day, once the decision is made
C1  The marking                           first-class; blocks on the KLV route landing
C2  Monitored, and the wall               a spike on multiple players, then the feature
C3  The grid at a thousand                virtualisation, preview on visibility, the poll shape
C4  KLV as text                           ST 0902 panel for the stream being watched; no map
C5  Detection                             toggle and overlay, against an assumed wire format
C6  Retention in the explorer             blocked on the service exposing a date
```

C0 first because it is the cheapest fix to the worst defect: the service can say a stream is
healthy while delivering a fifth of it, and the figures that say otherwise are already computed.
C1 next because the owner ranks it ahead of anything decorative, and it needs the same list-row
plumbing as C0. C2 before C3 because the wall is what the people who use this all day look at,
and the multi-player question has to be answered by running it, not by reading. C3 is the
largest piece and the only one that is actually about a thousand. C4 and C5 are per-watched-stream
features and work as well on a grid of ten as of a thousand, so they follow. C6 is small and
blocked.

C1 and C4 depend on the KLV route, which is being built in parallel. Everything in them that
touches the wire is stated as an assumption, and what the client asks of the route is listed in
one place so the KLV agent can read it.

---

## Phase C0: Health on the tiles that exist

A day. Nothing structural.

### Build

- `LiveStream.PacketsLost` and `PacketsDropped` reach the tile. Under B they are already in the
  record the client deserialises; under A they are two proto fields and two mapping lines.
- **One mark per tile, no number.** A 3 px bar along the bottom of the preview frame, the full
  width of the tile: green when both figures are zero, amber when only `packetsDropped` moved,
  red when `packetsLost` moved, grey when the stream is interrupted. It reads across a grid of a
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

The KLV agent is decoding the ST 0902 set now. This phase assumes:

- `GET /api/live/klv/{name}` returns, among the rest, a `security` object with the ST 0102
  fields the set carries: `classification` (the enumeration, plus its text), `classifyingCountry`,
  `releasingInstructions`, `caveats`, and the ST 0102 version. Names are assumed camelCase; the
  exact ones are a find-and-replace when the route lands.
- **The marking is also on the list.** `LiveStream` is gaining `hasKlv` and a last-packet time;
  it needs the classification beside them. A thousand tiles cannot each call the KLV route to
  find out what to print, and a tile that shows a picture without a marking is the exact failure
  this phase exists to prevent. This is the one thing the client plan asks of the KLV agent, and
  it is listed again under "Asks of the KLV route" below.

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
  for an unclassified one. `hasKlv == false` is `UNMARKED`; `hasKlv == true` with no security set
  is also `UNMARKED`; a last-KLV time older than a few seconds is `UNMARKED (stale)`, because a
  marking that stopped arriving is not a marking.
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
  itself marked imagery. The client can show a chip on a document tile if the document's
  metadata carries the marking; whether the service writes it there is a service decision and is
  listed under asks. Until it does, the explorer shows nothing on documents, and this plan says so
  rather than pretending the problem stops at live.

### Done when

- A stream with an ST 0102 set shows its marking on its tile, on the single player, on every wall
  player, and in fullscreen, or fullscreen is refused for it.
- A stream without one shows `UNMARKED` on the same surfaces.
- Pulling the marking out of the list response leaves no surface blank: the client falls back to
  `UNMARKED` rather than to nothing.
- Someone who cannot distinguish the colours can still read every marking.

---

## Phase C2: Monitored, and the wall

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
gRPC path most of them would return NOT_FOUND in a cluster anyway, since only the owning replica
answers; under REST each miss is proxied pod-to-pod, so the client would be generating five hundred
cross-pod requests a second against a service whose receive thread is the thing it is short of.
Then each JPEG that does arrive is decoded on the dispatcher at full size. It does not matter how
good the virtualisation is if that stays.

### Build

**The list.** One `GET /api/live` every two seconds, as now. Diff it against a `Dictionary` of
tiles by name instead of `FirstOrDefault` per stream; `Apply` compares before raising and raises
only what moved, which at a thousand streams is usually `Packets`, `Bytes` and the health pair.
The `SetStatus` per started stream becomes one line, `418 streams on air`, and a per-stream line
only when the count changed by one or two. `_streams.Insert(0, ...)` newest-first stays; it is a
list insert, not a sort.

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
to fetches nothing but its row in the list.

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

**What is not built.** No push feed for the live list, no incremental list endpoint, no
server-side paging. A 150 KB poll every two seconds is fine and everything else is cheaper on the
client.

### Done when

- The load rig's thousand-stream case, or the largest it will run, against this client: the
  window scrolls without stutter, the dispatcher is under ten percent between ticks, and the
  service sees about a dozen preview requests a second from the client whatever the list length.
- A tile scrolled into view shows a picture within one tick.
- `Problems only` with the baseline's 250 overloaded streams shows the degraded ones and nothing
  else.
- Memory for the client after an hour on a thousand streams is flat, measured, not assumed.

---

## Phase C4: KLV as text

For the stream being watched, the ST 0902 minimum set, shown as text. No map in this phase.

### Build

- A `Sensor` group in the metadata panel under the player, and a small version of it on the
  focused wall player's overlay. Rows, in the order the set lists them: timestamp, mission id,
  platform heading, pitch and roll, sensor latitude, longitude and altitude, horizontal and
  vertical field of view, sensor relative azimuth, elevation and roll, slant range, frame centre
  latitude, longitude and elevation, and the UAS LS version. The security set is C1's banner, not
  a row here.
- Polled from `GET /api/live/klv/{name}` once a second for the selected stream and for each wall
  player, six or seven requests a second at most. It is a per-watched-stream fetch; nothing about
  it scales with the grid.
- The raw packet the route returns goes behind a `Raw` expander as hex, for the day a field is
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
collapsed with `No KLV on this stream`, not an empty panel.

---

## Phase C5: Detection

Phase 8 is planned, not built, and its wire format does not exist. What can be planned is the
client side of turning it on and seeing results, with the assumptions written down so they can be
struck through when the real shape lands.

### What is assumed

- **The toggle** is a REST call on the live control plane, `POST /api/live/detect/{name}` with a
  body carrying the per-stream detection rate Phase 8 says is a setting, and `DELETE` to turn it
  off. The same path a detector would use to trigger a recording, in the same style as record.
- **The state** comes back on `LiveStream`: something like `detection: { enabled, rate, worker }`,
  so the list says which streams are on without another call per tile.
- **The results** come from a route per stream, `GET /api/live/detections/{name}`, returning the
  most recent set: for each detection a class, a score, a box in normalised frame coordinates, and
  the presentation timestamp of the frame it was found in. Whether that is a poll or a stream is
  unknown; the client polls at the detection rate until told otherwise.

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
when, and cannot today: nothing on `DocumentMessage`, `ProviderResponse` or any REST route exposes
the age ceiling or a per-document expiry.

### Build, once the service exposes it

The smallest useful service change is the retention configuration on `GetProviders`, since the
client already calls it on connect: enabled, and the age in days. From that and `CreatedAt` the
client computes `expires in 3 days` on the tile's `Details` line and in the metadata panel, for
documents whose metadata marks them as a recording or a snapshot, which is the mark retention
itself looks for. A document uploaded by hand shows nothing, because retention leaves it alone.

A per-document expiry field would be more accurate and is not worth asking for until per-stream
overrides exist, which Phase 10 says nothing has asked for.

### Done when

A recording older than the configured age minus a day shows `expires tomorrow`; an uploaded file
of any age shows nothing; with retention off, nothing shows anywhere.

---

## Asks of the service, in one place

For the KLV agent, working in parallel:

1. The ST 0102 classification on `LiveStream`, on the list, beside `hasKlv` and the last-packet
   time. The client cannot mark a thousand tiles from a per-stream route.
2. The `security` object on `GET /api/live/klv/{name}` with the full ST 0102 fields, for the
   banner text.
3. A recording or snapshot taken from a marked stream carrying the marking in its document
   metadata, so the explorer can mark it too. Not blocking the client; blocking the day someone
   asks why a snapshot has no marking.

For the owner, before anything: the gRPC-or-REST decision above. Under B, nothing else is asked of
the service for C0, C2, C3 or C4.

For Phase 8, when it is designed: whether detection results carry the frame timestamp, and whether
the worker will draw into the preview.

For Phase 10: the retention configuration on `GetProviders`, or an equivalent.

## Documentation, as each phase lands

- `README.md`, "The desktop client": the wall, monitored streams, the marking, the health bar
  and what its colours mean, and which surface the client uses for what once the decision is made.
- `README.md`, "Watching a stream": the paragraph describing the tiles is rewritten for
  visibility-gated previews.
- `DocumentsApi.cs` header comment: reversed or reworded, per the decision.
- This file: the assumptions in C1, C4 and C5 struck through as the routes land.

## Acceptance, end to end

Against the rig, at the largest stream count it will hold, with six streams monitored and the
baseline's overload case induced on a pod:

| Claim | Measurement | Target |
| --- | --- | --- |
| A degraded stream is visible without opening it | Overload one pod; count red tiles | Every stream on that pod red within three beats, no others |
| No imagery without a marking | Walk every surface with a marked stream and an unmarked one | Marking or `UNMARKED` on tile, player, wall, fullscreen; never blank |
| The wall does not depend on the rest of the window | Six monitored, open a recording, upload, switch tabs, come back | Six players never stopped |
| The client does not load the service | Count preview requests at the service from one client | About a dozen a second, independent of list length |
| The window stays usable at a thousand | Scroll the grid end to end | No stutter; dispatcher under ten percent between ticks |
| KLV is readable | Select a stream with ST 0902 | Every field in the set, updating once a second |
