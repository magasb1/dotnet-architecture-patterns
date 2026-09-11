# Storage Demo

An ASP.NET Core service where **file storage** and **database persistence** are two independent,
swappable infrastructure concerns, plus a Windows desktop client that talks to it over gRPC.

Nothing in the application or domain code knows whether bytes land on a disk or in a bucket, or
whether metadata lives in LiteDB or PostgreSQL. Configuration decides.

```
                              ASP.NET Core API
                  gRPC :5080 (primary)   REST :8080 (secondary)
                                     |
                                     v
                             DocumentService
                             /             \
                            v               v
                    IFileStorage       IDocumentRepository
                         |                    |
                   +-----+-----+        +-----+------+
                   v           v        v            v
              Filesystem      S3     LiteDB     PostgreSQL
```

## The four runtime combinations

| Scenario | Runtime | File storage | Database | Persistence needed |
| --- | --- | --- | --- | --- |
| A | Developer Windows | Filesystem | LiteDB | Local disk |
| B | Developer Linux | Filesystem | LiteDB | Local disk |
| C | Docker Compose | MinIO (S3-compatible) | PostgreSQL | Container volumes |
| D | Kubernetes | AWS S3 | PostgreSQL | None in the pod |

The two providers are chosen independently, so `Filesystem + PostgreSQL` and `S3 + LiteDB` work
too. Only the configuration changes between scenarios, never the source.

## Run it

### Scenario A and B: no infrastructure required

```bash
dotnet run --project src/StorageDemo.Api
```

gRPC listens on `http://localhost:5080` and REST on `http://localhost:8080`. The application
creates `./data/files` and `./data/database/app.db`, seeds a welcome document through the real
abstractions, and serves Swagger at `http://localhost:8080/swagger`.

Two ports, not one: cleartext HTTP/1.1 and HTTP/2 cannot share a port, because without TLS there
is no ALPN to negotiate the protocol. TLS-terminating ingress in front of the pod removes that
constraint in production, and the endpoints are configured in `appsettings.json` either way.

### Scenario C: containers, MinIO and PostgreSQL

```bash
docker compose -f docker/docker-compose.yml up --build
```

gRPC is published on `http://localhost:5081`, REST on `http://localhost:8081`, MinIO's console on
`http://localhost:9001` (`minioadmin` / `minioadmin`). The ports are offset by one from the local
scenario so both can run at once and the client can switch between them. No AWS account is
involved.

### Scenario D: Kubernetes

```bash
kubectl apply -f k8s/           # documents only
kubectl apply -f k8s/live/      # and live streaming: two media Services, and a Deployment
                                # carrying their ports, re-applied over the one above
kubectl -n storage-demo port-forward svc/storage-demo 5082:5080
```

Read `k8s/deployment.yaml` before applying: the image name, the IRSA role annotation and the
`CHANGE_ME` secrets are placeholders. The manifests include a demo Redis and PostgreSQL so they
apply on a laptop cluster; both should be managed services in a real one.

The Deployment runs two replicas, which is why its ConfigMap selects the Redis feed. With
`InMemory` there, the two pods would each scan the bucket and neither would tell the other's
clients about an upload.

### The desktop client

```bash
dotnet run --project src/StorageDemo.Client
```

A WPF file explorer. Thumbnails for images and video come from the server, video and audio play in
an embedded FlyleafLib player, text previews inline, and PDFs open in the system viewer. Everything
the media probe read is listed under the preview. Upload, download and delete all go over gRPC.

Player controls: scrub bar with click-to-seek, play and pause, ten-second skip, volume and mute,
playback speed, and fullscreen. Keyboard: space, left and right arrows, up and down for volume,
`M` to mute, `F` for fullscreen, `Esc` to leave it.

Above the grid there is a name search and two filters, by type and by age. They run against the
tiles already in memory through the collection view WPF puts around any bound list, so filtering
hides nothing it would have to fetch again and keeps every thumbnail it already has.

The **Server** box in the toolbar switches between endpoints without restarting, so one client
drives the Windows, Linux, Compose and Kubernetes instances in turn. It ships with the three
addresses above and remembers any address you type. The list lives in
`%APPDATA%\StorageDemoClient\servers.json`.

## Layout

