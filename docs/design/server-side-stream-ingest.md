# Server-side stream ingest

**Status:** design, not built. Nothing described here exists yet.

This replaces the live streaming that is in the repository today. The vocabulary it uses is defined
in `CONTEXT.md` at the root, and every decision below was reached and recorded in
`.scratch/server-side-ingest/map.md`, where each one links to the ticket holding its reasoning. When
this document and that map disagree, the map is right and this document is stale.

## What changes

Today a live stream is requested before it exists. A caller posts a name and a URL, the service opens
a socket, one remux writes a recording file, a sampler decodes previews from that file's tail, and
the recording becomes a document when the stream ends. Everything is built around that file.

The new shape inverts it. An encoder connects without asking, names itself in the protocol, and the
service does the rest. Nothing is written to disk unless somebody asks for it. Recordings and
snapshots are taken server-side and continue whether or not any client is connected, so closing the
client does not stop a recording that is running.

## Ingest

### The protocol

SRT only. An encoder connects to the ingest port and presents a stream identifier, which carries the
name. Manual setup remains for protocols that cannot name themselves, described further down.

### Reading the name

The stream identifier follows the SRT access control convention, structured as key and value pairs
prefixed with a shebang and a colon. The resource key carries the name and the mode key carries the
intent.

A bare unstructured string is also accepted and treated as a plain name. This is not a courtesy: the
reference listener in the SRT project itself does exactly this, and some senders have no field for a
structured identifier at all, so their operators put the whole address in one box and a bare name is
what arrives.

Parsing rules:

- Absent mode means publish. The convention makes mode optional when the caller is the sender, and at
  least one vendor's own documented example omits it. Only an explicit subscribe intent is refused.
- No escaping exists in the convention, so a comma or an equals sign cannot appear in a name.
- Names are validated and **rejected**, never cleaned up. Two names normalising to the same string
  would let one encoder take over another's stream, because the name is the identity.
- The field is limited to 512 bytes, not characters.
- A token, when authentication is picked up later, goes in the session key the convention already
  reserves. No format change will be needed.

### The accept loop

This is constrained by what the bundled libav can actually do, established empirically rather than
assumed.

A libav SRT listener accepts exactly one caller and then closes the listening socket. Multiple
concurrent streams on one port are still possible because libsrt multiplexes its sockets over one
shared UDP port per process and libav sets the reuse-address option, so the listener is re-opened
immediately after each accept while earlier connections keep running. This was proven with three
named callers accepted concurrently on one port, with re-listen latency under a millisecond.

Three consequences are load-bearing:

- **The identifier is only reachable through a log line.** FFmpeg reads it on accept and logs it at
  verbose level without storing it anywhere the application can reach. A custom log callback captures
  it without raising the global log level. This is brittle across an FFmpeg upgrade, so a boot-time
  self-test must confirm the capture still works and fail loudly if it does not.
- **The backlog is one**, giving roughly two or three accepts a second. Senders must retry, which
  encoders do by nature. A mass reconnect is absorbed by retrying, not by the service.
- **The name arrives after accept, never before.** Nothing can be refused by name. An unwanted
  connection is accepted and then dropped, and an unnamed caller is accepted before it is known to be
  unnamed. Any rule about who may push has to act during the handshake instead, which is why a
  passphrase is the stronger authentication candidate.

Calling libsrt directly was investigated and rejected. It is statically linked into libavformat and
not re-exported, so going direct means vendoring another binary for every platform and running a
second SRT stack beside the one already loaded.

### Claiming the name

Having read the name, the replica claims it on the distributed lock that already exists, renewed by
the heartbeat.

**The newest connection wins.** A replica finding the name already held takes it anyway and records
itself as owner. The previous owner discovers on its next heartbeat that it has lost the claim, shuts
its hub down, and closes any recording as a complete document.

Refusing the newcomer was rejected because it makes recovery wait on a timeout the service does not
control. An encoder actively pushing bytes is more real than a socket that has not yet noticed its
peer is gone, and SRT takes seconds to work that out. During those seconds, a refusing design turns a
live encoder away on the word of a dead connection.

