#!/usr/bin/env bash
# Produces models/yolo26-nano.onnx: YOLO26 Nano, COCO weights, with uint8 input and /255
# normalisation baked into the graph. See models/README.md for the tensor contract.
#
# **Separate from fetch-rfdetr.sh on purpose.** RF-DETR Nano is Apache-2.0 and is the default;
# ultralytics is AGPL-3.0 or a paid Ultralytics Enterprise licence, and the vendor lists hosted
# services and proprietary models under the commercial one. Installing it should be a thing
# somebody chose to run, not a side effect of fetching the default model. models/README.md states
# the position; it is not legal advice.
#
# Shares .venv/ with fetch-rfdetr.sh: ultralytics 8.4.150 resolves against the pins rfdetr already
# fixed (torch 2.14.0+cpu, torchvision 0.29.0+cpu, onnx 1.22.0, onnxruntime 1.30.0) with no
# upgrade or downgrade of any of them - it only adds opencv-python, polars, psutil and its own
# packages. If that ever stops being true, build a second venv rather than moving rfdetr's pins.
#
# The venv itself comes from fetch-rfdetr.sh so the pins live in exactly one file; it also fetches
# models/dog-2.jpeg, which export-yolo.py proves this model on.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VENV="$ROOT/.venv"

[ -d "$VENV" ] || "$ROOT/scripts/fetch-rfdetr.sh"

PY="$VENV/Scripts/python"
[ -x "$PY" ] || PY="$VENV/bin/python"

if command -v uv >/dev/null; then
  uv pip install --python "$VENV" ultralytics==8.4.150
else
  "$PY" -m pip install --quiet ultralytics==8.4.150
fi

"$PY" "$ROOT/scripts/export-yolo.py"

MODEL="$ROOT/models/yolo26-nano.onnx"
if [ -s "$MODEL" ]; then
  echo "Installed $MODEL ($(du -h "$MODEL" | cut -f1)) - AGPL-3.0 or Ultralytics Enterprise, see models/README.md"
else
  echo "ERROR: $MODEL was not produced" >&2
  exit 1
fi
