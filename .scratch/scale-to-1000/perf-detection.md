# Detection hot path: what was tried, what moved, what did not

**Every number here is a processor number, measured on a laptop-class part** — an Intel Core Ultra
7 265U, 12 cores (2 performance, 8 efficient, 2 low-power efficient) and 14 threads, 64 GiB, on
Windows 11, .NET 10.0.303, ONNX Runtime 1.30.0, RF-DETR Nano at 384x384 through the CPU execution
provider. **There is no GPU on this machine.** Nothing below sizes the GPU tier, and where a
conclusion would change on one it is said so explicitly rather than extrapolated. A CUDA-only
upgrade is labelled as such and was not built blind.

Measured 2026-09-13 against `srt-listener-libsrt` at 54aeecb. Method as `perf-ingest.md`: a
control, replicates, one change at a time, a number beyond the replicate spread or the change is
reverted.

**Nothing in `src/` changed.** Five experiments were run; none produced a number that survives its
replicate spread against what the runner already does, and two were bounded at nothing before
anything was built. The two settings the runner's author chose deliberately —
`GlobalSpinControl = false` and a global intra-op thread pool at ORT's default width — were both
measured and both are right: spin control alone is worth **+22 % throughput**, and no thread count
beats the default.

There are two deployment shapes and this file describes exactly one of them. **The Windows desktop
— five to ten ingest streams, detection on one or two — is what these numbers are, and for that
shape the answer is that one default serves it**: at every thread setting tried, with the detector
oversubscribed to twice what the machine can do, eight ingesting streams showed **zero lost
packets, zero drops and unchanged delivered bitrate**. The Kubernetes shape with GPU workers is
not sized here at all.

## The harness

A throwaway console in the session scratchpad (`perf-detect/`), project-referencing
`StorageDemo.Infrastructure` and driven by argv, one process per configuration because `OrtEnv` is
a process-wide singleton. It uses the real `OnnxDetector` wherever the configuration can be
expressed from outside, and raw `InferenceSession`s only where it cannot (arena, per-session
threads, graph-optimisation level). Warmed three runs, then ten timed, median and best reported,
with `GC.GetTotalMemory`, `Process.WorkingSet64`, gen0/1/2 counts and
`GC.GetTotalAllocatedBytes` around each loop.

**The runner's thread configuration is settable from outside with no change to `src/`**, which is
what made experiment 1 possible: `OnnxDetector`'s static environment is a `Lazy` that yields to an
`OrtEnv` already created, so the harness creates one with the thread options it wants first.

The end-to-end and desktop runs are two temporary xunit facts in `DetectionWorkerTests`, run by
filter and removed afterwards; the suite is back to 285 passing.

## Control: one detection on this processor

Three replicates, each a fresh process, machine otherwise idle.

| | ctl1 | ctl2 | ctl3 | mean |
| --- | --- | --- | --- | --- |
| `Detect` on the dog JPEG, 720x1280 yuvj420p | 461 ms | 463 ms | 433 ms | **452 ms** |
| `Detect` on a decoded video frame, 720x1280 yuv420p | 469 ms | 416 ms | 417 ms | **434 ms** |
| best of ten, video frame | 394 ms | 373 ms | 351 ms | 373 ms |
| session load (first in the process) | 2,582 ms | 2,170 ms | 1,813 ms | |
| working set after load | 186 MB | 185 MB | 185 MB | |
| working set, steady | 242 MB | 241 MB | 241 MB | |
| gen0/1/2 over ten detections | 0/0/0 | 0/0/0 | 0/0/0 | none |
| managed allocation per `Detect` | 2,880 B | 2,880 B | 2,880 B | |

**Replicate spread is ±6 % on the median and wider on any single run** (worst-of-ten is routinely
30 % above the median), so an experiment has to move a number by more than about 10 % to count.
This is a laptop under a thermal budget: over an hour of back-to-back inference the same
configuration drifted from 434 ms to 680 ms and back. Every comparison below is therefore between
runs *interleaved in the same minutes*, never between a control measured in the morning and an
experiment measured at noon.

The runner's author measured about 540 ms a frame and a batch of two at 1,065 ms. On an idle
machine it is 434 ms and 780 ms; the ratio, not the absolute, is what carries.

## Experiment 1: threads

**Verdict: the default is right, and nothing was changed. Spin control off, already in the code,
is the one setting that is worth real money.**

### Latency against intra-op width, inter-op 1 throughout

Two replicates each, one frame and a batch of two.

| intra-op | batch 1 | batch 2 | speed-up over 1 thread |
| --- | --- | --- | --- |
| 1 | 1,100 / 1,181 ms | 2,296 / 2,330 ms | 1.00 |
| 2 | 719 / 769 ms | 1,587 / 1,477 ms | 1.53 |
| 4 | 537 / 532 ms | 1,037 / 951 ms | 2.13 |
| 8 | 439 / 426 ms | 880 / 877 ms | 2.63 |
| 0 (ORT default, one per physical core = 12) | 472 / 540 ms | 860 / 982 ms | 2.25 |

