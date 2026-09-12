# Plan: a thousand streams, SRT both ways, lowest latency

## Destination

One cluster takes a thousand live inputs, most pushed over SRT by encoders and some pulled by the
service from cameras, and serves any of them to an SRT player with the least latency the transport
allows. A few are watched all the time; the rest have a live preview and can be opened on a click.
Processing attaches to any stream without living in the pod that owns it.

Constraints, fixed by the repository owner:

- Viewers speak SRT. Not HTTP, not HLS, not WebRTC.
- Lowest possible end-to-end latency.
- Push and pull ingest both exist and both matter.
- No media server in front. Everything is a service in this repository.
- The desktop client speaks gRPC. REST is the surface for AI agents and command-line tools, so a
  live feature is not finished until it is on the proto as well as on a route.

## What is wrong today, in one line each

| Ceiling | Cause | Fixed by |
| --- | --- | --- |
| Two or three accepts a second per port per pod | libav's SRT listener: backlog of one, socket closed after every accept | Phase 1 |
| Nothing refusable before accept, no authentication possible | The name is read from a log line after the handshake | Phase 1, Phase 4, Phase 5 |
| 120 ms of SRT latency on every hop, unconfigurable | No `latency` option set anywhere | Phase 2 |
| A viewer on the wrong pod pays a second SRT hop through the owner's carousel | The in-cluster relay is SRT to SRT | Phase 3 |
| A pod accepts until it falls over | No per-pod cap, and nothing to scale on | Phase 4 |
| A pulled stream dies with its pod | Manual streams are created on one pod and never re-claimed | Phase 6 |
| A second publisher with the same name takes the stream over | "Newest connection wins" was the design; the owner has decided a live name is locked | Phase 1b |

The pipeline behind the listener, one demultiplexer per stream feeding a hub with a rolling buffer,
one shared decoder and packet subscribers that come and go, is the right shape and is not touched.

## Research behind it

Three reports in `research/`, every fact with a source URL, written by subagents against primary
sources and then folded into the phases below. Where a report could not confirm something it says
so, and the plan carries that uncertainty rather than hiding it.

| Report | Feeds | Corrected on the way |
| --- | --- | --- |
| `libsrt-api.md` | Phases 1, 1b, 5 | The callback signature and thread, the rejection codes and values, the streamid not being inherited, packaging names, and that a local close is only documented to unblock `srt_accept` |
| `srt-latency-and-ffmpeg-caller.md` | Phases 0, 2, 3 | Latency negotiation is per direction so `SRTO_LATENCY` is needed, the 60 ms floor, `SRTO_MAXBW` defaults to unlimited, FFmpeg never reports a rejection reason and never retries, and Kestrel has no HTTP/1.1 trailers |
| STANAG 4609 Ed 5 / MISP-2019.1 (NSO 2907), via MISB ST 0902, 1402, 0102, 0601 | KLV extraction, Phase 8, the client | Named by the owner after the first KLV brief; replaced seven hand-picked tags with the mandated minimum set and added carriage-mode alignment and the security marking |
| `k8s-autoscaling-and-metrics.md` | Phase 4 | The Prometheus exporter is still prerelease, the gauge should be an up-down counter, pod-deletion-cost steers scale-down, and a retry from a new source port is re-balanced under every default |

## Where this stands

Phases 0, 1, 1b and 2 are built and committed on `srt-listener-libsrt`. Everything else is still
only a plan.

| | State |
| --- | --- |
| Phase 0, the load rig | Built and run against one container. Numbers in `baseline.md` |
| Phase 1, own the listener | Built, and passing on Linux and Windows |
| Phase 1b, the name lock | Built, and passing on Windows. Never run on real pods |
| Phase 2, latency | Built, and passing on Windows |
| Phase 3 through 8 | Planned only |

**The baseline changed the plan rather than confirming it.** Four things, all in `baseline.md`
and folded into the phases below: Phase 4's rule for sizing a pod has no solution and is rewritten;
a gauge of streams owned is blind to overload, which the API reported as 250 contented streams
while delivering a fifth of the media; memory is a function of bitrate and not of the buffer
ceiling; and the unconditional preview decode looked like the dominant cost at scale, which became
Phase 8.

**That last one was wrong, and `preview-cost.md` is the correction.** Isolating the preview put it
at about a fifth of the pod, 0.7 of a core at 100 streams and 0.9 at 150, measured two independent
ways that agree. What actually dominates is two threads per stream at about 1.7 percent of a core
between them, the demultiplexer this service starts per feed and libsrt's own per-socket timestamp
thread, which is linear and more than twice the preview. Phase 8 survives on its other argument,
that detection must decode every stream anyway and belongs on a GPU, but it is no longer the
largest win and its priority drops.

**What is actually verified.** `docker/Dockerfile.test` runs the suite on Linux with libsrt from
apt: 191 passed, 0 failed, 8 skipped, the 8 being the pre-existing Postgres tests. All eleven SRT
tests executed. Twenty senders started together were all accepted in 211 ms, which is the ceiling
this phase existed to remove. A rejection code was observed reaching the caller, which is what
Phases 1b and 4 are built on. And `srt_close` was shown to be what unblocks a blocked read, the
one behaviour the research could not settle from documentation.

**Windows runs it too, now.** libsrt 1.5.6 built through vcpkg, `scripts/fetch-libsrt.sh` put it
beside the FFmpeg natives, and the full suite is 194 passed, 0 failed, 8 skipped, the 8 being
Postgres. That exercised the one path Linux never takes: the resolver's first branch, finding the
library next to the application rather than falling through to the system. The three latency tests
ran there for the first time, so the negotiation is confirmed rather than reasoned.

**The race fix is verified**: 100 runs of the snapshot test under constrained CPU with no failure,
against a rate of roughly one in eight before it.

**Cross-pod behaviour is verified too, and it cost two defects.** `cross-pod.md` ran two replicas
on k3s. The name lock refuses a live name on another pod in about half a second with the right
rejection code while the incumbent carries on; an interrupted name is admitted anywhere and resumes
as one entry; a viewer on the wrong pod gets media; a preview and a snapshot asked of a non-owner
come back as real bytes. The two defects it found are fixed and described in the commit: a graceful
shutdown was worse for a viewer than a crash, and a stream that moved replicas got a new start
time. Phase 1b's real cost on a force-kill is about one second, one refusal and one retry, not the
several this plan guessed.

**One number moves Phase 3 up the list.** The SRT relay roughly doubles a viewer's join: 1.8
seconds pulling from the owner against 3.8 relayed. Phase 3's target was under ten milliseconds of
difference.

**What is owed.**

- **The load rig has never been pointed at anything.** Phase 0's five baseline numbers are all
  unmeasured, including the one that sizes pods.
- **A registry entry is never swept, and a graceful shutdown deletes rather than interrupts.**
  Found on k3s and deliberately not fixed, because both halves need a decision rather than a
  patch. A force-killed pod leaks one entry per stream into Redis forever, since nothing in the
  service ever removes an entry whose owner is gone. And a clean shutdown deletes its entries, so
  a rolling update still resets a stream's start time even after the fix above. Making shutdown
  interrupt instead of delete would preserve the start time and trade it for that leak on the
  common path, which is why it is a design question: something has to own sweeping abandoned
  entries before the obvious fix is safe.

**Two things Phase 1 left behind, both small and both recorded in code.** `SrtSocketStream`'s
receive timeout can be lengthened now that the close is proven to unblock a read, and
`AvioReader.Context`'s `ponytail:` comment argues from a situation that no longer holds.

## Next

1. **Verify Phase 1b on real pods.** It is the only built behaviour that has never run on more
   than one replica, and its whole point is what happens between two of them. The k3s rig in
   `docs/replica-failover.md` is the cheapest way to see it.
2. **Decide the overload signal**, which Phase 4 cannot be trusted without. See Phase 4.
3. **Raise the pod's memory limit**, which `baseline.md` shows OOM-kills at around 20 camera-rate
   streams against a plan that assumes 250. The cheapest fix on this list by a distance.
4. **Re-measure multi-port ingest on a real node.** `Live__IngestPortCount` is built and binding
   four ports does give libsrt four receive threads, evenly loaded. But the gain measured about
   two rather than four, and it costs roughly a third more processor and memory per stream, so a
   thousand camera-rate streams becomes eight or nine pods rather than seventeen. The load
   generator ran out of machine before the service did, so that ratio wants redoing where the
   senders are not competing with the receiver. See `multi-port.md`.