```
protos/documents.proto            The gRPC contract, compiled by both server and client
src/StorageDemo.Core              Domain + application. No infrastructure packages, by design
src/StorageDemo.Infrastructure    Filesystem, S3, LiteDB, EF Core, seeding, media, streaming
src/StorageDemo.Api               gRPC services, REST controllers, health checks, composition
src/StorageDemo.Client            WPF explorer (Windows only)
tests/StorageDemo.Tests           Unit, contract and gRPC integration tests
```

The spec's five-project layout is collapsed to three: Domain and Application are one assembly
(`Core`), because the rule that matters is the dependency direction, and one assembly with no
infrastructure package references enforces that just as well while being smaller to navigate.

**Dependency direction:** `Api -> Infrastructure -> Core`. `Core` references logging abstractions
and a media-type lookup table, so `LiteDatabase`, `DbContext`, `IAmazonS3` and `FileStream` cannot
leak into it. The compiler enforces this, not a convention. The rule that keeps it honest is about
I/O rather than package count: a dependency-free lookup table is fine in `Core`, anything that
opens a file, a socket or a database is not. `MagicBytesValidator` needs the ASP.NET framework
reference, which is exactly why upload sniffing lives in `Api`.

## The two abstractions

The shape of this, in three pictures, is in
[docs/storage-and-persistence.drawio](docs/storage-and-persistence.drawio): the interface it
deliberately is not, the six ports with their two implementations each, and what changes between a
laptop and a cluster.

```csharp
public interface IFileStorage           // object storage: save, open, delete, exists, list
public interface IDocumentRepository    // document metadata: get, get all, add, upsert, delete
```

Deliberately not one `IStorageProvider` with both sets of methods. They fail differently, scale
differently and are configured separately, so they are separate.

## Consistency

A file store and a database share no transaction. Upload writes the file first, then the metadata,
and deletes the file if the metadata write fails. If that cleanup also fails it is logged and the
original exception still propagates.

Delete works in the opposite order and is idempotent at every layer: deleting a missing object or a
missing row succeeds.

This compensation approach is right for a demo. A system that must not leak orphans under any
failure should use a transactional outbox or a state machine with a reconciliation job, so a crash
between the two writes is recovered rather than merely unlikely.

## Thumbnails and metadata

Every image and video gets a thumbnail and a full metadata read: dimensions, duration, codecs,
bitrate, frame rate and whatever tags are embedded. Both come from FFmpeg, which decodes images as
well as video, so there is no second image library to install and keep in step.

**The type tables are not hand-written.** Extension to media type comes from `MimeTypesMap`, and a
short override list covers the few it answers unhelpfully for a document store, `.ts` above all,
where the TypeScript reading beats the transport stream one. `ContentTypeTests` pins the answers
this application actually depends on so a package upgrade cannot quietly reclassify a video.

**An upload's declared type is not evidence.** `MagicBytesValidator` reads the real type from the
leading bytes on both upload paths, and that is what gets stored and served back later. A JPEG
announced as `text/html` is stored as an image. Formats with no signature, plain text above all,
keep their declared type: absence of a magic number is not proof of lying.

**FFmpeg is bundled, never the host's, and called in-process.** The server loads the libav
libraries through FFmpeg.AutoGen and calls them directly: one open of the file answers both
questions, where launching `ffprobe` and then `ffmpeg` meant two process starts and two reads of
the same bytes. The libraries come from `DevEnvy.FFmpeg.Binaries.LGPL` for six platforms, and the
desktop client carries the ones FlyleafLib loads. A machine with no FFmpeg installed, and a
container with no `apt-get install ffmpeg`, behave identically.

The bindings are pinned to the FFmpeg 8.0 ABI the binaries package ships. Those two move together;
a mismatch fails at load with a named path rather than at the first null function pointer.

`Media__LibraryPath` points libav somewhere else, which is the supported way to change what FFmpeg
can do without touching code. That is how SRT gets enabled; see "Live streaming".

That bundle is LGPL, which means no PNG encoder and no x264. It does not matter: thumbnails are
encoded as JPEG, and decoding, which is what reading someone's upload actually needs, covers
everything from H.264 and HEVC to AV1.

The Linux libraries link against a handful of hardware-acceleration libraries they never use in a
container, so the image installs those, and it sets `LD_LIBRARY_PATH` so they can find each other:
Windows searches next to a loaded library, Linux does not. The image carries all of it, not the host.