**Eight threads buy 2.6x over one and the default's twelve buy no more** — on these two replicates
they buy slightly less. That is the shape of a hybrid part: the eighth thread still helps, the
ninth through twelfth land on efficient cores and give the scheduler more to coordinate than to
compute. The difference between 0 and 8 does not survive replication, though: three further
interleaved replicates of 0 against 8 against 6 came out at 677 /
520 / 492 ms, 664 / 610 / 494 ms and 673 / 573 / 646 ms — **indistinguishable**. Setting a number
where ORT's default already sits is not a change worth making.

### Spin control, which is the setting that matters

`GlobalSpinControl = false` is in the code with a comment about not starving decoder threads. It is
also simply faster here. One session, one caller, the runner's own configuration otherwise:

| | rep 1 | rep 2 | rep 3 | mean |
| --- | --- | --- | --- | --- |
| spin off (as built) | 2.26 | 2.30 | 2.46 | **2.34 frames/s** |
| spin on | 1.75 | 1.91 | 2.10 | 1.92 frames/s |

**+22 %, every replicate clear of the other's spread.** Already in `src/`; it is recorded here
because it is the largest single configuration effect found and because the first version of this
experiment measured against a spin-on baseline and briefly produced a wildly inflated case for
removing the runner's lock. That mistake is the reason every number in this file names the
configuration it was taken under.

### Throughput: N frames across N smaller pools, against one frame across all cores

The real question in the brief. Two shapes were measured.

**N separate sessions, each with its own pool** (per-session threads, spin at ORT's default):

| shape | frames/s (2 reps) | working set |
| --- | --- | --- |
| 1 session x 12 threads | 1.62 / 1.83 | 241 MB |
| 2 x 6 | 1.68 / 1.90 | 387 MB |
| 3 x 4 | 2.17 / 2.24 | 546 MB |
| 4 x 3 | 2.05 / 2.34 | 716 MB |
| 6 x 2 | 2.38 / 2.45 | 1,035 MB |
| 12 x 1 | 2.58 / 2.71 | **1,984 MB** |

**One session, N concurrent `Run` calls** — which is what removing the runner's lock would buy,
with no extra weights — under the runner's own configuration (global pool, intra-op default,
spin off), three interleaved replicates:

| concurrent callers | frames/s | working set |
| --- | --- | --- |
| 1 | 2.26 / 2.30 / 2.46 → **2.34** | 241 MB |
| 2 | 2.49 / 2.73 / 2.50 → 2.57 (+10 %) | 259 MB |
| 4 | 2.57 / 2.86 / 2.71 → 2.71 (+16 %) | 318 MB |
| 12 | 2.94 / 2.89 / 2.84 → **2.89 (+23 %)** | 525 MB |

Two things fall out. **Extra sessions buy nothing that extra callers on one session do not**, and
they cost 112 MB of weights each: twelve sessions reach 2.65 frames/s for 1,984 MB where twelve
callers on one session reach 2.89 for 525 MB. Weights are not shared between sessions
(research/onnxruntime-dotnet.md §4) and this is what that costs. **A detector pool is measured
dead.**

And the concurrency win itself is +10 % to +23 %, which has to be set against experiment 2: the
batching the worker already does buys +13 % for no code at all. See the joint verdict there.

## Experiment 2: batching, honestly

**Verdict: batching is a real but small processor win that plateaus at two, and the worker already
has it. Nothing to change. It is not a GPU-only mechanism, but the GPU is where the shape of the
curve changes.**

Three replicates through `OnnxDetector`, same frame repeated:

| batch | median | per frame | frames/s (3 reps) |
| --- | --- | --- | --- |
| 1 | 450 / 449 / 436 ms | 445 ms | 2.22 / 2.23 / 2.30 → **2.25** |
| 2 | 821 / 778 / 775 ms | 396 ms | 2.44 / 2.57 / 2.58 → **2.53 (+13 %)** |
| 4 | 1,551 / 1,574 / 1,597 ms | 393 ms | 2.58 / 2.54 / 2.50 → 2.54 |
| 8 | 3,134 / 3,001 / 3,251 ms | 391 ms | 2.55 / 2.67 / 2.46 → 2.56 |
| 12 | 5,033 / 4,557 / 4,365 ms | 388 ms | 2.38 / 2.63 / 2.75 → 2.59 |
| 16 | 6,287 / 6,180 / 6,354 ms | 392 ms | 2.54 / 2.59 / 2.52 → 2.55 |