5. **Attack the per-stream thread cost**, which `preview-cost.md` found is what actually dominates:
   about 1.7 percent of a core per stream across two threads, linear, and 2.3 cores at 150 streams.
   One of the two is ours. See "The per-stream cost" below.
6. **Phase 8, the worker tier.** Still right, because detection has to decode every stream and that
   belongs on a GPU, but it is worth a fifth of the pod rather than the largest win, and it does not
   raise per-pod capacity. Benchmark RF-DETR on the target GPU before sizing anything.
7. **Phase 3, the in-cluster hop**, which also deletes `AvioStream` and gives the worker tier the
   route it subscribes through.
8. Then 4, 5 and 6 in order.

### The per-stream cost

`preview-cost.md` profiled by thread and found the shape of the bill, which is not what either
earlier document assumed. Two threads per stream, roughly 0.87 percent of a core each:

- **The demultiplexer this service starts per feed**, one long-running thread apiece. Ours, and
  therefore the only one that can be argued with.
- **libsrt's per-socket timestamp thread.** Not ours and not removable.

Together 1.7 percent of a core per stream, linear, which is 2.3 cores at 150 streams and more than
twice the preview. Plus the single receive thread at 60 to 80 percent.

One hypothesis worth testing before anything is designed: `StreamDemuxer.Pump` allocates a fresh
`byte[]` for every packet it reads. At a thousand streams that is on the order of half a million
allocations a second, all of them short-lived, and pooling them is a small change with a clear
before-and-after. Whether it accounts for a meaningful share of that 0.87 percent is unmeasured,
and measuring it is the next thing to do rather than the fix.

## Order

Phase 1 first. Phases 4 and 5 are impossible without it, and Phases 2 and 3 are tuning around it.
After Phase 1 every other phase ships on its own.

```
0  Measure                 a load rig and the numbers to beat
1  Own the listener        libsrt directly; deletes the carousel and the log scrape
1b Lock live names         a second publisher of a live name is refused, not admitted
2  Latency                 one option, applied to both ends
3  In-cluster hop          HTTP between pods; SRT only to the viewer
4  Capacity                refuse at the handshake when full; a gauge to scale on
5  Passphrase              optional; three lines once Phase 1 exists
6  Pulled streams          leases, so a pull survives its pod
7  Processing              the seam workers attach to; Phase 3's route
8  Worker tier             ingest stops decoding; one decode per stream, on a GPU
```

Phase 8 arrived late, from the baseline and from the owner correcting an assumption in it, and it
is now the phase the workload actually turns on. Everything from Phase 3 onward exists partly to
serve it.

---

## Phase 0: Measure

Nothing else in this plan is worth doing without a number that says it worked.

### Build

`scripts/load-senders.sh N HOST PORT` starts N `ffmpeg` processes pushing a low-bitrate test
pattern, each named `load/NNN`, all at once. One loop, no framework:

```bash
for i in $(seq -f '%04g' 1 "$N"); do
  # ffmpeg does not retry a refused or dropped connection, so the loop is the retry. That is
  # also what a real encoder does, and it is what a full pod in Phase 4 relies on.
  ( until ffmpeg -hide_banner -loglevel error -re -f lavfi -i "testsrc2=size=640x360:rate=25" \
      -c:v libx264 -preset ultrafast -tune zerolatency -g 25 -keyint_min 25 -sc_threshold 0 \
      -b:v 500k -f mpegts "srt://$HOST:$PORT?mode=caller&streamid=load/$i" </dev/null; do
      sleep 1
    done ) &
done
wait
```

The three keyframe flags make a one-second GOP that x264 will not stretch or cut on a scene
change, which Phase 2 explains. Without `-sc_threshold 0` the test pattern's hard cuts insert
extra keyframes and the join-latency measurement lies low.

A companion `scripts/count-live.sh` polls `GET /api/live` once a second and prints how many are
listed, so time-to-N is read straight off the terminal.

Three things about that endpoint, found while building the script rather than assumed. It answers
`{"transports":[...],"streams":[...]}` and not a bare array, so the count is of `"name"`
occurrences inside it and `transports` contributes none. It lists interrupted streams alongside
live ones, and state serialises as a number because no string enum converter is registered, so
filtering to live only would mean matching `"state":0` and is not worth the brittleness for a
cold-start ramp where the two counts are the same. And with live streaming switched off the
endpoint answers 404, which the script reports as unreachable rather than as zero.

Both scripts default to a direct `dotnet run`, API on 8080 and ingest on 9000. The Compose stack
publishes those as 8081 and 9001, so it needs both arguments given.

### Numbers to record before Phase 1

| Measurement | How | Expected today |
| --- | --- | --- |
| Time until 50 senders are all on air, one pod | load-senders 50, count-live | About 20 to 25 s |
| Accepts per second, one pod | 50 divided by the above | 2 to 3 |
| Join latency, viewer to live edge | The rig in `k8s/live/failover-test.yaml`, difference between producer and viewer frame timestamps | About 2 s plus one keyframe interval |
| Glass to glass on a LAN | Same rig | About 250 ms plus encoder |
| One pod at 250 streams | load-senders 250; `top -H` in the pod, find the libsrt receive thread | Unknown. This is the number that sizes pods |

The last row decides pod size for the whole plan. If that one thread saturates well below 250,
pods are smaller and there are more of them, and nothing else changes.

### Test

None. The rig is the test.

---

## Phase 1: Own the listener

Replace libav's SRT listener with libsrt called directly, on the two listening ports only. libav
keeps the caller side for pulled streams and is otherwise untouched.

### Why this and not more pods

More pods raise the accept rate linearly and leave the log scrape, the self-test and the
accepted-then-dropped path in production. Ten pods cold-start a thousand encoders in forty seconds
of retry storm. Owning the listener makes accept rate a non-issue and is the precondition for
refusing anything at the handshake.

### The objection on record, and why it no longer holds

`.scratch/server-side-ingest/issues/01-srt-listener-streamid.md` rejected this because libsrt is
statically linked into libavformat and not re-exported, so going direct means a second SRT stack
beside libav's. That is a binary-size cost. The two never share a socket, and the note already
established the symbols are not exported, so there is no clash to fear. Verify once on Linux:

```bash
nm -D ffmpeg/linux-x64/libavformat.so | grep -c ' srt_'   # must print 0
```

### Getting libsrt

| Platform | Source | Files |
| --- | --- | --- |
| Linux container | `apt-get install libsrt1.5-openssl` in the Dockerfile | `libsrt.so.1.5` and its OpenSSL |
| Windows developer | `vcpkg install libsrt:x64-windows`, copied by a `scripts/fetch-libsrt.sh` next to the fetched FFmpeg | `srt.dll`, `libssl-3-x64.dll`, `libcrypto-3-x64.dll` |

The runtime base image is `mcr.microsoft.com/dotnet/aspnet:10.0`, which is Ubuntu 24.04 rather
than Debian, so the package resolves to 1.5.3 rather than the 1.5.1 in the research file's Debian
row. Same package name, same soname, and 1.5.3 is clear of the two CVEs open against 1.5.1.
Verified by building the image and reading the installed soname, not by reading a table.

There is no extractable prebuilt Windows drop upstream: the only Windows asset on the 1.5.7
release is a 136 MB installer. vcpkg is the route, and vcpkg in classic mode has no per-package
pin, so the script enforces a 1.5 floor and warns below it instead of pinning. `Srt.IsAvailable`
checks the version at runtime anyway, which is the real gate.

**The Windows route needs the MSVC C++ workload, and that is a real barrier.** vcpkg builds
libsrt and OpenSSL from source and refuses to start without `vcvarsall.bat`. A Visual Studio
install carrying only the .NET workloads has a `VC` folder with nothing in it but `Auxiliary` and
`Redist`, and vcpkg reports "Unable to find a valid Visual Studio instance". Found the hard way on
this machine. Anyone following this plan on Windows either installs the C++ workload, several
gigabytes and an administrator, or does what the next paragraph says.

**The cheaper answer is to verify on Linux**, which is also the platform this service deploys to.
The container already installs libsrt from apt and carries an FFmpeg with SRT, so a test image is
a smaller thing to build than a Windows toolchain, and it exercises the code where it will
actually run. `docker/Dockerfile.test` is that image.

Same shape as `scripts/fetch-ffmpeg.sh`: gitignored folder, overlaid at build by
`Directory.Build.targets`, absent means "no SRT" and everything else still runs. Pin the version
the same way FFmpeg is pinned.

