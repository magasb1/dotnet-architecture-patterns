# The gateway surface: configured sources, forwarding, and a web UI

The ask, in the repository owner's words:

> Can you also create a web ui for the streaming services? A place to add pull-streams and add
> forwardings to other servers. It should be inspired by haivision media gateway. Each source has
> its default output locally, but can also be forwarded to another server.

That sentence contains three features, and only one of them is a UI.

## What existed before this

**Pull streams half-existed.** `POST /api/live/manual` takes a name and a URL, opens the input on
whichever pod received the request, and the stream dies with that pod. Nothing recreates it. Phase
6 was written to fix exactly this and was never built.

**Forwarding did not exist at all.** The only thing in the repository that sends a stream to
another address is the in-cluster relay, which exists so a viewer who reached the wrong replica
still gets media. That is an internal hop between two copies of this service, and it is nothing
like pushing a feed to a gateway in another building.

**There was no web UI and no static file hosting.** The API served Swagger and nothing else.

## Four decisions, taken by the repository owner

| | Chosen | The alternative, and what it would have cost |
| --- | --- | --- |
| UI technology | Blazor, interactive server, inside the API | Static HTML would have avoided a rendering model unlike anything else here; a bundled SPA would have added npm and a lockfile to a repository that has deliberately avoided both |
| Forward targets | All three: SRT caller, SRT listener, and plain UDP or RTP | SRT caller alone is symmetric with ingest and would have been a third of the work |
| Scope | Sources and forwards only | The whole live surface would have duplicated the WPF client's recording, snapshots, detection and KLV, for roughly double the work |
| Durability | One mechanism covering both | Pinning them to the pod that created them is faster to build and wrong in the cluster |

## Three shapes of forward, one code path

This is the decision that made "all of the above" cheap rather than three times the work.

`PacketMuxer` already writes a muxed container into any .NET `Stream`. `AvioReader` already
wraps a .NET `Stream` as a libav `AVIOContext` for reading. The missing piece was the mirror
image: an `AvioWriter`, a write-only `Stream` backed by an `AVIOContext` that libav opened from a
URL. With it, one unchanged muxer serves every protocol libav can write, and the difference
between the three shapes collapses into the text of the URL:

```
srt://host:9000?streamid=name      dial the far end and push
srt://0.0.0.0:9100?mode=listener   wait here for the far end to pull
udp://239.0.0.1:5000?ttl=2         push, no handshake, no recovery
rtp://host:5004                    the same, in RTP
```

Query options are libav's and pass straight through, so latency, passphrase and stream identifier
need no fields of their own.

Two caveats are worth stating. Plain `rtp` in libav expects a single elementary stream, so
MPEG-TS over RTP is the `rtp_mpegts` muxer and the scheme picks it. And SRT listener mode here is
libav's, not the direct libsrt listener that Phase 1 built for ingest. That is deliberate: Phase 1
moved ingest off libav because its accept rate was the ceiling at hundreds of concurrent senders,
and a forward is one socket that one operator configured. The ceiling that justified the rewrite
does not apply at this volume.

## Where a forward runs, and why that needs no new lease

A forward is attached to the local stream entry on whichever replica owns the stream. It is
started and stopped by the same per-stream heartbeat that already republishes the registry entry,
closes finished recordings and stands the replica down when it loses a name.

That single placement decision is what makes the durability answer free. A stream that moves to
another pod takes its forwards with it, because the pod losing the name disposes its entry and the
pod gaining it reconciles the same configuration from the same shared store. Nothing needs to
lease a forward separately, and there is no state that can outlive the bytes it was copying.

The retry loop lives in the reconcile pass rather than inside the forwarder, so exactly one place
decides whether a forward should be running. A forwarder that fails records why and stops.

## The store is a second store, deliberately

Phase 6 proposed putting sources on `ILiveStreamRegistry`, on the reasonable grounds that it is
already the one piece of shared state with both an in-memory and a Redis implementation. **That is
reversed here**, and the reason is worth keeping.

The registry answers "what is on air". Entries are removed the moment a stream ends, and that is
load-bearing: it is what stops the registry accumulating the dead, and what makes a listing a
picture of now. A configured source has to survive precisely the events that clear it - the stream
ending, the pod dying, the whole service restarting - because it answers a different question:
"what did somebody ask for". One structure serving both gives either configuration that
evaporates or a registry that never forgets.

So `ILiveSourceStore`, with the same two-implementation split the rest of the repository uses. The
Redis one is a hash, mirroring the registry. The standalone one is a **JSON file rather than a
dictionary**, which is the one place these two stores differ in kind. An in-memory registry is
correct because a reading taken by a process that has stopped is worthless. An in-memory source
list would lose the operator's camera list on every restart, which on the desktop shape is the
whole configuration.

One asymmetry in the Redis implementation, stated because it looks like an inconsistency: reads
degrade to empty with a logged warning, exactly as the registry does, but **writes let the failure
through**. A stream that is invisible to other replicas still works, so a failed registry write is
survivable. A save that silently vanished would have the UI report success for a change that never
happened.

## Push sources are configuration too

`LiveSource.Url` is nullable, and null is not "unconfigured". It is the statement that this name
arrives on the ingest port under its own power. An encoder that pushes a name still needs
forwarding configured against that name, and splitting push and pull into two types would have
meant two lists in the interface for what an operator thinks of as one row.

## What the page is, and is not

One page, at `/streaming`. A dense table of sources: name, push or pull, state, throughput, the
local SRT output URL with a copy button, and a forward indicator. Expanding a row shows each
forward with its URL, its state, its bytes and its last error.

The local output is the half of the request that is easy to skim past. Every source already has a
default output, which is the consumption port this service has always had, and the operator's most
common action is "give me the URL for this feed". Showing it per row is most of the page's daily
value.

**A forward that is failing shows why.** A dead forward that looks identical to one nobody
configured is the single most confusing state this page can have.

Because Blazor renders server-side in the same process, the page calls the services directly
rather than its own REST API. A hop, a serialisation round trip and a second failure mode, for
nothing.

It is gated by the same `X-Storage-Token` shared secret every live endpoint uses. This page
changes what the service dials out to, so leaving it open would be a way round that guard rather
than a convenience.

## What this does not do

- **No paging.** A thousand sources render as a thousand rows. The target deployment configures
  pull sources in the tens; a wall of a thousand is the desktop client's job.
- **No per-forward bitrate history.** Bytes and connected state only.
- **Deleting a source does not stop a stream already on air.** Deleting the configuration stops
  the service re-establishing it. Yanking a live feed out from under its viewers is a separate,
  explicit action, and conflating them would make a tidy-up destructive.
- **No SRT passphrase field.** It is a libav URL option and passes through as one. Phase 5's
  passphrase on the ingest and consumption ports is a different thing and still unbuilt.