**The client's FFmpeg is pinned to FlyleafLib's.** FlyleafLib 3.11 binds the FFmpeg 9 ABI, which no
NuGet package publishes natives for yet, so the client uses 3.10.4 against FFmpeg 7.1. Upgrade the
two together or neither; a mismatch fails at startup with "Loading FFmpeg libraries failed".

## Live streaming

An encoder connects to one address, names itself, and the stream exists from that moment. Nothing
is requested first, nothing is written to disk unless somebody asks for it, and recordings and
snapshots continue whether or not any client is watching.

The design and the reasoning behind every decision are in
[docs/design/server-side-stream-ingest.md](docs/design/server-side-stream-ingest.md); the
vocabulary it uses is defined in [CONTEXT.md](CONTEXT.md). For the shape of it in three pictures,
including what makes it scale in Kubernetes and what does not, open
[docs/video-server.drawio](docs/video-server.drawio).

### Three ports, one sentence each

| Port | Reaching it lets you |
| --- | --- |
| Ingest, SRT on 9000 | push a stream, and nothing else |
| Consumption, SRT on 9010 | watch live streams, and nothing else |
| API, 8080 and 5080 | everything else |

That is the whole reason they are separate: each can be exposed to a different network, and the
firewall statement stays short enough to be useful. Finished recordings and snapshots are documents
and stay on the API.

### Pushing a stream

The encoder names the stream in the SRT stream identifier. Two forms are accepted, because two
forms are what real encoders emit:

```bash
# The SRT Access Control convention. '#' must be written %23 inside a URL.
ffmpeg -re -i clip.mp4 -c copy -f mpegts 'srt://127.0.0.1:9001?mode=caller&streamid=%23!::r=live/camera1,m=publish'

# A bare name, which is what OBS and Teradek produce: their boxes are one free-text field.
ffmpeg -re -i clip.mp4 -c copy -f mpegts 'srt://127.0.0.1:9001?mode=caller&streamid=camera1'
```

`-re` matters. SRT is a connection, so a burst that ends immediately is not a stream and the
receiver sees the far end hang up mid-handshake.

Names are validated and **rejected**, never cleaned up. The name is the identity, so two names
normalising to the same string would let one encoder take over another's stream. Letters, digits,
dot, dash, underscore and slash, up to 128 of them.

The quickest way to see it work is the sender in the Compose stack, which just pushes and
reconnects on its own:

```bash
docker compose -f docker/docker-compose.yml --profile live up -d srt-sender
```

**To push something real**, `Restream.ps1` in the repository root takes any source ffmpeg can read
and stands in for a camera:

```powershell
.\Restream.ps1 .\clip.mp4 live/camera1 -Loop        # a file, played round and round
.\Restream.ps1 rtsp://192.168.1.50/stream1 carpark  # a camera that speaks RTSP
.\Restream.ps1 https://example.com/feed.m3u8 motorway
```

It remultiplexes rather than re-encodes, so a large feed costs almost no processor; pass
`-Transcode` for a source whose codecs a transport stream cannot carry. It uses the FFmpeg in
`ffmpeg/win-x64/` because that is the one with SRT, refuses a name the service would reject rather
than letting the connection be dropped silently, and reconnects on its own until you stop it.

### Watching a stream

A viewer connects to the consumption port the same way an encoder connects to ingest: one address,
the stream named in the identifier.

```bash
# A bare name. Works on every FFmpeg, and is what to use unless you need a position.
ffplay -analyzeduration 1000000 -probesize 1000000 -fflags nobuffer \
  'srt://127.0.0.1:9011?mode=caller&streamid=live/camera1'

# Twenty seconds back, resolved to the nearest position a decoder can start from, so what arrives
# is at least twenty seconds and usually a little more.
ffplay 'srt://127.0.0.1:9011?mode=caller&streamid=%23!::r=live/camera1,user_from=20,m=request'
```

**Prefer the bare name from a command line.** The convention's form begins with `#`, which starts a
fragment in a URL and so has to be written `%23` there, and FFmpeg versions disagree about when they
decode it:

| FFmpeg | What the service receives |
| --- | --- |
| Before 7.0 | the literal `%23!::...`, which the service rewrites and accepts |
| 7.x | nothing: the `#` is restored before the query is split, so it truncates there |
| 8.1 | the identifier, decoded correctly |

A bare name has no `#` and sidesteps all of it. Anything that needs the envelope, such as a rollback
position, should pass the identifier as a protocol option rather than in a URL, which is what the
service and the desktop client both do.