Confirmed against the package trackers and the vcpkg port (`research/libsrt-api.md`): Debian 12
ships 1.5.1 and Ubuntu 24.04 ships 1.5.3, both under the soname `libsrt.so.1.5`; the vcpkg port is
`libsrt` at 1.5.6 and produces `srt.dll` with OpenSSL 3 beside it. The listen callback and the
extended rejection codes exist in all of them. BtbN's gpl-shared builds do compile libsrt with
`ENABLE_SHARED=OFF`, so it is inside `libavformat` and the `nm` check above is the only thing left
to run.

### New code

Three files in `src/StorageDemo.Infrastructure/Streaming/`.

**`Srt.cs`**, the binding. `[DllImport("srt")]` with a `NativeLibrary.SetDllImportResolver` that
looks in `Ffmpeg.Directory` first, the same folder the libav libraries load from. Only what is used:

```csharp
srt_startup, srt_cleanup, srt_getversion
srt_create_socket, srt_bind, srt_listen, srt_accept, srt_close
srt_listen_callback
srt_setsockflag, srt_getsockflag          // SRTO_STREAMID, SRTO_RCVLATENCY, SRTO_PEERLATENCY,
                                          // SRTO_PASSPHRASE, SRTO_RCVTIMEO, SRTO_PEERIDLETIMEO,
                                          // SRTO_MAXBW, SRTO_REUSEADDR
srt_recvmsg, srt_sendmsg
srt_setrejectreason
srt_getlasterror, srt_getlasterror_str
srt_bstats                                // Phase 4's gauge, later
```

Callbacks are `[UnmanagedCallersOnly]` function pointers, not delegates, so there is nothing for
the garbage collector to move underneath libsrt. That deletes the pinned-delegate comment and the
fear it records.

`Srt.IsAvailable` is true when the library loaded and `srt_getversion()` reports 1.5 or later. It
replaces the boot-time self-test: a replica without libsrt has `LiveListeners.Fault` set and takes
itself out of the Service, exactly as a replica without SRT in FFmpeg does today.

**The three types the rest of the phase is written against**, fixed here so the listener, the
wiring and the tests can be built against one contract:

```csharp
// Replaces AcceptedConnection. Same shape: whoever handles it either takes the socket with
// Release() or disposes it, which drops the caller.
public sealed class AcceptedSocket(int socket, string streamId, string name) : IDisposable

// What the handshake callback knows before a connection exists. A struct, because it is created
// on libsrt's receiver thread for every caller.
public readonly record struct Admission(string Name, string StreamId, StreamIntent Intent)

// null admits. Anything else is an SRT_REJX code from Srt, sent with srt_setrejectreason.
public delegate int? Admit(Admission admission);

public sealed unsafe class SrtListener(
    StreamIntent intent, LiveOptions options, Admit admit,
    Action<AcceptedSocket> onAccepted, ILogger logger,
    LiveListeners? listeners = null)
{
    public void Run(int port, CancellationToken cancellationToken);
}
```

The trailing `listeners` is the one thing the built listener added to this block. Readiness is
reported from inside `Run`, because only `Run` knows whether `srt_listen` succeeded, and the
parameter is optional so a test can construct a listener without a readiness object.

A rejection code rather than an enum, because `Srt` already has the constants and a second set of
names mapping one to one onto them would be a type that only restates its own values.

**`SrtListener.cs`**, the accept loop. One per port, one dedicated thread.

```
Run(): create, set options, install the callback, listen, then loop on srt_accept.
  srt_create_socket, then srt_bind to IngestAddress:port
  SRTO_REUSEADDR      true, so both ports share one multiplexer per process
  SRTO_PEERIDLETIMEO  FeedTimeoutSeconds * 1000, which is what makes a feed go interrupted
  SRTO_RCVTIMEO       1000, so a blocked read notices cancellation; see SrtSocketStream
  srt_listen_callback BEFORE srt_listen, or it is never consulted
  srt_listen(sock, 128)
  while (!cancelled) { ns = srt_accept(...); onAccepted(new AcceptedSocket(ns, streamid, name)); }
```

Latency is set here too, from Phase 2. Phase 1 leaves it alone and takes libsrt's 120 ms default,
so the two phases stay separable.

Three details that are easy to get wrong and expensive to debug:

- **Getting from the static callback back to the instance.** `[UnmanagedCallersOnly]` means a
  static method with blittable parameters, so the instance travels in libsrt's `hook_opaque` as a
  `GCHandle`, allocated before `srt_listen_callback` and freed when the loop exits. Nothing else
  can be captured.
- **The callback must not throw.** An exception crossing back into C is undefined. Wrap the body
  and reject on failure: a caller refused because the callback faulted is recoverable, a torn-down
  process is not.
- **The name is read twice.** The callback parses the streamid to decide, and the accept path reads
  `SRTO_STREAMID` off the accepted socket and parses again. Two parses of a short string cost
  nothing next to a dictionary keyed by socket that has to be cleaned up on every rejection.

`OnHandshake` is libsrt's `srt_listen_callback_fn`:

```c
int fn(void* opaque, SRTSOCKET ns, int hsversion, const struct sockaddr* peer, const char* streamid)
```

It runs on libsrt's receiver worker thread when the conclusion handshake arrives, before the
connection exists, with the streamid passed in directly. It does three things and nothing else:
parse the streamid with the `StreamName.TryParse` that exists, ask `admit` whether this name may
connect right now, and either return 0 or call `srt_setrejectreason(ns, code)` and return -1. The
codes are from `access_control.h` and travel intact to the caller:

| Code | Value | When |
| --- | --- | --- |
| `SRT_REJX_BAD_REQUEST` | 1400 | The name will not parse, or the wrong `m=` for this port |
| `SRT_REJX_OVERLOAD` | 1402 | The pod is full, Phase 4 |
| `SRT_REJX_CONFLICT` | 1409 | The name is live and held, Phase 1b |

A wrong passphrase is not this callback's business: libsrt verifies the key material after the
callback returns and raises its own `SRT_REJ_BADSECRET`. An encoder that is rejected retries, and
a retry from a fresh source port is re-balanced to another pod under every load balancer default
in play, which Phase 4's research confirmed.

The callback is the only place in this plan that must not block, and it is on the same thread
that receives every packet for every socket on that port. It reads a dictionary and parses a
string. Nothing in it awaits, allocates a socket or touches the registry.

The streamid is the one option an accepted socket does not inherit from the listener, and is read
back with `srt_getsockflag(ns, SRTO_STREAMID, ...)`, up to 512 bytes. Everything else set on the
listener before `srt_listen`, latency included, is inherited.

**`SrtSocketStream.cs`**, an SRTSOCKET as a `Stream`. Replaces `AvioStream`.

- `Read` calls `srt_recvmsg`. Zero is an orderly close by the peer, `SRT_ECONNLOST` is a broken
  link, and `SRT_ETIMEOUT` is `SRTO_RCVTIMEO` expiring. The first two are end of stream, which the
  demultiplexer reports as `FeedEnded` exactly as it does today. The third is a chance to notice
  cancellation and read again; set `SRTO_RCVTIMEO` to one second so a shutdown never waits on a
  silent sender.
- `Write` splits into chunks of the socket's payload size, 1316 bytes by default and 1456 at most
  in live mode, because a larger message is refused. Seven transport-stream packets per send.
  `Faulted` is set on the first failed send, for the same reason `AvioStream.Faulted` exists.
- `Dispose` calls `srt_close`. Closing a listener from another thread is documented to unblock
  `srt_accept` with `SRT_ESCLOSED`. Whether the same holds for a blocked `srt_recvmsg` is not
  documented, which is why the receive timeout above exists and why one of the tests below
  establishes the behaviour on the real library.

### Wiring into what exists

The demultiplexer takes an `AVIOContext*` and nothing in it changes. Give it one made by
`avio_alloc_context` whose read callback pulls from the `SrtSocketStream`. That is the pattern
`PacketMuxer` already uses for writing, mirrored, and it is `AvioReader`.

Ownership splits three ways and all three have to be right. `StreamDemuxer.Run` frees the context,
because its `finally` already does and custom IO means avformat will not. `AvioReader.Dispose`
disposes the underlying stream, which is what closes the SRT socket. And the context leaks if one
is built and never handed to `Run`.

That last case is not hypothetical: `AttachAsync` closes the transport and returns when the claim
is lost, which is precisely "built, then abandoned". So **the accept path must not build an
`AvioReader` until the moment it calls `Run`.** Hold the `SrtSocketStream` through the claim, and
construct the reader inside `Feed`. The bail path then disposes a stream and nothing else, which
is the whole of what it should do.

The viewer path already writes through a `Stream`. Hand it the `SrtSocketStream` instead of an
`AvioStream`.