**A batch of two gets the whole win and every larger batch gets the same win**, at linearly worse
latency: batch 16 takes six seconds to produce sixteen results that a stream of singles would have
produced at 445 ms each. The earlier reading that "two bought nothing" (540 ms against 1,065 ms)
was taken while the machine was loaded; on an idle machine two frames cost 1.77x one, not 2.0x.

The mechanism is not mysterious and it is not GPU-specific: 12 threads across one 384x384 frame
leave the narrow layers of the graph under-occupied, and a second image in the batch widens every
operator. That is why concurrency (experiment 1) and batching land within a percent or two of each
other at the same total frames in flight — 2 callers 2.57 against batch 2's 2.53, 4 callers 2.71
against batch 4's 2.54. **They are two spellings of the same fix, and the worker already ships
one of them.**

**What is genuinely GPU-only** is the *size* of the win. On a processor the ceiling is the number
of cores and it is reached at batch 2. On a CUDA device a transformer's per-call launch and
synchronisation overhead is amortised across the batch and the arithmetic units are wide enough to
stay busy, so the curve keeps improving well past two — that is the claim detection-plan.md D3
makes and this measurement neither confirms nor refutes it. `MaxBatch = 8` is therefore the right
default to ship: on a processor everything from 2 up is equal, and on a GPU 8 is where the win
would be.

**Joint verdict on 1 and 2.** Removing the runner's lock to get concurrency would need a per-caller
input buffer and swscale context (the two things the lock protects), several detect loops in
`DetectionWorker` instead of one, and `SingleReader = false` on the due-frame channel. Against a
worker that already batches, the measured gain is 2.89 against 2.59 frames/s — **12 %, one
replicate spread wide**. Not built.

## Experiment 3: the input path

**Verdict: bounded at nothing. swscale is a quarter of one percent of a detection. Not built, and
the decoder should not be asked to emit at model size.**

