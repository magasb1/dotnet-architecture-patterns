# models/

Not committed. `scripts/fetch-rfdetr.sh` produces the default model and the shared `.venv/`;
`scripts/fetch-yolo.sh` adds the second one. They are separate scripts because the licences differ
— see the YOLO section.

## rf-detr-nano.onnx

RF-DETR Nano, COCO weights, Apache-2.0 (code and weights). Exported by `scripts/export-rfdetr.py`
with `rfdetr 1.10.1`, `torch 2.14.0+cpu`, `onnx 1.22.0`, opset 17, verified with `onnxruntime 1.30.0`.

Weights: `https://storage.googleapis.com/rfdetr/nano_coco/checkpoint_best_regular.pth`,
MD5 `fb6504cce7fbdc783f7a46991f07639f` (rfdetr's own asset table; the download is checked against it).

### Tensor contract, verified by running the file

| Tensor | Direction | dtype | Shape | Content |
| --- | --- | --- | --- | --- |
| `input` | in | uint8 | `(batch, 384, 384, 3)` **NHWC**, RGB | raw bytes, 0..255. Frame stretched to 384x384 (no letterbox); bilinear, antialias off |
| `dets` | out | float32 | `(batch, 300, 4)` | boxes `cx, cy, w, h`, normalised 0..1 of the *original* frame |
| `labels` | out | float32 | `(batch, 300, 91)` | raw logits; apply **sigmoid** per element, never softmax |

Batch axis is dynamic: batch 1 gives `(1,300,4)`/`(1,300,91)`, batch 2 gives `(2,300,4)`/`(2,300,91)`.
Bind by name, not position.

Preprocessing is in the graph: the first four nodes are `Cast(float) -> Transpose(NCHW) ->
Sub(255*mean) -> Div(255*std)` with ImageNet mean `[0.485, 0.456, 0.406]`, std
`[0.229, 0.224, 0.225]`. Against the stock float export fed the reference normalisation the outputs
agree to `max |diff| ~1e-4`. NHWC was chosen because a decoded RGB24 frame is already `H x W x 3`
interleaved, so the caller copies the buffer as-is.

Decode (from rfdetr `PostProcess`): `prob = sigmoid(labels)`, flatten to `300*91`, take the top
`num_select` (300) by score; `query = i // 91`, `class = i % 91`; `x1 = (cx - w/2) * W0`,
`y1 = (cy - h/2) * H0`, etc. No NMS.

Class ids are the **sparse COCO category ids 1..90, slot 0 = background** (never emitted with a
high score). The gaps are 12, 26, 29, 30, 45, 66, 68, 69, 71, 83:

```
1:person 2:bicycle 3:car 4:motorcycle 5:airplane 6:bus 7:train 8:truck 9:boat 10:traffic light
11:fire hydrant 13:stop sign 14:parking meter 15:bench 16:bird 17:cat 18:dog 19:horse 20:sheep
21:cow 22:elephant 23:bear 24:zebra 25:giraffe 27:backpack 28:umbrella 31:handbag 32:tie
33:suitcase 34:frisbee 35:skis 36:snowboard 37:sports ball 38:kite 39:baseball bat
40:baseball glove 41:skateboard 42:surfboard 43:tennis racket 44:bottle 46:wine glass 47:cup
48:fork 49:knife 50:spoon 51:bowl 52:banana 53:apple 54:sandwich 55:orange 56:broccoli 57:carrot
58:hot dog 59:pizza 60:donut 61:cake 62:chair 63:couch 64:potted plant 65:bed 67:dining table
70:toilet 72:tv 73:laptop 74:mouse 75:remote 76:keyboard 77:cell phone 78:microwave 79:oven
80:toaster 81:sink 82:refrigerator 84:book 85:clock 86:vase 87:scissors 88:teddy bear
89:hair drier 90:toothbrush
```

### Expected result on the sample image

`dog-2.jpeg` (720x1280, the rfdetr README example, fetched by the script from
`https://media.roboflow.com/notebooks/examples/dog-2.jpeg`), resized with the reference
`torchvision resize(antialias=False)`, CPU execution provider. Top ten after sigmoid, boxes in
original pixels `x1 y1 x2 y2`:

```
0.919 47 cup           474.4  887.4  642.6 1132.6
0.911 62 chair         528.3  674.5  719.5  980.5
0.892 62 chair           0.0  672.2   90.4  918.7
0.859 28 umbrella       32.2    1.0  720.0  314.2
0.845  1 person         14.1  347.3  496.6  922.3
0.841  1 person         16.2  492.3  175.7  695.2
0.799 67 dining table    2.0  893.7  718.6 1275.2
0.715 47 cup           281.5  891.9  385.8 1076.6
0.715 47 cup           315.7  845.8  442.1 1022.8
0.686 18 dog           158.2  493.1  459.1  850.0
```

Checked by eye: the dog box sits on the beagle, the top cup on the right-hand glass, the umbrella
across the top. A .NET test should assert `dog` (18) > 0.5 with a box within a few pixels of
`(158, 493, 459, 850)`, and a `person` (1) > 0.8; a different resize (OpenCV `INTER_LINEAR` is the
match, PIL is not) shifts scores by a few hundredths and boxes by a pixel or two, so do not assert
A blank (all-zero) frame is not silent: with ONNX Runtime 1.30 it yields `potted plant` at about 0.115 over the whole frame. Assert nothing above 0.2 rather than nothing at all; the .NET runner reproduces exactly this.

## yolo26-nano.onnx

YOLO26 Nano (`yolo26n.pt`, COCO weights, 2.4 M parameters, 5.5 GFLOPs), 9.8 MB. Exported by
`scripts/export-yolo.py` with `ultralytics 8.4.150`, `torch 2.14.0+cpu`, `onnx 1.22.0`, opset 18
(pinned — ultralytics otherwise picks it from the installed torch), verified with
`onnxruntime 1.30.0`. Weights come from ultralytics' own assets release,
`https://github.com/ultralytics/assets/releases/download/v8.4.0/yolo26n.pt`.

This is the second detector behind the same interface, and it exists to prove the abstraction is
honest. Read it next to the RF-DETR section above: the input convention is deliberately identical,
everything after it differs.

### Licence — state it before using it

**Ultralytics is AGPL-3.0 or a paid Ultralytics Enterprise licence.** The exporter stamps
`license = "AGPL-3.0 License (https://ultralytics.com/license)"` into this file's own
`metadata_props`, so the file says so itself. Ultralytics' licensing page lists
"Commercial products or services", "Proprietary/closed-source software", "SaaS platforms, APIs, or
cloud systems using YOLO" and "Custom-trained models in proprietary settings" as requiring the
Enterprise licence, and states that AGPL-3.0 compliance "means publicly releasing the complete
corresponding source code for the entire derivative work, including the larger application".
**No primary source was found exempting a service that merely runs an exported ONNX file**;
whether exported weights are a derivative work of AGPL code is contested in general, and the
vendor's published position is that this use is not exempt.

RF-DETR Nano is Apache-2.0 for both code and weights, with no copyleft reach into the service,
which is why it leads and why `fetch-rfdetr.sh` is the script that runs by default.

Exporting this file and testing against it locally is fine. **Shipping YOLO in a delivered product
is a decision the owner has to take deliberately**, with counsel — the above is the vendor's stated
position and the contents of the file, not legal advice.

### Tensor contract, verified by running the file

| Tensor | Direction | dtype | Shape | Content |
| --- | --- | --- | --- | --- |
| `images` | in | uint8 | `(batch, 640, 640, 3)` **NHWC**, RGB | raw bytes, 0..255. Frame **letterboxed** to 640x640 — uniform scale, pad 114, centred |
| `output0` | out | float32 | `(batch, 300, 6)` | `x1, y1, x2, y2, score, classId` — **xyxy in 640x640 model pixels**, rows sorted by score descending |

Batch axis is dynamic and was checked by running it: batch 1 gives `(1,300,6)`, batch 2 gives
`(2,300,6)`. Bind by name, not position. Note the input tensor is named `images`, not `input`.

**NMS is already in the graph and there is nothing to suppress.** The export used `nms=False`,
which selects YOLO26's NMS-free **one-to-one head**; `metadata_props["end2end"] == "True"` is how
the runner confirms that rather than being told. The 300 rows are the head's own top-k, not
zero-padded NMS output, so the only filter needed is a score threshold. **Scores are already
probabilities in 0..1** (sigmoid, no objectness term — do not multiply by anything, do not apply
another sigmoid); verified by asserting every one of the 300x2 rows lands in `[0,1]` and is
score-sorted. `classId` is a float column holding an integer 0..79.

Preprocessing is in the graph: the first three nodes are `Cast(float) -> Transpose(NCHW) ->
Div(255)`. **`/255` and nothing else — no ImageNet mean or std**, unlike RF-DETR. Against the stock
float export fed `x/255` the outputs agree at `max |diff| = 0.0` exactly, at batch 1 and batch 2.
(Exactly zero rather than RF-DETR's ~1e-4 because a single Div reproduces the reference division
bit for bit, where the mean/std rewrite folds two constants.)

### Embedded metadata — this model configures the runner, RF-DETR does not

`metadata_props`, read straight off the file:

| Key | Value |
| --- | --- |
| `task` | `detect` |
| `head` | `Detect` |
| `end2end` | `True` — the one-to-one head is active, so no NMS at runtime |
| `imgsz` | `[640, 640]` |
| `stride` | `32` |
| `channels` | `3` |
| `batch` | `1` — the export-time example batch, **not** a limit; the axis is dynamic |
| `version` | `8.4.150` |
| `license` | `AGPL-3.0 License (https://ultralytics.com/license)` |
| `names` | the full 80-class dict, below |
| `preprocessing` | added by the export script: `uint8 NHWC RGB; /255 is in the graph (no mean/std)` |

`names` is a **Python dict literal**, e.g. `{0: 'person', 1: 'bicycle', ...}` — not JSON, so the
keys are bare integers and the strings are single-quoted. A .NET reader needs a small parser or a
regex, not `JsonSerializer`.

Class ids are **0-based and contiguous, 0..79** — a different numbering from RF-DETR's sparse
1..90 with background at 0. The same label under two ids is exactly what the service-owned
`(modelId, classId)` table exists to absorb.

```
0:person 1:bicycle 2:car 3:motorcycle 4:airplane 5:bus 6:train 7:truck 8:boat 9:traffic light
10:fire hydrant 11:stop sign 12:parking meter 13:bench 14:bird 15:cat 16:dog 17:horse 18:sheep
19:cow 20:elephant 21:bear 22:zebra 23:giraffe 24:backpack 25:umbrella 26:handbag 27:tie
28:suitcase 29:frisbee 30:skis 31:snowboard 32:sports ball 33:kite 34:baseball bat
35:baseball glove 36:skateboard 37:surfboard 38:tennis racket 39:bottle 40:wine glass 41:cup
42:fork 43:knife 44:spoon 45:bowl 46:banana 47:apple 48:sandwich 49:orange 50:broccoli 51:carrot
52:hot dog 53:pizza 54:donut 55:cake 56:chair 57:couch 58:potted plant 59:bed 60:dining table
61:toilet 62:tv 63:laptop 64:mouse 65:remote 66:keyboard 67:cell phone 68:microwave 69:oven
70:toaster 71:sink 72:refrigerator 73:book 74:clock 75:vase 76:scissors 77:teddy bear
78:hair drier 79:toothbrush
```

### Geometry — letterbox, not stretch

RF-DETR stretches to 384x384 and its inverse is a plain multiply by `(W0, H0)`. YOLO letterboxes,
so the boxes come back through a different inverse. Forward, for original `(w0, h0)`:

```
gain = min(640/h0, 640/w0)                    # uniform, long side governs
pad_x = (640 - round(w0*gain)) / 2            # centred
pad_y = (640 - round(h0*gain)) / 2            # fill colour 114, bilinear INTER_LINEAR
```

Inverse, from model pixels to original pixels:

```
x = (x_model - round(pad_x - 0.1)) / gain     # then clip to [0, w0], likewise y to [0, h0]
```

The `- 0.1` before `round` is ultralytics' deliberate round-half-down; reproduce it or odd padding
drifts a pixel. For `dog-2.jpeg` at 720x1280 that is `gain = 0.5, pad_x = 140, pad_y = 0`.
`scripts/export-yolo.py` calls ultralytics' own `LetterBox` and `scale_boxes` rather than
reimplementing either, and asserts the whole chain against `predict()` on the float export.

### Expected result on the sample image

Same `dog-2.jpeg` (720x1280) the RF-DETR export uses, letterboxed with OpenCV `INTER_LINEAR`, CPU
execution provider. All detections above 0.25, boxes in original pixels `x1 y1 x2 y2`:

```
0.855 60 dining table    0.0  881.8  717.8 1278.8
0.848 41 cup           179.0  843.4  262.4 1006.7
0.803 56 chair           0.3  673.5   83.8  919.9
0.801 56 chair         580.5  669.5  720.0  975.3
0.790  0 person         13.0  490.3  176.7  691.1
0.782 25 umbrella       29.6    1.0  719.5  307.4
0.557 41 cup           306.0  846.1  441.9 1039.1
0.423  0 person         20.7  494.5  543.1  917.8
0.389  0 person         25.0  343.2  527.4  924.7
0.339  0 person        363.4  490.1  501.7  746.5
0.254 41 cup           282.2  849.3  384.9 1075.7
```

**There is no dog.** RF-DETR scores the beagle at 0.686; yolo26n does not find it at any score, and
neither does the `.pt` run through its one-to-many head with NMS, so this is the nano model's
recall and not a broken export. The 0.423 `person` box roughly covers the dog's region. A .NET test
must therefore assert *this* list, not a shared one — which is itself the useful result for D4: the
two detectors agree on the scene and disagree on its contents, so the geometry tests belong per
model and only the interface is common.

Sensible .NET assertions: top detection is `dining table` (60) above 0.8 with a box within a couple
of pixels of `(0, 882, 718, 1279)`; `umbrella` (25), `chair` (56), `person` (0) and `cup` (41) all
present above 0.25; exactly 11 detections above 0.25. A blank (all-zero) frame gives a top score of
0.001 (`person`) — assert nothing above 0.05, which is much quieter than RF-DETR's 0.115.

### A note on `predict()` as a reference

`YOLO('yolo26n.pt').predict(...)` is **not** a valid reference for this file: the checkpoint's
`end2end` flag is off, so it runs the one-to-many head plus NMS and legitimately returns different
scores and a different count. The reference is `predict()` on the **float ONNX export**, with
`rect=False` to force the full-square letterbox a static consumer gets — ultralytics otherwise pads
only to the next stride multiple. Against that, this file matches to 0.01 px on every box and 1e-4
on every score.

### How the two exporters share one venv

`fetch-yolo.sh` installs `ultralytics==8.4.150` into the same `.venv/` that `fetch-rfdetr.sh`
builds. It resolves cleanly against rfdetr's pins: no upgrade or downgrade of `torch 2.14.0+cpu`,
`torchvision 0.29.0+cpu`, `onnx 1.22.0` or `onnxruntime 1.30.0`, only additions
(`opencv-python 5.0.0.93`, `polars`, `psutil`, `cloudpickle`, `nvidia-ml-py`, `ultralytics-thop`,
`ultralytics-platform`). `scripts/export-rfdetr.py` was re-run after the install and still produces
the same numbers. If a future ultralytics stops resolving without moving rfdetr's pins, build a
second venv — do not move them.
