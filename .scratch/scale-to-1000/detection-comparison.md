# Their detection code against ours

Read on 2026-09-13: all thirteen files of `C:\Projects\cv\src\VisoNode.Runtime\Detection\` (1,430
lines) plus enough of `VisoNode.Runtime\Nodes\DetectionNode.cs`, `VisoNode.Core\Graph\NodeConfig.cs`,
`VisoNode.Runtime\Models\ModelManager.cs`, `VisoNode.Core\Data\RectF.cs` and `tools\export\README.md`
to know how they are configured and used. Against them: our
`src\StorageDemo.Infrastructure\Detection\` (624 lines), `DetectorGeometry.cs`, `CocoClasses.cs`,
`StorageDemo.Worker\DetectionWorker.cs`, and `detection-plan.md` / `perf-detection.md`.

Nothing was built or run. Every claim below is from reading, and where a claim would need a
measurement to settle it, it says so instead of guessing.

**The shapes are different in a way worth stating once.** Theirs is a desktop node-graph runtime:
many detection nodes in one process, a model the operator picks from a manifest or points at with a
file path, DirectML on whatever GPU is in the machine, one frame at a time, a UI status line. Ours
is a headless worker: one model chosen at deployment, exported by our own script, CUDA or the
processor, batched across streams, output as ST 0903. Several differences below are that difference
and not a judgement, and they are labelled.

---

## 1. Where they are right and we are wrong

### 1.1 They warm the session up; we do not

`RfDetrDetector.cs:65`, `Yolo26Detector.cs:65`, `Yolo26SegDetector.cs:65`, `YoloV4Detector.cs:58`,
all four identical:

```csharp
// Warm-up: DML shader compilation makes the first inference cost seconds.
using var _ = RunInference();
```

We do not. `OnnxDetector`'s constructor loads the session, verifies the contract, reads the metadata
and returns; the first real frame pays whatever the provider defers to first use. On the processor
that is small. On CUDA it is not — cuDNN algorithm selection and kernel load happen on the first
`Run`, and D3 is the phase where that starts mattering. Our own control measured session load at
1,813–2,582 ms (`perf-detection.md`, "Control") but never separated first-`Detect` from steady-state
`Detect`, so we do not actually know what our first frame costs. Theirs cannot be surprised by it.

This is the clearest "handled case we have missed" in the whole comparison, and it is the smallest.

### 1.2 Their errors print the shape, ours print only the name

`Yolo26Detector.cs:92`:

```csharp
throw new NotSupportedException(
    "Unrecognized YOLO26 export: expected a single [1,N,6] output or logits/pred_boxes outputs, got " +
    string.Join(", ", outputs.Select(o => $"'{o.Key}' [{string.Join(",", o.Value.Dimensions)}]")));