`LiveIngestService` and `LiveConsumptionService` construct an `SrtListener` each instead of
running `SrtAcceptLoop.Run`. `coordinator.OnAccepted` keeps its shape and takes an `AcceptedSocket`
instead of an `AcceptedConnection`.

`LiveListeners` loses the "told apart by how long the attempt took" heuristic: `srt_bind` and
`srt_listen` return an error or they do not. `Bound` is called once after `srt_listen` succeeds
and `Stopped` when the loop exits. Readiness is then a fact rather than a freshness window.

### Deleted

| File | Lines | Why it goes |
| --- | --- | --- |
| `SrtAcceptLoop.cs` | 235 | The carousel exists only because libav has no accept loop |
| `StreamIdCapture.cs` | 250 | The streamid is a socket option now |
| The self-test port, its firewall note, `SelfTestPort()` | | Nothing to self-test |
| `LiveListeners` timing heuristic and `Freshness` | | Errors are errors again |
| `SrtAcceptLoopTests.cs` | | Tests the thing being deleted |

**`AvioStream.cs` survives Phase 1**, which the first draft of this plan had wrong. It wraps a
libav transport in both directions, and only one of those two uses goes away here: the viewer's
own socket becomes an `SrtSocketStream`, but the relay still reads from the owning replica through
libav's SRT *caller*, and that is a libav transport until Phase 3 turns the hop into HTTP. So this
phase leaves the file in place and unused for writing. Phase 3 deletes it.

Roughly five hundred lines out, three files of perhaps three hundred in.

### Tests

All skip with `Assert.SkipUnless(Srt.IsAvailable, "libsrt is not installed. Run scripts/fetch-libsrt.sh.")`.
Senders stay `ffmpeg` processes from the fetched build, which has SRT as a caller. Move
`StartSender`, `Complaints` and `FreePort` out of the deleted test into a shared `SrtSenders`
helper.

`tests/StorageDemo.Tests/Infrastructure/SrtListenerTests.cs`:

- **Twenty senders started at once are all accepted within five seconds.** The proof the phase
  exists for. Today's test staggers three senders two seconds apart and says why; this one starts
  twenty together and fails if the backlog is not real.
- **The streamid is read from the socket, byte for byte.** Send `#!::r=live/cam-1,m=publish` and
  assert the accepted socket's name is `live/cam-1`, with no log callback installed. Fails if the
  option read breaks or if a future libsrt changes the convention.
- **A sender with an unparseable name is rejected during the handshake, not after.** Assert that
  `onAccepted` was never called, that the sender exited within two seconds, and that its stderr
  contains `Connection to srt://` and `failed`. That is FFmpeg's own line, confirmed in
  `libsrt.c`, and it is all a test can rely on: FFmpeg's caller path never asks libsrt for the
  rejection reason, and whether libsrt's default log handler prints its own rejection warning to
  stderr is not documented. The discriminator is therefore the first assertion, since a
  connection that was accepted and then dropped would have fired `onAccepted`. A helper
  `SrtSenders.WasRefused(process)` owns the string and the timing, so an FFmpeg upgrade that
  rewords the line breaks one place. This is the property that makes Phases 1b, 4 and 5 possible,
  so it is pinned.
- **A publisher on the consumption port and a subscriber on the ingest port are both rejected.**
  Same mechanism, the `m=` check, both directions.
- **A sender that connects and never sends a byte does not block the next accept.** Two callers,
  the first silent; the second is accepted within a second. Fails if anything in accept waits on a
  read, which is the bug the old carousel opened the transport separately to avoid.
- **Closing the socket from another thread ends a blocked read within the receive timeout.**
  Start a read on `SrtSocketStream`, dispose from the test thread, assert the read returns within
  two seconds. libsrt documents this for `srt_accept` and not for `srt_recvmsg`; the one-second
  `SRTO_RCVTIMEO` guarantees the bound either way, and the test says which mechanism actually
  fired so the timeout can be lengthened if the close alone turns out to suffice.

`SrtSocketStreamTests.cs`, no network:

- **Writes larger than the payload size are sent in payload-sized chunks.** Substitute the send
  function, write 5000 bytes, assert sends of 1316, 1316, 1316 and 1052. A one-screen unit test
  that fails if the chunking is off by one, which libsrt would report as a dropped message rather
  than an error. An earlier draft of this line said four sends of 1316 and one of 736, which is
  6000 bytes and not 5000; the figures above are the arithmetic.

`tests/StorageDemo.Tests/Integration/LiveStreamTests.cs` stays as it is and must stay green. It
drives the whole application with real `ffmpeg` senders and is the regression net for this phase.
The one edit: `A_replica_that_cannot_serve_media_is_not_ready` now provokes the fault by hiding
libsrt rather than by breaking the log capture.

### Done when

Phase 0's 50-sender measurement is under five seconds on one pod, `LiveStreamTests` is green, and
`grep -rn "accept streamid" src/` prints nothing.

---

## Phase 1b: A live name is locked

The design on record says the newest connection wins a contested name. The repository owner has
reversed that: while `demo` is live, nobody else may publish `demo`. This section replaces the
**Claim** paragraph in `CONTEXT.md` and the "newest connection wins" paragraphs in the README and
in `LiveStreamCoordinator.ClaimAsync`.

### The rule

A name is held by its owner while the owner is alive and the feed is live. It is free when the
feed is interrupted, when the owner has stopped heartbeating, or when nothing owns it. A publisher
presenting a held name is refused at the handshake. A publisher presenting a free name is
admitted, and if the name was interrupted it resumes that stream, exactly as today.

| The name is | A new publisher is |
| --- | --- |
| Owned here, feed live | Refused, `SRT_REJX_CONFLICT` |
| Owned here, feed interrupted | Admitted, resumes the same hub |
| Owned elsewhere, live, owner heartbeating | Refused |
| Owned elsewhere, interrupted | Admitted here, the previous owner stands down on its next heartbeat |
| Owned elsewhere, owner's heartbeat stale | Admitted here, the entry is overwritten |
| Nobody's | Admitted |

"Live" is the state already in the registry. "Owner heartbeating" is a shorter window than the
grace period: the heartbeat is two seconds, so a heartbeat older than three beats means the pod is
gone, and the name is free after about six seconds rather than thirty. The stream itself stays
listed as interrupted for the full grace period so a tile does not vanish; only the lock lets go
early. That is one new helper beside `LiveStreamStaleness.IsGone`:

```csharp
public static bool OwnerAlive(LiveStream stream, TimeSpan beat) => DateTimeOffset.UtcNow - stream.Heartbeat <= beat * 3;
```

### Two places enforce it

**At the handshake, from a cache.** Phase 1's callback must not block, so it cannot read Redis.
The heartbeat already walks the registry every two seconds; it now also takes one `ListAsync` and
keeps the result in a `ConcurrentDictionary<string, LiveStream>`. The callback answers from that.
The cache is at most two seconds stale, which leaves a window where two pods could each admit the
same name. That window closes at the second check.

**At the claim, authoritatively.** `ClaimAsync` already takes the `live-claim:{name}` lock and
reads the registry. Today it writes itself as owner regardless. Now: if the entry is live and the
owner is alive and not this pod, it does not write, closes the just-accepted socket, and logs that
the name is held. The connection was already accepted, so the encoder sees a close rather than a
rejection. This is the slow path and only the two-second race reaches it.

`LiveStreamEntry.TakeOverAsync` still cancels a previous feed, but under this rule it is only
reached when that feed has already stopped, so the cancel is a no-op in practice and stays as a
guard.

### What this costs, stated plainly

- **A dead pod's names are held for about six seconds.** Its encoders reconnect elsewhere in one
  to seven seconds, per `docs/replica-failover.md`, and any that arrive inside the window are
  refused and retry. Recovery from a force-kill grows by up to one refusal and one retry interval.
  The original design chose newest-wins precisely to avoid waiting on a timeout; the owner has
  decided the lock is worth it.
- **An encoder that reconnects before its old socket has timed out is refused.** The old feed is
  still "live" until `FeedTimeoutSeconds`, five seconds today, declares it interrupted. The
  encoder's retry gets in after that. Lowering `SRTO_PEERIDLETIMEO` shrinks the window; it cannot
  be zero.
- **Nothing here authenticates anybody.** An impostor that arrives while the real encoder is down
  is admitted, and the real encoder is then locked out until the impostor stops. The lock is a
  collision guard, not a security measure. Phase 5's passphrase is the security measure, and the
  two compose: the passphrase decides who may publish at all, the lock decides who has the name
  right now.

