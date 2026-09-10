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
kubectl apply -f k8s/
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

> **Being redesigned.** What follows describes what is built today, where a stream is requested
> before it exists. The replacement, where an encoder connects unannounced and names itself, is
> designed in [docs/design/server-side-stream-ingest.md](docs/design/server-side-stream-ingest.md).


Streams go both ways, as MPEG-TS over a UDP-family transport, remuxed rather than re-encoded:
packets are copied from one container to the other with their timestamps rescaled, so a session
costs almost no CPU.

```
POST   /api/live/ingest      {"name":"match","url":"udp://0.0.0.0:9000"}   listen, record, store
POST   /api/live/egress      {"documentId":"...","url":"udp://host:9000"}  send a document out
GET    /api/live                                                          transports and sessions
GET    /api/live/{id}/stream                                              watch it while it runs
GET    /api/live/{id}/thumbnail                                           latest preview frame
DELETE /api/live/{id}                                                     ask the owner to stop
```

gRPC carries the read side for the desktop client, `ListLive` and `DownloadLivePreview`, so the
client needs only its one connection. Starting and stopping stay on REST.

**In:** the service listens, records what arrives, and when the stream ends hands the recording to
the ordinary upload path. It becomes a document with a thumbnail and metadata like any other file,
because it goes through the same `IDocumentService`. It records to disk first: a live stream has no
length until it ends, and the storage abstraction wants a stream it can read to completion.

**Out:** a stored document is multiplexed to a destination. Both directions are the same remux
loop, so the transport is only ever the scheme on the URL.

### About SRT

SRT is a build choice, not a code one. FFmpeg exposes `srt://` as just another protocol to the same
muxer, so the code here already accepts it. What decides whether it works is whether the loaded
libraries were compiled with libsrt.

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
packaged one. The folder is gitignored: 190 MB of binaries do not belong in a repository. Skip the
script and everything still builds and runs, minus SRT, so a fresh clone needs no setup.

The version is pinned deliberately. The FFmpeg.AutoGen bindings target one ABI, and 8.1 carries the
same library majors as 8.0 (avcodec 62, avutil 60, avformat 62). A 9.x build loads and then fails on
the first call, so the two move together.

Asking for `srt://` on a build without it gets a refusal naming the protocols that are available,
rather than a numeric failure from inside libav. `GET /api/live` reports the same list, so a caller
never has to guess.

`Media__LibraryPath` points libav somewhere else entirely, for a build kept outside the repository:

```
Media__LibraryPath=D:\ffmpeg-full-shared\bin
```

**It has to be a shared build.** Since the libraries are loaded into this process, there have to be
libraries: `avcodec-62.dll` and friends on Windows, `libavcodec.so.62` elsewhere. A static build
ships `ffmpeg.exe` and `ffprobe.exe` and nothing else, so there is nothing to load. Most
ready-made installs are static, including the Chocolatey one, which is why pointing this at
`where ffmpeg` usually will not work. Look for a download whose name says "shared".

The setting is checked at startup and refuses a directory with no libraries in it, naming the
reason, rather than starting and then failing on the first thumbnail.

Using the machine's FFmpeg is therefore possible but not what this repository does: it reintroduces
exactly the dependency bundling removed. Fetching a shared libsrt build into the application's own
output keeps the property that nothing depends on what the host happens to have, at the cost of a
GPL-flavoured licence, which for a demo is not a constraint.

### Running it

Live streaming is **off unless configured**, because these endpoints make the service open sockets
and send data to an address the caller chooses:

```
Live__Enabled=true
Live__Token=<secret>        # required in X-Storage-Token on every call
Live__AllowedSchemes=udp,rtp,srt
```

The scheme allowlist is the part that matters. Without it, `file:///etc/passwd` as an ingest URL
would turn "start a stream" into "read anything on this disk", and an egress URL would turn the
service into a way to post bytes to an arbitrary host.

**The quickest way to see it work** is the sender that ships with the Compose stack. It starts a
session, then pushes a test pattern into it over SRT, and reconnects on its own:

```bash
docker compose -f docker/docker-compose.yml --profile live up -d srt-sender
```

It is off unless asked for, since a permanently running stream is not what every `up` should mean.
Alpine's ffmpeg is built with libsrt, so nothing has to be built for it. `docker/srt-sender.sh` is
about thirty lines and shows the shape of a real contribution feed: the service listens, the encoder
calls it.

```bash
curl -X POST http://127.0.0.1:8081/api/live/ingest \
  -H 'X-Storage-Token: local-demo-token' -H 'Content-Type: application/json' \
  -d '{"name":"demo","url":"srt://0.0.0.0:9000?mode=listener"}'

ffmpeg -re -i clip.mp4 -c copy -f mpegts 'srt://127.0.0.1:9001?mode=caller'
```

