using System.Runtime.InteropServices;
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
public sealed unsafe class OnnxDetector : IDetector, IDisposable
{
    private const int Channels = 3;

    /// <summary>
    /// One thread pool for the process rather than one per session, which is the default and
    /// fights itself once a service holds several sessions (research section 3, "Threads").
    /// Zero threads means ORT's own default, one per physical core; that is the knob to turn.
    /// Spinning between operators is off: it buys latency on a dedicated box and costs every
    /// decoder thread sharing this one.
    /// </summary>
    private static readonly Lazy<OrtEnv> Environment = new(() =>
    {
        if (OrtEnv.IsCreated)
        {
            return OrtEnv.Instance();
        }

        var options = new EnvironmentCreationOptions
        {
            logId = "storagedemo",
            threadOptions = new OrtThreadingOptions { GlobalIntraOpNumThreads = 0, GlobalInterOpNumThreads = 1, GlobalSpinControl = false },
        };

        return OrtEnv.CreateInstanceWithOptions(ref options);
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
            throw new NotSupportedException("Non-maximum suppression is not built; it arrives with the YOLO descriptor (detection-plan.md D4).");
        }

        FfmpegLibrary.EnsureLoaded();

        _descriptor = descriptor;
        _cutoff = descriptor.ScoresAreLogits ? MathF.Log(threshold / (1 - threshold)) : threshold;
        _frameBytes = descriptor.InputSize * descriptor.InputSize * Channels;
        _inputNames = [descriptor.InputName];
        _outputNames = [descriptor.BoxesOutput, descriptor.ScoresOutput];

        _ = Environment.Value;

        using var options = new SessionOptions();
        options.DisablePerSessionThreads();
        Provider = AppendBestProvider(options, logger);
        _session = new InferenceSession(modelPath, options);

        VerifyContract();

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

            var boxes = outputs[0].GetTensorDataAsSpan<float>();
            var scores = outputs[1].GetTensorDataAsSpan<float>();

            // (batch, queries, classes): the counts are the tensor's, not the descriptor's.
            var shape = outputs[1].GetTensorTypeAndShape().Shape;
            var queries = (int)shape[1];
            var classes = (int)shape[2];

            var results = new VmtiDetection[frames.Length][];

            for (var i = 0; i < frames.Length; i++)
            {
                var frame = (AVFrame*)frames[i];

                results[i] = Decode(
                    boxes.Slice(i * queries * 4, queries * 4),
                    scores.Slice(i * queries * classes, queries * classes),
                    classes,
                    frame->width,
                    frame->height);
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

        // Any padding around the placement is left as it was; the letterbox fill is D4's, with
        // the descriptor that needs it.
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
            if (raw <= _cutoff || !_descriptor.Classes.TryGetValue(index % classes, out var name))
            {
                continue;
            }

            var query = index / classes;
            var score = _descriptor.ScoresAreLogits ? 1 / (1 + MathF.Exp(-raw)) : raw;

            // BoxFormat.CentreNormalised is the only layout; YOLO's canvas-pixel xywh adds a branch here.
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
}