### Tests

`LiveStreamTests.cs`, one host, real senders:

- **A second publisher of a live name is refused and the first is undisturbed.** Sender one on
  `demo`, wait until live, read the packet count; sender two on `demo`; assert sender two's stderr
  says rejected, and that the packet count read again a second later has grown. The second half is
  the point: a refused publisher must not have interrupted anything.
- **A publisher of an interrupted name resumes it.** Sender one on `demo`, kill it, wait past the
  feed timeout, sender two on `demo`; assert accepted, same `StartedAt`, state back to live. This
  is today's reconnect test with a second process, and it pins that the lock lets go when the
  feed does.

`LiveRelayTests.cs`, two hosts sharing a registry, from Phase 3's fixture:

- **A name live on one pod is refused on the other.** Sender on A's ingest port, wait for the
  cache on B to see it, sender with the same name on B's ingest port; assert refused and that A
  still owns it. Fails if the handshake cache is not populated or is consulted wrongly.
- **A name whose owner has stopped heartbeating is free.** Upsert an entry for `demo` owned by
  `ghost` with a heartbeat ten seconds old, sender on B; assert admitted and the registry now names
  B. Fails if the lock uses the thirty-second grace instead of three beats.
- **The race window resolves to one owner.** Upsert an entry for `demo` owned by A with a fresh
  heartbeat, but leave B's cache empty so the handshake admits; sender on B; assert the socket is
  closed within a second and A is still the owner. This is the claim-time check, and it is the one
  a cache bug would otherwise hide.

`LiveStreamStalenessTests.cs`, unit:

- **`OwnerAlive` is three beats and `IsGone` is the grace period.** Two assertions each side of
  each boundary. Trivial, and both thresholds now carry meaning.

---

## Phase 2: Latency

### Build

One option, `Live__SrtLatencyMs`, default 120 so that nothing changes for anyone who does not set
it. Applied as `SRTO_LATENCY` on both listening sockets before `srt_listen`, which sets both the
receive and the peer value and is inherited by every accepted socket. Both are needed because the
two ports sit on opposite ends of the negotiation: on ingest this service receives, on consumption
it sends, and per direction the effective latency is the larger of the receiver's own latency and
the sender's peer latency. An encoder or player configured higher therefore wins, which is
correct, since it knows its link. Read the negotiated value back after accept for the log line so
an operator can see what a connection actually got.

Guidance in the option's summary, not in code, taken from the SRT deployment guide: about four
round-trip times, three on a link with under one percent loss, and never below 60 ms. On a LAN
that means 60. On the internet it is whatever the internet is. `SRTO_TLPKTDROP` and
`SRTO_TSBPDMODE` stay at their live-mode defaults of on; they are what keep latency constant
under loss instead of growing.

Nothing else in the server path buffers. The muxer already writes through with `flush_packets`.
The hub hands a packet to every subscriber under one lock on the demultiplexer's thread. There is
nothing to remove.

### Join latency is the encoder's

A viewer starts at the newest keyframe and stays that far behind. Every packet passthrough system
has this floor and it equals the keyframe interval. Document it in the README next to
`-analyzeduration`, and put a one-second GOP in the `Restream.ps1` and `docker/srt-sender.sh`
examples so the reference senders demonstrate the right setting. For x264 at 25 fps that is
`-g 25 -keyint_min 25 -sc_threshold 0`; the last flag matters, since without it a scene cut
inserts a keyframe and the interval is no longer what was asked for.

One awkwardness, worth stating rather than papering over: `Restream.ps1` remultiplexes by default,
and on a copy the GOP is the camera's and ffmpeg cannot change it without re-encoding. So the
keyframe flags only apply on its `-Transcode` branch, and the reference sender demonstrates the
right setting only when it is actually encoding. A camera sending a keyframe every ten seconds
means a ten second join wait, and the only fixes are the camera's own settings or transcoding.
`docker/srt-sender.sh` always encodes, so it carries the flags unconditionally. Both senders use
mpeg2video rather than x264, and the three flags are generic encoder options that transfer
unchanged.

`SRTO_MAXBW` defaults to -1, which is unlimited with a 1 Gbps cap in live mode, so the join burst
is sent at once and nothing needs setting. Leave it. The earlier worry that the default paces
relative to input rate was wrong; that is what 0 means, and nothing sets 0.

### Tests

- **The configured latency is what an accepted socket negotiates.** Listener with
  `SrtLatencyMs = 60`, sender with none, read `SRTO_RCVLATENCY` off the accepted socket, assert 60.
  Fails if the option is set on the wrong socket or after listen.
- **The encoder's higher latency wins.** Same test, sender with `latency=200000`, which is
  microseconds in FFmpeg's option, assert 200. Pins the negotiation direction so nobody "fixes" it
  later.
- **The consumption side inherits it too.** Player asking for 20 ms against a consumption listener
  at 60, read the accepted socket's `SRTO_PEERLATENCY`, assert 60. The other half of
  `SRTO_LATENCY`, and the one a plain `SRTO_RCVLATENCY` would have missed.

  This test is written with a player that asks for something, which an earlier draft had wrong:
  it said a player with no options at all. A caller setting nothing asks for libsrt's live
  default of 120 ms, the listener takes the larger of the two, and the socket would report 120
  whether or not this service had configured anything. The assertion would have passed on a
  service that set no latency at all, which is the definition of a vacuous test. Asking for 20
  makes 60 an answer that can only have come from `SRTO_LATENCY`, since with a plain
  `SRTO_RCVLATENCY` the peer half would still be its default of zero and the answer would be the
  player's own 20.

Glass-to-glass is measured, not tested: the failover rig with `Live__SrtLatencyMs=60` and a
one-second GOP, both hops on the same LAN. Expect about 150 ms plus encoder and player, down from
about 250.

---

## Phase 3: The in-cluster hop

A viewer's SRT connection lands on whichever pod the load balancer picked. When that pod is not
the owner it relays. Today it relays by dialling the owner's SRT consumption port, which costs a
second latency window, a handshake through the owner's accept path, and loses the muxer timeline
on every re-attach because a streamid has nowhere to carry it.

### Build

The hop becomes HTTP inside the cluster. SRT reaches the viewer and nothing else changes for them.

**Owner side.** One route on `LiveStreamsController`:

```
GET /api/live/peer/view/{*name}?from=0&continue=0      video/mp2t, chunked, token-guarded
```

It calls `WriteToViewerAsync(new ViewerRequest(name, from), Response.Body, continue)`, which
already takes any `Stream`. Ten lines.

**Relaying side.** `LiveConsumptionService.RelayAsync` calls the `LivePeerProxy.OpenAsync` that
already exists with that path and copies the body into the viewer's `SrtSocketStream`. The
timeline passed as `continue` is the viewer's wall-clock seconds since connect. Stream time can
never run ahead of wall time, so the output timeline is monotonic across a re-attach, which is the
property a player needs. A small forward jump at the seam is what a player tolerates; backwards is
what breaks it.

`ponytail:` wall-clock continuation. If a player visibly objects to the forward jump at an owner
move, return the exact timeline as an HTTP trailer. Kestrel only supports response trailers on
HTTP/2 and HTTP/3 and throws on HTTP/1.1, so that upgrade means pointing the peer client at the
gRPC port with an exact HTTP/2 version policy, which is cleartext HTTP/2 with prior knowledge and
works pod to pod. `HttpClient` fills `TrailingHeaders` once the body is read. Not before a player
complains.

### Deleted

- `RelayAsync`'s SRT dial, `Open`, `OpenTransport`, `DialTimeout`, and the comment explaining why
  the identifier is an option rather than a URL. The HTTP client's timeout does the bounding.
- `AvioStream.cs`, all 118 lines, which Phase 1 had to keep precisely because that dial was its
  last caller. With the hop over HTTP, nothing in the service reads a libav transport as a
  `Stream` any more.
- `LiveStream.ConsumptionAddress`, `LiveOptions.PeerConsumptionBaseUrl`, the
  `Live__PeerConsumptionBaseUrl` env in `k8s/live/deployment.yaml`, and the field in
  `RedisLiveStreamRegistry`. Media still never travels over the API port to a viewer; it travels
  over it between two pods, which is what the peer proxy was built for.

### Tests

`LiveRelayTests.cs`, integration. Two `WebApplicationFactory` hosts in one process, given the same
`InMemoryLiveStreamRegistry` and `InMemoryLock` instances through `ConfigureServices`, so they
behave as two replicas with a shared registry and no Redis. Host A has ingest on one port, host B
has consumption on another.