The runner's `Place` replicated byte for byte — same `SWS_FAST_BILINEAR | SWS_FULL_CHR_H_INT |
SWS_ACCURATE_RND`, same cached context, same target plane — 200 runs against the same `Detect` on
the same frame, two replicates:

| source | swscale median | swscale best | full `Detect` median | swscale share |
| --- | --- | --- | --- | --- |
| 720x1280 yuv420p (the stream) | 1.02 / 1.27 ms | 0.88 ms | 421 / 398 ms | **0.28 %** |
| 1920x1080 yuv420p | 3.66 / 4.20 ms | 1.30 ms | 470 / 393 ms | **0.91 %** |
| 720x1280 yuvj420p (the JPEG) | 4.15 / 3.45 ms | 1.21 ms | 470 / 403 ms | 0.88 % |

Even at 1080p the resize is under one percent of the call it feeds. The full `Detect` medians for
720p and 1080p sources do not differ by more than the replicate spread, which is the same statement
from the other side. **There is nothing here to win**, and a decoder emitting at model size would
cost a filter graph per stream and a second scaler configuration for the preview, for under one
percent. This is the pooling case from `perf-ingest.md`: bounded by measurement before building.

On a GPU this reverses in *mechanism* but not in importance: the host-side swscale disappears into
`scale_cuda` or into a host round trip of about 3.1 MB a frame, and it is the round trip rather
than the resize that would matter. Still not a reason to change the processor path.

## Experiment 4: memory

**Verdict: the arena setting makes no difference either way, the per-call allocation is as close to
zero as the claim implies, and the worker's working set is flat after the first minute. Nothing
changed.**

### Session memory at load

Three replicates each, one configuration per process so the second session never measures the
first's leftovers:

| | arena on (default) | arena off |
| --- | --- | --- |
| working set at load | 141 / 142 / 140 MB | 142 / 142 / 139 MB |
| further growth over the first three runs | +47 MB | +47 MB |
| steady process working set | 240 / 241 / 239 MB | 241 / 241 / 239 MB |
| `Run` median | 650 / 522 / 464 ms | 580 / 466 / 471 ms |

A 108 MB model file costs about **141 MB of working set at load and another 47 MB once activations
are allocated**, so roughly 190 MB of the process is the detector. `AppendExecutionProvider_CPU(0)`
changes neither the memory nor the time by anything that clears the spread. The arena is a knob
worth having on a GPU, where `gpu_mem_limit` and `arena_extend_strategy` decide whether several
sessions can share a device (research §4); on the processor here it is inert.

### The per-call allocations the runner claims to avoid

Through `OnnxDetector` itself, `GC.GetAllocatedBytesForCurrentThread` around ten calls, twice:

| | bytes per `Detect` | gen0 collections over ten calls |
| --- | --- | --- |
| video frame, 10 boxes kept | 2,464 B | 0 |
| blank frame, no boxes kept | **784 B** | 0 |

So the claim holds as far as it goes: **784 bytes per call is the whole fixed cost** — the
`OrtValue` wrapper, the output collection, the empty result list — and the rest is the detections
themselves, which are the point. There is no per-frame pixel allocation: the input buffer is one
`NativeMemory.AlignedAlloc` grown only when the batch grows, and swscale writes into it. At one
detection a second on a thousand streams that is 2.5 MB/s of managed allocation, which is a third
of what the ingest path already does and which `perf-ingest.md` bounded at 1 to 2 % of one core.

### Is the worker's working set flat?

One stream, one detection a second, 210 s, sampled every 5 s (test host and worker in one process;
99 MB before the worker started):

```
313 327 333 336 337 338 339 336 344 346 346 350 352 352 352 352 352 352 352 353
350 350 350 350 350 350 346 346 346 346 347 346 346 347 347 347 349 349 348 348 348
```

**It rises 313 → 352 MB over the first 60 seconds and is flat for the next 150.** The rise is the
arena and the activation buffers reaching their working size, not a leak; the last 150 seconds sit
inside a 7 MB band with no trend. Under the desktop load below (8 streams, one detecting) the same
shape holds at a higher level.

## Experiment 5: startup

**Verdict: model load is 1.3 to 1.8 s warm, about 3 s cold, and it is a third of the time to the
first detection. Graph-optimisation level changes nothing. A pre-optimised model saves about half a
second and costs a second, version-pinned artefact. Not kept.**

Five loads per level, median:

| model | optimisation | load median | load best | first `Run` |
| --- | --- | --- | --- | --- |
| `rf-detr-nano.onnx` | `ORT_ENABLE_ALL` (default) | 1,635 ms | 1,269 ms | 287 ms |
| | `ORT_ENABLE_EXTENDED` | 1,560 ms | 1,237 ms | 271 ms |
| | `ORT_ENABLE_BASIC` | 1,327 ms | 1,246 ms | 302 ms |
| | `ORT_DISABLE_ALL` | 1,552 ms | 1,419 ms | 294 ms |
| `rf-detr-nano.opt.onnx` (saved by ORT) | `ORT_ENABLE_ALL` | 1,264 ms | 981 ms | 233 ms |
| | `ORT_DISABLE_ALL` | **991 ms** | 784 ms | 221 ms |

**Optimising the graph is free at load time**: disabling it entirely saves nothing, so all four
levels sit inside the spread. What the pre-optimised file saves is the parse of the larger source
graph — about 550 ms, 35 % of the load.

Against the end-to-end that is not worth an artefact. First detection served **3.6 to 4.1 s after
the worker starts** (the brief's 5.8 s, on a quieter machine), of which:

| | |
| --- | --- |
| model load | 1.3 to 1.8 s warm, up to 3.2 s cold |
| the worker's first poll beat, claim and subscribe | up to one beat |
| waiting for a keyframe at the sender's 1 s cadence | up to 1 s |
| the first detection itself | 0.4 to 0.5 s |

Saving 0.55 s of a 4 s one-time cost, in exchange for a second model file that
`OptimizedModelFilePath` binds to this ORT version and this machine's instruction set, is a bad
trade for a pod that lives for days. Recorded, not kept. **If a worker ever has to cold-start per
job it becomes worth revisiting**, and on a GPU it becomes a different question entirely: TensorRT
engine build is minutes, not seconds, and that is where startup caching earns its keep
(research §2).

## The end to end, on the processor

The worker against the test host in one process, the README's dog pushed as a still MPEG-2
transport stream over SRT at 15 fps with a keyframe every second, detection at one a second.

| | |
| --- | --- |
| first VMTI frame served after the worker started | **3.6 s** |
| VMTI frames served | 145 in 206 s at rate 1 |
| arrival interval, median / min / max | **994 / 879 / 2,038 ms** |
| serve time against the frame's own timestamp | median +34 ms, range −44 to +269 ms |
| CPU, host + one stream's ingest + worker, at 1 detection/s | **131 % of one core** |
| working set over the run | 313 → 352 MB in the first minute, then flat (see experiment 4) |

For scale, the desktop table below puts **eight** streams ingesting with detection off at 13 % of
one core, so essentially all of the 131 % above is the detector.

**At one detection a second the worker keeps exact pace** and the served timestamps track the
wall clock with no drift, which is the useful reading of the +34 ms figure: it is not a latency,
because the timestamp is derived from the frame's presentation time against an anchor, but its
staying near zero over 200 seconds says the pipeline is not falling behind.

Asked for five a second on the same stream, arrivals settle at a **462 ms median — 2.16 a second,
not five** — and CPU goes to 381 % of one core. That is the same ceiling the harness measures
(2.25 to 2.34 frames/s single-caller) arriving from the other direction, and it is the honest
statement of what this processor does: **one detection costs about 1.3 core-seconds**, so the
machine holds about two and a quarter of them a second, whatever the rate asks for.

### Frames decoded per detection

**One**, by construction, and the decode is not a cost worth counting either way.

`FrameDecoder.KeyframesSuffice` decodes keyframes only when they arrive at least as often as the
subscriber's rate. The test stream is `-g 15` at 15 fps, so keyframes come at 1.0 s and a rate of
1/s takes `1 x 1.0 <= 1.0001` — every predicted frame is skipped and exactly one picture is decoded
per detection. Raising the rate to five breaks that and every frame is decoded, which is visible in
the CPU figure above.

What a picture costs, measured directly:

| source | ms per picture (2 reps) | one core carries |
| --- | --- | --- |
| 720x1280 MPEG-2 | 0.72 / 0.83 ms | ~1,300 pictures/s |
| 1920x1080 MPEG-2 | 1.32 / 1.44 ms | ~725 pictures/s |

So the decode that "one per detection" saves is **0.8 ms against a 434 ms detection, 0.2 %**, and
decoding every frame instead — all fifteen a second — would cost 12 ms a second, **1.2 % of one
core per stream**. The rule is right and costs nothing to keep, but it is not where the money is
on a processor: it is a rule for the GPU tier, where the decoder and the model are separate budgets
and the decoder is the one that can saturate a bus (detection-plan.md D3). Nothing to change.

## The desktop shape: does the detector starve the ingest?

**Verdict: no. Not at any thread setting, not even when the detector is deliberately oversubscribed
to twice what the machine can do. The default is right for the desktop too, and a per-shape setting
is not warranted. Capping the thread pool only loses detections and buys nothing.**

This is the question the owner's constraint asks, and it is a different question from throughput. A
desktop runs five to ten ingest streams with detection on one or two; a setting that halves
detection latency and drops packets is a loss there. So: eight streams of the dog still pushed over
SRT at 2.49 Mbit/s each, D of them detecting at one a second, one 60 s window after a 30 s settle,
one configuration per process. The health reading is `perf-ingest.md`/`health-signal.md`'s:
per-stream `packetsLost` and `packetsDropped` from `GET /api/live` read once a second, plus the
delivered bitrate as a `bytes` delta.

| detecting | intra-op | delivered/stream | lossy beats | lost | dropped | detections served in 60 s | CPU |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 (ingest only) | — | 2.490 Mbit/s | **0/60** | 0 | 0 | — | 13 % |
| 1 | 0 (default) | 2.488 | **0/60** | 0 | 0 | 61 | 129 % |
| 1 | 8 | 2.488 | **0/60** | 0 | 0 | 61 | 138 % |
| 1 | 4 | 2.489 | **0/60** | 0 | 0 | 61 | 137 % |
| 1 | 2 | 2.489 | **0/60** | 0 | 0 | 61 | 115 % |
| 2 | 0 (default) | 2.489 | **0/60** | 0 | 0 | 59 + 56 | 416 % |
| 2 | 4 | 2.489 | **0/60** | 0 | 0 | 58 + 51 | 312 % |
| 4 (oversubscribed) | 0 (default) | 2.495 | **0/60** | 0 | 0 | 27 + 27 + 27 + 26 | 388 % |

**Every cell of the health columns is zero**, including the last row, where four streams demand
four detections a second from a machine that can do 2.25 and get 1.78 between them. Delivered
bitrate is 2.488 to 2.495 Mbit/s across the whole table — a 0.3 % band, tighter than the ingest-only
control's own run-to-run variation. The detector degrades by serving fewer detections, which is
exactly the right failure: the ingest path does not notice.

**Why it does not starve — mechanism, stated as a hypothesis because it was not measured under
this load.** `GlobalSpinControl = false` is the likely reason. With spinning on, ORT's idle pool
threads busy-wait between operators and are indistinguishable to the scheduler from work, which is
what would starve a demultiplexer blocked in `srt_recvmsg` and needing a slice the moment bytes
arrive; with spinning off they sleep, and the ingest threads — I/O-bound, 29 wake-ups a second,
250 µs of CPU each (`perf-ingest.md`) — get their slice immediately. What is measured here is the
throughput half (+22 %, above) and the fact that with spin off there is no loss at all; **spin on
was not re-run under the eight-stream ingest load**, so "spinning would have caused loss" is a
prediction, not a result. It is worth one run if anyone ever proposes turning spinning back on for
latency — and that is the only reason anyone would. Until then the setting should be treated as
load-bearing rather than as a tuning preference, which is what the comment on it in
`OnnxDetector.cs` already says.

The saturation the author saw in the test suite is a different thing and is not contradicted here:
`OnnxDetectorTests` runs detections back to back with no gap, for seconds at a time, which is the
4-detecting row sustained rather than one detection a second with 57 % of the machine idle between
them. The non-parallel collection is still right for the suite.

**Capping intra-op is a loss, not a safeguard.** At one detecting, intra-op 2 and 4 serve exactly
the same 61 detections as the default, because 0.72 s and 0.54 s both fit inside the 1 s budget —
a cap costs nothing *while there is slack*. At two detecting the slack is gone and the cap shows
its price: 58 + 51 served against the default's 59 + 56, for 312 % of a core against 416 %. It
gives back CPU that nothing else wanted and loses six detections a minute doing it. **There is no
setting here that trades ingest health for detection latency, because ingest health was never at
risk**, so there is nothing to trade and no reason for a second default.

**Therefore one default, not two.** `GlobalIntraOpNumThreads = 0` with `GlobalSpinControl = false`
serves the desktop and the cluster alike on a processor. The one place a per-shape setting will be
justified is the GPU, where intra-op should be 1 because CPU operator threads do nothing while the
graph runs on the device — see "What of this changes on a GPU" below. That is a per-*provider*
setting, not a per-deployment one, and it is measurable only on a GPU.

A caveat worth stating: at 416 % of one core for two streams detecting, **a desktop with two
detections a second running is using a third of a twelve-core machine**, and on a four-core
laptop it would be using all of it. The margin measured here is a twelve-core margin. The health
columns would be the first thing to check again on a smaller part, and the harness and the
temporary fact that produced this table are written down below so that check is a re-run rather
than a rebuild.

## What is kept, and what it cost

Kept in `src/`: **nothing.** Every experiment either failed to clear its replicate spread or was
bounded at nothing before it was built.

| Experiment | Number | Verdict |
| --- | --- | --- |
| 1. Threads, latency | intra-op 0, 6 and 8 indistinguishable; 4 is 6 % slower, 2 is 47 %, 1 is 2.3x | ORT's default kept |
| 1. Threads, throughput | 12 concurrent callers +23 %, but +12 % over what batching already gives | not built |
| 1. Separate sessions | 12 x 1 thread = 2.65 frames/s for 1,984 MB against 2.89 for 525 MB | measured dead |
| 1. Spin control off | **+22 %**, three clean replicates | already in the code |
| 2. Batching | batch 2 +13 %, batch 4/8/12/16 the same +13 % | already in the code, `MaxBatch = 8` kept |
| 3. Input path | swscale is 0.28 % of a detection at 720p, 0.91 % at 1080p | bounded at nothing, not built |
| 4. Arena on/off | 141 against 142 MB at load, no time difference | default kept |
| 4. Per-call allocation | 784 B fixed, 0 gen0 collections in ten calls | claim holds |
| 5. Startup | pre-optimised model saves 550 ms of a 4 s first-detection | not kept |
| Desktop shape, ingest health | zero loss and unchanged bitrate at every thread setting, even oversubscribed | one default, not two |
| Desktop shape, capped threads | intra-op 4 at two detecting: 109 served against 115, for 312 % against 416 % | not adopted |

Reverted: nothing, because nothing was written into `src/`. The harness lives in the session
scratchpad and the end-to-end facts were temporary additions to `DetectionWorkerTests`, removed
afterwards; that file is back at its 256 lines. The suite after the revert: **285 passed, 0 failed,
8 skipped, 0 warnings**, 2 m 52 s.

One aside from running it twice. The first run failed
`SrtListenerTests.A_sender_with_an_unparseable_name_is_rejected_during_the_handshake_and_not_after`
with "the callers said nothing"; it passes alone and the full suite passed on the next run with no
change to anything. It is a wall-clock timing assertion about how fast a rejection reaches an
FFmpeg caller, on a machine that had just spent an hour saturated. Noted rather than chased: it is
the same class of flake that put the detector tests in their own non-parallel collection, and the
same class the desktop measurement above was asked about.

**One hazard found and deliberately not fixed.** `OnnxDetector`'s static environment returns
`OrtEnv.Instance()` when one already exists, but the constructor then calls
`DisablePerSessionThreads()`, which throws `"the env must be created with the
CreateEnvWithGlobalThreadPools API"` if that pre-existing environment has no global pools. The
harness hit this the moment it created a plain session before a detector. Nothing in `src/` creates
an `OrtEnv` before `OnnxDetector` does, so it is unreachable today; it becomes reachable the moment
a second runtime component (a YOLO descriptor in its own runner, an ORT Extensions session, a
warm-up probe) shares the process. Recorded rather than patched, because a fix for a caller that
does not exist is the thing this project keeps not building.

