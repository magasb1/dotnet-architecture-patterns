using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Detection;

/// <summary>How a model's box output is laid out. Only the RF-DETR shape exists until D4.</summary>
public enum BoxFormat
{
    /// <summary>Centre x, centre y, width, height, each 0..1 of the canvas.</summary>
    CentreNormalised,
}

/// <summary>
/// Everything about one model that the runner cannot read off the ONNX file, as data
/// (detection-plan.md, "The seam"). The query count and class count are not here on purpose:
/// they are read from the output tensor's shape.
/// </summary>
/// <param name="Classes">
/// Logit slot to class name. A slot missing from the table is not a class (RF-DETR's background
/// slot 0, the COCO gaps) and is never emitted.
/// </param>
public sealed record DetectorDescriptor(
    string InputName,
    string BoxesOutput,
    string ScoresOutput,
    BoxFormat BoxFormat,
    bool ScoresAreLogits,
    bool NeedsNms,
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
        Classes: CocoClasses.RfDetr,
        Geometry: new DetectorGeometry.Stretch(384));
}
