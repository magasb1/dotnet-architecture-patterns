# RF-DETR and YOLO from ONNX — what a .NET service actually has to do

Researched 2026-09-12. Primary sources: `roboflow/rf-detr` @ `develop` (last push 2026-09-11), `ultralytics/ultralytics` @ `main`, and the two doc sites. Line references are to files as fetched today.

**Recently changed, flagged up front:**
- ONNX Model Zoo (`onnx/models`) is **deprecated** — "preserving the ONNX Model Zoo repository for historical purposes only… models will no longer be available for LFS download starting July 1st, 2025". It has no RF-DETR and only YOLOv2–v4 era YOLO. It is not a usable source for either family. <https://github.com/onnx/models>
- **YOLO26 released January 2026** and is Ultralytics' current flagship; YOLO27 is "in final development with no confirmed launch date". <https://docs.ultralytics.com/models/> · <https://docs.ultralytics.com/models/yolo26/>
- RF-DETR moved its package to `src/rfdetr/` and its default branch is `develop`. Export I/O names below are from that tree, not from older blog posts.

---

## 1. RF-DETR

### Variants (README table, <https://github.com/roboflow/rf-detr>)

| Class | Input res | Licence |
|---|---|---|
| `RFDETRNano` | 384×384 | Apache 2.0 |
| `RFDETRSmall` | 512×512 | Apache 2.0 |
| `RFDETRMedium` | 576×576 | Apache 2.0 |
| `RFDETRBase` | 560×560 (`config.py` `resolution: int = 560`) | Apache 2.0 |
| `RFDETRLarge` | 704×704 | Apache 2.0 |
| `RFDETRXLarge` | 700×700 | **PML 1.0** |
| `RFDETR2XLarge` | 880×880 | **PML 1.0** |

Seg variants (`RFDETRSegNano` 312 … `RFDETRSeg2XLarge` 768) and `RFDETRKeypointPreview` (576) also exist, all Apache 2.0. Resolutions confirmed in `src/rfdetr/config.py`.

`num_queries` / `num_select`: **300** for all detection variants (`config.py` defaults `num_queries: int = 300`, `num_select: int = 300`; `RFDETRLarge` re-states 300 explicitly). Seg variants differ (Nano/Small 100, Medium/Large 200) — <https://github.com/roboflow/rf-detr/blob/develop/src/rfdetr/config.py>

### Export

```python
from rfdetr import RFDETRMedium
model = RFDETRMedium(pretrain_weights="<path/to/checkpoint.pth>")
model.export()          # -> output/inference_model.onnx
```
Default `opset_version=17`, `output_dir="output"`, `format="onnx"`. <https://rfdetr.roboflow.com/latest/learn/export/>

Export bakes in **neither preprocessing nor postprocessing**. `src/rfdetr/export/_onnx/exporter.py` calls `torch.onnx.export` with `do_constant_folding=True` and nothing else; the only metadata written is an optional user string under the key `rfdetr_notes`. **Class names are not embedded in the ONNX file.**

### Input

- name **`"input"`** (`src/rfdetr/export/main.py:253` `input_names = ["input"]`)
- shape `(batch, 3, H, W)`, **NCHW**, float32
- `dynamic_axes = {name: {0: "batch"} for name in input_names + output_names}` when dynamic export is requested — **only the batch axis is dynamic; H and W are fixed at export**. (`main.py:261`)
- pixel range: RGB scaled to **0..1 first**, then ImageNet-normalised. `predict()` raises `"Image has pixel values above 1. Please ensure the image is normalized (scaled to [0, 1])."` for tensor input. (`src/rfdetr/detr.py`)
- `mean = [0.485, 0.456, 0.406]`, `std = [0.229, 0.224, 0.225]` (`detr.py:470-471`), applied as `F.normalize(batch, means, stds)` i.e. `x = (x/255 - mean) / std`.

### Output

`output_names = ["dets", "labels"]` for detection; `["dets", "labels", "masks"]` for segmentation (`main.py:255-259`).

| Name | Shape | Content |
|---|---|---|
| `dets` | `(B, 300, 4)` | boxes, **cxcywh**, **normalised 0..1** |
| `labels` | `(B, 300, num_classes+1)` | **raw logits, un-activated** |

`num_classes + 1` slots: `src/rfdetr/models/lwdetr.py:785` `num_classes = args.num_classes + 1`. COCO checkpoints use `args.num_classes = 90`, so `labels` is `(B, 300, 91)`.

