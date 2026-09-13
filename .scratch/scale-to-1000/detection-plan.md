# Plan: detection, model-agnostic, RF-DETR first

The worker tier's inference half. Phase 8 of `plan.md` says where detection runs and why; this says
what runs there. Read that section first, then `research/detector-models.md` and
`research/onnxruntime-dotnet.md`, which are where every number below comes from.

## What is already decided

- **Detection is per stream, on demand.** A toggle, not a constant. A thousand streams ingest and a
  chosen subset decode.
- **Detections leave as MISB ST 0903**, standalone, never burned into the picture and never nested
  in the platform metadata. `Misb0903.cs` encodes it and is round-trip tested.
- **A capture says which detection caused it.** Built. A detection is identified by stream,
  precision timestamp and target id.
- **The ingest pod does not decode.** A worker subscribes per stream over
  `GET /api/live/peer/view/{name}` and decodes once, on a GPU in the cluster.
- **RF-DETR through ONNX, as fast as it can go.** YOLO supported too, and the abstraction must fit
  both without lying about their differences.

## The one thing most likely to go wrong

Not throughput. **Coordinates.** The two model families disagree on geometry in a way that fails
silently: RF-DETR *stretches* the frame to a square and destroys the aspect ratio; YOLO
*letterboxes* it, preserving aspect with padding of value 114. So the inverse transforms are
different shapes, not different constants: RF-DETR maps a normalised box straight onto the original
frame, YOLO must subtract padding and divide by a gain before it clips.

Get that wrong and every box is plausibly placed and quietly incorrect, which is exactly the class
of bug the ST 0601 scale validation existed to catch, and exactly what a person only notices when a
box sits next to the thing it is supposed to be on rather than on it.

Three smaller versions of the same hazard:

- **Normalisation.** RF-DETR wants ImageNet mean and standard deviation after dividing by 255. YOLO
  wants only the division. Swapping them degrades accuracy quietly rather than failing.
- **Class identity.** RF-DETR's COCO checkpoints emit the *sparse* COCO category id, 1 to 90, with
  slot 0 as background. YOLO emits contiguous 0 to 79. The same integer means `dog` in one and
  `sheep` in the other. Neither publishes an ontology URI, so the mapping to an ST 0903 class name
  is a table this service owns and must get right.
- **Scores.** RF-DETR emits raw logits needing a sigmoid. A YOLO export may or may not have done
  the work for you, depending on flags frozen at export time.

**Therefore the geometry strategy owns the forward transform and its inverse as one object.**
Splitting them across two classes is precisely how they drift apart. This is the single most
important structural decision here.

## The seam

```
IDetector.Detect(frame) -> Detection[]        // in ORIGINAL-frame pixels, always
```

One interface, two implementations, and behind it exactly two per-model things:

1. **A geometry strategy**, `Stretch` or `Letterbox(padValue)`, owning both directions.
2. **A decode descriptor, as data**: input and output tensor names, box format, whether scores need
   a sigmoid, whether non-maximum suppression is required, and the class table.

Do **not** abstract the query or anchor count; read it from the tensor shape. Do **not** unify the
class-id schemes; they are genuinely different and pretending otherwise is a lie that surfaces as
mislabelled targets.

Bind tensors **by name**, never by position. RF-DETR's own documentation warns that the class count
can coincide with the box count, which makes a positional mistake invisible.

## The phases

Each is independently shippable and each ends with something you can point at.

### D0. Geometry, alone, before any model exists

The highest-risk piece, and it needs no ONNX, no GPU and no weights. Implement both strategies and
their inverses, and test them the way the KLV decoder was tested: so that agreement is evidence
rather than a tautology.

- **Round trip.** A box in original-frame pixels, forward into model space, back out, must land
  where it started within a pixel, for both strategies, across aspect ratios wider and taller than
  square, and for boxes touching each edge.
- **Against the reference.** RF-DETR's resize is half-pixel bilinear with antialiasing off, and it
  ships a module specifically to pin that down; YOLO's rounding is deliberately half-down. Assert
  those behaviours explicitly, citing the source, because a plausible-looking reimplementation that
  rounds the ordinary way is off by a pixel forever.
- **A synthetic end-to-end.** Draw a rectangle at known pixels on a known frame size, run the
  forward transform, place a box exactly on it in model space, invert, and assert it returns to the
  original rectangle. This is the test that would have caught the whole class of failure.

**Done when** both strategies round-trip and the reference behaviours are pinned.

### D1. The runner, and RF-DETR on the processor

