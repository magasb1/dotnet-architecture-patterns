using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Detection;

/// <summary>
/// Runs one ONNX detector, described by a <see cref="DetectorDescriptor"/>, on the processor or
/// on CUDA when CUDA is there.
///
/// One session per model, shared: <c>Run</c> is thread-safe and sessions do not share weights, so
/// a session per caller would multiply memory for nothing (research/onnxruntime-dotnet.md
/// section 4). The frame goes from libav's pixel format straight into the model's uint8 NHWC
/// input through one swscale pass; normalisation is in the graph (models/README.md), so there is
/// no float conversion here and nothing to allocate per frame.
/// </summary>
public sealed unsafe partial class OnnxDetector : IDetector, IDisposable
{
    private const int Channels = 3;

    /// <summary>
    /// One thread pool for the process rather than one per session, which is the default and
    /// fights itself once a service holds several sessions (research section 3, "Threads").
    /// Zero threads means ORT's own default, one per physical core; that is the knob to turn.
    /// Spinning between operators is off: it buys latency on a dedicated box and costs every
    /// decoder thread sharing this one.
    /// </summary>
    /// <returns>
    /// True when this process's ORT environment is ours and carries the global thread pools, so a
    /// session may hand its threading to it. False when somebody else created the environment
    /// first: only its creator can give it global pools, and asking a session to use pools that do
    /// not exist throws. Unreachable while this is the only ORT component here, and reachable the
    /// moment a second one shares the process, which is why it is a branch rather than an
    /// assumption.
    /// </returns>
    private static readonly Lazy<bool> GlobalThreadPools = new(() =>
    {
        if (OrtEnv.IsCreated)
        {
            return false;
        }

        var options = new EnvironmentCreationOptions
        {
            logId = "storagedemo",
            threadOptions = new OrtThreadingOptions { GlobalIntraOpNumThreads = 0, GlobalInterOpNumThreads = 1, GlobalSpinControl = false },
        };

        _ = OrtEnv.CreateInstanceWithOptions(ref options);

        return true;
    });

    private readonly DetectorDescriptor _descriptor;
    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly string[] _inputNames;
    private readonly string[] _outputNames;
    private readonly OrtValue[] _inputValues = new OrtValue[1];

    /// <summary>
    /// Kept as a logit: sigmoid is monotonic, so comparing the raw logit against the threshold's
    /// logit keeps every score the threshold would and skips 27,000 exps per frame for the rest.
    /// </summary>
    private readonly float _cutoff;

    private readonly byte _padValue;

    // ponytail: one input buffer and one scaler behind one lock, so calls serialise. Concurrent
    // Run on one session is legal; per-caller buffers are the upgrade if a GPU sits idle.
    private readonly Lock _gate = new();
    private readonly int _frameBytes;
    private readonly int _stride;
    private byte* _input;
    private int _capacity;
    private SwsContext* _scaler;

    // swscale reads plane arrays, four entries at least; filled per frame, never reallocated.
    private readonly byte*[] _source = new byte*[8];
    private readonly int[] _sourceStride = new int[8];
    private readonly byte*[] _target = new byte*[4];
    private readonly int[] _targetStride = new int[4];

    /// <summary>
    /// The reference resize is two-tap bilinear with antialiasing off (DetectorGeometry.Stretch,
    /// citing rfdetr's _resize.py). swscale's SWS_BILINEAR widens its kernel on a downscale, which
    /// is antialiasing, and SWS_FAST_BILINEAR is the two-tap one. Measured on models/dog-2.jpeg
    /// against the torchvision canvas: the dog scores 0.659 this way and 0.518 with SWS_BILINEAR,
    /// against 0.687 in the reference; the other classes sit within a few hundredths either way.
    /// Full chroma interpolation and accurate rounding close the last hundredths. The fast
    /// horizontal path is corner-aligned rather than centred, a sub-pixel shift on the canvas that
    /// is under a source pixel here; that is the trade for matching the reference's sharpness.
    /// </summary>
    private const SwsFlags Flags = SwsFlags.SWS_FAST_BILINEAR | SwsFlags.SWS_FULL_CHR_H_INT | SwsFlags.SWS_ACCURATE_RND;

