using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;

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
    /// How long a detection took, in milliseconds, published on the pod's own meter
    /// (<see cref="LiveMetrics"/>) so <c>dotnet-counters monitor --counters StorageDemo.Live</c>
    /// reads it with no package and no exporter.
    ///
    /// A histogram rather than a log line, because at five a second on a thousand streams a line
    /// each is five thousand lines a second to say a number that only matters as a distribution;
    /// and because the question this answers is "is this pod slow", which is the p50 and the p99,
    /// not any single call. It is the total only: about 99 % of a detection is
    /// <c>InferenceSession.Run</c> and swscale is a quarter of one percent, so a preprocess and a
    /// postprocess series would be two instruments reporting rounding error
    /// (perf-detection.md, experiment 3).
    ///
    /// Static because there is one detector per process and the meter belongs to the process, not
    /// to the object. Two tags, both with a small fixed set of values, as this meter's rule
    /// requires: <c>provider</c> says CPU or CUDA, which is how a pod that silently fell back to
    /// the processor is visible at all, and <c>batch</c> separates the calls that carry eight
    /// frames from the ones that carry one — without it the distribution is the mixture of the two
    /// and neither mode means anything. No stream name, ever.
    /// </summary>
    private static readonly Histogram<double> Duration = new Meter(LiveMetrics.MeterName).CreateHistogram<double>(
        "live.detection.duration",
        unit: "ms",
        description: "Wall time of one detection call, by execution provider and batch size.");

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
    ///
    /// Asked once more whether swscale will give both at once — centred <em>and</em> two-tap — and
    /// the answer is no. The centred kernel is the general filter path, and that path widens its
    /// support by the downscale ratio by construction; no flag in <c>SwsFlags</c> reaches that,
    /// and <c>sws_getCachedContext</c>'s <c>param</c> is read only by the bicubic, gauss, sinc and
    /// spline branches, never by the bilinear one. Its <c>srcFilter</c>/<c>dstFilter</c> are
    /// convolved with the kernel, so they can only widen it further. Re-measured on the dog to be
    /// sure the question was asked of the right thing: 66 with these flags and 53 with
    /// SWS_BILINEAR, which is the pair already recorded above. Closing the last 0.028 means a
    /// resampler, not a flag, and perf-detection.md prices that at 0.25 % of a detection.
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

        // Asked before the options are made, and that order is load-bearing: constructing
        // SessionOptions initialises the runtime's default environment, so asking afterwards
        // always answers "somebody else made it" and every session silently runs its own thread
        // pool. Measured at 22 percent of throughput on a processor, and invisible except for a
        // warning that made no sense in a worker that is the only component in its process.
        var ours = GlobalThreadPools.Value;

        using var options = new SessionOptions();

        if (ours)
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

        Warm(logger);
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
            // Timed inside the lock: what is wanted is what a detection costs, not how long this
            // caller queued behind another one. The worker has one detect loop, so there is no
            // second caller to queue behind anyway.
            var started = Stopwatch.GetTimestamp();

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

            Duration.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("provider", Provider),
                new KeyValuePair<string, object?>("batch", frames.Length));

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

    /// <summary>
    /// <c>'dets' [-1,300,4], 'labels' [-1,300,91]</c>. The name says a descriptor and a file
    /// disagree; the shape says which file you have, which is the question anyone reading this
    /// error is actually asking. A dimension of -1 is the dynamic axis, as ORT reports it.
    /// </summary>
    private static string Shapes(IReadOnlyDictionary<string, NodeMetadata> metadata)
        => string.Join(", ", metadata.Select(node => $"'{node.Key}' [{string.Join(",", node.Value.Dimensions)}]"));

    /// <summary>The file matches the descriptor: names exist, input is uint8 NHWC at the canvas size.</summary>
    private void VerifyContract()
    {
        if (!_session.InputMetadata.TryGetValue(_descriptor.InputName, out var input))
        {
            throw new InvalidOperationException($"The model has no input named '{_descriptor.InputName}'; it has {Shapes(_session.InputMetadata)}.");
        }

        foreach (var name in _outputNames)
        {
            if (!_session.OutputMetadata.ContainsKey(name))
            {
                throw new InvalidOperationException($"The model has no output named '{name}'; it has {Shapes(_session.OutputMetadata)}.");
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

    /// <summary>
    /// One inference on a blank canvas, at construction, so whatever the provider defers to its
    /// first <c>Run</c> is paid at startup rather than by the first stream's first frame. The blank
    /// canvas decodes to nothing, so the result is dropped without looking at it.
    ///
    /// On the processor this is worth nothing and was measured saying so: the first `Run` costs
    /// what any other `Run` costs, so the whole of it is one extra inference at startup
    /// (perf-detection.md, "Warming the session"). It is here for CUDA, where cuDNN algorithm
    /// selection and kernel load happen on the first call and TensorRT's engine build is minutes.
    ///
    /// The time is logged because it is the only place the deferred cost is visible: this run
    /// against the steady-state figure the histogram then publishes is what the provider held
    /// back, and on a GPU node that difference is the one worth looking at.
    /// </summary>
    private void Warm(ILogger logger)
    {
        var started = Stopwatch.GetTimestamp();

        EnsureCapacity(1);
        NativeMemory.Fill(_input, (nuint)_frameBytes, _padValue);

        var size = _descriptor.InputSize;

        using (var input = OrtValue.CreateTensorValueWithData(
            OrtMemoryInfo.DefaultInstance,
            TensorElementType.UInt8,
            [1, size, size, Channels],
            (IntPtr)_input,
            _frameBytes))
        {
            using var _ = _session.Run(_runOptions, _inputNames, [input], _outputNames);
        }

        logger.LogInformation(
            "Session warmed on {Provider} in {Elapsed:F0} ms; the first frame pays a steady-state detection now.",
            Provider,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds);
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
