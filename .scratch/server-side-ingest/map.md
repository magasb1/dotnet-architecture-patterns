# Map: server-side stream ingest

## Destination

**Reached.** The document is at `docs/design/server-side-stream-ingest.md`. Every ticket on this map
is resolved and nothing is left to decide before someone builds, with one edge named in the document
as needing an answer first: what the buffer does with a feed carrying no startable point.


A design document a developer picks up and builds from, covering SRT streams that arrive
unannounced and name themselves by streamid, a server-side harvester that keeps previews current,
and snapshots and recordings taken on request that continue whether or not a client is watching and
land in documents. It has to scale across Kubernetes replicas and stay unchanged as a standalone
service on Windows or Linux.

Planning only. This map ends when a developer can start building without another decision to make.

## Notes

**Domain.** This repository is a demo service with pluggable file storage and pluggable persistence.
Live streaming already exists in `src/StorageDemo.Infrastructure/Streaming/`: an ingest is requested
over REST, one remux writes a recording file, a sampler decodes a preview from that file's tail, and
the recording becomes a document when the stream ends. That whole shape is what this map replaces.

**Skills.** Every session invokes `/grilling` and `/domain-modeling`. Research tickets go to a
`/research` subagent.

**Settled while charting.** These are constraints, not open questions:

1. Ingest is SRT only, arriving with no request first.
2. A stream names itself through the SRT streamid.
3. The name is the identity. A reconnect under the same name resumes the same stream after a grace
   period, rather than creating a second one.
4. The pipeline is fan-out: one demultiplexer per stream, consumers that attach and detach. The
   harvester is always attached, a recorder attaches only when asked, a viewer only while watching.
5. The harvester decodes previews continuously and writes bytes only on request, with a rolling
   buffer behind it so a recording can reach back a few seconds.
6. Manual setup survives as the second ingest mode, for protocols that cannot name themselves. It
   feeds the same pipeline and produces a stream that behaves identically once it exists.
7. One published ingest endpoint. The load balancer picks the pod, and that pod owns the stream from
   the moment the connection arrives.
8. A reconnect landing on a different replica makes that replica the new owner. Any recording in
   progress on the old owner closes as a complete document. Stated in the design as a known limit.
9. A stream no longer becomes a document by itself. Documents come only from snapshots and
   recordings someone asked for.
10. Security is deferred. A passphrase is the natural answer when it is picked up.
11. The harvester needs the demultiplexed stream and not only decoded pictures, because KLV rides as
    its own stream inside the transport.
12. Ingest and consumption are on separate ports, so a deployment can expose one and keep the
    other behind a firewall.
13. The stream behaves like IPTV. Rollback is bounded to ten to thirty seconds and comes from the
    buffer, always, whether or not a recording is running. The recording is a durable capture, never
    the thing rollback reads.
14. A recording always begins in the buffer. A trigger arriving at time T produces a capture that
    starts before T and continues past it, roughly five seconds either side, so an event that was
    already happening when it was noticed is still recorded. This makes an automatic trigger, such as
    an object detection, a first-class caller rather than an afterthought.

**Defect diagnosed while charting.** The preview freeze that prompted this work is understood and
recorded in `Why the preview sampler stops after the first frames`. It does not carry into the new
design, but one part of it does: `LibavMediaAnalyzer.Seek` targets an absolute timestamp instead of
one relative to the stream's start. The new pipeline still calls that analyzer whenever a snapshot or
a recording becomes a document, and a recording cut from a rolling buffer starts at a large
timestamp, so this has to be fixed whichever sampler survives. The wider lesson is worth carrying:
"the frame three seconds in" and "the picture right now" are different questions, and one interface
answering both is what let a live preview serve a fixed frame while everything reported success.

## Decisions so far

- [The design document](issues/10-design-document.md) — written to
  `docs/design/server-side-stream-ingest.md`. The destination of this map.