## The pipeline

One stream is one demultiplexer feeding one hub. The hub owns the rolling buffer and the set of
subscribers. Packets flow one way through it.

### Two tiers

**The packet tier is the fan-out.** Subscribers receive demultiplexed packets filtered by stream
index, with no decoding anywhere near them. The recorder, the viewer and a future KLV extractor all
live here. This is what keeps a stream cheap when nobody is watching, and it is why KLV needs no
special path: it is a subscriber on a different stream index.

**The frame tier hangs off it.** A single decoder is one packet subscriber and republishes frames to
its own subscribers. Exactly one decode exists however many things want pictures, and consumers that
only move bytes never pay for it. It decodes at the highest rate its subscribers ask for, which today
is keyframes only, because the preview needs one picture every couple of seconds.

Those two tiers are the seam that detection, tracking and KLV extraction attach to later. Detection
raises the decode rate by asking for it, rather than getting it by default. Nothing more needs to
exist now.

### Backpressure

Every subscriber has a bounded queue. An unbounded one turns a slow viewer into a memory leak, and
blocking turns one into a stall on ingest. Overflow does not mean the same thing for everyone:

- **A viewer never accumulates delay.** If it cannot keep up it skips forward to the newest startable
  position. Deliberate rollback is a chosen position and is not this; unintended lag is a fault and is
  corrected.
- **A recorder overflowing is a hard failure.** Its queue is larger, and exhausting it ends the
  recording and marks the document truncated. Dropping packets to keep going would write a hole into a
  file that claims to be a recording, and silence is worse than stopping.

### Muxing

Each consumer that writes bytes owns its own muxer, created when it attaches and closed when it
detaches, with its own timestamp base. A recording started at ten past and a viewer who joined at
twelve past have different timelines and different first packets, so a shared muxer would force both
to inherit whichever attached first.

## The buffer

In memory. It is recent history for a live pipeline and dies with the stream regardless, so disk buys
no durability: losing the process loses the connection too.

**Bounded by seconds and by a per-stream byte ceiling, whichever binds first.** Seconds are the
promise, ten to thirty of them. The byte ceiling is what stops one careless encoder evicting the
service. Thirty seconds is roughly six megabytes for a modest feed and seventy-five for a
contribution one.

**Held as segments**, each beginning at a position a decoder can start from and running to the next.
Every job the buffer does asks the same question and segments answer it once. Joining a viewer,
rolling back, and cutting a recording's pre-roll all become choosing a segment. Eviction becomes
dropping the oldest.

The cost is granularity, and it is not ours to control. Segment length is the sender's keyframe
interval, so an encoder with a ten second interval leaves a window of two or three coarse steps while
a one second interval leaves thirty fine ones. Therefore:

- The window promise is **at least** the requested seconds, never exactly.
- A pre-roll starts at the segment boundary at or before the requested point, so a recording routinely
  begins earlier than asked. This is harmless and should be documented rather than corrected.
- The byte ceiling must be forgiving enough to hold at least a couple of segments from a coarse
  sender.
- A feed that runs a long way with no startable point needs a fallback, because a buffer holding one
  open-ended segment can serve nobody. **This edge is unresolved and needs an answer before build.**

**Buffering is unconditional.** A stream nobody is watching and nobody is recording still buffers,
because a trigger can arrive at any moment and the pre-roll is the entire reason the detection
scenario works. Retaining packets costs no decode, which is what makes always-on affordable.

## Lifecycle

The registry is keyed by **name**, not by an opaque identifier. An identifier survives as a
per-connection detail for logs and for telling one attempt from the next, but nothing looks up by it.
This is what lets a reconnect resume rather than create.

**Interrupted** is a state the session model does not have today. When a feed stops arriving the
stream stays claimed, stays listed, and keeps its hub, its buffer and any recording alive for a grace
period of thirty seconds. A recording keeps running and records the silence. The client shows the
stream as interrupted rather than removing it, because a tile that vanishes and returns is worse than
one showing a state, and after a resume it would be showing the same stream on both sides of the gap.

