# Ingest hot path: what was tried, what moved, what did not

Measured 2026-09-12/13 against `srt-listener-libsrt` at db91a03 (the tree of that commit; the other
agent's uncommitted `Detection/` work was excluded from every image so control and experiment differ
by one thing only). Same rig as `preview-cost.md`, 100 streams of the test pattern, three replicates
per configuration, plus two things the earlier runs did not have: `System.Runtime` counters from a
`dotnet-counters` sidecar attached before the ramp, and the demultiplexer threads' CPU and wake-ups
from `/proc`.

**Nothing in `src/` changed.** Two code experiments were built and measured and neither moved a
number past the replicate spread, so both were dropped and never touched the working tree (they
lived in a copy of the tree in the session scratchpad). One thing did move a number, and it is a
runtime setting rather than code: **workstation GC cuts the container's memory by about 30 %
(0.4 to 0.5 GiB at 100 streams) at no CPU cost.** It is recorded here as a one-line change for the
owner to apply, because the file it belongs in is outside this task's remit.

The hypothesis that started this - half a million short-lived allocations a second at a thousand
streams - is off by twenty. `av_read_frame` on MPEG-TS returns one PES packet per video frame, not
one 188-byte TS packet: **25 packets/s per stream at about 2,540 bytes each**, so 2,500 allocations
and 7 MB a second at 100 streams, 25,000 and 70 MB at a thousand. The GC's whole share of the
container at 100 streams is 1 to 2 % of one core.

## Control: where 100 streams go

Three clean replicates (`ctl2`, `ctl3`, `ctl4`). Per-thread CPU is the `(utime+stime)` delta over a
30 s window as a percentage of one core, grouped by thread name; the counters are 2 s samples
averaged over the same window.

| | ctl2 | ctl3 | ctl4 | mean |
| --- | --- | --- | --- | --- |
| Container CPU, `/proc` | 243 % | 256 % | 255 % | **252 %** |
| `.NET Long Runni` (one demultiplexer per feed) | 72.9 % | 74.6 % | 77.3 % | **74.9 %** |
| `.NET TP Worker` (the preview: decode + JPEG) | 70.4 % | 69.5 % | 69.6 % | 69.8 % |
| `SRT:TsbPd` (libsrt, one per socket) | 48.3 % | 57.5 % | 54.0 % | 53.3 % |
| `SRT:RcvQ:w2` (libsrt, the receive thread) | 48.8 % | 52.3 % | 51.0 % | 50.7 % |
| `.NET Server GC` + `BGC` | 1.7 % | 1.1 % | 2.2 % | **1.7 %** |
| Demux wake-ups/s, whole group | 2,800 | 2,966 | 2,940 | 29/s per thread |
| Allocation rate | 7.0 MB/s | 7.1 MB/s | 7.2 MB/s | 7.1 MB/s |
| Gen0 / gen1 / gen2 collections per s | 0.1 / 0.05 / 0 | same | same | one gen0 every 10 s |
| GC committed | 862 MB | 469 MB | 881 MB | |
| Working set (counter) | 1,556 MB | 1,152 MB | 1,551 MB | **1,420 MB** |
| `docker stats` memory | 1,373 MiB | 1,047 MiB | 1,290 MiB | |
| `Monitor` lock contentions per s | 9.0 | 8.6 | 8.2 | vs 2,500 publishes/s |
| Delivered per stream | 0.505 | 0.611 | 0.507 | Mbit/s, source 0.567 |
| Packets/s per stream, bytes/packet | 24.8, 2,546 | 30.2, 2,525 | 25.0, 2,536 | |

The demultiplexer is **0.75 % of a core per stream** here (preview-cost saw 0.87 %; same order,
different day). Its thread wakes about 29 times a second, once per PES packet rather than once per
1,316-byte SRT message, so the sender's burst-per-frame is being read in one or two `srt_recvmsg`
calls. That is **about 250 µs of CPU per wake-up** for 2.5 KB of media, which is the number every
experiment below is really about.

