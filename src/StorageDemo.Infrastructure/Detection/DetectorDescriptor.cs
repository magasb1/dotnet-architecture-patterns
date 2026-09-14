using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Detection;

/// <summary>
/// How a model's box output is laid out — and, with it, how many output tensors there are, because
/// the two families differ on both at once and pretending otherwise would need a second flag that
/// is never independently set.
/// </summary>
public enum BoxFormat
{
    /// <summary>
    /// RF-DETR: two tensors. Boxes are <c>(batch, queries, 4)</c> centre x, centre y, width,
    /// height, each 0..1 of the canvas; scores are a separate <c>(batch, queries, classes)</c>
    /// tensor wanting a sigmoid and an argmax the decode does not take (multi-label).
    /// </summary>
    CentreNormalised,

    /// <summary>
    /// YOLO26 with the end-to-end head: one tensor, <c>(batch, queries, 6)</c> rows of
    /// <c>x1, y1, x2, y2, score, classId</c>, corners in canvas pixels and the class as an integer
    /// in a float column. Rows are sorted by score descending, so the decode stops at the first one
    /// under the threshold.
    /// </summary>
    PixelCorners,
}

/// <summary>
/// Everything about one model that the runner cannot read off the ONNX file, as data
/// (detection-plan.md, "The seam"). The query count and class count are not here on purpose:
/// they are read from the output tensor's shape.
/// </summary>
/// <param name="ScoresOutput">
/// The second output tensor, or null when one tensor carries box, score and class together
/// (<see cref="BoxFormat.PixelCorners"/>). Null is not "unknown": it is the model having one
/// output, which the runner then binds alone.
/// </param>
/// <param name="Classes">
/// Logit slot to class name. A slot missing from the table is not a class (RF-DETR's background
/// slot 0, the COCO gaps) and is never emitted. A file carrying its own <c>names</c> metadata
/// overrides this at load; this is the fallback for the families that do not, which is RF-DETR.
/// </param>
public sealed record DetectorDescriptor(
    string InputName,
    string BoxesOutput,
    string? ScoresOutput,
    BoxFormat BoxFormat,
    bool ScoresAreLogits,
    bool NeedsNms,
    bool SupportsOpenVinoNpu,
    IReadOnlyDictionary<int, string> Classes,
    DetectorGeometry Geometry)
{
    public int InputSize => Geometry.InputSize;

    /// <summary>
    /// RF-DETR Nano as exported by scripts/export-rfdetr.py, every fact from models/README.md's
    /// "Tensor contract, verified by running the file": uint8 NHWC <c>input</c>, <c>dets</c> as
    /// normalised cxcywh, <c>labels</c> as raw logits wanting a sigmoid, no NMS, sparse COCO ids,
    /// stretched to 384.
    /// </summary>
    public static readonly DetectorDescriptor RfDetrNano = new(
        InputName: "input",
        BoxesOutput: "dets",
        ScoresOutput: "labels",
        BoxFormat: BoxFormat.CentreNormalised,
        ScoresAreLogits: true,
        NeedsNms: false,
        SupportsOpenVinoNpu: true,
        Classes: CocoClasses.RfDetr,
        Geometry: new DetectorGeometry.Stretch(384));

    /// <summary>
    /// YOLO26 Nano as exported by scripts/export-yolo.py, every fact from models/README.md's
    /// "yolo26-nano.onnx": uint8 NHWC <c>images</c> (not <c>input</c>), one <c>output0</c> of
    /// xyxy in 640-space with the score and class beside it, scores already probabilities, the
    /// one-to-one head so nothing to suppress, contiguous ids, letterboxed to 640 with 114.
    ///
    /// The class table here is only a fallback: this file carries its own <c>names</c> and the
    /// runner reads them. RF-DETR's does not, which is the asymmetry that earns the branch.
    /// </summary>
    public static readonly DetectorDescriptor Yolo26Nano = new(
        InputName: "images",
        BoxesOutput: "output0",
        ScoresOutput: null,
        BoxFormat: BoxFormat.PixelCorners,
        ScoresAreLogits: false,
        NeedsNms: false,
        // This export's dynamic GatherElements post-processing does not compile for the Intel
        // NPU. Auto mode goes straight to the GPU instead of paying for a known failed compile;
        // explicitly requesting openvino-npu still probes it, which keeps re-exports testable.
        SupportsOpenVinoNpu: false,
        Classes: CocoClasses.Yolo.Index().ToDictionary(c => c.Index, c => c.Item),
        Geometry: new DetectorGeometry.Letterbox(640));
}