**Those probe flags are most of a viewer's join time.** libav reads five seconds of a transport
stream before deciding what is in it, and for a camera that is dead time on every join and every
reconnect. Capping it takes a join from about eight seconds to two, measured in
[docs/replica-failover.md](docs/replica-failover.md). The desktop client sets the same limits for
live playback and drops them again for a stored recording, which wants libav's own judgement back.

Raise them for a camera that starts its audio late: a probe that ends before the audio track
appears yields a stream that plays without sound.

Rollback is bounded by the buffer, available whether or not anything is recording, and never served
from a recording. Returning to live is a new connection rather than a control message, which keeps
the connection one-way and stateless.

The desktop client keeps streams and documents in separate tabs, streams first. A stream is a tile
with a LIVE badge carrying a preview the server keeps current, and selecting it plays from the
consumption port. An interrupted stream is dimmed rather than removed: a tile that vanishes and
returns is worse than one showing a state.

### Recording and snapshots

Both are taken server-side and continue whether or not any client is connected, so closing the
client does not stop a recording that is running.

```
GET    /api/live                                       transports and streams on air
GET    /api/live/stream/{name}                         one stream
GET    /api/live/preview/{name}                        the picture now
POST   /api/live/snapshot/{name}                       store a full-resolution picture as a document
POST   /api/live/record/{name}    {"seconds":30}       start, or extend what is running
DELETE /api/live/record/{name}                         end it now
POST   /api/live/manual  {"name":"...","url":"..."}    a stream for a protocol that cannot name itself
DELETE /api/live/stream/{name}                         drop the connection and end the stream
```

The verb comes before the name in every route, which reads oddly and is deliberate: a stream name
may contain slashes, so it has to be the trailing catch-all.

**A trigger is a trigger.** A person pressing record and a detector firing are one caller on one
path, which is what makes detection genuinely free to add later rather than a second code path.

**Every recording begins in the buffer.** A trigger at a moment yields a capture starting about
five seconds before it, so an event already under way when it was noticed is still caught. One
recording runs at a time per stream, and a further trigger extends its end rather than starting a
near-duplicate.

**A recording is written in parts and stored as it goes, and read back as one file.** A camera
recording for six hours cannot wait until it ends to be stored: the bytes would sit on one pod's
disk the whole time and die with it. So a few minutes are muxed to a local file, uploaded through
the ordinary storage path as an ordinary object, and the local file deleted.

| | |
| --- | --- |
| Disk and memory while recording | one part, whatever the total length |
| Lost if the pod goes | at most the part in progress |
| Visible to whoever opens it | one document, one name, one size, one download |

None of that reaches the document list. The parts are joined on the way out and the
joined stream is seekable end to end, so a player scrubs through six hours with one ranged read
rather than fetching the lot. The document appears with the first part and grows, so a recording
still running can already be opened and watched.

Nothing here needs multipart upload or anything else only S3 has: each part is one ordinary
write, and both providers stay equal. That is the constraint the whole repository exists to
demonstrate, and it is why this is parts rather than a single streaming upload.

Parts live under `recordings/` rather than `documents/`, exactly as thumbnails do. Under the
scanned prefix, the storage monitor would import each one as a document of its own and a recording
would appear as a list of five-minute files.

**A snapshot is a fresh decode**, not the preview. The preview is decoded at keyframes only, so it
is stale by up to a keyframe interval; a snapshot is a deliberate act performed once, and one
decode is nothing next to being twelve seconds wrong about the moment somebody meant to capture.

**A stream never becomes a document by itself.** Documents come only from snapshots and recordings
someone asked for, which is what makes unattended ingest safe to leave running.

### How it is built

One demultiplexer per stream feeds one hub, and packets flow one way through it.

- **The packet tier is the fan-out.** Subscribers receive demultiplexed packets filtered by stream
  index, with no decoding anywhere near them. The recorder, the viewer and a future KLV extractor
  all live here, which is what keeps a stream cheap when nobody is watching.
- **The frame tier hangs off it.** One decoder is a packet subscriber and republishes pictures, so
  a stream is decoded once however many things want them, at the rate its subscribers ask for.
  Today that is keyframes only, for the preview.
- **The buffer is held as segments**, each beginning where a decoder can start. Joining a viewer,
  rolling back and cutting a recording's pre-roll are all one operation: choose a segment.
