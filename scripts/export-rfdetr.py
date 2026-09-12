#!/usr/bin/env python
"""Exports RF-DETR Nano (COCO weights, Apache-2.0) to models/rf-detr-nano.onnx with preprocessing
baked into the graph, then proves the contract with onnxruntime. Run via scripts/fetch-rfdetr.sh,
which builds the venv this needs; see models/README.md for the verified tensor contract.

The rfdetr exporter produces a float32 NCHW model with no preprocessing. This script prepends four
ONNX nodes so the .NET caller feeds raw RGB bytes:

    input (uint8, B x 384 x 384 x 3, NHWC)
      -> Cast(float32) -> Transpose(NCHW) -> Sub(255*mean) -> Div(255*std) -> original graph

NHWC because a decoded RGB24 frame is already interleaved H x W x 3: the caller copies the buffer
as-is and the one Transpose node does the deinterleave, instead of .NET doing it per frame.
(x - 255*mean) / (255*std) is algebraically (x/255 - mean)/std, the reference normalisation.
"""

import io
import sys
import urllib.request
from importlib.metadata import version
from pathlib import Path

import numpy as np
import onnx
import onnxruntime as ort
from onnx import TensorProto, helper, numpy_helper
from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
MODELS = ROOT / "models"
WEIGHTS = MODELS / "rf-detr-nano.pth"  # downloaded by rfdetr, MD5-verified against its asset table
OUT = MODELS / "rf-detr-nano.onnx"
RES = 384
MEAN = np.array([0.485, 0.456, 0.406], dtype=np.float32)
STD = np.array([0.229, 0.224, 0.225], dtype=np.float32)
SAMPLE_URL = "https://media.roboflow.com/notebooks/examples/dog-2.jpeg"  # the rfdetr README example


def export_float() -> Path:
    from rfdetr import RFDETRNano

    model = RFDETRNano(pretrain_weights=str(WEIGHTS))
    return model.export(
        output_dir=str(MODELS), output_name="rf-detr-nano-float", dynamic_batch=True, opset_version=17, verbose=False
    )


def bake_preprocessing(float_path: Path, out_path: Path) -> None:
    m = onnx.load(float_path)
    g = m.graph
    assert g.input[0].name == "input", [i.name for i in g.input]
    assert [o.name for o in g.output] == ["dets", "labels"]

    for n in g.node:
        for i, name in enumerate(n.input):
            if name == "input":
                n.input[i] = "input_normalised"
    g.input.remove(g.input[0])
    g.input.insert(0, helper.make_tensor_value_info("input", TensorProto.UINT8, ["batch", RES, RES, 3]))
    g.initializer.extend(
        [
            numpy_helper.from_array((255 * MEAN).reshape(1, 3, 1, 1), "imagenet_mean_x255"),
            numpy_helper.from_array((255 * STD).reshape(1, 3, 1, 1), "imagenet_std_x255"),
        ]
    )
    pre = [
        helper.make_node("Cast", ["input"], ["input_f32"], to=TensorProto.FLOAT),
        helper.make_node("Transpose", ["input_f32"], ["input_nchw"], perm=[0, 3, 1, 2]),
        helper.make_node("Sub", ["input_nchw", "imagenet_mean_x255"], ["input_centred"]),
        helper.make_node("Div", ["input_centred", "imagenet_std_x255"], ["input_normalised"]),
    ]
    for i, n in enumerate(pre):
        g.node.insert(i, n)
    m.metadata_props.add(key="preprocessing", value="uint8 NHWC RGB; /255 and ImageNet mean/std are in the graph")
    m.metadata_props.add(key="rfdetr_version", value=version("rfdetr"))
    onnx.checker.check_model(m)
    onnx.save(m, out_path)