## The largest remaining cost per detection, on a processor

1. **The model, and nothing else.** 434 ms a frame of which swscale is 1.2 ms, the decode is
   0.8 ms, the tracker and the ST 0903 encode are not measurable beside it, and managed
   allocation is 784 bytes. **About 99 % of a detection is `InferenceSession.Run`.** There is no
   second cost to go after: the runner is already the thin wrapper it claims to be.
2. **Which means the only processor levers are model-side**: a smaller input canvas (384 is
   RF-DETR Nano's export; 320 or 256 would be a re-export and an accuracy decision, not a code
   change), a smaller checkpoint, or quantisation. None of those is a `Detection/` change and all
   of them trade accuracy, so they belong to whoever owns the export.
3. **One detection costs 1.1 to 2.2 core-seconds** depending on how saturated the machine already
   is, so this twelve-core part holds 2.25 to 2.9 detections a second whatever is asked of it. At
   one a second per stream that is **one stream detecting with the machine 57 % idle, two streams
   just about keeping pace** (59 and 56 served of 60), **and four streams getting 1.78 between
   them.** The desktop shape's one or two detecting fits; a third would not, and the symptom would
   be fewer detections rather than anything the ingest side notices.
4. **Memory is 190 MB for the detector** (141 MB of weights at load plus 47 MB of activations) and
   it is flat. A second session would be another 112 MB for no throughput.

