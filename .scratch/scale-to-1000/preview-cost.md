# What the preview actually costs

Measured 2026-09-11 against `srt-listener-libsrt` at 9f23e40, with one throwaway probe that skips
the decode and the JPEG encode. The probe is reverted; its diff is at the end so the run can be
redone.

**The answer: the preview does not dominate. It is about a fifth to a quarter of the container's
CPU, not "the bulk" of it.** Turning it off leaves roughly 0.78 of the CPU behind. `baseline.md`
defect 3 says "This, not the receive thread, is what the container's 250 to 440 % CPU is"; that is
wrong. The dominant cost is the pair of per-stream threads that move bytes from the socket into the
rolling buffer, and those are not optional.

## The four rows

CPU is the sum of `(utime+stime)` deltas over every thread of PID 1 across one 30 s window, as a
percentage of **one** core - so 300 % is three cores. That is the container's own CPU, measured
from the inside; `docker stats` is sampled over the same 30 s and shown beside it as a cross-check.
Three replicates of each row; the table gives the mean and the spread.

| Streams | Preview | Container CPU, `/proc` | `docker stats` | `SRT:RcvQ:w2` | Memory | Delivered | All live, bytes rising |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 100 | **on** | **295 %** (253, 330, 303) | 300 % (274, 363, 264) | 55 to 68 % | 1.29 to 1.35 GiB | 0.50 Mbit/s, clean | yes, 100 of 100 |
| 100 | **off** | **226 %** (194, 220, 264) | 246 % (219, 250, 269) | 57 to 72 % | 1.01 to 1.14 GiB | 0.51 Mbit/s, clean | yes, 100 of 100 |
| 150 | **on** | **415 %** (425, 401, 419) | 390 % (412, 378, 382) | 72 to 77 % | 1.52 to 1.90 GiB | 0.34 to 0.60, unstable | yes, 150 of 150 |
| 150 | **off** | **326 %** (330, 334, 313) | 264 % (212, 315) | 75 to 80 % | 1.15 to 1.20 GiB | 0.50 Mbit/s, clean | yes, 150 of 150 |

Source bitrate is 0.567 Mbit/s (63,822,616 bytes of pattern over 900 s). `hasPreview` was true on
every stream in the "on" rows and false on every stream in the "off" rows, which is the probe
doing what it claims. Stream counts, liveness and rising byte counters were identical either way.

The 100-stream "on" row reproduces `baseline.md`'s own 100-stream row closely (it recorded 302 %,
1.13 GiB, 0.51 Mbit/s delivered, 250 threads; this run saw 295 %, 1.3 GiB, 0.50, 245 to 250
threads), so the rig is measuring the same thing the baseline measured.

## The number that decides Phase 8

**Ratio of container CPU with the preview off to with it on, at the same stream count:**

| Streams | Ratio off / on | Preview's absolute cost | Preview's share |
| --- | --- | --- | --- |
| 100 | **0.76** | **0.70 cores** (2.26 vs 2.95) | 24 % |
| 150 | **0.79** | **0.89 cores** (3.26 vs 4.15) | 21 % |

A second, independent measurement of the same quantity agrees. With the preview on, the decode and
the JPEG encode run on `.NET TP Worker` threads and nothing else in the service uses them; with the
probe on, that thread group goes to exactly zero. Totalling by thread name over one 30 s window:

| Thread group | 100 on | 100 off | 150 on | 150 off | What it is |
| --- | --- | --- | --- | --- | --- |
| `.NET TP Worker` | **75.2 %** | **0.0 %** | **109.7 %** | **0.0 %** | the preview: keyframe decode plus JPEG encode |
| `.NET Long Runni` | 88.6 % | 92.9 % | 128.6 % | 123.4 % | one per feed: the demultiplexer pumping SRT into the hub |
| `SRT:TsbPd` | 76.3 % | 95.9 % | 105.1 % | 108.1 % | libsrt, one per socket |
| `SRT:RcvQ:w2` | 60.2 % | 71.7 % | 72.1 % | 75.1 % | libsrt's single receive thread, the ceiling |
| `.NET Server GC` + `BGC` | 1.3 % | 2.1 % | 2.4 % | 5.2 % | |
| **TOTAL** | **302.7 %** | **263.9 %** | **418.8 %** | **312.9 %** | |

So the preview is **0.75 cores at 100 streams and 1.10 cores at 150** measured directly, against
0.70 and 0.89 measured as a difference of totals. Two methods, same answer inside the noise.
**Call it 0.7 to 1.1 cores, about a fifth to a quarter of the container.**

## What does dominate

**Two threads per stream, neither of them the preview.** The service's own demultiplexer
(`Task.Factory.StartNew(() => Feed(...), LongRunning)` in `LiveStreamCoordinator.cs:118`) costs
about **0.87 % of a core per stream**, and libsrt's per-socket `SRT:TsbPd` costs about **0.85 %**.
Together that is **1.7 % of a core per stream**, linear in stream count, and at 150 streams it is
2.3 cores - more than twice the preview. Add the single `SRT:RcvQ:w2` receive thread at 60 to 80 %
and you have three quarters of the container.