def verify(float_path: Path, out_path: Path) -> None:
    sess = ort.InferenceSession(str(out_path), providers=["CPUExecutionProvider"])
    ref = ort.InferenceSession(str(float_path), providers=["CPUExecutionProvider"])
    (inp,) = sess.get_inputs()
    print(f"input  {inp.name} {inp.type} {inp.shape}")
    for o in sess.get_outputs():
        print(f"output {o.name} {o.type} {o.shape}")
    assert inp.name == "input" and inp.type == "tensor(uint8)" and inp.shape[1:] == [RES, RES, 3]

    rng = np.random.default_rng(0)
    for b in (1, 2):
        x = rng.integers(0, 256, (b, RES, RES, 3), dtype=np.uint8)
        dets, labels = sess.run(["dets", "labels"], {"input": x})
        assert dets.shape == (b, 300, 4), dets.shape
        assert labels.shape == (b, 300, 91), labels.shape
        print(f"batch {b}: dets {dets.shape} labels {labels.shape}")
        x_ref = ((x.transpose(0, 3, 1, 2).astype(np.float32) / 255) - MEAN[None, :, None, None]) / STD[None, :, None, None]
        dets_ref, labels_ref = ref.run(["dets", "labels"], {"input": x_ref})
        diff = max(np.abs(dets - dets_ref).max(), np.abs(labels - labels_ref).max())
        assert diff < 1e-3, diff
        print(f"batch {b}: max |baked - float| = {diff:.2e}")

    from rfdetr.assets.coco_classes import COCO_CLASSES

    print("class table (sparse COCO id -> name, slot 0 = background):")
    print("  " + ", ".join(f"{k}:{v}" for k, v in COCO_CLASSES.items()))

    blank = np.zeros((1, RES, RES, 3), dtype=np.uint8)
    _, labels = sess.run(["dets", "labels"], {"input": blank})
    blank_max = float(1 / (1 + np.exp(-labels)).max())
    print(f"blank image: max sigmoid score {blank_max:.3f}")
    assert blank_max < 0.3, blank_max

    sample = MODELS / "dog-2.jpeg"
    if not sample.exists():
        sample.write_bytes(urllib.request.urlopen(SAMPLE_URL, timeout=60).read())
    img = Image.open(io.BytesIO(sample.read_bytes())).convert("RGB")
    w0, h0 = img.size
    top = detect(sess, img, COCO_CLASSES)
    print(f"{sample.name} ({w0}x{h0}), top detections (score, class, x1 y1 x2 y2 in original pixels):")
    for score, cid, name, box in top[:10]:
        print(f"  {score:.3f} {cid:2d} {name:<12} {box[0]:6.1f} {box[1]:6.1f} {box[2]:6.1f} {box[3]:6.1f}")
    assert any(name == "dog" and score > 0.5 for score, _, name, _ in top[:10]), top[:10]


def detect(sess, img, classes):
    """Reference preprocessing (stretch, bilinear, antialias off) and the PostProcess decode."""
    import torch
    from torchvision.transforms import functional as F

    t = F.resize(F.pil_to_tensor(img), [RES, RES], antialias=False)  # uint8, matches rfdetr predict()
    x = t.permute(1, 2, 0).numpy()[None]
    dets, labels = sess.run(["dets", "labels"], {"input": x})
    prob = 1 / (1 + np.exp(-labels[0]))  # (300, 91), per-class sigmoid, no softmax
    order = np.argsort(-prob.reshape(-1), kind="stable")[:300]
    w0, h0 = img.size
    out = []
    for flat in order:
        q, cid = divmod(int(flat), prob.shape[1])
        cx, cy, w, h = dets[0, q]
        box = ((cx - w / 2) * w0, (cy - h / 2) * h0, (cx + w / 2) * w0, (cy + h / 2) * h0)
        out.append((float(prob[q, cid]), cid, classes.get(cid, "background"), box))
    return out


def main() -> int:
    MODELS.mkdir(exist_ok=True)
    float_path = export_float()
    bake_preprocessing(float_path, OUT)
    verify(float_path, OUT)
    Path(float_path).unlink()
    print(f"OK {OUT} ({OUT.stat().st_size / 1e6:.1f} MB), rfdetr {version('rfdetr')}, onnxruntime {version('onnxruntime')}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