- **Every consumer that writes bytes owns its own muxer.** A recording started at ten past and a
  viewer who joined at twelve past have different timelines and different first packets.

Backpressure differs by consumer, because overflow does not mean the same thing for everyone. A
viewer that falls behind skips forward to live, because a viewer must never accumulate delay. A
recorder that overflows stops and marks its document truncated, because dropping packets would
write a hole into a file that claims to be a recording.

### About SRT

SRT is a build choice, not a code one, and the whole ingest design rests on what the bundled libav
can actually do. That was established empirically rather than assumed; see
[.scratch/server-side-ingest/issues/01-srt-listener-streamid.md](.scratch/server-side-ingest/issues/01-srt-listener-streamid.md).

**A libav SRT listener accepts exactly one caller and then closes the listening socket.** Many
concurrent streams on one port are still possible, because libsrt multiplexes its sockets over one
shared UDP port per process and libav sets the reuse-address option, so the listener is re-opened
immediately after each accept while earlier connections keep running. Both media ports run one of
these carousels.

Three consequences are load-bearing and are not going away:

- **The identifier is only reachable through a log line.** FFmpeg reads it on accept and logs it at
  verbose level without storing it anywhere the application can reach. A custom log callback
  captures it without raising the global log level, and a **boot-time self-test** confirms the
  capture still works and fails loudly if it does not. Upgrade FFmpeg and that is what tells you.
- **The backlog is one**, giving roughly two or three accepts a second, on each port. Senders and
  players must retry, which encoders do by nature.
- **The name arrives after accept, never before.** Nothing can be refused by name; an unwanted
  connection is accepted and then dropped. Any rule about who may push has to act during the
  handshake instead, which is why an SRT passphrase is the stronger authentication candidate.

Calling libsrt directly was investigated and rejected: it is statically linked into libavformat and
not re-exported, so going direct means vendoring another binary for every platform and running a
second SRT stack beside the one already loaded.

**Two builds are in play.** The NuGet package is LGPL and has no libsrt, so it is replaced by one
that does:

| | Where it comes from | Transports |
| --- | --- | --- |
| Default | the FFmpeg NuGet package | udp, rtp, rtmp, tcp, http |
| With libsrt | `scripts/fetch-ffmpeg.sh` locally, downloaded in the Dockerfile for the image | the above plus **srt** and **rist** |

```bash
scripts/fetch-ffmpeg.sh   # then rebuild
```

It drops an FFmpeg 8.1 GPL shared build into `ffmpeg/win-x64/`, which the build overlays over the
packaged one. The folder is gitignored: 190 MB of binaries do not belong in a repository. Without
it everything still builds and runs, minus live streaming, so a fresh clone needs no setup.

The version is pinned deliberately. The FFmpeg.AutoGen bindings target one ABI, and 8.1 carries the
same library majors as 8.0 (avcodec 62, avutil 60, avformat 62). A 9.x build loads and then fails on
the first call, so the two move together. `Media__LibraryPath` points libav somewhere else entirely,
and **it has to be a shared build**: a static build ships `ffmpeg.exe` and `ffprobe.exe` and nothing
to load. Most ready-made installs are static, including the Chocolatey one, which is why pointing
this at `where ffmpeg` usually will not work.

### Running it

Live streaming is **off unless configured**, because switching it on opens a port anybody who can
reach it may push a stream into:

```
Live__Enabled=true
Live__Token=<secret>        # required in X-Storage-Token on every API call
Live__IngestPort=9000
Live__ConsumptionPort=9010
Live__SrtLatencyMs=120      # SRT's buffering delay on both ports; 60 on a LAN, more on the internet
Live__ProbeSeconds=1        # how long libav may spend working out what a camera is sending
Live__ProbeBytes=1048576    # and how much it may read doing it
Live__RecordingPartMinutes=5   # how much of a recording a pod holds at a time
Live__MaxRecordingMinutes=720     # the ceiling on one recording

Documents__PublicBaseUrl=http://127.0.0.1:8081   # where a client streams a long recording from
```

`ProbeSeconds` and `ProbeBytes` are dead time between a camera connecting and its stream being on
air. libav's own defaults are five seconds and five megabytes; MPEG-TS repeats its tables every
hundred milliseconds, so a second is generous for a camera that presents everything at once. Raise
them for one that starts its audio late.