**Match outputs by name, not position** — the docs warn that `num_classes + 1` can equal 4, making the two outputs indistinguishable by trailing dim. <https://rfdetr.roboflow.com/latest/learn/export/>

### Decode (from `src/rfdetr/models/postprocess.py`, `PostProcess`)

```
prob      = sigmoid(labels)                         # per-class, multi-label — NOT softmax
flat      = prob.reshape(B, 300*C)
topk      = argsort(flat, desc)[:, :num_select]     # num_select = 300
score     = flat[topk]
query_idx = topk // C
class_id  = topk %  C
box       = cxcywh_to_xyxy(dets[query_idx])         # still 0..1
box      *= [img_w, img_h, img_w, img_h]            # target_sizes = ORIGINAL image size
```
`_select_topk` uses `torch.argsort(..., stable=True)` then slices, not `topk`. Same queries may appear multiple times with different classes — that is intended (multi-label).

**NMS is not needed.** Nothing in the RF-DETR inference path applies NMS; the one-to-one Hungarian matching at training time is what removes duplicates. The docs list NMS as optional ("Implement NMS if needed").

### Class index → label — this is the sharp edge

For **pretrained COCO checkpoints** the logit slot index **is the COCO category id (1..90, sparse)**, not a 0-based index into an 80-name list. `detr.py`:

> "raw COCO category IDs (1–90, sparse) are looked up by category ID rather than by position — so `class_id=18` yields `"dog"`, not `class_names[18]`."

Slot 0 is the unused/background slot. `src/rfdetr/assets/coco_classes.py` holds `COCO_CLASSES = {1: "person", 2: "bicycle", 3: "car", …}` with the gaps (no 12, no 26, no 29/30, …) — 80 entries over ids 1..90.

For **fine-tuned** checkpoints, `class_id` is a plain 0-based index into `class_names`. The library distinguishes them by `args.num_classes > len(class_names)`. A .NET consumer has no `args` — **the label layout must be supplied as config alongside the .onnx file.** `src/rfdetr/export/_class_layout.py` exposes `background_class_id` as an explicit, caller-supplied parameter for exactly this reason.

### Licence

- `LICENSE` in the repo is **Apache License, Version 2.0**.
- README: "The open-source `rfdetr` package and Apache-designated model weights are licensed under Apache License 2.0." — code **and** those weights, both Apache-2.0, freely usable commercially.
- "Plus components, including the `rfdetr_plus` extension and RF-DETR-XL / RF-DETR-2XL detection models, are licensed under **PML 1.0**" (Platform Model License 1.0) — conditioned on holding a Roboflow platform plan in good standing. <https://github.com/roboflow/rf-detr_plus/blob/main/LICENSE>

---

## 2. YOLO — covering **YOLO26** (current flagship) and **YOLO11** (stable alternative)

Both are what <https://docs.ultralytics.com/models/> names as current: YOLO26 "Released January 2026", YOLO11 the "stable production alternative". Notes on v8/v5 where a consumer would trip.

### Export

```bash
yolo export model=yolo26n.pt format=onnx          # or yolo11n.pt
```
```python
YOLO("yolo26n.pt").export(format="onnx", nms=None, dynamic=False, imgsz=640)
```
<https://docs.ultralytics.com/modes/export/>

**Opset**: not fixed. `self.args.opset or best_onnx_opset(onnx)`; `best_onnx_opset` maps torch 1.13–2.3 → 17 and **torch ≥ 2.4 → 18**, then clamps to the installed onnx's max. Deliberately capped below 19 because "ONNX Runtime CUDA has no Resize-19 or ReduceMax-20 kernel". (`ultralytics/utils/export/engine.py:51-68`) — **pin `opset=` explicitly if you want reproducibility.**

### Input

- name **`"images"`** — `torch2onnx(... input_names: list[str] | None = None)`, "Defaults to `["images"]`" (`utils/export/engine.py:95-96`)
- shape `(batch, 3, imgsz, imgsz)`, NCHW, float32
- **range 0..1, no mean/std at all.** `predictor.preprocess`: BGR→RGB via `im.flip(1)`, then `.float().div_(255)`. Nothing else. (`ultralytics/engine/predictor.py`)
- `dynamic=True` makes batch/height/width dynamic; default is fully static.

### Output — three distinct contracts selected by the `nms` export arg

Docs: "None exports raw one-to-many predictions for external NMS; True embeds NMS where supported; False selects the NMS-free head when available." <https://docs.ultralytics.com/modes/export/>

Output names are always `["output0"]`, plus `"output1"` for segmentation (`engine/exporter.py:1086`).