A fourth control (`ctl1`) is in the results directory but its CPU rows are excluded: the first
version of the sampler read `/proc` one thread at a time, took ten seconds per snapshot, and
quietly stretched a "30 s" window to 49 s. The sampler now does one `awk` per snapshot and divides
by the elapsed time it measured. Its counters and packet-rate rows agree with the other three.

## Experiment 1: pool the packet buffer

**Verdict: not built as a pool, and the probe that bounds it did not move. Dropped.**

The ownership graph does not admit `ArrayPool<byte>.Shared` for `MediaPacket.Data` without a
reference count, and a reference count is not enough on its own:

- `RollingBuffer` holds every packet for the window (30 s, so ~750 per stream, 75,000 at 100
  streams, 750,000 at a thousand). `ArrayPool.Shared` keeps a few dozen arrays per bucket per core;
  at this outstanding count it would rent from the heap for nearly everything and behave as
  `new byte[]` plus an `Interlocked` per hand-off. The rolling buffer *is* the pool.
- `Rent` returns an oversized array, and three consumers read `Data.Length` as the packet's size:
  `PacketMuxer.Write`, `VideoDecoder.Decode`, `Misb0601.Decode` in `KlvExtractor`. The last two are
  not files this task may edit, and feeding a decoder 4,096 bytes for a 2,540-byte packet is a
  correctness bug, not a slowdown. `Data` would have to become `ReadOnlyMemory<byte>` everywhere.
- `KlvExtractor` stores `packet.Data` in a 64-deep ring (`KlvSample`), so a pooled KLV buffer would
  be reused under a sample still on show. That consumer would need a copy.
- Release points would be needed in `FrameDecoder`, `StreamRecorder`, the viewer loop in
  `LiveStreamCoordinator`, `NewestStartablePackets`' snapshot, and on every channel drain and
  completion in `PacketSubscription`. A missed release is only a missed return, but a double
  release is data corruption in somebody's recording.

So the honest cost is a refcount touching seven files, two of them outside this task, for a ceiling
that the control run already bounds: everything pooling could give back is the GC's share plus the
zeroing of new arrays, and the GC's share is **1.1 to 2.2 % of one core** (0.7 % of the container).

The zeroing half was measured directly, because it is a one-line, safe probe:
`GC.AllocateUninitializedArray<byte>(packet->size)` in place of `new byte[packet->size]` in
`StreamDemuxer.Pump` (the array is fully overwritten by `Marshal.Copy` on the next line).

| | exp1-1 | exp1-2 | exp1-3 | control mean |
| --- | --- | --- | --- | --- |
| Container CPU | 271 % | 276 % | 230 % | 252 % |
| Demultiplexer group | 75.5 % | 79.8 % | 62.8 % | 74.9 % |
| GC threads | 2.3 % | 2.5 % | 1.4 % | 1.7 % |
| Allocation rate | 7.2 | 7.2 | 5.9 | 7.1 MB/s |
| Gen0 per s | 0.1 | 0.1 | 0.1 | 0.1 |
| Delivered per stream | 0.507 | 0.515 | 0.425 | Mbit/s |

Nothing moved. (exp1-3's low row is the senders under-delivering - 20.6 packets/s - a host hiccup,
not the change.) The thread-time trace agrees: `GC.AllocateUninitializedArray` is 0.49 % of all
samples and `Buffer.Memmove` 0.12 %. The allocation is not where the demultiplexer's time goes.

## Experiment 2: copy less (`av_packet_ref`)

**Verdict: not built. Bounded by measurement at 0.1 % of samples, and unsafe for the retention.**

- The copy is 2,540 bytes 25 times a second per stream: 64 KB/s per stream, 6.4 MB/s at 100 streams.
  In the trace the copy (`Buffer.MemmoveInternal`) is **0.12 % of all thread-time samples**. There is
  nothing to win that a 30 s window at ±5 % could see.