A resumed feed **appends** when its layout matches, as a new segment boundary, and a recording in
progress carries the discontinuity as a gap in presentation time. When the encoder was reconfigured
while it was away and returns with a different layout, the recording closes as a complete document and
the buffer starts fresh, because a file whose codec configuration changes halfway is not something
anything will reliably play.

**After the grace period the stream is gone and leaves nothing behind.** No entry, no history. The
documents it produced are its trace. The registry stays a picture of what is live now, which is the
only thing every replica needs to agree on.

**A stream never becomes a document by itself.** Documents come only from snapshots and recordings
someone asked for, which makes unattended ingest safe to leave running.

### Manual streams

Manual setup survives as the second ingest mode, for protocols that cannot name themselves. A manual
stream is created by request, named at creation, and owned by the replica that opened its socket. It
sits in the registry waiting for bytes.

Manual and automatic streams share **one namespace and one claim**. A manual stream is simply one
that claimed its name early. An encoder presenting a name a manual stream holds is the same conflict
as any other and resolves the same way, with the newest connection taking it. Two namespaces would
mean two lookup paths and a name that means different things depending on how it arrived.

Once a demultiplexer exists the two are indistinguishable. The only difference is how the input was
opened, and that difference ends before the hub begins.

## Previews

The harvester is the frame subscriber that is always attached, keeping each stream's preview current.

A **preview** is the picture right now. It is distinct from a **poster frame**, which is the picture a
fixed number of seconds into a piece of media. These are different questions, and the analyzer in this
repository answers only the second one. That is not a footnote: asking it the wrong question is
precisely what made the current implementation serve a fixed frame for minutes while every component
reported success. See `.scratch/server-side-ingest/issues/03-sampler-freeze.md`.

## Snapshots

A snapshot is a **fresh decode**, not the harvester's frame. The newest segment in the buffer is
decoded forward and its last picture kept.

The harvester decodes keyframes only, at the rate the preview needs, so what it holds is stale by up
to a keyframe interval plus the preview cadence: about three seconds for a fine sender and about
twelve for a coarse one. A snapshot is a deliberate act performed once, and one decode is nothing next
to being twelve seconds wrong about the moment somebody meant to capture.

- **Full source resolution, JPEG at high quality.** The preview stays small because it is a tile in a
  grid. A snapshot is a document someone opens, and sometimes the only surviving evidence of an event.
  A lossless format is worth exposing as a setting if snapshots ever need to be evidence-grade.
- **Named by stream and wall-clock capture time**, matching recordings, so the two sit together and
  read as related. A live feed has no beginning, so an offset into it means nothing to a person. The
  presentation timestamp rides in metadata for correlating against a recording.
- **Taken by the same callers as a recording.**
- **Served while a stream is interrupted**, returning the last picture that arrived, and refused once
  the stream is gone. While the tile is there and marked interrupted, the button still works.

## Recordings

**One shape with an optional duration.** A clip is a recording whose stop time was set when it
started. Two operations for one thing would drift apart.

**A trigger is a trigger.** A person pressing record and a detector firing are one caller on one path.
This is what makes detection genuinely free to add later rather than a second code path.

**Every recording begins in the buffer.** A trigger at a moment yields a capture starting before it
and running past it, about five seconds either side, so an event already under way when it was noticed
is still caught. The driving scenario is automatic: something is detected, recording starts, and the
moments before the detection are the ones that matter.

**Starting returns immediately** with the recording's identity and state. The recording then runs in
the owning replica as a packet subscriber and has no further relationship with whoever asked for it.
Closing the client, losing the client, or never having had one changes nothing.

**One recording at a time per stream.** A trigger arriving while one runs **extends its end** rather
than starting a second. Continuous detection then produces one clip covering the whole event instead
of a drift of overlapping near-duplicates. Someone wanting a separate file stops and starts.

**Named by stream and start time.** One stream can leave several documents behind, which happens on a
name takeover mid-recording and whenever a maximum duration is reached.

### Storage

Bytes go to a **local file** and are uploaded through the ordinary storage path when the recording
ends. Both storage providers stay equal, which is the point of the abstraction: streaming straight
into object storage would mean multipart upload, which S3 has and the filesystem provider does not,
and the repository exists to demonstrate that neither provider is special.