## YOLO26 Nano beside RF-DETR Nano, same runner, same frame

Measured 2026-09-13 when D4 landed the second detector, on the same laptop part, same CPU
execution provider, same `models/dog-2.jpeg` decoded to 720x1280 yuvj420p, through the same
`OnnxDetector`. Warmed, then ten timed runs, median and best. Two conditions, because they say
different things:

| Condition | YOLO26 Nano (640) | RF-DETR Nano (384) | Ratio |
| --- | --- | --- | --- |
| One session alive, mean of five | **113 ms** | **584 ms** | 5.2x |
| One session alive, batch of two, per frame | 109 ms | 558 ms | 5.1x |
| Both sessions alive, alternating, median of ten | **~280 ms** | **~1,125 ms** | 4.0x |
| Both sessions alive, alternating, best of ten | ~160 ms | ~900 ms | 5.6x |

The interleaved rows are four replicate runs (YOLO medians 267/274/290/304, RF-DETR
1,107/1,115/1,124/1,162), alternated round by round because this file's own list of traps says a
before-and-after pair on this machine differs by more than any change worth making. The
single-session rows come from each model's `A_batch_of_two...` test, which is why they are five
rounds rather than ten.

Three things worth keeping:

- **YOLO26 Nano is about four to five times faster per frame on this processor**, and it does that
  on a 640x640 canvas — 2.8x the pixels of RF-DETR's 384x384. **So the cost is the architecture,
  not the canvas**: a DINOv2-backed transformer against a convolutional one-to-one head. That is
  the one processor lever the previous section said was model-side and not a code change, priced.