`Microsoft.ML.OnnxRuntime.Gpu` at 1.30.0, **one package reference for both environments**: the GPU
natives include the processor execution provider and the CUDA library loads lazily, so the same
build runs on a developer machine with no GPU. The cost is about 146 MB of unused natives in a
developer restore, which is the right trade against two project configurations.

**A missing execution provider throws rather than falling back**, so selection is a try-and-catch
with an honest log line, not an assumption. Unsupported *operators* do fall back per node, which is
a different mechanism and should not be confused with it in a comment.

One `InferenceSession` per model, shared across threads, because `Run` is thread-safe and sessions
do not share weights. Set a global intra-operation thread pool rather than letting each session
spin one per core, which is the default and is wrong for a service holding several.

**Export with normalisation baked into the graph, taking uint8 input.** This is the decision that
matters most for speed, and it is made at export time rather than in code: it removes the
per-frame floating-point conversion entirely, and it is what makes the device-memory path in D3
reachable at all. Also export with dynamic axes, or batching is impossible later; verify the model
actually accepts a changed batch rather than trusting the flag.

**Done when** a still image through the full path produces boxes on the right objects, checked by
eye once and then pinned by a test asserting a known image yields a known class in a known region.

### D2. The worker, on the processor, one stream

A separate deployment, not a change to the ingest pod. It subscribes to a stream over the peer-view
route, demultiplexes, decodes, detects at the configured rate, and emits ST 0903.

- **Detection rate is per stream and separate from the tracker's rate.** Pattern of life needs
  stable identities, which is a tracker property; detecting every frame on every stream is the most
  expensive option available and is rarely what is wanted.
- **The preview becomes a by-product.** The worker already has the decoded frame, so the thumbnail
  comes from here rather than from a second decode in the ingest pod. That is the claim Phase 8
  makes and this is where it is honoured.
- **Carriage.** The VMTI packets must reach a consumer on the same presentation clock as the
  platform metadata. This is unbuilt and is the largest unknown in the phase: decide whether the
  worker muxes its own transport stream or hands packets back for the owner to carry, and write the
  decision down before building either.

**Carriage, decided.** The worker posts each VMTI frame back to the stream's owner over the peer
route it already subscribes through, and the owner keeps a short ring of them beside the KLV ring
and serves them on both surfaces. That is the smallest thing that gets a detection to a client
with its timestamp intact, and it reuses the one hop that already exists. Muxing the VMTI packets
into the SRT output as a KLV stream, so a conforming external consumer sees them beside the video
with no API call, is the upgrade and is recorded as such: it is the same bytes, on the same clock,
one muxer away.

**Decode only at the detection rate.** The tracker predicts between detections without a frame, so
the worker never decodes a frame it will not detect on. At five detections a second on a
twenty-five frame stream that is one decode in five, and it is the largest single saving in the
phase. Detect on keyframes when the rate allows it, since a keyframe decodes standalone and a
predicted frame needs everything since the last one.

**The control plane, fixed here so the worker and the client build against one contract.**

```
// Registry: LiveStream gains
bool   DetectionEnabled;        // the toggle, set through the owner, read by workers
int    DetectionRate;           // detections per second while enabled; 0 means the worker's default
string? DetectionWorker;        // which worker holds it, null when none has claimed it yet

// REST, guarded like record and snapshot, forwarded to the owner
PUT    /api/live/detect/{name}     {"enabled":true,"rate":5}     -> the stream
GET    /api/live/detections/{name}                              -> the latest VMTI frame, decoded + raw
POST   /api/live/peer/detections/{name}   (worker -> owner)      body: one ST 0903 packet + its PTS

// gRPC, same guard, proto fields 19..25 on LiveStreamMessage are held for exactly this
rpc SetLiveDetection (SetLiveDetectionRequest) returns (LiveStreamMessage);
rpc GetLiveDetections (LiveStreamName)          returns (LiveDetectionsMessage);
message LiveDetectionsMessage { google.protobuf.Timestamp timestamp; int32 frame_width, frame_height;
                                repeated VmtiTargetMessage targets; bytes raw; }
message VmtiTargetMessage     { int32 id; int32 left, top, right, bottom; optional int32 confidence_percent;
                                optional string ontology_class; optional string track_id;
                                optional VmtiTrackStatus track_status; }
```

The owner keeps a short ring of VMTI frames beside the KLV ring, so `GetLiveDetections` and the
REST route answer from memory and the client polls them the way it polls KLV. A worker learns what
to detect by listing streams with `DetectionEnabled` and no worker, claims one by writing its name
through the owner, and stands down when the toggle clears. That is the pull-lease shape Phase 6
describes for cameras, applied to workers, and it is why nothing pushes work to a worker.