- **A viewer on the pod that does not own the stream receives it.** `ffmpeg` pushes to A. A second
  `ffmpeg` reads from B's consumption port with the same name and writes to `-f null -`. Assert it
  reports frames within ten seconds. Fails if the route, the proxy or the copy breaks.
- **A viewer following a rollback gets at least what it asked for.** Same shape with
  `user_from=5`; read the relay's log line for the resolved figure and assert it is at least 5.
  Pins that `from` survives the hop.
- **The timeline does not go backwards across a re-attach.** Unit test on `PacketMuxer`: create
  one with `continueFromSeconds = 12`, write a packet whose PTS is zero, assert the first output PTS
  is at least twelve seconds. The relay depends on this and it is one assertion.

The owner-move case stays where it is measured, in `docs/replica-failover.md`: rerun the rig after
this phase and record the viewer gap. Expect it to shrink, since the hop no longer pays an SRT
handshake and latency window.

---

## Phase 4: Capacity and the scale-out signal

### Build

`Live__MaxStreams`, default 0 meaning unlimited. Phase 1's `admit` function returns
`SRT_REJX_OVERLOAD` when the local count has reached it, unless the name is already owned here, so
a reconnect of a stream this pod holds is always let in. The encoder retries, likely from a new
source port, and lands elsewhere. The consumption port is never capped by this: a viewer is cheap
and refusing one helps nobody.

A `System.Diagnostics.Metrics` `Meter` named `StorageDemo.Live` with three instruments, from the
shared framework and nothing else:

| Instrument | Type | Read by |
| --- | --- | --- |
| `live.streams.owned` | ObservableUpDownCounter, which is what the docs prescribe for the size of a set | The autoscaler |
| `live.accepts` | Counter, tag `port` | Phase 0's rig, after |
| `live.rejects` | Counter, tag `reason` | Whoever is wondering why an encoder cannot connect |
| `live.bytes.demuxed` | Counter, tag `name` | The overload signal below. Without it none of the others can tell health from collapse |

**Owning a stream is not the same as serving it, and the baseline proved the difference matters.**
At 250 streams the API reported 250 live, every one in the live state with packets and bytes
rising, while the service was demultiplexing a fifth of the media that was being sent, with
319,000 kernel UDP input errors in fifteen seconds and 236 Mbit/s arriving to deliver 26. A gauge
of streams owned would have read full health at the moment every stream was broken, and an
autoscaler reading it would have done nothing.

So capacity cannot be inferred from a count. The honest signal is delivered bitrate against
expected, which means a stream has to carry what it ought to be receiving, and that is not
something the service knows today: nothing declares a stream's expected rate, and an encoder's
own bitrate is not reported anywhere the listener can see it. The cheapest usable proxy is the
derivative of `live.bytes.demuxed` per stream against its own recent history, so a stream that
falls to a fifth of what it was delivering a minute ago is visibly degraded even though nobody
ever declared what it should be.

That is a design question this plan has not answered, and it is the one thing standing between
Phase 4 and an autoscaler that can be trusted. It belongs on the map rather than in this section,
because the answer may be that a stream declares its rate at claim time.

`dotnet-counters monitor -n <process> --counters StorageDemo.Live` reads these with no package.

When an HPA is actually configured, the export is `OpenTelemetry.Extensions.Hosting` plus
`OpenTelemetry.Exporter.Prometheus.AspNetCore`, two calls in `Program.cs` and `/metrics`. The
exporter has never shipped a stable release, 1.18.0-beta.1 at the time of writing, so it needs
`--prerelease` and is one more reason not to add it before an HPA exists. The instrument renders
as `live_streams_owned`. The manifest is an `autoscaling/v2` HPA, `type: Pods`,
`target.type: AverageValue`, a little under `MaxStreams`, served by prometheus-adapter with one
rule mapping the `pod` label. KEDA does the same with a `prometheus` trigger on
`sum(live_streams_owned)` and is only worth its install if scale-to-zero or other triggers are
wanted. Example manifests are in `research/k8s-autoscaling-and-metrics.md`.

Two scale-down details, both cheap:

- Each pod writes `controller.kubernetes.io/pod-deletion-cost` on itself from the heartbeat, equal
  to streams owned. The ReplicaSet then removes the emptiest pod first. Best effort, on by
  default, and it needs a patch verb on pods in the RBAC.
- `behavior.scaleDown.stabilizationWindowSeconds` stays at its default of 300 s. Encoders that
  land on a pod about to be removed reconnect in seconds, so the cost of a scale-down is bounded
  and there is no reason to hurry one.

Two load-balancer facts the plan depends on, now confirmed rather than assumed: a retry from a new
source port is re-balanced under kube-proxy iptables, IPVS and Cilium defaults alike, so a refused
encoder does land elsewhere; and `sessionAffinity` must stay `None`, since `ClientIP` applies to
UDP and would pin the retry to the full pod. SRT keepalives every second keep a flow inside the
conntrack UDP timeouts, so an idle connection is never re-hashed underneath itself.

**Pod sizing, rewritten against the measurements in `baseline.md`.** The rule this plan first gave,
set `MaxStreams` where the receive thread sits at about 70 percent, has no solution: the knee is
around 60 percent of one core and 70 is never reached at any load. Past the knee the thread's
share *falls*, because what it cannot do becomes kernel UDP drops and a retransmit storm rather
than more processor. Sizing on a number that moves the wrong way under overload cannot work.

The ceiling is a joint budget of open sockets and packet rate, roughly

```
streams/175 + pps/45000 < 1
```

which is neither a stream count nor a throughput. Twenty streams carrying 300 Mbit/s sit at a
third of the thread; seventy-five streams carrying the same 313 Mbit/s collapse. So `MaxStreams`
is set per deployment from the expected per-stream bitrate. Measured knees: about 150 streams at
half a megabit, about 60 at a camera-like four megabits, fewer than 20 at fifteen.

Memory is two to two and a half times `BufferWindowSeconds` times bitrate per stream, measured at
9.4 MB at half a megabit and 39 MB at four. The original prediction, `MaxStreams` times the 96 MiB
buffer ceiling, over-predicts tenfold below about 25 Mbit/s a stream, because the ceiling is a cap
that low-bitrate streams never approach.

**`k8s/live/deployment.yaml` is short by roughly an order of magnitude on both**, which makes it a
Phase 4 deliverable rather than a note. It requests 200m and 512Mi against a measured 2.5 to 3.4
cores and 1.38 GiB for 150 low-bitrate streams, and its 1Gi limit OOM-kills at around 100 test
streams or 20 camera streams. Its comment promises the numbers are a calculation; they now are
one, so it should carry the formula rather than the guess.

**Threads are a third bound**, about 2.2 per stream and 549 at 250 streams: one long-running
demultiplexer each plus libsrt's own per-socket timestamp thread. Nothing to do about it yet, but
it caps `MaxStreams` independently of processor and memory.

### Tests

- **A full pod refuses a new name at the handshake.** `MaxStreams = 1`, two senders; the second's
  stderr says rejected, `onAccepted` fired once. Fails if the count is read from the wrong place
  or the callback is skipped.
- **A full pod accepts a reconnect of a name it already owns.** `MaxStreams = 1`, one sender,
  kill it, start it again with the same name inside the grace period, assert accepted. Without this
  every full pod would refuse its own encoders after a blip, and that is the bug the exception
  exists to prevent.
- **The gauge equals the local dictionary count.** Read it through a `MeterListener`, add and
  remove an entry, assert. Trivial, and it is what the autoscaler will be trusting.

---

## Phase 5: Passphrase, optional

Not asked for. Included because it is three lines once Phase 1 exists and it is the answer the map
recorded as the stronger candidate. Off by default.

### Build

`Live__Passphrase`. When set, Phase 1's handshake callback calls
`srt_setsockflag(ns, SRTO_PASSPHRASE, ...)` on the pending socket before returning 0. That is the
documented access-control pattern: libsrt verifies the key material after the callback returns
and rejects a mismatch with its own `SRT_REJ_BADSECRET`, so the callback never sees a wrong
phrase and never has to. `SRTO_ENFORCEDENCRYPTION` stays at its default of on, which is what makes
an unencrypted caller a rejection rather than a plaintext stream. Apply to both ports, so
watching needs it as much as pushing.

Per-stream passphrases later are the same call with a lookup by name. Not now.

### Tests

- **A sender with the wrong passphrase is rejected and one with the right one is accepted.** Two
  senders, one assertion each. Fails if the option is set on the listening socket instead of the
  pending one, which silently does nothing.

---

## Phase 6: Pulled streams that survive their pod

