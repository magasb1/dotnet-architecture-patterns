namespace StorageDemo.Core.Streaming;

/// <summary>
/// How a frame becomes a model input and how a model's box comes back out, as one object. The
/// forward resize and its inverse are one formula read in two directions, and the failure the
/// detection plan fears most is the two drifting apart when they are written in different places:
/// every box then lands plausibly and wrongly, and nothing throws.
///
/// Pure arithmetic. It says where the frame's pixels land on the model canvas so that swscale can
/// do the resize later, and it turns model-space boxes into <see cref="VmtiDetection"/>
/// coordinates, which are 1-based and inclusive (see Misb0903.cs). Model space is the
/// <see cref="InputSize"/> x <see cref="InputSize"/> canvas with (0, 0) at the outer corner of the
/// top-left pixel, the edge convention both families' box outputs use. Square only, because every
/// export in research/detector-models.md is (RF-DETR fixes H = W = resolution at export; YOLO
/// defaults imgsz to one number).
///
/// Sources are cited per formula below; all were read from the repositories on 2026-09-12.
/// </summary>
public abstract record DetectorGeometry(int InputSize)
{
    /// <summary>
    /// Where the frame's pixels land on the canvas. The rest of the canvas, if any, is padding.
    /// The pixel resize into that rectangle must be bilinear with half-pixel centres, which is what
    /// swscale does by default and what both references do (RF-DETR's <c>_resize.py</c>, OpenCV's
    /// <c>INTER_LINEAR</c>); a corner-aligned or antialiased resize shifts content sub-pixel.
    /// </summary>
    public abstract (int Left, int Top, int Width, int Height) Place(int frameWidth, int frameHeight);

    /// <summary>A frame box onto the canvas, edges in canvas pixels. The forward direction.</summary>
    public (double X1, double Y1, double X2, double Y2) ToModel(VmtiDetection box, int frameWidth, int frameHeight)
        // 1-based inclusive columns L..R cover the continuous span [L - 1, R).
        => Forward(box.Left - 1, box.Top - 1, box.Right, box.Bottom, frameWidth, frameHeight);

    /// <summary>A canvas box, edges in canvas pixels (YOLO's xyxy), back to the frame.</summary>
    public VmtiDetection ToFrame(int id, (double X1, double Y1, double X2, double Y2) box, int frameWidth, int frameHeight)
    {
        var (x1, y1, x2, y2) = Inverse(box.X1, box.Y1, box.X2, box.Y2, frameWidth, frameHeight);

        // Continuous edges to ST 0903's pixels: a left edge at 100.0 makes 0-based column 100 the
        // first inside, which is column 101; a right edge at 140.0 makes 139 the last, which is
        // 140. Nearest edge, so a value a rounding error past an integer does not grow the box.
        // Clipping happens here for both strategies: scale_boxes clips (cited in Letterbox), RF-DETR
        // does not, and VmtiDetection rejects a pixel outside the frame either way.
        var left = Math.Clamp((int)Math.Round(x1) + 1, 1, frameWidth);
        var top = Math.Clamp((int)Math.Round(y1) + 1, 1, frameHeight);

        return new VmtiDetection(
            id,
            left,
            top,
            Math.Max(left, Math.Clamp((int)Math.Round(x2), 1, frameWidth)),
            Math.Max(top, Math.Clamp((int)Math.Round(y2), 1, frameHeight)));
    }

    /// <summary>
    /// RF-DETR's <c>dets</c> box: centre, width and height, each 0..1 of the canvas
    /// (research/detector-models.md section 1, "Output"). Scaled to canvas pixels and handed to
    /// <see cref="ToFrame"/>, so the two families share one inverse and one rounding. A raw YOLO
    /// xywh box is the same shape in canvas pixels; convert it in model space, then ToFrame.
    /// </summary>
    public VmtiDetection ToFrameNormalised(int id, (double Cx, double Cy, double W, double H) box, int frameWidth, int frameHeight)
        => ToFrame(
            id,
            ((box.Cx - box.W / 2) * InputSize, (box.Cy - box.H / 2) * InputSize,
             (box.Cx + box.W / 2) * InputSize, (box.Cy + box.H / 2) * InputSize),
            frameWidth,
            frameHeight);

    protected abstract (double X1, double Y1, double X2, double Y2) Forward(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight);

    protected abstract (double X1, double Y1, double X2, double Y2) Inverse(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight);

    /// <summary>
    /// RF-DETR: the whole frame resized onto the whole canvas, aspect ratio destroyed, no padding.
    /// <c>src/rfdetr/detr.py</c> (predict): <c>F.resize(t, [resolution, resolution], antialias=False)</c>,
    /// a two-element size forcing both dimensions. Bilinear, half-pixel centres, no antialias:
    /// <c>src/rfdetr/export/_resize.py</c> exists purely to reproduce that torch-free, and says
    /// PIL's resize diverges on both counts.
    /// </summary>
    public sealed record Stretch(int InputSize) : DetectorGeometry(InputSize)
    {
        public override (int Left, int Top, int Width, int Height) Place(int frameWidth, int frameHeight)
            => (0, 0, InputSize, InputSize);