**Done when** a stream with detection switched on produces VMTI packets whose timestamps align with
the video, and a capture triggered by one carries its reference.

### D3. The GPU, and the honest path to it

The ideal is a frame going from the hardware decoder into the model without crossing the bus.
**That is not practical from .NET and the plan should stop pretending otherwise.** ONNX Runtime can
wrap a device pointer, and FFmpeg's hardware frame is one, but the bridge between pitched NV12 and
a dense tensor is a CUDA kernel that cannot be written in C#.

The realistic path, in order of preference:

1. **`scale_cuda` for resize and format conversion on the device, with normalisation baked into the
   graph**, which removes the kernel requirement entirely. This is the plan.
2. A host copy per frame otherwise, about 3.1 MB per 1080p frame and roughly 93 MB/s per stream at
   30 frames per second. Acceptable at small scale, not at large.
3. A small native shim. Option three, and not the plan.

**Batch across streams.** ONNX Runtime does not batch concurrent calls for you; the caller
assembles. A worker holding many streams fills one batch from several cameras in the same tick, and
that is what makes a transformer model efficient.

**On TensorRT: measure before adopting.** It is faster for a fixed detector, but a built engine is
not portable across GPU models, driver versions or runtime versions, so it cannot simply be baked
into an image for a mixed cluster. Cold build was measured at several minutes. Ship the timing
cache, cache engines on a volume per node, or stay on CUDA. Decide with a number, not a preference.

The container needs an NVIDIA CUDA runtime base image; the plain .NET base will not do. The pod
requests a GPU under limits, with the device plugin present.

**Done when** the same worker runs on GPU with a measured throughput figure, and the processor path
still works unchanged for development.

### D4. YOLO, which is how the abstraction is proved

Add the second implementation. If D0's geometry is right and the descriptor is honest data, this is
small. If it is not, this is where it hurts, which is the point of doing it second rather than
never.

Two things to handle that RF-DETR does not have. A YOLO export **self-describes**, carrying its
class names, image size and whether an end-to-end head is present in the model's metadata, so read
that rather than requiring configuration. And there are **three possible output contracts**
depending on flags frozen at export: with suppression baked in, without it, or the newer
suppression-free head. Detect which from the output shape and the metadata rather than asking the
operator to declare it.

**Licensing is a real constraint here, not a footnote.** RF-DETR's smaller variants are Apache-2.0
for both code and weights, which is clean for a commercial service, and that is a good reason to
lead with it. Ultralytics is AGPL-3.0 or a commercial licence, and the vendor's own licensing page
lists hosted services and proprietary custom-trained models as requiring the commercial one. No
primary source was found exempting a service that merely runs an exported file. That is the
vendor's stated position rather than legal advice, and somebody should decide it deliberately
before YOLO ships in anything delivered.

**Done: both run behind one interface, and the abstraction did not bend.** `DetectorGeometry` and
the class tables were not touched at all; the letterbox and its inverse took the second family
unchanged, including the reference's half-down rounding. Three honest changes were needed and none
of them were YOLO pretending to be RF-DETR: a pixel-corner box format, which carries the tensor
arity with it because one is two tensors wanting a sigmoid and an argmax while the other is one row
packing box, score and class; the letterbox fill the geometry phase had left a note predicting; and
the class table being whichever the file itself declares, falling back to the descriptor.

**The choice this forces, and it is the owner's.**

| | RF-DETR Nano | YOLO26 Nano |
| --- | --- | --- |
| Per frame, this processor | 584 ms | **113 ms** |
| Streams at one detection a second | about 2 | about 8 |
| Canvas | 384 | 640, so 2.8x the pixels |
| File | 108 MB | 9.8 MB |
| The sample's dog | found at 0.686 | not found at any score |
| Licence | Apache-2.0, code and weights | AGPL-3.0 or commercial |

Four to five times faster on nearly three times the pixels, so the cost is the architecture rather
than the canvas. On the desktop shape that is the difference between two streams detecting and
eight. It buys nothing on accuracy, and it does not touch the licence, which remains the reason
RF-DETR leads.

**Do not load both in one process.** Two sessions roughly double each other's latency, because they
share the one global thread pool. A worker offering a choice of model loads the one it was told to.

**One check added that D4 did not ask for**, and it earns its place: a file whose own metadata says
it uses the other head is refused at load, because such an export has a differently shaped output
and would decode into plausible nonsense in silence. Sniffing the shape instead was rejected as
guessing at a contract this project has never produced.

### D5. Tracking, which is what pattern of life actually needs

Detections are per frame; pattern of life is about identity over time. A motion-model tracker runs
at full frame rate between reduced-rate detections and is cheap on the processor.