- libav's MPEG-TS demuxer has no packet pool to "stay in". `mpegts_push_data` builds each PES
  payload with `av_new_packet`/`av_buffer_realloc`, plain heap allocations wrapped in an
  `AVBufferRef`; `AVBufferPool` is a decoder-side thing. `av_packet_ref` would keep 75,000 native
  mallocs alive for the window, invisible to the GC's accounting and to the buffer's byte ceiling
  except by hand, and the muxer and decoder would still `av_new_packet` + copy on the way out (or
  need their own `av_packet_ref`, which is the refcount of experiment 1 again, now across the
  native boundary).

## Experiment 3: fewer threads

**Verdict: the cost is about half thread and half work, and libav cannot be driven from one
thread without an interrupt callback, which still needs a thread to interrupt. Not built.**

The measurement that splits it, `/proc/1/task/*/stat` user against system time over 30 s on a
loaded container (workstation-GC run, same per-thread shares as control):

| Thread group | user | sys | sys share |
| --- | --- | --- | --- |
| `.NET Long Runni` (demultiplexers) | 37.4 % | 31.5 % | **46 %** |
| `.NET TP Worker` (preview) | 41.6 % | 25.3 % | 38 % |
| `SRT:RcvQ:w2` (receive) | 14.9 % | 33.2 % | 69 % |
| `SRT:TsbPd` (per socket) | 6.9 % | 40.4 % | **85 %** |