`-re` matters. SRT is a connection, so a burst that ends immediately is not a stream and the
receiver sees the far end hang up mid-handshake.

Plain UDP works the same way, with any encoder or with the ffmpeg that ships in the image:

```bash
curl -X POST http://127.0.0.1:8081/api/live/ingest \
  -H 'X-Storage-Token: local-demo-token' -H 'Content-Type: application/json' \
  -d '{"name":"demo","url":"udp://0.0.0.0:9000"}'

ffmpeg -re -i clip.mp4 -c copy -f mpegts 'udp://127.0.0.1:9001?pkt_size=1316'
```

Compose publishes UDP 9001 upward, one per replica.

**If a stream never arrives**, check the machine before the code. Inbound UDP to an unknown
executable is blocked by default on Windows, loopback included on some managed devices, and the
symptom is a session that stays in `Waiting` with no packets while the port shows as bound. It
reproduces without this application at all: run one ffmpeg as a receiver and push to it with
another. Allow the API through the firewall, or run the Compose stack, where the listener is inside
a container.

### Watching a stream

The desktop client keeps streams and documents in separate tabs, streams first. They are different
things: a stream is happening now and disappears when it stops, a document is a file that stays, and
the sorting, filtering and actions that apply to one make no sense for the other.

A running stream is a tile with a LIVE badge, carrying a preview frame the server decodes from the
recording every few seconds. Selecting it plays the stream straight from `GET /api/live/{id}/stream`,
which serves the recording as it is written rather than a download of whatever existed when the
request arrived. When the stream stops, the tile goes.

For that to work the server has to know its own public address, since the URL it hands out has to
be one the viewer can actually reach:

```
Live__PublicBaseUrl=http://127.0.0.1:8081
```

Without it the client says so plainly instead of handing the player an address that goes nowhere.

The muxer writes through rather than buffering, which is what makes the preview and the playback
live at all. Left to itself, libav holds a recording in memory until the muxer closes, so a viewer
sees nothing until the stream has already ended.

### Sessions and replicas

A session owns a socket, so it belongs to the replica that started it. Everything else about it is
shared, which is what lets any replica answer for a stream it is not running:

| Concern | Where it lives | Why |
| --- | --- | --- |
| The socket and the recording | The one replica | Cannot be anywhere else |
| Session list, state, stats | Shared registry, in-memory or Redis | Any replica may be asked |
| Stopping | A flag the owner acts on | Only the owner can close the socket |
| Preview and playback | Proxied to the owner | The bytes exist in one place |

That split is what decides the deployment shape. **Inbound media has to be pod-addressable**,
because a load balancer spreading MPEG-TS datagrams across replicas gives each of them a fraction
of a stream and none of them a decodable one. **Everything else does not**, because a replica that
receives a request for someone else's stream reads through to the owner over the headless Service.

`k8s/live/statefulset.yaml` is that shape: a StatefulSet for stable pod names, a headless Service
for pod-to-pod DNS, and one Service per pod for the ingest port. Apply it instead of
`deployment.yaml` and `service.yaml`, never alongside.

- **k3s on one node:** the per-pod Services are `NodePort`, so a sender uses `node-ip:30901`.
- **Cilium across nodes:** make them `LoadBalancer` with addresses from an LB-IPAM pool, and keep
  `externalTrafficPolicy: Local` to preserve the sender's address and avoid a second hop.

A single shared Service with `sessionAffinity: ClientIP`, or Cilium's Maglev hashing, mostly works
for a steady sender and is not what I would build on: affinity lapses on conntrack timeout, rehashes
when replicas change, and tells the caller nothing about which pod took the stream. SRT survives
that better than raw UDP because it re-handshakes; plain MPEG-TS over UDP does not.

Local runs need none of this. With `Messaging__Provider=InMemory` the registry is a dictionary,
there are no peers to proxy to, and the whole thing works with nothing installed.

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
streaming) and `GetProviders`.

REST covers the same ground for curl and Swagger:

```
POST   /api/documents                 multipart/form-data
GET    /api/documents
GET    /api/documents/{id}
GET    /api/documents/{id}/content    add ?download=true to force a save dialog
GET    /api/documents/{id}/thumbnail  404 when the type has no preview
DELETE /api/documents/{id}
GET    /health/live                   process only, no external dependencies
GET    /health/ready                  storage and database reachable
```

REST, Swagger and the health probes are on port 8080; gRPC is on 5080. The live streaming control
plane is REST only, deliberately: it is a handful of administrative calls, and the stream itself
travels over neither protocol. See "Live streaming".

```
```

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