    /// <param name="threshold">Scores below this, after the sigmoid where one applies, are dropped. 0..1 exclusive.</param>
    public OnnxDetector(string modelPath, DetectorDescriptor descriptor, float threshold, ILogger logger)
    {
        if (threshold is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "A probability strictly between 0 and 1.");
        }

        if (descriptor.NeedsNms)
        {
            // Still unbuilt after D4, and deliberately: RF-DETR's decode is set-based and YOLO26's
            // export uses the one-to-one head, so neither descriptor here asks for it. An older
            // YOLO export with nms=False on the one-to-many head would, and would want the raw
            // (batch, 84, anchors) transposed layout as well — a third BoxFormat, not a flag.
            throw new NotSupportedException("Non-maximum suppression is not built; no descriptor here needs it (detection-plan.md D4).");
        }

        FfmpegLibrary.EnsureLoaded();

        _descriptor = descriptor;
        _cutoff = descriptor.ScoresAreLogits ? MathF.Log(threshold / (1 - threshold)) : threshold;
        _frameBytes = descriptor.InputSize * descriptor.InputSize * Channels;
        _inputNames = [descriptor.InputName];
        _outputNames = descriptor.ScoresOutput is null
            ? [descriptor.BoxesOutput]
            : [descriptor.BoxesOutput, descriptor.ScoresOutput];

        // The letterbox fill, 114, from the geometry that has one; Stretch covers the whole canvas
        // and never shows it. Read here rather than added to the base record, because a strategy
        // without padding has no honest value to give.
        _padValue = descriptor.Geometry is DetectorGeometry.Letterbox letterbox ? (byte)letterbox.PadValue : (byte)0;

        using var options = new SessionOptions();

        if (GlobalThreadPools.Value)
        {
            options.DisablePerSessionThreads();
        }
        else
        {
            logger.LogWarning(
                "Another component created this process's ONNX Runtime environment, so this "
                + "detector runs its own thread pool rather than the shared one. It works and "
                + "costs threads; measured at 22 percent throughput on a processor.");
        }
        Provider = AppendBestProvider(options, logger);
        _session = new InferenceSession(modelPath, options);

        VerifyContract();
        Classes = ReadEmbeddedMetadata(logger) ?? descriptor.Classes;

        logger.LogInformation(
            "Detector {Model} on the {Provider} execution provider (available: {Providers}), input {Input} {Shape}",
            Path.GetFileName(modelPath),
            Provider,
            string.Join(", ", OrtEnv.Instance().GetAvailableProviders()),
            descriptor.InputName,
            string.Join("x", _session.InputMetadata[descriptor.InputName].Dimensions));