`RecordingPartMinutes` is what bounds a pod's disk for a recording of any length, and how much
is lost if the pod goes. `Documents__PublicBaseUrl` is how the desktop client knows where to stream
a long recording from; without it the client downloads instead, which is right for ordinary files
and painful for a six hour one.

**Readiness is about the media ports**, not only about storage and a database. A replica whose
ingest port is not accepting, whose stream identifier self-test failed, or whose FFmpeg has no SRT
takes itself out of the Service rather than swallowing encoders it cannot serve.

Compose publishes both upward, offset by one from the container ports so a local `dotnet run` and
the stack can coexist: ingest on 9001, consumption on 9011.

**If a stream never arrives**, check the machine before the code. Inbound UDP to an unknown
executable is blocked by default on Windows, loopback included on some managed devices, and the
symptom is a port that shows as bound with nothing ever accepted. Allow the API through the
firewall, or run the Compose stack, where the listener is inside a container.

### Streams and replicas

The registry is keyed by **name**, not by an opaque identifier, which is what lets a reconnect
resume rather than create. A connection identifier survives as a per-connection detail for logs and
for telling one attempt from the next, but nothing looks a stream up by it.

| Concern | Where it lives | Why |
| --- | --- | --- |
| The connection, the buffer, any recording | The one replica that owns it | Cannot be anywhere else |
| Which streams exist, their state and stats | Shared registry, in-memory or Redis | Any replica may be asked |
| Who owns a name | The owner field of the registry entry | It is the claim |
| Preview, snapshot, record | Forwarded to the owner over HTTP | The bytes exist in one place |
| A viewer on the wrong replica | Relayed from the owner over SRT | SRT has no redirect |

**The newest connection wins a contested name.** A replica finding the name already held takes it
anyway and records itself as owner; the previous owner discovers on its next heartbeat that it has
lost the claim, shuts its hub down and closes any recording as a complete document. Refusing the
newcomer was rejected because it makes recovery wait on a timeout this service does not control: an
encoder actively pushing bytes is more real than a socket that has not yet noticed its peer is gone,
and SRT takes seconds to work that out.

**Interrupted is a state.** When a feed stops arriving the stream stays claimed, stays listed, and
keeps its hub, buffer and any recording alive for a grace period of thirty seconds. A recording
keeps running and records the silence. After that the stream is gone and leaves nothing behind: the
documents it produced are its trace.

`k8s/live/deployment.yaml` is a **Deployment**, not a StatefulSet, with two Services. Stable pod
identity existed only so a sender could be pointed at a particular pod, and nothing does that any
more: a replica records its own address when it claims a name. That deletes the headless Service
and the per-pod ingest Services together.

Two things are easily conflated and only one is needed. **Per-flow stickiness is required**, because
an SRT connection is a UDP flow whose packets must all reach the same pod; that is ordinary
connection tracking and every load balancer in play does it without being configured to.
**Stickiness across reconnects is not required**, because the newest connection wins the name. No
affinity configuration, no session tables, nothing to get wrong.

What a replica disappearing actually costs a producer and a viewer is measured rather than guessed,
in [docs/replica-failover.md](docs/replica-failover.md).

Local runs need none of this. With `Messaging__Provider=InMemory` the registry is a dictionary,
there is nowhere to forward to, and the two ports are just two ports. The cluster shape is
configuration, not a different program.

### Accepted limits

Each of these is a deliberate trade, not an oversight.

- A displaced owner closes its recording rather than moving it, so a flapping encoder leaves
  several documents. What was already recorded is kept: parts are stored as they complete, so
  only the part in progress is lost.
- A recording still running is visible in the document list and grows. That reverses the original
  design, which held a document back until there was a whole file; a part that has been stored
  is not half-written, and for a camera the alternative was losing everything before a restart.
- Viewers of one stream all funnel through its owner, so adding replicas does nothing for a single
  popular stream. Correct for contribution, where streams outnumber viewers; wrong for
  distributing one stream to an audience.
- The stream identifier is captured from a log line, guarded by a boot-time self-test.
- Nothing can be refused by name; only accepted and dropped.

## Why uploads feel instant

An upload returns as soon as the bytes and the metadata row are safe. Probing a video and decoding
a preview frame takes seconds, and no caller should wait for that.

```text
upload  ->  store bytes  ->  write row  ->  publish Added  ->  return
                                                    |
                                          analysis queue
                                                    |
                                    probe + thumbnail  ->  update row  ->  publish Updated
```