        // Half-pixel centres (align_corners=False) mean frame edge to canvas edge, so a pixel's
        // centre i + 0.5 lands at (i + 0.5) * InputSize / frameWidth. Corner alignment would send
        // centre 0.5 to 0.5 instead; that is the sub-pixel shift _resize.py warns about.
        protected override (double X1, double Y1, double X2, double Y2) Forward(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight)
            => (x1 * InputSize / frameWidth, y1 * InputSize / frameHeight, x2 * InputSize / frameWidth, y2 * InputSize / frameHeight);

        // src/rfdetr/models/postprocess.py, PostProcess._gather_and_scale_boxes: the normalised
        // box times [W0, H0, W0, H0] of the ORIGINAL image, no gain, no pad, per axis. Dividing by
        // InputSize first undoes the scaling ToFrame(cxcywh) applied, so this is that formula.
        protected override (double X1, double Y1, double X2, double Y2) Inverse(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight)
            => (x1 / InputSize * frameWidth, y1 / InputSize * frameHeight, x2 / InputSize * frameWidth, y2 / InputSize * frameHeight);
    }

    /// <summary>
    /// YOLO: the frame scaled uniformly by the long side and centred on a canvas of
    /// <paramref name="PadValue"/>. <c>ultralytics/data/augment.py</c>, <c>class LetterBox</c>,
    /// defaults <c>auto=False, scale_fill=False, scaleup=True, center=True, padding_value=114,
    /// interpolation=cv2.INTER_LINEAR</c>; a static ONNX export always pads to the full square
    /// (research/detector-models.md section 3).
    /// </summary>
    public sealed record Letterbox(int InputSize, int PadValue = 114) : DetectorGeometry(InputSize)
    {
        public override (int Left, int Top, int Width, int Height) Place(int frameWidth, int frameHeight)
        {
            // LetterBox.__call__:
            //   r = min(new_shape[0] / shape[0], new_shape[1] / shape[1])
            //   new_unpad = round(shape[1] * r), round(shape[0] * r)
            //   dw, dh = (new_shape[1] - new_unpad[0]) / 2, (new_shape[0] - new_unpad[1]) / 2
            //   top, bottom = round(dh - 0.1), round(dh + 0.1)
            //   left, right = round(dw - 0.1), round(dw + 0.1)
            // The 0.1 makes an x.5 pad round down on the leading edge and up on the trailing one,
            // so the two always sum to the whole gap; ordinary rounding would give x.5 to the
            // same side twice on odd gaps. Python's round is half-to-even, as is Math.Round.
            var gain = Gain(frameWidth, frameHeight);
            var width = (int)Math.Round(frameWidth * gain);
            var height = (int)Math.Round(frameHeight * gain);

            return (
                (int)Math.Round((InputSize - width) / 2.0 - 0.1),
                (int)Math.Round((InputSize - height) / 2.0 - 0.1),
                width,
                height);
        }

        // Frame pixels times the gain, plus the pad. Note the reference resizes to the ROUNDED
        // new_unpad but scales boxes by the unrounded gain, so on a frame like 1280x721 the pixels
        // end half a canvas row short of where the boxes say; that is Ultralytics' behaviour and
        // matching it is the point, not fixing it.
        protected override (double X1, double Y1, double X2, double Y2) Forward(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight)
        {
            var gain = Gain(frameWidth, frameHeight);
            var (padX, padY, _, _) = Place(frameWidth, frameHeight);

            return (x1 * gain + padX, y1 * gain + padY, x2 * gain + padX, y2 * gain + padY);
        }

        // ultralytics/utils/ops.py, scale_boxes with ratio_pad=None:
        //   gain  = min(img1_shape[0] / img0_shape[0], img1_shape[1] / img0_shape[1])
        //   pad_x = round((img1_shape[1] - round(img0_shape[1] * gain)) / 2 - 0.1)
        //   pad_y = round((img1_shape[0] - round(img0_shape[0] * gain)) / 2 - 0.1)
        //   boxes[..., [0, 2]] -= pad_x; boxes[..., [1, 3]] -= pad_y; boxes /= gain
        //   clip_boxes(boxes, img0_shape)
        // pad_x and pad_y are the same expression as Place's left and top, so they come from it.
        protected override (double X1, double Y1, double X2, double Y2) Inverse(double x1, double y1, double x2, double y2, int frameWidth, int frameHeight)
        {
            var gain = Gain(frameWidth, frameHeight);
            var (padX, padY, _, _) = Place(frameWidth, frameHeight);

            return ((x1 - padX) / gain, (y1 - padY) / gain, (x2 - padX) / gain, (y2 - padY) / gain);
        }

        private double Gain(int frameWidth, int frameHeight)
            => Math.Min((double)InputSize / frameHeight, (double)InputSize / frameWidth);
    }
}