**(a) `nms=None` (default) — raw one-to-many.** `output0` = `(B, 4+nc, N)`; for 640 and COCO that is **`(1, 84, 8400)`**, 8400 = 80²+40²+20². From `Detect._inference`: `torch.cat((dbox, x["scores"].sigmoid()), 1)`.
- boxes **xywh (cx,cy,w,h)**, in **model-input pixels** (not normalised) — `decode_bboxes` → `dist2bbox(..., xywh=True)` then `* self.strides`.
- scores are **sigmoid class probabilities only. No objectness term.** Confidence = `max(scores)`, do **not** multiply by anything.
- **NMS required.**

**(b) `nms=True` — NMS embedded in the graph.** `NMSModel` wraps the model and emits `(B, max_det, 6)` = `[x1, y1, x2, y2, score, class_id]`, zero-padded to `max_det` (default 300). Boxes become **xyxy** because the exporter sets `m.xyxy = self.args.nms and (fmt != "coreml" or model.task != "detect")` (`exporter.py:907`). `conf` and `iou` thresholds are **baked in at export time** from `args.conf`/`args.iou`. **No NMS needed at runtime**; you must drop the all-zero padding rows.

**(c) `nms=False` — NMS-free end-to-end head (YOLO26 only).** Docs: "One-to-One Head (NMS-free): Produces end-to-end predictions without NMS, outputting **(N, 300, 6)** with a maximum of 300 detections per image." <https://docs.ultralytics.com/models/yolo26/>. Same `[x1,y1,x2,y2,score,class_id]` layout via `Detect.postprocess`, which documents its output as "`(batch_size, min(max_det, num_anchors), 6 + extra)` and last dimension format `[x1, y1, x2, y2, max_class_prob, class_index, extra]`". `nms=True` is rejected for such models: "'nms=True' is not available for end2end models. Forcing 'nms=None'." (`exporter.py:792-793`)

YOLO11 has no one-to-one head, so `nms=False` falls back — hence the docs' hedge "*where supported*" / "*when available*".

### **Embedded metadata — YOLO ONNX files self-describe**

`exporter.py:940-963` writes `metadata_props` including `names` (the full class-id→name dict), `imgsz`, `stride`, `task`, `batch`, `channels`, **`end2end`**, and `license: "AGPL-3.0 License (https://ultralytics.com/license)"`. A .NET service can read `end2end` and `names` straight off the ONNX file and configure itself. **RF-DETR does not do this.**

### Version differences that bite a consumer

- **YOLOv5** (`ultralytics/yolov5`, `models/yolo.py`): `self.no = nc + 5` — there **is** an objectness channel, confidence = `obj * cls`, and the tensor is **transposed** relative to v8+: `(B, 25200, 85)` for 640. Anchor-based.
- **YOLOv8 / v9 / v10 / v11 / v12**: anchor-free, `(B, 4+nc, N)`, no objectness. v10 has a one-to-one head (NMS-free) predating YOLO26.
- **YOLO26**: same raw layout as v11 plus the optional one-to-one head.

### Licence