The client meets it halfway. A tile appears the moment you pick a file, before a byte is sent,
carrying the local image as its thumbnail where it can decode one. When the upload returns, that
same tile becomes the real document. When the analysis finishes, the `Updated` event fills in the
server thumbnail and the metadata. A failed upload removes the tile again, and a failed delete puts
it back, so the grid never claims something the store cannot back up.

Change events update one tile rather than reloading the grid, which is what keeps thumbnails from
flickering every time an analysis completes.

## The storage monitor

Files can appear, change or vanish without going through this API: an operator copies something
into the bucket, another tool deletes an object. A background service rescans the store on an
interval and brings the metadata back in line.

- An object with no row is imported, with an id derived from its key so rescans never duplicate it,
  then queued for a thumbnail like any upload.
- A row whose object is gone loses its row.
- A changed size updates the row.

Storage is the source of truth. Every change is published on a feed that the gRPC `Watch` stream
forwards to connected clients, so the explorer updates without polling.

That feed is one of three things that have to be shared once there is more than one replica. See
"Scaling out".

A pass runs on whichever comes first, a notification or the interval:

- **Local changes** reach it through `FileSystemWatcher`, registered only when the filesystem
  provider is active. A file dropped into the storage directory shows up in about a second.
- **S3 and MinIO** post their bucket notifications to `POST /api/storage/events`. The body is never
  parsed: a pass is a full diff, so the only thing a notification usefully says is "look again",
  and that holds whatever shape the sender uses.
- **The interval** is the backstop. Watchers drop events when their buffer overflows and a webhook
  delivery can be lost, so a missed event costs latency and nothing else.

The endpoint stays closed until `StorageMonitor__WebhookToken` is set, and compares the
`X-Storage-Token` header in fixed time. Anyone who can reach it can make the service do work.

Tune the rest with `StorageMonitor__IntervalSeconds`, `StorageMonitor__DebounceMilliseconds` (a
folder copy fires one event per file, so a burst is allowed to settle) and `StorageMonitor__Enabled`.

## Scaling out

The goal is a service that scales in Kubernetes and still runs on a laptop with nothing installed.
Those pull in opposite directions, so anything held in memory is behind a seam with two
implementations, chosen by one setting:

```
Messaging__Provider=InMemory   # default: single instance, nothing to install
Messaging__Provider=Redis      # several replicas
Messaging__Redis__ConnectionString=redis:6379
```

Three pieces sit behind it, and each breaks differently with replicas:

| Piece | In memory | On Redis | What breaks without it |
| --- | --- | --- | --- |
| Change feed | Subscribers in this process | Pub/sub channel | A client only hears about uploads that landed on its own pod |
| Live session registry | A dictionary | A hash, one field per session | The API reports whichever pod you happened to reach, and a stop cannot find its stream |
| Analysis queue | A channel | A list every replica pops from | The pod that accepted an upload is the only one that can thumbnail it, and the work dies with that pod |
| Scan lock | Names held in a dictionary | `SET NX PX` with a compare-and-delete release | Every pod lists the whole bucket on its own schedule and announces the same changes again |

Verified with two replicas against real Redis: a file uploaded to the first was analysed by the
second, and an object dropped straight into the bucket was imported once rather than twice.

```bash
docker compose -f docker/docker-compose.yml up -d --scale api=3
```

The published ports are ranges, so each replica takes the next free one: 8081, 8082, 8083 for REST
and 5081 upward for gRPC. Point the desktop client at any of them.

**What stays per-process on purpose.** The subscriber list inside a replica is local because those
are its own client connections. The media-type table is a static lookup. The reconciler's listing
buffer lives for one pass. None of those are shared state pretending to be local.

**What is deliberately not durable.** Work already popped off the queue is lost if that pod dies,
in both implementations. The cost is one document without a preview, and re-uploading it fixes
that. Claim-and-acknowledge, on a Redis stream with a consumer group, is the upgrade when a
thumbnail has to be guaranteed rather than merely likely.

**LiteDB does not scale out, and is not meant to.** It is a file, so it belongs to one process.
`Filesystem + LiteDB` is the laptop scenario; `S3 + PostgreSQL + Redis` is the cluster one, and the
providers are chosen independently so anything in between also works.

## Configuration

Development defaults, from `appsettings.json`:

```json
{
  "Storage": { "Provider": "FileSystem", "FileSystem": { "RootPath": "./data/files" } },
  "Database": { "Provider": "LiteDb", "LiteDb": { "Path": "./data/database/app.db" } }
}
```