A manual stream is created by a `POST`, owned by whichever pod took the request, and dies with it.
For a thousand inputs where some are pulled, a pull has to be a standing instruction the cluster
carries out, not a one-off on one pod.

### Build

The registry gains the standing instructions, since it is already the one piece of shared state
with an in-memory and a Redis implementation:

```csharp
Task AddSourceAsync(string name, string url, CancellationToken ct = default);
Task RemoveSourceAsync(string name, CancellationToken ct = default);
Task<IReadOnlyList<(string Name, string Url)>> SourcesAsync(CancellationToken ct = default);
```

In memory it is a dictionary. In Redis it is one hash, `live:sources`.

`POST /api/live/manual` writes the source and returns. It no longer starts anything itself.
`DELETE /api/live/stream/{name}` on a manual stream removes the source as well, or the stream would
come back on the next heartbeat.

The heartbeat picks sources up. At the end of `TickAsync`, for every source whose registry entry
is missing or gone, and while this pod is under `MaxStreams`: take `live-claim:{name}` on the
distributed lock that already serialises claims, re-check, and if still unowned start the manual
demultiplexer here exactly as `CreateManualAsync` does today. Whichever pod's heartbeat fires
first takes it. A source whose camera is down is claimed, fails to open, sits interrupted for the
grace period, is removed, and is claimed again on the next pass. That is the retry loop, and it
costs no new code.

`ponytail:` sources live in Redis, so they last as long as Redis does. Move them to a Postgres
table when a Redis wipe must not lose the camera list.

### Tests

`PulledStreamTests.cs`, unit, no ffmpeg: two `LiveStreamCoordinator`s sharing one
`InMemoryLiveStreamRegistry` and one `InMemoryLock`, sources pointing at a UDP port nothing sends
to, so the demultiplexer returns `NeverStarted` at once and the claim is what is under test.

- **A source is claimed by exactly one pod.** Add a source, tick both coordinators, assert the
  registry names one owner and the other coordinator owns nothing.
- **A source moves when its owner disappears.** Dispose the owner, tick the other, assert it is
  now the owner. This is the whole reason the phase exists.
- **Deleting a manual stream deletes its source.** Delete, tick both, assert nothing is claimed.
  Fails in exactly the way that would otherwise be reported as "the camera keeps coming back".
- **A full pod leaves sources for others.** `MaxStreams = 1` on A, two sources, tick A then B,
  assert one each.

---

## Phase 7: Processing

Nothing to build in this repository yet. A detector is a viewer that decodes, and Phase 3's route
is how a worker in another Deployment reaches a stream: `GET /api/live/stream/{name}` for the
owner's address, then `GET {owner}/api/live/peer/view/{name}` for the bytes. Ingest pods scale on
`live.streams.owned`; worker pods scale on CPU or GPU with an ordinary HPA. When a detector exists,
its manifest goes in `k8s/workers/` and its only dependency on this service is those two routes.

---

## Phase 8: The ingest pod stops decoding, and a worker tier decodes once

Not in the original plan. The baseline found that every stream decodes keyframes and JPEG-encodes
a preview whether or not anyone is watching. The first draft of this phase said to decode it on
demand. **The repository owner corrected that**: detection and tracking have to run on every
stream, so there will always be a frame subscriber and "on demand" answers nothing.

**A second correction, from `preview-cost.md`.** This phase was first justified by the preview
being the dominant cost, taken from the baseline. It is not. Isolated, it is about a fifth of the
pod and roughly 0.8 of a core, against two threads per stream that cost more than twice that.
Turning it off did not move the receive thread at all, so **this phase does not raise the number
of streams a pod can hold**. What it does buy is a fifth of the pod's processor and 15 to 30
percent of its memory back from work nobody asked for, and stability at the knee: at 150 streams
the preview's extra core was the difference between delivering the media and silently delivering
two thirds of it. That, plus detection needing the decode on a GPU regardless, is the case for
building it. It is not the case for building it first.

The right move is not to decode less. It is to decode somewhere else, exactly once.

### What each tier does

**The ingest pod never decodes.** It accepts, demultiplexes, buffers, records, serves viewers and
extracts KLV. All of that is packet work.

**A worker tier decodes once per stream and returns two things**, detections and a preview
thumbnail. The preview stops being a second decode in the ingest pod and becomes a by-product of
the decode detection already pays for, which is what the design claimed all along: "a stream is
decoded once however many things want pictures".

Three reasons it belongs in its own tier rather than in the ingest pod. The ingest pod is bounded
by libsrt's single receive thread at around sixty camera-rate streams, while decode is bounded by
cores or a GPU, and tying the two together means scaling the scarcer one to satisfy the other.
Detection wants a GPU and ingest does not, so co-locating them buys a GPU for every ingest replica.
And one decode serving both detection and the preview is only possible if they are in the same
process.

The cost is that every stream leaves the ingest pod a second time, so internal traffic roughly
doubles. At camera rates that is a few gigabits inside a cluster, which is ordinary, and it is the
price of scaling two very different resources independently.

### KLV costs nothing, and must not be confused with detection

MISB metadata rides as its own elementary stream on its own stream index. Extracting it is reading
packets from that index and never touching a decoder, which is what the fan-out ticket meant by "a
KLV extractor would be a subscriber on a different stream index". At a thousand streams this is
close to free and it must not be allowed to argue for decoding anything.

**The standard is named: STANAG 4609 Edition 5**, the NATO Digital Motion Imagery Standard (NSO
publication 2907, AEDP-4609), which adopts the NGA Motion Imagery Standards Profile MISP-2019.1. It
defines no metadata of its own; it mandates MISB standards, and three of them decide what "extract
KLV" means here:

- **ST 0902, the Minimum Metadata Set.** Every conforming stream carries a fixed subset of the ST
  0601 UAS Datalink Local Set. That subset, not a hand-picked one, is what the extractor decodes.
- **ST 1402, KLV carriage in MPEG-2 TS.** Two modes. Synchronous KLV carries a PES timestamp and
  aligns to a frame by it; asynchronous carries none and aligns by the 0601 timestamp against the
  video's own (ST 0604 in the codec, ST 0603 for the time system). The extractor detects which and
  records which alignment it used, because a worker geo-locating a detection needs to know how
  much to trust it.
- **ST 0102, the Security Metadata Local Set**, nested in 0601 and part of the minimum set. The
  classification marking is decoded far enough to display; a client showing imagery without its
  marking is a real problem in this domain, not a cosmetic one.

**But the worker needs the KLV alongside the video.** Geo-locating a detection from a moving
platform needs position and sensor pointing from MISB 0601, aligned to the frame by presentation
timestamp. So a worker subscribes to the whole transport and takes both indexes, and the alignment
is designed in rather than bolted on afterwards.

### The rate is the bill

Detection rate is the most expensive decision in this phase, and it is not the same as tracking
rate. Pattern of life needs identities that stay stable, which is a tracking property, and a
motion-model tracker running at full frame rate between reduced-rate detections is the standard way
to get it. RF-DETR is transformer-based and end-to-end, so its cost is per frame with no cheap
shortcut: a thousand streams at twenty-five detections a second is twenty-five thousand inferences
a second, and at five it is five thousand.

So **detection rate is a per-stream setting, not a constant**, and the tracker runs at full rate.
A stream that genuinely needs every frame says so, rather than every stream paying for the one that
does. `FrameDecoder`'s existing `DecodeRate` of keyframes-or-everything is too coarse for this and
becomes a frames-per-second figure.

### Decided: detection is switched on per stream, and the model is RF-DETR through ONNX

The repository owner settled both. Detection is **on demand per stream**, a toggle rather than a
constant, so a thousand streams ingest and a chosen subset decode. That is the same shape as the
recording trigger: a person or a detector turning it on are one caller on one path. The rate
setting above applies to a stream once it is on.

The model runs through ONNX Runtime, with making that path as fast as it can go as the explicit
goal: the GPU execution provider in the cluster, batching across the streams that are on, and
frames staying in device memory from decode to inference. On a developer machine the CPU provider
runs the same graph slowly, which is what keeps the seam honest without a GPU.

### Two things that decide whether the GPU is used well

**Decode and inference share the GPU.** Frames go from NVDEC into device memory and into the model
without crossing PCIe. This is why the worker decodes rather than having decode live anywhere else.

**Batches are filled across streams.** A DETR-family model gains a lot from batching, and a worker
holding many streams can fill one batch from several cameras in the same tick.

### Development is CPU, and that is a seam question