ST 0903 carries tracks as well as detections, in a tracker local set that the encoder deliberately
left out, and it is named there as the first thing to add. So this phase is a tracker plus that
extension to the encoder.

**Done when** a target keeps its identity across frames and the track appears in the emitted
metadata.

## OpenCV 5 was measured against ONNX Runtime, and loses on the thing that matters

Asked by the owner on 2026-09-12: would OpenCV 5 through OpenCvSharp be a better runtime? Tested
rather than argued, on the same export, the same input and the same processor, warmed, ten runs:

| Runtime, CPU | Median | Best | Outputs |
| --- | --- | --- | --- |
| ONNX Runtime 1.30.0 | 1039 ms | 736 ms | reference |
| OpenCV 5.0.0 DNN | 860 ms | 703 ms | identical to 1e-4 on boxes, 1e-5 on scores |

Two surprises worth keeping. OpenCV 5's ONNX importer loads a DINOv2-backed DETR with the uint8
input and the preprocessing nodes intact, which older OpenCV could not have done, and on this
processor it is as fast or slightly faster. The medians are noisy because another agent was
building at the time; the ratio is the trustworthy part, and it is about one.

Why it is still the wrong choice here: **OpenCvSharp does not support CUDA**, in the project's own
words, and no runtime package it publishes carries the GPU backend; a CUDA-enabled OpenCV is a
build the user does themselves, per platform. ONNX Runtime ships CUDA and TensorRT natives from
NuGet and one package reference already serves both the developer machine and the cluster. Since
the whole point of the worker tier is a GPU, a runtime that reaches one only by hand-building its
natives is a step backwards, however it does on a processor. It would also be a fourth native
stack, and the preprocessing it would bring is already done in one swscale pass with the
normalisation in the graph.

The processor result is not wasted: it is a second independent confirmation that the export and
the runner's numbers are right, since two unrelated runtimes agree to five decimal places.

## Measured on a processor, and the desktop worry did not survive contact

`perf-detection.md` ran five experiments on a twelve-core laptop part with no GPU. **Nothing was
kept in the source**, because the two settings worth having were already there and the rest were
bounded at nothing.

- **Threads: one default serves both shapes.** The worry that a detector would starve ingest on a
  desktop is not what happens. With eight streams ingesting and two detecting, and again with four
  detecting on a machine that can sustain about two, the per-stream loss and drop counters read
  **zero on every beat** and delivered bitrate stayed inside a third of a percent, at every thread
  setting. The detector degrades by serving fewer detections; ingest does not notice. Capping
  threads is therefore a loss rather than a safeguard, and no per-shape configuration is needed.
  The one setting that pays is already in the code: spinning off is worth 22 percent.
- **Batching plateaus at two on a processor**, worth 13 percent, and the earlier "two bought
  nothing" reading was taken under load. The worker already batches and its limit of eight is right
  for a processor and righter for a GPU, where the curve keeps going.
- **A detector pool is measured dead.** Twelve sessions reach less throughput than twelve callers
  on one session, for four times the memory.
- **The input path is bounded at nothing**: the resize is a quarter of a percent of a detection.
- **About 99 percent of a detection is the model call.** There is no second cost in the detection
  code to chase; the remaining levers are the canvas size, the checkpoint and quantisation, and
  they belong to the export rather than to the runner.

One hazard was found and fixed rather than documented: the runner reused an ORT environment created
by anything else in the process but still asked its session to use global thread pools that such an
environment cannot have, which throws. Unreachable while this is the only such component, and
reachable the moment a second one shares the process.

**What a GPU is expected to change**, none of it measured here: batching keeps improving past
eight, intra-op threads should drop to one on the CUDA path, the input lock becomes worth removing,
the resize becomes a device operation or a host round trip, and the model load becomes a TensorRT
engine build that needs a per-node cache.

## What to measure, and when

Nothing here is sized until there is a GPU node.

| Question | How |
| --- | --- |
| Frames per second per GPU at a useful batch | Benchmark RF-DETR at the real resolution, not a published figure |
| Whether the decoder or the model saturates first | The two are separate budgets and only one can be relieved |
| What a detection rate costs | Measure at one, five and twenty-five per second; the difference is the bill |
| Whether TensorRT earns its operational cost | Against CUDA, on the target hardware |

## What this plan deliberately does not do

- No model training, no fine-tuning, no dataset work.
- No ontology server. The class name travels with a URI and something must eventually publish one;
  this plan treats that as an external decision.
- No geo-referencing of detections. The worker has the platform metadata and could compute map
  positions, but standalone carriage gives up the offset items anyway, so it is a later phase.
- No re-identification across cameras. Pattern of life within one stream first.