**The document appears at the end.** A document is a stored file someone can download, and a
half-written one that cannot be is a lie the storage abstraction would have to carry everywhere. The
visibility gap belongs to the stream: a stream reports that it is being recorded and for how long, and
a document appears when there is a file.

A configured **maximum duration** makes a recording fail predictably instead of filling a disk. On
reaching it the recording closes as a complete document, and a still-firing trigger starts the next
one.

## Consumption

**MPEG-TS over HTTP, on its own port, live only.**

A stream listener symmetric with ingest was the more elegant candidate and was rejected on three
concrete costs. Rollback needs a position and the stream identifier field has nowhere sensible to
carry one. Reaching a replica that does not own the stream is already solved for HTTP and would be a
new media relay otherwise. And the accept rate that limits ingest to a couple of connections a second
would then limit viewers too.

- **A stream is addressed by name**, since the registry is keyed by name and no identifier remains to
  embed in a URL.
- **Position is a parameter saying how far back to start**, resolved to the segment at or before that
  point. The response says what was actually given, because asking for twenty seconds and receiving
  twenty-six is normal rather than an error. Omitting it means live. Returning to live is a new
  request, not a control message, which keeps the connection one-way and stateless.
- **A viewer reaching a replica that does not own the stream is forwarded to the owner**, using the pod
  address the owner recorded when it claimed the name.
- **During an interruption the connection is held open and nothing is sent.** The client already knows
  the stream is interrupted from its state. Closing would push every viewer into reconnecting at the
  exact moment a reconnect storm is under way on the ingest side.

Finished recordings and snapshots are documents and stay on the API. That is what keeps the firewall
statement short enough to be useful, which is the whole reason the ports are separate:

| Port | Reaching it lets you |
| --- | --- |
| Ingest | push a stream, and nothing else |
| Consumption | watch live streams, and nothing else |
| API | everything else |

## Kubernetes

### The workload

A **Deployment**, not a StatefulSet. Stable pod identity existed only so a sender could be pointed at
a particular pod, and nothing points at a particular pod any more. When a replica claims a name it
records its own pod address, so a replica needing to forward a request looks the address up rather
than deriving it from a predictable name. That deletes the headless Service and the per-pod ingest
Services together.

### Two Services

Ingest and consumption are separate Services so each can be exposed to a different network, with its
own type, annotations and source ranges. That separation is the reason for the split.

### How a connection finds a pod

Two things are easily conflated here and only one is needed.

- **Per-flow stickiness is required.** An SRT connection is a UDP flow and its packets must all reach
  the same pod. This is ordinary connection tracking, and every load balancer in play does it without
  being configured to.
- **Stickiness across reconnects is not required.** A returning encoder may land anywhere, because the
  newest connection wins the name and the previous owner stands down. No affinity configuration, no
  session tables, nothing to get wrong. This is the payoff of that decision.

The external traffic policy stays **local**, so a sender's address survives. That is a diagnostic
argument rather than a security one, since the chosen authentication candidate is a passphrase, but an
encoder nobody can identify in a log is painful exactly when it matters. The cost is that only nodes
actually running a pod receive ingest traffic.

### How it scales

Streams spread across replicas because the load balancer spreads connections. Each replica owns some
streams and holds everything for them. Adding replicas adds capacity for more streams, and the useful
autoscaling signal is **streams owned per replica**, not processor load, because an idle stream still
costs a buffer.

A replica's ceiling is calculable rather than empirical, which is what lets a resource request be
honest:

| Resource | Bounded by |
| --- | --- |
| Memory | per-stream buffer ceiling, times streams owned |
| Ephemeral disk | maximum recording duration, times concurrent recordings |
| Accept rate | roughly two or three connections a second, per replica |

### What does not scale this way

Everyone watching a given stream funnels through that stream's owner, because only the owner holds the
bytes. Spreading streams across replicas does nothing for a single popular stream, and the owner's
egress is the ceiling. This is correct for the contribution-style use this is built for, where streams
outnumber viewers, and it is the wrong shape for distributing one stream to an audience.