- **Holding two sessions in one process roughly doubles each one's latency.** RF-DETR alone is
  584 ms and 1,125 ms while YOLO runs between its calls; YOLO alone is 113 ms and ~280 ms the same
  way. They share one global intra-op pool by design, so alternating calls are not free and a
  worker that offers a choice of model should load one, not both. The seam test loads both on
  purpose and its numbers are labelled accordingly.
- **What it means for the desktop shape.** At one detection a second per stream, RF-DETR's 452 ms
  control holds about **two streams** on this part (the end-to-end run above: 59 and 56 served of
  60, and four streams getting 1.78 between them). At 113 ms, YOLO26 Nano holds about **eight**.
  The desktop shape — five to ten ingesting, one or two detecting — fits comfortably either way,
  which is the honest conclusion: **this difference does not change the desktop answer, it changes
  the headroom above it.** Where it would matter is the cluster tier, where a fourfold difference
  is a fourfold difference in GPU nodes, and there is no measurement here for that.

**Accuracy is not measured and the two are not interchangeable.** yolo26n misses the beagle that
RF-DETR scores at 0.686, at any threshold (`YoloDetectorTests.There_is_no_dog_at_any_score`), and
`models/README.md` establishes that this is the nano checkpoint's recall rather than a broken
export. A four-times-faster detector that does not find the animal is not a free upgrade. **And the
licence is the harder constraint**: RF-DETR Nano is Apache-2.0 for code and weights, Ultralytics is
AGPL-3.0 or a paid enterprise licence, and the vendor's own position covers a service that runs an
exported file. Speed does not settle that; see `models/README.md`, "Licence — state it before using
it".

## What of this changes on a GPU

Labelled honestly: none of it was measured, because there is no GPU here.

- **Batching.** The mechanism is the same — a wider batch occupies more of the machine — but the
  processor's ceiling is 12 cores and it is reached at two. A CUDA device's is thousands of lanes
  plus a per-call launch overhead that only a batch amortises, so the curve that goes flat at two
  here is expected to keep improving well past eight. `MaxBatch = 8` is a processor-neutral, GPU-
  friendly default and should stay.