- [How a viewer consumes a stream](issues/11-viewer-consumption.md) — MPEG-TS over HTTP on its own
  port, live only. A stream listener symmetric with ingest was rejected because rollback has nowhere
  to carry a position, cross-replica forwarding would become a media relay, and the ingest accept rate
  would then limit viewers too. Streams are addressed by name, position is a how-far-back parameter
  resolved to a segment with the response saying what was actually given, and returning to live is a
  new request rather than a control message. An interrupted stream holds the connection open and sends
  nothing. Documents stay on the API, which keeps the firewall statement to one sentence per port.

- [One ingest endpoint in Kubernetes](issues/09-kubernetes-shape.md) — a Deployment rather than a
  StatefulSet, because nothing points at a particular pod once ownership is claimed on connect; the
  owner records its own pod address instead, deleting the headless Service and the per-pod ingest
  Services. Per-flow stickiness is required and is ordinary connection tracking; stickiness across
  reconnects is not, which is the payoff of newest-connection-wins. Traffic policy stays local so a
  sender's address survives, a diagnostic rather than a security argument. Capacity is streams per
  replica, bounded by buffer memory and recording disk, both calculable from earlier decisions, and
  the useful autoscaling signal is streams owned rather than processor load. Standalone is the same
  code with the in-memory registry and lock and nothing to forward to.

- [Snapshot semantics](issues/08-snapshot-semantics.md) — a fresh decode of the newest segment, not
  the harvester's frame, which is stale by up to a keyframe interval plus the preview cadence and can
  be twelve seconds behind on a coarse sender. Full source resolution rather than the preview's tile
  size. Named by stream and wall-clock capture time, matching recordings, with presentation
  timestamps kept in metadata for correlation. Triggered by the same callers as a recording, so a
  detector needs no separate path. Served while a stream is interrupted, refused once it is gone.
  Requires the latest-frame capability the analyzer lacks, which is the same gap the sampler
  diagnosis found.

- [Recording from request to document](issues/07-recording-lifecycle.md) — one shape with an optional
  duration, so a clip is a recording whose stop time was set at the start and a detector is the same
  caller as a person. Starting returns immediately and the recording then has no relationship with
  whoever asked. Bytes go to a local file and are uploaded through the ordinary storage path at the
  end, which keeps both storage providers equal; the accepted limits are that a recording is bounded
  by pod disk and dies with the pod, capped by a configured maximum duration. The document appears
  only when there is a file, and in-progress visibility belongs to the stream instead. One recording
  at a time per stream, and a trigger while one runs extends it rather than starting a near-duplicate.
  Named by stream and start time, since one stream can leave several documents behind.

- [The rolling buffer and live playback](issues/06-rolling-buffer.md) — held in memory, since it dies
  with the stream and disk buys no durability. Bounded by seconds and by a per-stream byte ceiling,
  whichever binds first, and a binding ceiling has to be observable rather than quietly shrinking a
  pre-roll. Held as segments that each begin at a position a decoder can start from, so joining,
  rolling back and cutting a pre-roll are all one operation and eviction drops the oldest. Segment
  length is the sender's keyframe interval, not ours, so the window moves in coarse steps for some
  encoders and the promise is at least the requested seconds. Buffering is unconditional, because a
  trigger can arrive at any moment. A resumed feed appends when its layout matches and closes the
  recording when it does not.

- [Stream lifecycle and the registry](issues/05-stream-lifecycle-registry.md) — the registry is keyed
  by name, not by an opaque identifier, which is what lets a reconnect resume rather than create. A
  connection is accepted before its name can be read, so nothing is ever refused by name, only
  dropped afterwards. The newest connection wins a contested name: it claims with the distributed
  lock that already exists, and the previous owner learns it lost the claim on its next heartbeat and
  shuts down, closing any recording as complete. The grace period is thirty seconds and everything
  survives it, including a recording, which records the silence; the client shows the stream as
  interrupted rather than removing it. After that the stream leaves no trace, since its documents are
  the trace. Manual and automatic streams share one namespace and one claim.