```

Ours, `OnnxDetector.cs:356`:

```csharp
throw new InvalidOperationException($"The model has no output named '{name}'; it has {string.Join(", ", _session.OutputMetadata.Keys)}.");
```

When a descriptor and a file disagree, the name tells you *that* they disagree and the shape tells
you *which* file you have. Theirs is strictly more useful for the same line of code.

### 1.3 The letterbox and its inverse: a tie on arithmetic, a real loss on the resample

The 204-to-159 line comparison is misleading and the answer to the question posed is neither of the
two offered. Of `Letterbox.cs`, roughly 150 lines are a bilinear resampler (`ComputeXTables`,
`BilinearRow`, `BilinearSequential`, `BilinearParallel`, `FillPadding`). We do not have those lines
because swscale does that job. The arithmetic that corresponds to our whole geometry file is fifteen
lines: `LetterboxMapping` (`Letterbox.cs:6-14`) and `Geometry` (`Letterbox.cs:65-72`).

**On the arithmetic they are equivalent to ours, including the fiddly part.** Theirs:

```csharp
float scale = Math.Min((float)dstSize / srcW, (float)dstSize / srcH);
int scaledW = (int)MathF.Round(srcW * scale);
...
return (scale, scaledW, scaledH, (dstSize - scaledW) / 2, (dstSize - scaledH) / 2);
```

Integer division truncates, and for a gap of `2k+1` that gives `k` — which is exactly what
Ultralytics' `round(dw - 0.1)` gives, and exactly what our `Place` computes as
`(int)Math.Round((InputSize - width) / 2.0 - 0.1)` (`DetectorGeometry.cs:123`). They resize to the
rounded `scaledW` and unmap with the unrounded `scale` (`Letterbox.cs:9-13`), the same asymmetry our
comment at `DetectorGeometry.cs:129-132` documents as Ultralytics' behaviour. **Neither of us handles
a case the other misses here.** The only divergence is `float` against our `double`, which is taste
until someone finds a frame size where a `.5` boundary lands differently.

**They also keep the forward and the inverse in one object, for the letterbox.** `Letterbox.Apply`
*returns* the `LetterboxMapping` it just resized by, so the inverse is minted by the forward and
cannot drift from it. That is the same principle as ours and, structurally, a slightly stronger
version of it: ours recomputes `Gain` and `Place` on the inverse path rather than carrying the
forward's own numbers forward. Nobody should be told we invented this idea; we did not.

**Where they genuinely handle something we do not is the resample kernel.** `Letterbox.cs:106` and
`:121`:

```csharp
float sx = (dx + 0.5f) * invScaleX - 0.5f;
float sy = (dy + 0.5f) * invScaleY - 0.5f;
```

Half-pixel centres in both axes, two taps, no antialiasing widening — the exact convention
`_resize.py` and `INTER_LINEAR` define and the one our `DetectorGeometry` doc comment cites as
required. We could not get all three from swscale and said so at `OnnxDetector.cs:96-97`:
`SWS_BILINEAR` is centred but antialiases on a downscale (dog 0.518), `SWS_FAST_BILINEAR` is two-tap
but corner-aligned horizontally (dog 0.659), against the reference's 0.687. **Their resampler is the
option we could not buy**, and the residual 0.028 on that one measurement is plausibly exactly this.
Plausibly, not certainly — it is one image, one class, and the accurate-rounding and full-chroma
flags were moving at the same time.

This is a real gap, honestly rated, and Section 4 explains why it is still ranked last.

### 1.4 Non-maximum suppression: correct, small, and still not ours to want

`Nms.cs` is 50 lines, class-aware, greedy, does not require sorted input, allocates one `int[]`, one
`bool[]` and a result list, and the inner loop terminates at the class-group boundary
(`Nms.cs:42`) rather than scanning all `n`. I read it looking for the off-by-one that usually lives
in hand-written NMS and did not find one. If we ever need suppression, this file is the one to copy
rather than write.

We have never needed it, and `OnnxDetector.cs:109-116` refuses `NeedsNms` with an exception that
names the reason and the phase. That is the right placeholder and it should stay. **The thing to
notice is why they need it and we do not**: `YoloV4Detector` decodes three raw anchor heads, which
is a family we would not choose to run. Their NMS exists to support a 2020 model, not to cover a
case in the 2026 models we both use. Nothing about our position is wrong here.

### 1.5 Session sharing: they solve a problem we do not have, and validate a design we might

`OrtSessionFactory` + `RefCountedCache` are 281 lines doing two things:

1. **Deduplicate sessions within a process**, keyed on
   `(Path.GetFullPath(modelPath).ToUpperInvariant(), forceCpu, dmlDeviceId)` (`OrtSessionFactory.cs:53`).
   Two detection nodes on the same model file share one `InferenceSession`.
2. **Keep up to two idle sessions warm** after their last user disposes (`RefCountedCache`,
   `maxIdle: 2`), so a stop → edit → run cycle in their editor skips model load and DML shader
   compilation.

Neither problem is ours. Our worker constructs exactly one `OnnxDetector` in `ExecuteAsync`
(`DetectionWorker.cs:85`) and holds it for the process lifetime; there is no second node, no second
model file, and no stop-and-restart cycle to make warm retention pay. Adopting 281 lines to
deduplicate a set of size one is the abstraction-with-one-implementation our plan explicitly costs.

**And our measurement already answers the question this was meant to answer.** `perf-detection.md`
experiment 1 found twelve sessions reach 2.65 frames/s for 1,984 MB where twelve callers on one
session reach 2.89 for 525 MB — *extra sessions buy nothing that extra callers do not*. A cache that
makes extra sessions cheaper is optimising the loser. The "two sessions in one process halve each
other" finding (D4, "Do not load both in one process") is about two **different models** sharing one
global thread pool, which their cache does not address at all: two different paths are two different
keys and two live sessions.

What is worth taking from those 281 lines is one comment, `OrtSessionFactory.cs:36-38`:

> Two nodes sharing one session is safe: `InferenceSession.Run` is thread-safe, and every detector
> keeps its own pre-bound input/output OrtValues.

That is precisely the upgrade our own `ponytail:` note at `OnnxDetector.cs:74-75` names — per-caller
buffers instead of one buffer behind `_gate` — described by someone who has shipped it. It is
corroboration for a path we have already priced at 12 % and declined (`perf-detection.md`, "Joint
verdict on 1 and 2"). Not a reason to change our mind; a reason to trust our note.

### 1.6 StageTimings: they are right that the code should measure, we are right about the split

`StageTimings.cs` is four lines. Each detector takes three `Stopwatch.GetTimestamp()` calls around
preprocess / run / postprocess and exposes `LastTimings`, and `DetectionNode.cs:65-77` averages them
over ten frames into the node's status line: `pre 2.1 · run 41.3 · post 0.8 ms`.

**They are right about the principle and we are behind on it.** Our detection path emits no timing
at all — `DetectionWorker.cs` and `StreamJob.cs` log claims, stand-downs and failures, and nothing
about how long a detection took. Every number we have is from a throwaway harness in a scratch
directory, on one laptop, on one afternoon. That harness cannot tell us that a CUDA pod fell back to
the processor and is now ten times slower, that a batch of eight is taking six seconds, or that a
machine is thermally throttled — all three of which `perf-detection.md` observed happening.

**They are wrong, for us, about the three-way split.** We measured that about 99 % of a detection is
the model call and the input path is a quarter of one percent (`perf-detection.md`, experiments 3 and
the summary). Carrying a `PreprocessMs` and a `PostprocessMs` through an interface to report numbers
we already know are noise is instrumentation for its own sake. The version that earns its place for
us is the total and the batch size.

---

## 2. What they support that we do not, and whether it is worth having

| | Theirs | Ours |
| --- | --- | --- |
| Model families | 4 (yolo26, yolo26-seg, rfdetr, yolov4) | 2 (RF-DETR, YOLO26) |
| Instance segmentation | yes | no |
| Anchor-decoded exports + NMS | yes | refused by design |
| Arbitrary third-party ONNX files | yes, sniffed | no, must match a descriptor |
| Export-layout variants per family | auto-detected | declared |
| Batching | **no — batch pinned to 1** | yes, 1..8 |
| Model download from a manifest | yes | no |

**Segmentation.** `Yolo26SegDetector` is the best-written file in their tree. `BuildBoxLocalMask`
(`Yolo26SegDetector.cs:142-176`) crops the prototype window to the box before doing any work, walks
one coefficient at a time so the strided prototype reads stay contiguous, and uses
`TensorPrimitives.MultiplyAdd` + `TensorPrimitives.Sigmoid` over the window. If we ever do masks,
copy this. Is it worth having now? No. ST 0903 does carry boundary geometry, but nothing in
`detection-plan.md` asks for it, and the cost is not the 50 lines of decode — it is a mask type
through `Misb0903`, a second inverse mapping (mask space is 160×160 prototype pixels, not canvas
pixels), and a renderer. Speculative.

**YOLOv4.** A strictly worse model than YOLO26 at greater cost — 245 MB against 9.8 MB, and it needs
NMS, anchor tables and `scale_x_y` constants (`YoloV4Detector.cs:30-37`). It exists in their tree
because the ONNX model zoo file is MIT-licensed and Ultralytics is AGPL, which is a licensing answer
to our D4 licensing problem and worth knowing about. It is not a capability we want.

**Does their factory make a fourth model cheap?** No — and the factory is not the mechanism either
way. `DetectorFactory.cs:35-42` is a six-line switch; adding YOLOv4 cost them one line there and
**176 lines of detector**, of which about 70 are session setup, buffer allocation, `RunInference`,
the four interface properties and `Dispose` copied from the other three files (`RfDetrDetector.cs:72-78`
is byte-identical to `Yolo26Detector.cs:72-78`, `Yolo26SegDetector.cs:72-78` and
`YoloV4Detector.cs:65-71`, comment included). Adding YOLO26 cost us a nine-line descriptor
(`DetectorDescriptor.cs:80-88`), a ~30-line `DecodeRows` and one enum case, and D4 records that the
geometry and the class tables were not touched.

So: **for a family that fits the descriptor, ours is several times cheaper. For a family that does
not — YOLOv4's anchors, or segmentation — the costs converge**, because we would need a third
`BoxFormat` and a decode method anyway, and theirs would still be paying the boilerplate tax a fifth
time. The switch statement is not what we would rather not have; the four copies of the same
constructor are. We should not take the factory: we have two static descriptors and one selection
point, and a switch over two values is the config-for-a-value-that-never-changes our plan warns
about.

**Layout sniffing is a real capability and actively wrong for us.** `Yolo26Detector.DetectFormat`
(`:85-95`) picks between the Ultralytics end-to-end export and the Transformers.js logits/pred_boxes
export from the output signature; `RfDetrDetector` (`:54-59`) picks between `logits`/`pred_boxes` and
`labels`/`dets`. That is what lets their operator paste a HuggingFace URL into a manifest. Our D4
note rejected exactly this — "Sniffing the shape instead was rejected as guessing at a contract this
project has never produced" — and the reason holds: we ship `scripts/export-rfdetr.py` and
`scripts/export-yolo.py`, so we always know what the file is. Keep the refusal.

---

## 3. Where we are right and they are not

### 3.1 They never read the model's own metadata. At all.

`grep -rn "ModelMetadata\|CustomMetadataMap"` across their `src/` returns nothing. And
`DetectorFactory.cs:37-40` never passes `classNames`, so every detector takes the
`classNames ?? CocoClassNames.Coco80` fallback (`Yolo26Detector.cs:61`, `RfDetrDetector.cs:51`,
`Yolo26SegDetector.cs:48`, `YoloV4Detector.cs:56`).

**Consequence: a custom-trained YOLO26 export loaded through their manifest — two classes, "vehicle"
and "person" — is labelled with COCO-80.** Class 0 becomes `person`, class 1 becomes `bicycle`. No
error, no warning, plausible output, wrong. Their own `tools/export/README.md:14-24` tells the user
to export their own model and add it to the manifest, so this is a path they invite people down.
`ClassNames` is a constructor parameter on every detector and the only thing that ever constructs
them does not set it.

We read `names`, `imgsz` and `end2end` out of `metadata_props` and let the file's own table win
(`OnnxDetector.cs:287-335`). The `imgsz` cross-check refuses a file whose own idea of its canvas
contradicts the descriptor, and the `end2end` check refuses a file whose head contradicts
`NeedsNms`. That last one is the check D4 did not ask for, and this comparison is the argument for
it: the alternative is their position, which is that the file is whatever you said it was.

### 3.2 The stretch family has no mapping object, and its inverse is copied three times

Section 1.3 credits them for `LetterboxMapping`. The stretch path has no equivalent.
`Letterbox.Stretch` returns `void` (`Letterbox.cs:49`), and the inverse is written inline in each
detector that uses it:

- `RfDetrDetector.cs:130-131`: `boxRow[0] * frame.Width, boxRow[1] * frame.Height, ...`
- `Yolo26Detector.cs:175-176`: the same four expressions again
- and the same `.Clamp(frame.Width, frame.Height)` and `if (box.Width <= 1 || box.Height <= 1)`
  guard in four places (`RfDetr:132`, `Yolo26:143` and `:177`, `Yolo26Seg:125`, `YoloV4:148`).

Three copies of the same inverse in three files, with the forward in a fourth. It happens to be a
trivial inverse, so it happens not to have drifted. **That is luck, not structure**, and it is the
failure mode `detection-plan.md` names as the one thing most likely to go wrong. Our
`DetectorGeometry.Stretch` puts `Forward` and `Inverse` four lines apart in the same record with the
source citation between them (`DetectorGeometry.cs:88-95`), and the clamping and the rounding happen
once for both families in `ToFrame`. We are right about this and it is worth saying plainly.

### 3.3 They cannot batch, and the shape of their code is why

`OrtSessionFactory.cs:125-126` pins the batch dimension:

```csharp
TryOverrideFreeDimension(options, "batch");
TryOverrideFreeDimension(options, "batch_size");
```

with the comment that this "lets ORT fully static-shape the graph". Every detector then allocates one
input buffer and one `OrtValue` at `[1, 3, S, S]` in its constructor (`Yolo26Detector.cs:53`), and
`IDetector.Detect` takes one `VideoFrame`. Batching is not merely absent; it is designed out at four
levels, and adding it would mean re-cutting every detector's buffers, its `OrtValue`, its interface
and its postprocess loop.

We measured batching at +13 % on a processor, plateauing at two, and D3's claim is that the curve
keeps going on a GPU where launch and synchronisation overhead amortise. `MaxBatch = 8` is already in
the worker. Their pin is the right call *for their shape* — a desktop node graph has one frame in
hand at a time and static shapes are worth more than a batch they will never form — but it is a
ceiling they have built into four files, and it is the single largest architectural difference
between the two codebases.

### 3.4 Preprocessing on the host, in float, in managed code

`ImagePreprocessor.BgrToChwFloat` runs per frame: 1.2 M byte-to-float widens, three multiplies each,
optionally a subtract and a multiply for ImageNet normalisation — plus the managed bilinear resize in
`Letterbox`. Their own comment at `ImagePreprocessor.cs:26-30` records that they tried to vectorise
it and could not: "a two-pass deinterleave + TensorPrimitives.ConvertChecked variant benchmarked
slower than this single pass (the byte→float widen doesn't vectorize)".

We have none of it. Normalisation is in the graph, the input is uint8 NHWC, swscale writes the
model's bytes directly into the pinned buffer (`OnnxDetector.cs:393-436`), and `Detect` allocates
2,880 managed bytes with gen0/1/2 at 0/0/0 over ten detections. D1 calls this "the decision that
matters most for speed", and it is also what keeps the D3 device-memory path reachable at all: you
cannot hand a GPU a tensor you build on the host in float.

How much does it cost them? Unknown from reading, and I will not invent a number. Structurally it is
a full resize plus a full float conversion per frame that we simply do not perform, and their
`Parallel.For` in `BilinearParallel` capped at `ProcessorCount / 2` (`Letterbox.cs:193`) suggests it
was large enough to be worth parallelising and large enough to need capping so it would not starve
the rest of the pipeline.

### 3.5 Their preprocessing knobs are unreachable from configuration

`RfDetrDetector`'s constructor takes `bool imagenetNormalize = false` (`:38`) and
`DetectorFactory.cs:39` never sets it. Their `tools/export/README.md:8` says their manifest's
RF-DETR export is "/255, stretch resize, no ImageNet norm", so for the model they ship, `false` is
correct.

But `DetectionConfig.ModelPath` lets an operator point at any local `.onnx`
(`NodeConfig.cs:117-118`), and an official `rfdetr` package export — which their own README tells
people to produce, at line 23 — is a different preprocessing contract. If it wants ImageNet
normalisation, there is no way to ask for it and no error: the boxes come back slightly wrong and
the scores come back slightly low, forever. This is `detection-plan.md`'s "Normalisation … degrades
accuracy quietly rather than failing" hazard, sitting in a `default` parameter value. It is a latent
trap rather than a live bug, and I am flagging it as such.

### 3.6 Where our strictness is a real cost, and we should keep paying it

`VerifyContract` (`OnnxDetector.cs:345-375`) refuses anything that is not uint8 NHWC at the
descriptor's canvas size. That means **we cannot run a third-party ONNX file at all** — not a
HuggingFace RF-DETR, not a model zoo YOLOv4, not a float NCHW anything. Theirs runs all of them.

That is a genuine capability we do not have, and it is a consequence of a choice (normalisation in
the graph, uint8 input) that buys us the speed and the GPU path. It is only tolerable because we
control the exports. If that ever stops being true — a customer hands us a model — this is the wall
we hit, and it will be a real piece of work, not a flag.

---

## 4. What is worth stealing, ranked, smallest first

### 1. Warm up the session in the constructor — ~5 lines

`EnsureCapacity(1)`, zero the canvas, one `Run`. It is `using var _ = RunInference();` for them
because they already hold a `[1,3,S,S]` input; for us it is a couple of lines more because we size
the input buffer per call. Do it inside the constructor so the cost lands at startup where it is
visible, not on the first stream's first frame. Matters most on the CUDA path that D3 is heading
for. Cost: five lines and one Run at startup. Take it.

### 2. Put the output shapes in the contract-mismatch errors — 1 line

`OnnxDetector.cs:349` and `:356` list names; add dimensions the way `Yolo26Detector.cs:92-94` does.
When a descriptor and a file disagree, the shape is what tells you which file you have. Cost: one
line each. Take it.

### 3. Time the detection in the code, total only — ~6 lines

Two `Stopwatch.GetTimestamp()` calls in `Detect`, a `LastDetectionMs` (or a log line in
`DetectionWorker.DetectAsync` with the batch size, which needs no interface change at all). We have
zero production visibility into the one number that governs the whole worker, and the harness that
produced every figure in `perf-detection.md` does not exist any more. Deliberately **not** their
three-way split: we measured the split and 99 % of it is one term. Cost: six lines, no interface
change if it goes in the worker. Take the reduced version.

### 4. Keep `Nms.cs` on file; do not port it — 0 lines now, ~50 when needed

It is correct, it is class-aware, it does not need sorted input, and it terminates on the group
boundary. Our `NotSupportedException` at `OnnxDetector.cs:109-116` is the right state of affairs
until a descriptor needs suppression, and that descriptor would also need a third `BoxFormat` for
the transposed `(batch, 84, anchors)` layout — which our exception already says. The steal is
knowing where the 50 lines are when the day comes. Cost now: nothing.

### 5. Consider the resample kernel — measure first, port only if the measurement says so

Their `BilinearRow` is centred *and* two-tap; our `SWS_FAST_BILINEAR` is two-tap and corner-aligned,
and our one measurement has us at dog 0.659 against the reference's 0.687. This is the only place
where their letterbox genuinely handles something ours does not.

It is ranked last anyway, for three reasons. Our measurement says the input path is 0.25 % of a
detection, so this is an accuracy question and not a speed one, and the accuracy evidence is a single
class on a single image. Porting 150 lines would cost us more than it costs them: their frames are
already BGR24, ours are arbitrary `AVFrame` pixel formats, so we would need a swscale pass to RGB24
at source resolution *and then* a managed resample — two passes where we now have one — or our own
YUV conversion. And the cheap experiment has not been run: find out whether swscale can be asked for
a centred two-tap kernel (the scaler's filter parameters, or a flag combination we did not try), and
re-measure the dog. That is an afternoon against a week, and it might close the whole gap. Cost:
one experiment. Only then, maybe, 150 lines.

### Explicitly do not take

- **`RefCountedCache` + `OrtSessionFactory` (281 lines).** Deduplicates sessions we do not have and
  warms idle sessions for a restart cycle we do not have. Our own experiment 1 says extra sessions
  are the losing shape regardless. Keep the one comment at `OrtSessionFactory.cs:36-38` as
  corroboration for the `ponytail:` note at `OnnxDetector.cs:74-75`, and nothing else.
- **`TryCreatePreBoundOutputs`.** It returns `null` for any dynamic dimension
  (`OrtSessionFactory.cs:97-99`), so it is structurally incompatible with our dynamic batch. It
  would save part of 2,880 bytes per `Detect` on a path with gen0 = 0 and 99 % of the time in the
  model call, in exchange for giving up the +13 % batching. Strictly negative.
- **`DetectorFactory`.** A switch over the two values we have is exactly the configuration our plan
  says not to write. The worker already picks its descriptor at `DetectionWorker.cs:85`.
- **Per-family detector classes.** They are what makes YOLOv4 possible for them and what makes 70
  lines of constructor exist four times. Our descriptor held through D4; the moment it stops
  holding, revisit, and not before.
- **Layout sniffing.** D4 already decided this and the reason still holds: we produce the exports.
- **Segmentation and YOLOv4.** Speculative and worse-than-what-we-run respectively. If segmentation
  is ever asked for, `Yolo26SegDetector.BuildBoxLocalMask` is the reference implementation and the
  cost is the ST 0903 carriage, not the decode.

---

## The one-sentence version

Their code is more general and reads more files off the internet; ours is faster, batches, and
refuses a model that lies about itself — the only things worth taking are a warm-up call, a shape in
an error message, a timer, and an experiment about a resample kernel.
