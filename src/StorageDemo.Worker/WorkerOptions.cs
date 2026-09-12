using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Worker;

/// <summary>
/// Configuration for one detection worker, bound from the <c>Worker</c> section, so in a
/// container it reads <c>Worker__ApiBaseUrl</c> and <c>Worker__Token</c> the way the API reads
/// <c>Live__PeerBaseUrl</c>.
/// </summary>
public sealed class WorkerOptions
{
    public const string SectionName = "Worker";

    /// <summary>
    /// Where the worker lists streams, for example "http://storage-demo:80". Any replica answers a
    /// listing; the owner of each stream is reached at the address the listing carries, and at
    /// this one when the owner recorded none, which is what a single replica does.
    /// </summary>
    [Required]
    public string ApiBaseUrl { get; init; } = string.Empty;

    /// <summary>The live token, sent as X-Storage-Token on every call. The API guards its peer routes with it.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// How this worker names itself in a stream's registry entry. Defaults to POD_NAME, which
    /// Kubernetes supplies, and to the machine name elsewhere.
    /// </summary>
    public string? NodeName { get; init; }

    public string ModelPath { get; init; } = "models/rf-detr-nano.onnx";

    /// <summary>
    /// The detector keeps boxes scoring above this. Low on purpose: the tracker wants the weak
    /// boxes too, for its second association (Tracking.cs), and applies its own thresholds.
    /// </summary>
    [Range(0.01, 0.99)]
    public float Threshold { get; init; } = 0.1f;

    /// <summary>
    /// The tracker's tau: boxes above it go into the first association, and a box needs tau + 0.1
    /// to start a track. The paper's 0.6, which Tracking.cs defaults to, was set for a YOLOX
    /// detector; RF-DETR's sigmoid scores sit lower, and its own default reporting threshold is
    /// 0.5 (rfdetr's predict), so track birth is put there: a box the model would itself report
    /// may start a track. Measured on the README's dog: 0.69 from the JPEG, 0.58 after MPEG-2,
    /// both above 0.5 and neither above the paper's 0.7. A knob because it is a calibration:
    /// raise it for a stronger detector, lower it for a weaker one.
    /// </summary>
    [Range(0.1, 0.99)]
    public double TrackThreshold { get; init; } = 0.4;

    /// <summary>Detections per second for a stream whose toggle says zero. One is what a processor manages.</summary>
    [Range(0.1, 60)]
    public double DefaultRate { get; init; } = 1;

    /// <summary>
    /// The most frames sent to the model as one batch. Frames from different streams that fall
    /// due while a detection is running are batched into the next one, which is what makes a
    /// transformer efficient on a GPU (detection-plan.md D3); on a processor it changes little.
    /// </summary>
    [Range(1, 64)]
    public int MaxBatch { get; init; } = 8;
}