Containers use environment variables:

```
Storage__Provider=S3
Storage__S3__Bucket=storage-demo
Storage__S3__Region=eu-north-1
Storage__S3__ServiceUrl=http://minio:9000     # unset against real AWS
Storage__S3__ForcePathStyle=true              # false against real AWS
Database__Provider=Postgres
Database__Postgres__ConnectionString=...
```

Options are validated at startup, so a misconfigured deployment fails immediately rather than on
the first upload.

Compose adds the change feed and the notification endpoint:

```
Messaging__Provider=Redis
Messaging__Redis__ConnectionString=redis:6379
StorageMonitor__WebhookToken=local-demo-token
```

## API

gRPC is the primary surface (`protos/documents.proto`): `List`, `Get`, `Download` and
`DownloadThumbnail` (server streaming), `Upload` (client streaming), `Delete`, `Watch` (server
streaming) and `GetProviders`. It also carries what the desktop client needs of live streaming:
`ListLive`, `DownloadLivePreview`, `SnapshotLive`, `RecordLive` and `StopLiveRecording`, so the
client needs only its one connection.

REST covers the same ground for curl and Swagger:

```
POST   /api/documents                 multipart/form-data
GET    /api/documents
GET    /api/documents/{id}
GET    /api/documents/{id}/content    add ?download=true to force a save dialog
GET    /api/documents/{id}/thumbnail  404 when the type has no preview
DELETE /api/documents/{id}
GET    /health/live                   process only, no external dependencies
GET    /health/ready                  storage, database, and the media ports accepting
```

REST, Swagger and the health probes are on port 8080; gRPC is on 5080. Media travels over neither:
it arrives on the ingest port and leaves on the consumption port, both SRT. See "Live streaming".

Uploads and downloads stream end to end. Nothing buffers a whole file.

## Security

- Storage keys are generated, never taken from the client. Uploaded filenames are stripped of any
  path and kept only as display metadata.
- The filesystem provider resolves every key and refuses anything that escapes the configured root,
  including a sibling directory that merely shares its prefix.
- Uploaded bytes are served with `X-Content-Type-Options: nosniff` and a sandboxing
  `Content-Security-Policy`, so an uploaded HTML or SVG file cannot run as first-party script.
- Upload size is capped by `Uploads__MaxBytes`.
- No credentials in the repository. S3 uses the AWS provider chain, which means IRSA or an instance
  role in a cluster. Database credentials come from environment or secret references.
- Provider exceptions are translated at the infrastructure boundary, so `AmazonS3Exception` and
  `NpgsqlException` never reach the application layer.

## Migrations

PostgreSQL uses EF Core migrations, in `src/StorageDemo.Infrastructure/Database/PostgreSql/Migrations`.

```bash
dotnet ef migrations add <Name> --project src/StorageDemo.Infrastructure
```

The application applies them at startup, which is convenient for Compose. In Kubernetes run the
migration Job in `k8s/deployment.yaml` instead, so replicas cannot migrate concurrently.

## Tests

```bash
dotnet test
```

- **Unit:** `DocumentService` and the reconciler against in-memory fakes. No disk, no network.
- **Contract:** one specification per abstraction, executed against every implementation, so
  LiteDB and PostgreSQL are held to identical behaviour, as are the filesystem and S3 providers.
- **Integration:** the real application hosted in-process and driven over a real gRPC channel.
- **Media:** the real analyzer calling libav in-process, on sample files the bundled ffmpeg command
  line generates, so the repository carries no binary fixtures.
- **Streaming:** the multiplexer for real, including a stream sent over a network transport and read
  back by a listener, a stream pushed into the running application that comes out as a document, a
  viewer watching that stream while it runs, and a preview frame appearing as it is decoded.

PostgreSQL contract tests skip unless a database is available:

```bash
POSTGRES_TEST_CONNECTION="Host=localhost;Database=storagedemo_test;Username=storagedemo;Password=storagedemo" dotnet test
```

## Persistence, per scenario

`Filesystem + LiteDB` needs durable local storage: both the files and the database are on disk, and
a replaced container without a volume loses everything.

`S3 + PostgreSQL` keeps the pod stateless, which is why the Kubernetes deployment declares no
volume beyond an `emptyDir` for `/tmp` and can scale to several replicas.