## Standalone

The same code with nothing switched on. One process owns every stream, the registry and the lock are
the in-memory implementations, no request is ever forwarded because there is nowhere to forward it,
and the two ports are just two ports.

None of the Kubernetes section applies, which is the property worth protecting: the cluster shape is
configuration, not a different program.

## What happens to the code that exists now

| Today | Becomes |
| --- | --- |
| `LiveStreamManager` | splits three ways: an accept loop, a per-stream hub, and a recording consumer. The remux-to-file-per-session at its centre goes away entirely. |
| `LibavRemuxer` | its one loop splits. The demux side feeds the hub; the mux side becomes the per-consumer muxer. |
| `LibavMediaAnalyzer` | gains a latest-frame capability it does not have. Its `Seek` is fixed, see below. |
| `ILiveSessionRegistry` | keyed by name rather than by identifier. Entries are removed when a stream ends. The owner records its own reachable address. |
| `LiveSessionStaleness` | the grace period survives; its meaning changes from "failed" to "interrupted". |
| `IDistributedLock` | unchanged, now also used for name claims. |
| `LiveStreamsController` | the ingest request becomes manual-stream-only. Recording and snapshot operations are new. Playback moves off to the consumption port. |
| gRPC `ListLive`, `DownloadLivePreview` | keyed by name. The session model gains an interrupted state. |
| Desktop client | tiles keyed by name; record and snapshot buttons; live playback via the consumption port; an interrupted state to display. |
| `k8s/live/statefulset.yaml` | replaced by a Deployment and two Services. |
| `docker/docker-compose.yml` | publishes a second port for consumption. |
| `docker/srt-sender.sh` | stops posting first. It just pushes, with a name in the stream identifier. |

### Fixes that carry over regardless

These are real defects in code the new design still uses. They are not optional cleanups.

1. **`LibavMediaAnalyzer.Seek` targets an absolute timestamp** rather than one relative to the
   stream's start. Any media not beginning at zero gets its poster frame from frame zero. A recording
   cut from a rolling buffer begins at a large timestamp, so every such document would silently be
   affected.
2. **The analyzer has no latest-frame verb.** Both the preview and the snapshot need one.
3. **The live thumbnail response sets a no-store header on its proxy branch but not on its local
   branch.** Set it on both.

## Accepted limits

Each of these is a deliberate trade, not an oversight. State them in any handover.

- A recording is bounded by pod disk and dies with its pod.
- A displaced owner closes its recording rather than moving it, so a flapping encoder leaves several
  documents.
- A stream being recorded is visible on the stream, not in the document list, until it finishes.
- Viewers of one stream all funnel through its owner.
- The stream identifier is captured from a log line, guarded by a boot-time self-test.
- Nothing can be refused by name; only accepted and dropped.

## Conditions an operator must be able to see

- A buffer whose **byte ceiling is binding before its time window is reached**. This silently shortens
  every pre-roll taken from it, and nobody finds out until they open a document and the event is
  missing.
- A recording ended by **queue overflow**, which produces a truncated document.
- A **failed boot-time self-test** of the stream identifier capture, which means every stream will
  arrive unnamed.

## Deliberately deferred

Recorded on the map as fog, wanted but not now.

- **Reconnect hand-off across replicas**, so a recording survives moving. Pairs with uploading a
  recording in completed pieces as it runs, since both are a recording outliving one pod. Neither is
  worth solving alone. Only a problem in Kubernetes.
- **Authentication.** The passphrase is the stronger candidate because it acts during the handshake,
  before a connection is accepted. A token in the stream identifier can only be checked afterwards.
- **Replacing the log-line capture** of the stream identifier, and the conditions under which
  vendoring libsrt directly becomes worth its cost.
- **Retention and cleanup** of recordings and snapshots once they are documents.
- **Serving many viewers of one stream.**

## Out of scope

- RTMP and RTSP as ingest protocols.
- A server-fetched source the service pulls and forwards to the multiplexer.
- Building detection, tracking or KLV extraction. Only the seam they attach to is in scope.