- Code and models: "**AGPL-3.0** and Enterprise licenses" (<https://docs.ultralytics.com/models/yolo26/>); the exporter stamps `AGPL-3.0 License` into the ONNX metadata of every file it produces.
- Ultralytics' own statement of what AGPL-3.0 requires: "compliance means publicly releasing the complete corresponding source code for the entire derivative work, including the larger application, modifications, scripts, configuration files". <https://www.ultralytics.com/license>
- Same page lists the Enterprise licence as required for "Commercial products or services", "Proprietary/closed-source software", "SaaS platforms, APIs, or cloud systems using YOLO", and "Custom-trained models in proprietary settings".

### Weights / label set

COCO, 80 classes, contiguous **0-based** ids (`0: person, 1: bicycle, 2: car, …`) — the `names` dict in the ONNX metadata. Note this is a *different* numbering from RF-DETR's sparse 1..90.

---

## 3. Preprocessing and the inverse mapping

### RF-DETR — **stretch, no padding**

`detr.py:2724-2728`:
```python
resize_to = list(shape) if shape is not None else [self.model.resolution, self.model.resolution]
batch_tensor = torch.stack([F.resize(t, resize_to, antialias=False) for t in processed_images])
batch_tensor = F.normalize(batch_tensor, self.means, self.stds)
```
`F.resize` with a 2-element size **forces both dimensions** — aspect ratio is **not** preserved, there is **no letterbox and no pad colour**. Confirmed by the inverse: `PostProcess._gather_and_scale_boxes` scales normalised boxes by `[img_w, img_h, img_w, img_h]` taken from the *original* image size with no pad subtraction.

Interpolation is **bilinear, `antialias=False`, half-pixel centres** — the repo ships `src/rfdetr/export/_resize.py` purely to reproduce it torch-free, noting that `PIL.Image.resize(BILINEAR)` "applies an adaptive antialias filter when downscaling and a corner-aligned half-pixel convention, both of which diverge from `F.interpolate`". **Use OpenCV `INTER_LINEAR` (which matches), not a PIL-style resize**, or detections shift subtly on downscale.

**Inverse — RF-DETR**, given `(cx, cy, w, h)` normalised and original `(W0, H0)`:
```
x1 = (cx - w/2) * W0
y1 = (cy - h/2) * H0
x2 = (cx + w/2) * W0
y2 = (cy + h/2) * H0
```
That is all. No gain, no pad. Non-uniform scaling in x and y is correct here, because the forward pass stretched.

### YOLO — **letterbox, padding 114, long side**

`ultralytics/data/augment.py`, `class LetterBox`, defaults `auto=False`, `scaleup=True`, `center=True`, `stride=32`, **`padding_value: int = 114`**.
```python
r  = min(new_shape[0] / shape[0], new_shape[1] / shape[1])   # long side governs; uniform
new_unpad = round(shape[1]*r), round(shape[0]*r)
dw, dh = new_shape[1]-new_unpad[0], new_shape[0]-new_unpad[1]
dw /= 2; dh /= 2                                             # center=True
top, bottom = round(dh-0.1), round(dh+0.1)
left, right = round(dw-0.1), round(dw+0.1)
```
`auto` (pad only to the next stride multiple rather than to full `imgsz`) is off for a static ONNX export: `predictor.pre_transform` sets `auto = same_shapes and args.rect and (format == "pt" or (dynamic and format != "imx"))` — false for a static ONNX backend, so **you always pad to the full square `imgsz`**. Resize is `cv2.resize(..., INTER_LINEAR)`, then BGR→RGB, then `/255`.

**Inverse — YOLO** (`ultralytics/utils/ops.py`, `scale_boxes`), model shape `(h1,w1)`, original `(h0,w0)`:
```
gain  = min(h1/h0, w1/w0)
pad_x = round((w1 - round(w0*gain)) / 2 - 0.1)
pad_y = round((h1 - round(h0*gain)) / 2 - 0.1)

x1 = (x1_m - pad_x) / gain
y1 = (y1_m - pad_y) / gain
x2 = (x2_m - pad_x) / gain
y2 = (y2_m - pad_y) / gain
then clip to [0,w0] x [0,h0]
```
The `- 0.1` before `round` is a deliberate round-half-down; reproduce it or you get a one-pixel drift on odd padding. If you did the letterbox yourself, carry your own `(gain, pad_x, pad_y)` through instead of re-deriving — `scale_boxes` accepts exactly that as `ratio_pad`.

For raw `nms=None` output, convert xywh→xyxy in **model space first**, then apply the above.

---

## 4. What one abstraction has to absorb

**Common — belongs behind the interface:**
- Decode → `(x1,y1,x2,y2 in original-frame pixels, score, classId, label)`. Both families can reach this.
- Load an ONNX, bind one image input, run, read named outputs. Both are NCHW float32 RGB, batch-major.
- Score thresholding and top-k.
- Sigmoid on class scores — both families use per-class sigmoid, never softmax, never an objectness product (v5 excepted).

**Differs — the interface must carry these as per-model data, not code branches:**

| | RF-DETR | YOLO (26/11) |
|---|---|---|
| Input tensor name | `input` | `images` |
| Output names | `dets`, `labels` (**bind by name**) | `output0` |
| Geometry | **stretch to square** | **letterbox, pad 114, centred** |
| Normalisation | ImageNet mean/std after /255 | **/255 only** |
| Box format | cxcywh **normalised 0..1** | xywh **model pixels** (raw) or xyxy (nms/e2e) |
| Score state | **raw logits, sigmoid yourself** | already sigmoid'd |
| Fixed count | 300 queries, always | 8400 anchors (raw) or 300 (e2e/nms) |
| NMS | never | **required for `nms=None`**, not otherwise |
| Class ids | COCO **1..90 sparse**, slot 0 background | COCO **0..79 contiguous** |
| Labels in file | **no** | **yes**, `names` in `metadata_props` |
| Opset | 17 (fixed default) | torch-dependent, 17 or 18 |

**Opinion on where the seam goes.** One interface: `Detect(frame) -> Detection[]` in original-frame pixels. Behind it, exactly **two** things are genuinely per-model and must be declared, not sniffed:

1. **A geometry strategy** — `Stretch` or `Letterbox(padValue)`. It produces the input tensor *and* returns the inverse transform. Boxes go wrong precisely when the forward and inverse are written in different places, so they must be one object. Two implementations, and that is the whole list; do not invent a third for a hypothetical model.
2. **A decode descriptor** — input name, output names, box format (`cxcywh_norm` | `xywh_px` | `xyxy_px`), whether scores need sigmoid, whether NMS runs, and the class-id→label map with its offset. Data, not subclasses.

Do **not** abstract: opset, num_queries/num_anchors (read the output shape), or the class-id scheme. The COCO 1..90-vs-0..79 split cannot be hidden — a shared "COCO class 18" means dog in one family and dog in neither in the other (YOLO 18 = `sheep`). Label mapping must be an explicit per-model table shipped with the weights, and for RF-DETR it must be supplied externally because the ONNX file does not carry it.

Also do not pretend confidence is comparable across families: RF-DETR's score is a per-query-per-class sigmoid over 300 Hungarian-matched queries; YOLO's is a per-anchor sigmoid post-NMS. Same range, different distributions — threshold per model.

---

## 5. Licensing, plainly (facts and sources only, not legal advice)

**RF-DETR**
- Repo `LICENSE` = **Apache License, Version 2.0**. Package and Apache-designated weights (Nano/Small/Medium/Base/Large, all seg, keypoint) both Apache-2.0 — permissive, commercial use allowed, no copyleft on your service. <https://github.com/roboflow/rf-detr/blob/develop/LICENSE>
- XL / 2XL detection weights and `rfdetr_plus` = **PML 1.0** (Platform Model License 1.0): usable only while you hold and comply with a Roboflow platform plan/agreement in good standing. <https://github.com/roboflow/rf-detr_plus/blob/main/LICENSE>
- Running an exported .onnx and linking the Python package are under the same permissive terms — no distinction to make.

**Ultralytics YOLO**
- **AGPL-3.0** or a paid **Ultralytics Enterprise License**. <https://www.ultralytics.com/license>
- Linking/embedding the `ultralytics` Python package in a closed service: Ultralytics states AGPL-3.0 compliance "means publicly releasing the complete corresponding source code for the entire derivative work, including the larger application". AGPL-3.0 §13 extends this to network-accessed users, so a hosted service is in scope.
- Merely running an exported ONNX in .NET with ONNX Runtime: **Ultralytics' stated position is that this still requires the Enterprise licence** — their licensing page lists "SaaS platforms, APIs, or cloud systems using YOLO", "Proprietary/closed-source software" and "Custom-trained models in proprietary settings" under Enterprise, and the exporter stamps `"license": "AGPL-3.0 License"` into every ONNX it writes. Whether an exported weights file is a "derivative work" of AGPL code is contested in general; **I did not find an Ultralytics source that says running exported weights is exempt**, and the vendor's published position is that it is not. Take counsel; do not infer permission from this document.
- Practical consequence for this project: **RF-DETR (Apache-2.0 variants) is the licence-clean default for a closed service. YOLO needs a commercial licence or an explicit legal decision.**

---

## 6. Class labels and ontology (MISB ST 0903 relevance)

- **RF-DETR**: COCO 80 names keyed by COCO category id 1..90 sparse — `src/rfdetr/assets/coco_classes.py`. Fine-tuned models use 0-based contiguous ids into a user-supplied `class_names`.
- **YOLO26 / YOLO11**: COCO 80 names, ids 0..79, available at runtime from ONNX `metadata_props["names"]`.

**There is no canonical URI or identifier scheme for COCO classes from either project.** COCO ships integer `category_id` plus `name` and `supercategory` in its annotation JSON and nothing more; neither Roboflow nor Ultralytics publishes an ontology IRI, and I found no such thing in either repo. For MISB ST 0903's VMTI ontology field (class name + ontology URI) the mapping to an external ontology — Wikidata, WordNet, or a project-local IRI namespace — **is yours to author and maintain**; treat it as a third, service-owned table keyed by `(modelId, classId)`, which also absorbs the 1..90-vs-0..79 divergence in one place rather than two.

*Unverified:* I did not check ST 0903 itself (paywalled/controlled distribution), only that neither detector supplies a URI to put in that field.