- [The fan-out pipeline](issues/04-fan-out-pipeline.md) — one demultiplexer per stream feeding a hub
  that owns a rolling buffer and a set of subscribers. Two tiers: a packet tier carrying
  demultiplexed packets by stream index, where the recorder, the viewer and a future KLV extractor
  live, and a frame tier where one decoder subscribes to packets and republishes frames, decoding
  only at the rate its subscribers ask for. Those two tiers are the seam for detection and KLV. The
  buffer is part of the hub and does three jobs: the join point for a new viewer, the rollback
  window, and what a recording reaches back into. A viewer never accumulates delay: deliberate
  rollback is a position, unintended lag is corrected by skipping to live. A recorder overflowing is
  a hard failure that truncates rather than a drop. Every consumer that writes bytes owns its muxer.

- [Why the preview sampler stops after the first frames](issues/03-sampler-freeze.md) — the sampler
  asks the document analyzer for a poster frame, "the picture three seconds in", and calls the answer
  a live preview. While the recording is smaller than the sampling window the window always starts at
  the head of the stream, so that absolute target resolves to the same frame every pass. Once the
  recording is larger, the target falls below everything in the window and clamps to its first frame,
  so the preview advances but stays permanently a window behind live. Every component reported
  success throughout. The freeze dies with the fan-out design, which holds one long-lived decoder and
  never seeks. One defect does transfer: the analyzer's seek is absolute rather than relative to the
  stream's start, so any media not beginning at zero gets its poster frame from frame zero, and a
  recording cut from a rolling buffer begins at a large timestamp.

- [SRT listener: streamid and concurrent connections in libav](issues/01-srt-listener-streamid.md) —
  one port serving many named streams works with the bundled build, but not as assumed. A libav
  listener accepts exactly one caller and closes the listening socket, so the accept loop re-opens it
  after every accept; libsrt's per-process UDP multiplexer and the reuse-address option let earlier
  connections keep running. Proven with three concurrent named callers on one port. The streamid is
  only reachable by capturing a verbose libav log line through a custom log callback, so a boot-time
  self-test has to guard it. Backlog is one, giving roughly two or three accepts a second, so senders
  must retry. The name arrives after accept, never before, so a connection cannot be refused by name.
  Calling libsrt directly is off the table: it is statically linked into libavformat and not
  re-exported.

- [How encoders set streamid](issues/02-streamid-conventions.md) — parse the SRT Access Control
  convention `#!::r=name,m=publish`, and fall back to treating the whole field as a bare name, which
  is what SRT's own reference listener does. Absent `m` means publish; reject only an explicit
  `m=request`. Names are validated and rejected rather than sanitised, since two names normalising
  to one would let an encoder take over another's stream. Limit is 512 bytes. A token later goes in
  the `s` key, so no format change is needed to add it.

## Not yet specified

- Smoothing a reconnect that lands on a different replica, so a recording survives the move. Wanted,
  and only a problem in Kubernetes: standalone and Docker have one owner by construction. Pairs with
  uploading a recording in completed pieces as it runs, since both are a recording outliving one pod
  and neither is worth solving alone.
- Authentication on an unannounced push. The passphrase is the stronger candidate, because it is
  enforced during the handshake, whereas a token in the streamid's `s` key can only be checked after
  the connection has already been accepted.
- What would justify replacing the log-line capture of the streamid with something sturdier, and the
  conditions under which vendoring libsrt directly would become worth its cost.
- Serving many viewers of one stream. They all funnel through that stream's owner, since only the
  owner holds the bytes, so adding replicas does nothing for a single popular stream. Correct for
  contribution, where streams outnumber viewers; wrong for distribution to an audience.
- Retention and cleanup of recordings and snapshots once they are documents.

## Out of scope

- RTMP and RTSP as ingest protocols.
- A server-fetched source that the service pulls and forwards to the multiplexer. Wanted later, not
  a first priority.
- Building detection, tracking or KLV extraction. Only the seam they attach to is in scope.