Neither of those is work done for nobody in the way the preview is. The demultiplexer is how bytes
reach the rolling buffer, which is what the pre-roll is made of; `TsbPd` is libsrt's own delivery
scheduler and is not ours to switch off. **There is no equivalent of Phase 8 available for the
73 % of the container that is not the preview.**

Two smaller findings:

- **The preview does not steal from the receive thread.** `SRT:RcvQ:w2` was *higher* with the
  preview off (72 to 80 % versus 55 to 77 %), because more media got through for it to handle.
  Removing the preview therefore does not raise the per-pod stream ceiling, which is that one
  thread. It lowers the pod's CPU bill without moving the knee's stream count.
- **It does buy headroom at the knee, though.** At 150 streams with the preview on, delivered
  bitrate was unstable across replicates (0.335, 0.405, 0.602 against a 0.567 source); with it off
  it was 0.499, 0.500, 0.537 every time. 150 is the measured knee for this bitrate, and the
  preview's extra core is enough to push a marginal pod over it.
- **Memory falls too**, by 0.2 GiB at 100 streams and 0.5 GiB at 150 - roughly 2 to 3 MB per
  stream of decoder frame buffers. That is 15 to 30 % of the container's memory, which matters
  more than the CPU does against defect 2's `1Gi` limit.

## How to repeat it

```bash
docker network create loadnet
docker run -d --name srt-api --network loadnet -e Live__Enabled=true -e Live__Token=t \
  -e Storage__FileSystem__RootPath=/data/files -e Database__LiteDb__Path=/data/database/app.db \
  -p 8085:8080 storagedemo-api                       # add -e LIVE_NO_PREVIEW=1 for the "off" rows

docker run -d --name senders --network loadnet -v "$PWD/scripts:/rig:ro" \
  --entrypoint sleep storagedemo-api infinity
docker exec senders /app/ffmpeg/linux-x64/ffmpeg -y -f lavfi -i "testsrc2=size=640x360:rate=25" \
  -t 900 -c:v libx264 -preset ultrafast -tune zerolatency -g 25 -keyint_min 25 -sc_threshold 0 \
  -b:v 500k -f mpegts /tmp/pattern.ts
docker exec -d -e PATTERN=/tmp/pattern.ts -e FFMPEG=/app/ffmpeg/linux-x64/ffmpeg \
  senders bash /rig/load-senders.sh 100 srt-api 9000
```

No `--cpus` or `--memory` limits on either container, same as the baseline. 14 vCPU, 15.5 GiB.

Three things the baseline's write-up leaves out and that cost an hour each here:

- **`FFMPEG` must be set** unless the scripts are mounted at `<something>/scripts`, because
  `load-senders.sh` resolves `$(dirname $0)/../ffmpeg/...`. Without it the script's `until` retry
  loop spins on a missing binary forever and no stream ever appears, silently.
- **The scripts check out with CRLF on Windows**, and `set -euo pipefail\r` is a parse error. The
  run converted with `tr -d '\r'` into the container's `/tmp`.
- **Make the pattern much longer than the whole run.** At 300 s the first attempt reached the loop
  point during the settle, `-re` stopped pacing, and the container jumped from 290 % to 425 % with
  `SRT:RcvQ:w2` at 82 % - all of it an artefact. 900 s covers a 230 s run with room to spare.

Method per run, and the settle time: the service container is **recreated** and the senders
container **restarted** before every run, so nothing is left over from the previous step - the
baseline was caught by exactly that. `GET /api/live` is then asserted to report **zero streams**
before the ramp, and the run aborts if it does not. That assertion passed before all twelve runs.
The ramp reached 100 in 2 to 4 s and 150 in 8 to 11 s. Each run then **settled 120 s** before
anything was read; 90 s was tried first and was not enough, the totals still drifting. Sampling
took a further 30 s for CPU and 10 s for the delivered-bitrate window, all inside the pattern's
900 s. Per-thread CPU is `/proc/1/task/*/stat` deltas, which is what `top -H` shows; `top` is not
in the runtime image.

## The WSL2 caveat

Docker Desktop for Windows, everything inside one WSL2 virtual machine, service and senders sharing
14 vCPU across a Docker bridge. **Every absolute figure here is indicative only.** The senders
container alone used 3.5 to 7 cores, and another agent was building on the same machine throughout,
which is visible in the spread: the same configuration measured 253, 330 and 303 percent on three
different replicates. `docker stats` is noisier still - individual samples ranged 154 % to 560 %
within one steady run, which is why the `/proc` integral over a fixed window is the number quoted
and `docker stats` only the cross-check.

