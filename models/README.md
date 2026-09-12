# models/

Not committed. `scripts/fetch-rfdetr.sh` produces everything here; `.venv/` holds the exporter.

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