So of the demultiplexer's 0.7 % of a core per stream, **about 0.3 % is the kernel** - the blocking
`srt_recvmsg` sleeping and waking 29 times a second, on a WSL2 guest where every futex is dear -
and **about 0.4 % is user code**, of which the thread-time trace puts `StreamDemuxer.Pump`
exclusive (libav's TS parsing) at 7.4 % of samples against 68.9 % inside `SrtSocketStream.Read`
(libsrt's own receive path, blocked or running), with `StreamHub.Publish` at 0.31 %, the
allocation at 0.49 % and the copy at 0.12 %. **The hub - the part this service wrote - is about
one percent of the demultiplexer's time.** The rest is libsrt and libav and the kernel.

The single-thread design was checked and is not available with this libav:

- `av_read_frame` drives the transport through the `AVIOContext` read callback and expects it to
  block or return data. A custom callback returning `AVERROR(EAGAIN)` is treated as an error in
  `fill_buffer`: it sets `eof_reached` and `error` on the context, and the demuxer's parse state is
  not resumable across it. `AVFMT_FLAG_NONBLOCK` is honoured by device inputs only.
- An `AVIOInterruptCB` only makes the blocking read give up sooner; it does not turn the read into
  something an `srt_epoll` loop can drive. One thread reading 100 non-blocking sockets would need
  either our own TS demultiplexer or a libav driven through its own thread anyway.
- libsrt's `SRT:TsbPd` thread is 85 % kernel time, one per socket, and is not ours.

What that leaves: at a thousand streams the demultiplexer threads' kernel half is about three
cores and the user half about four, and the second of those is libsrt and libav rather than anything
in `Streaming/`.

## Experiment 4: the hub's lock and copy-on-write subscriber array

**Verdict: measured no. Not touched.**

- `dotnet.monitor.lock_contentions` for the whole process: **8 to 9 per second** in every control
  replicate, against about 2,500 `Publish` calls a second. Even if every one of those were the hub's
  gate (they are not; the coordinator and registry have locks too), that is 0.3 % of publishes.
- In the trace `Lock.TryEnterSlow` is 0.29 % and `Monitor.Enter_Slowpath` 0.11 % of all samples;
  `StreamHub.Publish` inclusive is 0.31 %.
- The subscriber array is rebuilt on `Subscribe`/`Detach` only; per packet it is a `foreach` over
  one or two entries (the frame decoder, and the KLV extractor once a layout carries KLV). No
  per-packet allocation, nothing to contend.

There is one lock per stream and one writer per lock. It does not contend at 100 streams and has no
mechanism by which it would at a thousand.

## The one thing that moved: workstation GC

Server GC on a 14-vCPU host runs 14 heaps with a gen0 budget sized for throughput, so at 7 MB/s it
collects gen0 every ten seconds and every packet in flight gets promoted first: gen1 was seen at
**673 MB** for a rolling buffer whose contents are about 200 MB. `DOTNET_gcServer=0`, nothing else
changed, control image:

| | wks-1 | wks-2 | wks-3 | control (ctl2-4) |
| --- | --- | --- | --- | --- |
| Working set (counter) | 971 MB | 1,034 MB | 988 MB | 1,556 / 1,152 / 1,551 MB |
| `docker stats` memory | 880 MiB | 900 MiB | 842 MiB | 1,373 / 1,047 / 1,290 MiB |
| GC committed | 295 MB | 353 MB | 321 MB | 862 / 469 / 881 MB |
| Gen1 size after last GC | 8 MB | 7 MB | 8 MB | 397 / 226 / 474 MB |
| Container CPU | 261 % | 245 % | 240 % | 243 / 256 / 255 % |
| Demultiplexer group | 80.0 % | 75.2 % | 71.8 % | 72.9 / 74.6 / 77.3 % |
| GC threads | 0.4 % | 0.9 % | 0.0 % | 1.7 / 1.1 / 2.2 % |
| Gen0 / gen1 / gen2 per s | 0.7 / 0.7 / 0.05 | same | same | 0.1 / 0.05 / 0 |
| GC pause per 2 s | 0.1 s | 0.1 s | 0.1 s | 0.0 to 0.1 s |
| Delivered per stream | 0.509 | 0.508 | 0.508 | Mbit/s |

**Every replicate's working set is below the lowest control replicate.** Mean 998 MB against
1,420 MB, −30 %; `docker stats` 874 against 1,237 MiB. CPU is inside the control spread, GC threads
are lower, delivered bitrate is identical. Against defect 2's `1Gi` limit this is the difference
between a pod that OOM-kills at about 100 test-pattern streams and one that does not.

It is one line and it is not in a file this task owns. Either of:

- `src/StorageDemo.Api/StorageDemo.Api.csproj`: `<ServerGarbageCollection>false</ServerGarbageCollection>`
  (bakes it into `runtimeconfig.json`; also `<ConcurrentGarbageCollection>true</ConcurrentGarbageCollection>`
  is the default and was what ran here), or
- `docker/Dockerfile` / `k8s/live/deployment.yaml`: `ENV DOTNET_gcServer=0` / an `env` entry.

The csproj is the better home: it travels with the binary and the tests. A middle setting such as
`DOTNET_GCHeapCount=2` with server GC was not measured and might keep server GC's throughput for
the HTTP side; at this workload the HTTP side is idle, so workstation is the simplest thing that
works.

## What is kept, and what it cost

Kept in `src/`: nothing. Kept as a recommendation with three replicates behind it: workstation GC.
Reverted: the `AllocateUninitializedArray` probe (one line, never in `src/`). Not built: the pool
with refcount, `av_packet_ref`, the single-thread reader, any hub change.

The build and test step in the brief was for whatever was kept in `src/`; with nothing kept there
was nothing to build, and the working tree's `Streaming/` files this task was allowed to edit carry
no modification from this session. (`FrameDecoder.cs`, `LiveStreamCoordinator.cs`,
`LiveStreamEntry.cs` and a new `StreamDetection.cs` were edited by the other agent at 23:34 while
these runs were going; they were not in any image measured here.)

## The largest remaining per-stream cost

Unchanged from `preview-cost.md`, now with a shape:

1. **Two threads per stream, about 1.3 % of a core between them here** (0.75 demultiplexer + 0.53
   `TsbPd`), roughly half of it kernel time spent sleeping and waking. The demultiplexer's user half
   is libsrt's receive path and libav's TS parse; the hub is a hundredth of it. Nothing in
   `Streaming/` can make this smaller by more than a few percent. What can: fewer wake-ups per
   packet (a larger `SRTO_RCVBUF`/latency does not change the per-message delivery; libsrt has no
   batch receive), or not running libsrt and libav per stream in this process at all, which is the
   Phase 3/8 shape.
2. **The preview, 0.7 % of a core per stream**, which Phase 8 removes for the streams nobody watches.
3. **The single receive thread at 50 %**, the per-pod ceiling, unchanged by anything here.
4. **Memory, 10 MB per stream with workstation GC** (14 with server GC), of which the rolling
   buffer is 2 MB; the rest is libsrt's per-socket receive buffers, libav contexts and the preview's
   decoder frames.

## How to repeat it

Everything is in the session scratchpad under `perf/`: `run.ps1` (one run: recreate the service
from `-Image`, restart senders, assert zero streams, start the counters sidecar, ramp, settle 120 s,
30 s per-thread CPU, `docker stats`, 10 s delivered + packet rate, counters sliced to the CPU
window), `cpu.sh` (the sampler, copied into the container), `trace.ps1` (the thread-time trace),
`results/*.txt` (every run, raw), `ctx/` and `ctx-exp1/` (the two build contexts).

```bash
# Images. Container DNS on this daemon broke again mid-session, so the experiment image was
# published in an SDK container started with --dns and overlaid onto the control image; the
# control image itself was a plain docker build while DNS still worked.
docker build -f docker/Dockerfile -t storagedemo-api:ctl <clean tree>
docker run --rm --dns 8.8.8.8 -v <tree>:/src -v <out>:/out -w /src mcr.microsoft.com/dotnet/sdk:10.0 \
  bash -c "dotnet publish src/StorageDemo.Api/StorageDemo.Api.csproj -c Release -o /out && rm -rf /out/ffmpeg"
printf 'FROM storagedemo-api:ctl\nCOPY --chown=storagedemo:storagedemo out/ /app/\n' | docker build -t storagedemo-api:exp1 -f - .

# Sidecars: dotnet-counters and dotnet-trace, sharing the service's pid namespace and /tmp.
docker build -t counters:1 - <<'EOF'
FROM mcr.microsoft.com/dotnet/sdk:10.0
RUN dotnet tool install -g dotnet-counters
ENV PATH=/root/.dotnet/tools:$PATH
ENTRYPOINT ["dotnet-counters"]
EOF
docker run -d --name counters --pid container:srt-api -v perf-tmp:/tmp counters:1 \
  collect -p 1 --counters System.Runtime --format csv --refresh-interval 2 --duration 00:03:40 -o /tmp/counters.csv

# Senders, as before (FFMPEG set, CRLF stripped, 900 s pattern).
pwsh perf/run.ps1 -Image storagedemo-api:ctl -Label ctl2 -N 100 -Settle 120
pwsh perf/run.ps1 -Image storagedemo-api:ctl -Label wks-1 -N 100 -Settle 120 -Env DOTNET_gcServer=0
pwsh perf/trace.ps1 -Seconds 30      # against whatever run.ps1 left up and loaded
```

Three traps beyond preview-cost's list:

- **A per-thread shell loop over `/proc/1/task/*` is not free at 250 threads.** It took ten
  seconds a snapshot and inflated every rate by the ratio; use one `awk` over all the files and
  divide by measured elapsed time.
- **`dotnet-trace`'s `dotnet-sampled-thread-time` profile samples wall time, not CPU.** A thread
  blocked in `recv` is sampled as though it were running. It gives the shape of where a thread
  spends its life; the user/system split from `/proc` is what says how much of that is CPU.
- **The counters csv is UTC with 2 s samples; slice it to the CPU window** or the ramp and the
  settle leak in. `run.ps1` does.

## Honesty

Docker Desktop through WSL2, 14 vCPU shared with the senders (4.5 cores) and, for part of the
session, with another agent building and running tests. The three control replicates spanned
243 to 256 % on the container and 72.9 to 77.3 % on the demultiplexer group, a ±3 % spread on the
number that matters; the working set spanned 1,152 to 1,556 MB, ±15 %. Experiments 1 and 2 could
not have been seen at this spread even if they worked, which is why each was first bounded by a
direct measurement (GC threads' CPU; the copy's share of samples) and only then, for the one that
was safe to build, run. The workstation-GC result clears its spread on every replicate and
reproduces in three independent containers; it is the only claim here strong enough to act on.