- **Threads.** `intra_op_num_threads` should go to **1** on the CUDA path, because CPU operator
  threads do nothing while the graph runs on the device (research §3). The runner currently sets
  one global pool at ORT's default width for both; that is right for the processor and wasteful,
  though not harmful, on a GPU. **This is the one place a per-provider setting would be justified**,
  and it is a two-line change gated on `Provider == "CUDA"` — worth making when there is a GPU to
  measure it on, not before.
- **The lock.** On the processor, concurrent `Run` buys 12 % over batching and is not worth the
  surgery. On a GPU the arithmetic is different: `Run` blocks the calling thread while the device
  works, and the runner's `ponytail:` comment already says so ("per-caller buffers are the upgrade
  if a GPU sits idle"). **That comment is correct and this measurement does not contradict it** —
  it only says the upgrade has no processor justification.
- **The input path.** swscale disappears into either `scale_cuda` or a 3.1 MB host round trip per
  1080p frame; at 30 frames a second per stream that round trip is 93 MB/s each way and becomes a
  real budget where the 1.2 ms resize never was. `IOBinding` with device `OrtValue`s is the
  answer and is GPU-only by construction.
- **Startup.** 1.5 s of ONNX parse becomes minutes of TensorRT engine build unless a timing cache
  or an engine cache is mounted per node (research §2). The pre-optimised-model trick rejected
  above is the small version of a problem that is large on a GPU.
- **The per-stream ceiling.** 2.25 detections a second here; a GPU worker's ceiling will be set by
  the decoder or the bus rather than by the model, which is exactly the question
  detection-plan.md's measurement table asks and which nothing here can answer.

## Honesty

A laptop-class 12-core part with no GPU, on Windows, under a thermal budget that moves the same
configuration by 50 % across an hour. Three control replicates spanned 416 to 469 ms on the number
that matters, ±6 %, and single runs within a replicate spanned 351 to 630 ms. Experiments 3 and 4
could not have been seen at that spread even if they worked, which is why each was bounded by a
direct measurement first — swscale's share of the call, the allocation counter — and only the ones
that survived that bound were run properly. Experiment 1's first pass measured against a baseline
configured differently from the runner (per-session threads, spin on) and produced a 78 % figure
for a change that is worth 12 %; every table above now names its configuration for that reason.

The end-to-end and desktop runs have the API, the SRT ingest, the senders, the worker and the test
harness in **one process on one machine**, so every CPU percentage is the whole system's and the
working-set figures include the test host (about 100 MB before the worker starts, 230 MB with eight
streams ingesting). A real desktop deployment would have the same components with the same total
cost; a cluster would not, and the ingest figures here should not be read as pod numbers.

## How to repeat it

Everything is in the session scratchpad under `perf-detect/`:

```bash
# The harness. One process per configuration, because OrtEnv is a process-wide singleton.
dotnet build -c Release
./bin/Release/net10.0/perf.exe control          # the control, dog frame and video frame
./bin/Release/net10.0/perf.exe threads 8 1      # intra-op 8, inter-op 1, through OnnxDetector
./bin/Release/net10.0/perf.exe share 12 0 global  # one session, 12 concurrent Run, runner's config
./bin/Release/net10.0/perf.exe conc 12 1        # 12 sessions, one thread each
./bin/Release/net10.0/perf.exe batch 1 2 4 8 12 16
./bin/Release/net10.0/perf.exe sws              # swscale alone against the model
./bin/Release/net10.0/perf.exe decode           # what one decoded picture costs
./bin/Release/net10.0/perf.exe mem arenaon      # and arenaoff, and "mem alloc"
./bin/Release/net10.0/perf.exe startup          # and "startup save", "startup loadopt"

# The end to end and the desktop shape: temporary facts in DetectionWorkerTests, since removed.
PERF_STREAMS=8 PERF_DETECT=1 PERF_INTRA=4 PERF_SECONDS=60 \
  dotnet test tests/StorageDemo.Tests --filter FullyQualifiedName~Perf_desktop -- --verbosity detailed
```

Four traps beyond `perf-ingest.md`'s list:

- **`new SessionOptions()` creates the default `OrtEnv`.** Any call that wants a global thread pool
  must run `OrtEnv.CreateInstanceWithOptions` before the first `SessionOptions` exists, or the
  runtime refuses with "singleton instance already exists". This is also what makes the thread
  sweep possible without touching `src/`.
- **This laptop drifts 50 % across an hour of inference.** Interleave every comparison; a control
  taken before a sweep and an experiment taken after it will differ by more than any change worth
  making.
- **Measure the baseline in the configuration you will ship.** A per-session-threads, spin-on
  baseline made a 12 % change look like 78 %.
- **Two sessions in one process do not give clean memory numbers.** Native allocations do not
  return to the OS on `Dispose`, so the second configuration measures the first's leftovers. One
  configuration per process.