        _stride = descriptor.InputSize * Channels;
        _targetStride[0] = _stride;
    }

    /// <summary>"CUDA" or "CPU": which provider the session was built with.</summary>
    public string Provider { get; }

    /// <summary>
    /// The table actually in use: the file's own <c>names</c> when it carries them, the
    /// descriptor's otherwise. Exposed because which one won is the difference between a `dog` and
    /// a `sheep` and should be visible without reading a log line.
    /// </summary>
    public IReadOnlyDictionary<int, string> Classes { get; }

    public VmtiDetection[] Detect(IntPtr frame) => Detect([frame])[0];

    public VmtiDetection[][] Detect(ReadOnlySpan<IntPtr> frames)
    {
        if (frames.IsEmpty)
        {
            return [];
        }

        lock (_gate)
        {
            EnsureCapacity(frames.Length);

            for (var i = 0; i < frames.Length; i++)
            {
                Place((AVFrame*)frames[i], _input + i * _frameBytes);
            }

            var size = _descriptor.InputSize;

            // Wraps the pinned native buffer; nothing is copied. Bound by name, because RF-DETR's
            // own docs warn the two outputs can be indistinguishable by shape (research/detector-models.md section 1).
            // ponytail: IOBinding with OrtValues on device memory is the GPU-side upgrade, so Run
            // does no host-device copy; on the processor it would gain nothing.
            using var input = OrtValue.CreateTensorValueWithData(
                OrtMemoryInfo.DefaultInstance,
                TensorElementType.UInt8,
                [frames.Length, size, size, Channels],
                (IntPtr)_input,
                frames.Length * _frameBytes);

            _inputValues[0] = input;

            using var outputs = _session.Run(_runOptions, _inputNames, _inputValues, _outputNames);

            // (batch, queries, columns): four for RF-DETR's cxcywh, six for a YOLO row that
            // carries its own score and class. The counts are the tensor's, not the descriptor's.
            var boxes = outputs[0].GetTensorDataAsSpan<float>();
            var boxShape = outputs[0].GetTensorTypeAndShape().Shape;
            var queries = (int)boxShape[1];
            var columns = (int)boxShape[2];

            var scores = ReadOnlySpan<float>.Empty;
            var classes = 0;

            if (_descriptor.ScoresOutput is not null)
            {
                scores = outputs[1].GetTensorDataAsSpan<float>();
                classes = (int)outputs[1].GetTensorTypeAndShape().Shape[2];
            }

            var results = new VmtiDetection[frames.Length][];

            for (var i = 0; i < frames.Length; i++)
            {
                var frame = (AVFrame*)frames[i];
                var rows = boxes.Slice(i * queries * columns, queries * columns);

                results[i] = _descriptor.BoxFormat == BoxFormat.PixelCorners
                    ? DecodeRows(rows, columns, frame->width, frame->height)
                    : Decode(rows, scores.Slice(i * queries * classes, queries * classes), classes, frame->width, frame->height);
            }

            return results;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session.Dispose();
            _runOptions.Dispose();

            ffmpeg.sws_freeContext(_scaler);
            _scaler = null;

            NativeMemory.AlignedFree(_input);
            _input = null;
            _capacity = 0;
        }
    }

    /// <summary>
    /// A missing provider library throws from the append rather than falling back; only an
    /// unsupported operator falls back, per node, which is a different mechanism (research
    /// section 2). So: try, catch, and say which one won.
    /// </summary>
    private static string AppendBestProvider(SessionOptions options, ILogger logger)
    {
        try
        {
            options.AppendExecutionProvider_CUDA();
            return "CUDA";
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogInformation("CUDA execution provider is not available here, using the processor: {Reason}", ex.Message);
            return "CPU";
        }
    }

    /// <summary>
    /// A YOLO export self-describes: <c>metadata_props</c> carries <c>names</c> and <c>imgsz</c>,
    /// so the runner reads them instead of being configured (detection-plan.md D4). RF-DETR's
    /// export carries neither, and that asymmetry is real: no metadata means the descriptor's
    /// table stands, not that something is wrong.
    ///
    /// <c>names</c> is a <em>Python dict literal</em> — <c>{0: 'person', 1: 'bicycle', ...}</c>,
    /// bare integer keys and single quotes — so it is a regex and not <c>JsonSerializer</c>
    /// (models/README.md, "Embedded metadata").
    /// </summary>
    /// <returns>The embedded table, or null when the file does not carry one.</returns>
    private IReadOnlyDictionary<int, string>? ReadEmbeddedMetadata(ILogger logger)
    {
        var metadata = _session.ModelMetadata.CustomMetadataMap;

        // imgsz is "[640, 640]". It says the same thing the input tensor's shape does, and the two
        // disagreeing means the file is not what it claims, which is worth refusing over.
        if (metadata.TryGetValue("imgsz", out var imgsz))
        {
            var sizes = EmbeddedInteger().Matches(imgsz).Select(m => int.Parse(m.Value)).ToArray();

            if (sizes.Length != 2 || sizes[0] != _descriptor.InputSize || sizes[1] != _descriptor.InputSize)
            {
                throw new InvalidOperationException(
                    $"The model's own imgsz is {imgsz}, not [{_descriptor.InputSize}, {_descriptor.InputSize}]; "
                    + "the descriptor's geometry and the file disagree.");
            }
        }

        // The three output contracts D4 warns about are decided by flags frozen at export, and this
        // is the one that is visible: end2end says the one-to-one head is active, which is why
        // NeedsNms is false and why the 300 rows are objects rather than 8400 anchors. A file
        // exported the other way has a differently shaped output0 and would decode to nonsense
        // quietly, so it is refused here rather than detected from a shape we have never seen.
        if (metadata.TryGetValue("end2end", out var end2end)
            && !_descriptor.NeedsNms
            && !end2end.Equals("True", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The model says end2end={end2end}, so its head emits anchors needing suppression, "
                + "and this descriptor says none is needed. Re-export with the one-to-one head.");
        }

        if (!metadata.TryGetValue("names", out var names))
        {
            logger.LogInformation("The model carries no class names of its own; using the descriptor's table of {Count}.", _descriptor.Classes.Count);
            return null;
        }

        var embedded = EmbeddedName().Matches(names).ToDictionary(m => int.Parse(m.Groups[1].Value), m => m.Groups[2].Value);

        if (embedded.Count == 0)
        {
            throw new InvalidOperationException($"The model's 'names' metadata parsed to nothing: {names}");
        }

        logger.LogInformation("Class names read from the model's own metadata: {Count}.", embedded.Count);

        return embedded;
    }

    [GeneratedRegex(@"\d+")]
    private static partial Regex EmbeddedInteger();

    /// <summary><c>0: 'person'</c>. Single-quoted, so an apostrophe would be backslash-escaped; none of COCO's are.</summary>
    [GeneratedRegex(@"(\d+)\s*:\s*'((?:[^'\\]|\\.)*)'")]
    private static partial Regex EmbeddedName();

    /// <summary>The file matches the descriptor: names exist, input is uint8 NHWC at the canvas size.</summary>
    private void VerifyContract()
    {
        if (!_session.InputMetadata.TryGetValue(_descriptor.InputName, out var input))
        {
            throw new InvalidOperationException($"The model has no input named '{_descriptor.InputName}'; it has {string.Join(", ", _session.InputMetadata.Keys)}.");
        }

        foreach (var name in _outputNames)
        {
            if (!_session.OutputMetadata.ContainsKey(name))
            {
                throw new InvalidOperationException($"The model has no output named '{name}'; it has {string.Join(", ", _session.OutputMetadata.Keys)}.");
            }
        }

        var size = _descriptor.InputSize;
        var dimensions = input.Dimensions;

        // Batch is dimension 0 and is allowed to be anything, including fixed; a fixed batch
        // fails at Run with the runtime's own shape error when a larger batch arrives.
        if (input.ElementDataType != TensorElementType.UInt8
            || dimensions.Length != 4
            || dimensions[1] != size
            || dimensions[2] != size
            || dimensions[3] != Channels)
        {
            throw new InvalidOperationException(
                $"Input '{_descriptor.InputName}' is {input.ElementDataType} [{string.Join(", ", dimensions)}], "
                + $"not uint8 [batch, {size}, {size}, {Channels}]; the descriptor and the file disagree.");
        }
    }

    private void EnsureCapacity(int frames)
    {
        if (frames <= _capacity)
        {
            return;
        }

        NativeMemory.AlignedFree(_input);
        _input = (byte*)NativeMemory.AlignedAlloc((nuint)(frames * _frameBytes), 64);
        _capacity = frames;
    }

    /// <summary>
    /// The frame onto its place on the canvas, converting pixel format on the way. The target
    /// plane points into the input buffer, so swscale writes the model's bytes directly.
    /// </summary>
    private void Place(AVFrame* frame, byte* canvas)
    {
        var (left, top, width, height) = _descriptor.Geometry.Place(frame->width, frame->height);

        // The cached context is reused while the source keeps its size and format, which a
        // stream does; sws_scale_frame was tried and left the destination untouched when source
        // and destination matched, so the plain call with explicit planes is used instead.
        _scaler = ffmpeg.sws_getCachedContext(
            _scaler,
            frame->width,
            frame->height,
            (AVPixelFormat)frame->format,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_RGB24,
            (int)Flags,
            null,
            null,
            null);

        if (_scaler is null)
        {
            throw new InvalidOperationException($"swscale cannot convert a {frame->width}x{frame->height} frame of format {frame->format}.");
        }

        for (var plane = 0u; plane < 8; plane++)
        {
            _source[plane] = frame->data[plane];
            _sourceStride[plane] = frame->linesize[plane];
        }

        // The padding around the placement, when the geometry leaves any: 114 everywhere, then
        // swscale writes the picture over the middle of it. The whole canvas rather than the four
        // margins because a 1.2 MB fill is microseconds against a model call of hundreds of
        // milliseconds, and the margins are four rectangles to get wrong.
        if (width != _descriptor.InputSize || height != _descriptor.InputSize)
        {
            NativeMemory.Fill(canvas, (nuint)_frameBytes, _padValue);
        }

        _target[0] = canvas + top * _stride + left * Channels;

        ffmpeg.sws_scale(_scaler, _source, _sourceStride, 0, frame->height, _target, _targetStride);
    }

    /// <summary>
    /// The reference decode is sigmoid, flatten queries x classes, keep the best 300
    /// (models/README.md, "Decode"); with a threshold that is every (query, class) above it, and
    /// the same query may appear under two classes, which is intended (multi-label).
    /// </summary>
    private VmtiDetection[] Decode(ReadOnlySpan<float> boxes, ReadOnlySpan<float> scores, int classes, int frameWidth, int frameHeight)
    {
        var kept = new List<VmtiDetection>();

        for (var index = 0; index < scores.Length; index++)
        {
            var raw = scores[index];
            if (raw <= _cutoff || !Classes.TryGetValue(index % classes, out var name))
            {
                continue;
            }

            var query = index / classes;
            var score = _descriptor.ScoresAreLogits ? 1 / (1 + MathF.Exp(-raw)) : raw;

            var box = boxes.Slice(query * 4, 4);

            kept.Add(_descriptor.Geometry.ToFrameNormalised(
                kept.Count + 1,
                (box[0], box[1], box[2], box[3]),
                frameWidth,
                frameHeight) with
            {
                ConfidencePercent = (int)Math.Round(score * 100),
                OntologyClass = name,
            });
        }

        return [.. kept];
    }

    /// <summary>
    /// <see cref="BoxFormat.PixelCorners"/>: one row per query, <c>x1, y1, x2, y2, score,
    /// classId</c>, corners already in canvas pixels so the geometry's own inverse takes them
    /// straight. No sigmoid — the head's scores are probabilities — and no suppression, because
    /// YOLO26's one-to-one head emits one row per object (models/README.md, "NMS is already in the
    /// graph"). The rows are score-sorted, so the first one under the threshold ends the frame.
    /// </summary>
    private VmtiDetection[] DecodeRows(ReadOnlySpan<float> rows, int columns, int frameWidth, int frameHeight)
    {
        var kept = new List<VmtiDetection>();

        for (var row = 0; row + columns <= rows.Length; row += columns)
        {
            var score = rows[row + 4];

            if (score <= _cutoff)
            {
                break;
            }

            if (!Classes.TryGetValue((int)rows[row + 5], out var name))
            {
                continue;
            }

            kept.Add(_descriptor.Geometry.ToFrame(
                kept.Count + 1,
                (rows[row], rows[row + 1], rows[row + 2], rows[row + 3]),
                frameWidth,
                frameHeight) with
            {
                ConfidencePercent = (int)Math.Round(score * 100),
                OntologyClass = name,
            });
        }

        return [.. kept];
    }
}