**The ratio is the trustworthy part**, and even it should be read as "about 0.8", not as 0.76
versus 0.79. What carries more weight than either is the direct attribution: the preview's work
runs on a thread group that does nothing else, that group is 75 to 110 % of one core, and it drops
to precisely zero when the probe is on. That is a measurement the host's noise cannot fake.

## Recommendation: build Phase 8, but after the receive-thread work

**Phase 8 is worth building and it is not the largest win available.** It buys about a fifth to a
quarter of a pod's CPU and 15 to 30 % of its memory, and it buys them back almost entirely, because
the user's shape is a thousand streams with a handful watched. That is real: across a fleet sized
at the receive-thread bound of roughly 60 camera-rate streams per pod, seventeen pods for a
thousand streams, it is on the order of eight cores and several gigabytes of memory saved for work
nobody asked for. It is also the only line in the profile that *can* be removed - the demultiplexer
and `TsbPd` threads above it are load-bearing. But it does **not** raise the number of streams a pod
can carry, because that number is set by the single `SRT:RcvQ:w2` thread, and removing the preview
left that thread's share unchanged or slightly higher. Anything that raises the per-pod ceiling -
more than one bound port per pod, or more than one process - is worth more per hour spent than
Phase 8 is, and so is defect 2's `1Gi` memory limit, which OOM-kills a pod today at about a fifth
of the load measured here. Rank it third: fix the limits, then attack the receive-thread bound,
then take the preview off the streams nobody is watching. The one qualification that could promote
it is the 150-stream row - the preview's extra core is what turned a clean 150 into an unstable
one, so on a pod sized close to its knee, Phase 8 is the difference between delivering the media
and silently delivering two thirds of it.

## The probe, for redoing it

Applied to `src/StorageDemo.Infrastructure/Streaming/LiveStreamEntry.cs`, reverted afterwards. It
reads the environment variable directly rather than going through `LiveOptions`, because nothing
about it is meant to survive.

```diff
@@ -14,16 +14,20 @@ public sealed class LiveStreamEntry : IAsyncDisposable
 {
     private readonly Lock _gate = new();
 
+    // PROBE, not a feature: LIVE_NO_PREVIEW=1 skips the decode and the JPEG encode so the preview's
+    // share of the container's CPU can be measured. Remove with the measurement.
+    private static readonly bool NoPreview = Environment.GetEnvironmentVariable("LIVE_NO_PREVIEW") == "1";
+
     public LiveStreamEntry(StreamHub hub, Harvester harvester, ILogger logger, bool manual, string? manualUrl)
     {
         Hub = hub;
         Harvester = harvester;
-        Decoder = new FrameDecoder(hub, logger);
+        Decoder = NoPreview ? null : new FrameDecoder(hub, logger);
         Manual = manual;
         ManualUrl = manualUrl;
 
-        PreviewSubscription = SubscribePreview(Decoder, harvester);
-        Decoding = Decoder.RunAsync(Lifetime.Token);
+        PreviewSubscription = Decoder is null ? null : SubscribePreview(Decoder, harvester);
+        Decoding = Decoder?.RunAsync(Lifetime.Token) ?? Task.CompletedTask;
     }
 
     /// <summary>The harvester's handler takes a libav frame, so binding it needs an unsafe context.</summary>
@@ -36,14 +40,14 @@ public sealed class LiveStreamEntry : IAsyncDisposable
 
     public Harvester Harvester { get; }
 
-    public FrameDecoder Decoder { get; }
+    public FrameDecoder? Decoder { get; }
 
     /// <summary>Ends the stream: the decoder, the current feed and any recording.</summary>
     public CancellationTokenSource Lifetime { get; } = new();
 
     public Task Decoding { get; }
 
-    private IDisposable PreviewSubscription { get; }
+    private IDisposable? PreviewSubscription { get; }
 
     public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
 
@@ -158,8 +162,8 @@ public sealed class LiveStreamEntry : IAsyncDisposable
 
         await Task.WhenAny(Decoding, Task.Delay(TimeSpan.FromSeconds(10)));
 
-        PreviewSubscription.Dispose();
-        Decoder.Dispose();
+        PreviewSubscription?.Dispose();
+        Decoder?.Dispose();
         Feed?.Dispose();
         Lifetime.Dispose();
         Hub.Dispose();
```

Nothing else changed. `LiveStreamEntry.Decoder` is public but has no callers outside the class, so
making it nullable breaks nothing; `Harvester` stays attached and simply never receives a frame,
which is why `hasPreview` reads false rather than the API changing shape.

One thing worth knowing if this is redone on this machine: **the daemon's container DNS was broken**
(any container failed to resolve a public name unless started with `--dns 8.8.8.8`), so
`docker build -f docker/Dockerfile` could not restore NuGet packages. The image was assembled by
running the Dockerfile's two stages as containers started with an explicit `--dns`, committing each,
and joining them with a `COPY --from`. The result is byte-for-byte the same set of steps; if the
daemon's DNS is healthy, the plain `docker build` in `baseline.md` is the way to do it.
