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
    ///
    /// The local default is 127.0.0.1 rather than localhost, deliberately. Kestrel binds IPv4, and
    /// on a Windows machine where localhost resolves to ::1 first the connection spends about five
    /// seconds failing over to IPv4 - longer than the connect budget below, so every poll failed
    /// with a timeout against an API that was answering in milliseconds. Measured on this machine:
    /// localhost 5.2 s, 127.0.0.1 1.9 s.
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

    /// <summary>
    /// Which model's contract to decode, because a file alone does not say. The two families
    /// disagree on tensor names, box format, whether scores are logits and whether the frame is
    /// stretched or letterboxed, so pointing <see cref="ModelPath"/> at one family's file while
    /// this says the other is refused at load rather than decoded into plausible nonsense.
    ///
    /// "rf-detr" is the default because its weights are Apache-2.0. "yolo26" is four to five times
    /// faster on a processor and misses things RF-DETR finds, and its licence is AGPL or
    /// commercial; see .scratch/scale-to-1000/detection-plan.md before shipping it.
    /// </summary>
    public string Model { get; init; } = "rf-detr";

    /// <summary>The file. Its default follows <see cref="Model"/> when this is left empty.</summary>
    public string ModelPath { get; init; } = string.Empty;

    /// <summary>
    /// Hardware used for inference. "auto" tries CUDA when that build is deployed, then Intel
    /// NPU, GPU and CPU when the OpenVINO build is deployed, then DirectML, and finally ORT CPU.
    /// A named provider is fail-fast except "openvino", which tries all three Intel devices.
    /// </summary>
    [RegularExpression(
        "(?i)^(auto|cpu|cuda|tensorrt|directml|openvino|openvino-npu|openvino-gpu|openvino-cpu)$",
        ErrorMessage = "ExecutionProvider must be auto, cpu, cuda, tensorrt, directml, openvino, openvino-npu, openvino-gpu, or openvino-cpu.")]
    public string ExecutionProvider { get; init; } = "auto";

    /// <summary>
    /// Persistent compiled-model cache for OpenVINO. Empty uses a process temporary directory;
    /// production should point this at a node-local volume so restarts do not recompile the model.
    /// </summary>
    public string OpenVinoCachePath { get; init; } = string.Empty;

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