The target cluster has GPUs; development does not. RF-DETR on a CPU is not a throughput story at
all, so what development needs is the seam rather than the model: the detector is pluggable, a stub
or a tiny variant runs locally on a couple of streams, and the real model is a deployment concern.
That also keeps the tests honest, since they can assert the pipeline end to end without a GPU.

### What to measure before building

The running preview measurement gives the number that justifies moving decode out: what it costs
inside the ingest pod today. After that, and before sizing anything, RF-DETR wants benchmarking on
the actual target GPU at the actual resolution, because published figures rarely survive a real
pipeline. The two numbers that matter are frames per second per GPU at a useful batch size, and
whether NVDEC or the model saturates first.

### What this does not change

The packet tier, the hub, the rolling buffer, recordings and viewers are all untouched. This phase
removes a frame subscriber from the ingest pod and adds a packet subscriber somewhere else, which
is the seam working as designed rather than a change to it.

## Phase 9: Answer "which of my thousand streams is broken"

Not in the original plan, and the largest operational gap in the service. There is no metric, no
trace and no per-stream health anywhere in the codebase today. `baseline.md` showed why that is
worse than it sounds: at 250 streams the API reported 250 of them live and contented while a fifth
of the media was arriving. The system does not merely lack instruments, it actively reports health
it does not have.

This phase also settles the overload signal that Phase 4 has been blocked on, because they are the
same question asked twice.

### The signal is a measurement, not an inference

Two earlier proposals inferred trouble from throughput: compare against a declared expected rate,
or watch a stream's delivered rate against its own history. Both are guesses about a thing that can
be measured directly. The baseline's failure was 319,000 kernel UDP input errors in fifteen
seconds, and that is the mechanism rather than a symptom.

So the signal is two counters at two levels, and neither of them is new work in the sense of new
mechanism:

- **Per stream, from libsrt itself.** `srt_bstats` is already bound in `Srt.cs` and has never been
  called. It reports packets lost and dropped on a socket, which is the transport saying it failed
  rather than something deducing it from a byte count.
- **Per pod, from the kernel.** Receive errors on the UDP socket, which catch what never reached
  libsrt at all. That is precisely the multiplexer-level overload the baseline hit, and no
  per-socket counter can see it.

A stream is healthy when both are quiet. Nothing has to declare an expected bitrate, and a stream
that was never healthy is as visible as one that degraded.

### Two audiences, two shapes, and the cardinality trap

**An operator asking about one stream** wants per-stream detail, and the natural home is the thing
that already answers per-stream questions: `LiveStream` gains loss and drop figures, so
`GET /api/live` and the stream tile answer "which one is broken" directly. No exporter, no
dashboard, no new dependency.

**An autoscaler asking about a pod** wants aggregates. A `Meter` named `StorageDemo.Live` with
streams owned, accepts and rejects tagged by reason, and the pod-level receive errors.

**Do not tag a metric with the stream name.** A thousand streams times several instruments is
thousands of time series, and the per-stream question is already answered better by the API. This
is the trap that makes naive instrumentation expensive, and it is worth one sentence in the code.

The Prometheus exporter stays out until an autoscaler exists, for the reason `research/k8s-autoscaling-and-metrics.md`
records: it has never shipped a stable release. `dotnet-counters` reads the meter with no package.

### What good looks like

Re-run the baseline's 250-stream case. Today it reports 250 healthy. After this phase it should
show the drops, and an operator should be able to name the degraded streams from one API call.
That is the acceptance test, and it is runnable on the rig that exists.

## Phase 11: The gRPC surface catches up with REST

Decided by the owner: the desktop client stays gRPC, and REST is for agents and command-line
tools. The client plan found that everything the client now needs is REST-only, and one thing is
worse than missing: `DownloadLivePreview` deliberately does not proxy to the owning replica, so in
a cluster of eight pods the current client shows icons on most tiles. That was a documented
trade-off when one replica was the normal case; it is a defect now.

So one proto change, once, carrying all of it rather than one per feature: the health figures and
the classification marking on `ListLive`; a preview that proxies to the owner the way the REST
route does; a KLV call returning the ST 0902 minimum set and the raw packet; the detection toggle
when Phase 8 exists; and whatever retention exposes. The client plan lists which are a field on an
existing message and which are a new RPC, in the order its phases need them.

Whether the live RPCs are guarded at all is a question to answer while in there: the REST live
routes require a token, and a gRPC surface that bypasses it would be a hole rather than a
convenience.

## Phase 10: Retention, and the sweeper nothing owns

Your original map listed retention as not yet specified and it never reached this plan. Nothing
expires a recording or a snapshot. At a thousand camera-rate streams, recording everything is about
43 TB a day and recording a tenth of them is about 4.3 TB. Storage grows without bound from the day
this goes live, and it is much harder to retrofit over a year of data than to build now.

### What it deletes, and the two traps

Age is the default policy, with the ceiling in configuration rather than in code. Per-stream
overrides can come later if something asks; nothing has.

Two things make this less trivial than "delete old rows":

- **A recording is many objects.** `Document.Parts` carries them and they live under `recordings/`
  rather than the scanned prefix, so deleting the row is not enough and the reconciler will not
  clean up after a half-done delete. Bytes first, then the row, which is the opposite order to
  upload and is what leaves no orphan.
- **A recording still being written must not be swept.** One is open whenever `Recording` is set on
  its stream, and a document appears with its first part and grows, so age alone would delete a
  six-hour recording's early parts while it is still running.

### The sweeper is the thing to get right, and there is already one

Do not invent a scheduler. `StorageMonitor` already runs a periodic pass under
`IDistributedLock.TryAcquireAsync`, so exactly one replica does the work and the others skip it.
Retention is the same shape with a different body, and following that pattern is most of the
design.

### Sweep the registry too, while something finally owns sweeping

`cross-pod.md` found that nothing ever removes a registry entry whose owner is gone, so a
force-killed pod leaks one per stream into Redis forever. That is the same unanswered question,
"who sweeps", and it should be answered once here rather than twice.

It also unblocks something else. A graceful shutdown currently deletes its entries rather than
interrupting them, which is why a rolling update still resets a stream's start time. The fix is to
interrupt instead of delete, and the only reason not to was that abandoned entries accumulate.
Once a sweeper exists, that reason is gone and the rolling-update reset can be fixed properly.

### The disk ceiling nobody has written down

While in here: a part is five minutes and the pod's volume is 8 GiB, so at camera rate that is
roughly fifty-four concurrent recordings per pod. With multi-port ingest pushing a pod toward a
hundred and twenty-five streams, a detector triggering broadly could exceed it, and nothing bounds
concurrent recordings or says what happens when the volume fills. State the ceiling in the manifest
beside the sizing arithmetic; bounding it is a separate decision.

## Documentation, as each phase lands

- `README.md`: the "About SRT" section is rewritten around libsrt direct, the carousel and the
  self-test paragraphs go, `Live__SrtLatencyMs`, `Live__MaxStreams`, `Live__Passphrase` join the
  configuration block, and the join-latency floor gets its sentence.
- `CONTEXT.md`: **Claim** is rewritten with Phase 1b, since a contested name no longer goes to the
  newest connection; **Consumption address** is deleted with Phase 3; **Source** is added with
  Phase 6.
- `.scratch/server-side-ingest/map.md`: the "Not yet specified" entries for authentication and
  for replacing the log-line capture are resolved by pointing here.
- `README.md` links `docs/design/server-side-stream-ingest.md`, which is not in the repository.
  Either commit it or drop the link. Found while reading, not caused by this plan.

## Acceptance, end to end

Run after Phase 6 on the same k3s rig as `docs/replica-failover.md`, four pods.

| Claim | Measurement | Target |
| --- | --- | --- |
| A thousand encoders cold-start together | load-senders 1000 against the ingest Service, count-live | All on air inside 30 s |
| A full pod turns encoders away and they land elsewhere | `MaxStreams=250`, the same run | Four pods at 250, zero over |
| A viewer on the wrong pod costs almost nothing | Rig, viewer pinned to a non-owner, compare with a viewer on the owner | Under 10 ms difference |
| Lowest latency on a LAN | Rig, `SrtLatencyMs=60`, one-second GOP | Glass to glass about 150 ms plus encoder and player |
| A pulled stream survives its pod | Add a source, delete its owner | Back on air within one grace period |
| A live name cannot be taken | Second sender with a live name, against the ingest Service | Refused at the handshake, first sender's packet count keeps rising |
| A dead pod's names free up quickly | Force-kill the owner, watch its encoders | All back on air within about 15 s |
| The self-test is gone | `grep -rn "accept streamid" src/` | Nothing |
