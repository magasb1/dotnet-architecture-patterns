using System.Net.Http.Json;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Worker;

/// <summary>
/// One decoded picture waiting for the detector, cloned out of the decoder so it survives the
/// callback that produced it. A clone is a reference, not a copy: libav's decoder frames are
/// reference counted, so this costs a few dozen bytes and the frame's buffer stays alive until
/// the last reference is freed.
/// </summary>
internal sealed unsafe class PendingFrame : IDisposable
{
    private AVFrame* _frame;

    private PendingFrame(StreamJob job, AVFrame* frame)
    {
        Job = job;
        _frame = frame;
        Pts = frame->pts;
        Width = frame->width;
        Height = frame->height;
    }

    public StreamJob Job { get; }
    public long QueuedAt { get; } = Stopwatch.GetTimestamp();
    public DateTimeOffset DecodedAt { get; } = DateTimeOffset.UtcNow;

    public long Pts { get; }

    public int Width { get; }

    public int Height { get; }

    public IntPtr Frame => (IntPtr)_frame;

    public static PendingFrame? Clone(StreamJob job, IntPtr frame)
    {
        var clone = ffmpeg.av_frame_clone((AVFrame*)frame);

        return clone is null ? null : new PendingFrame(job, clone);
    }

    public void Dispose()
    {
        if (_frame is not null)
        {
            var frame = _frame;
            _frame = null;
            ffmpeg.av_frame_free(&frame);
        }

    }
}

/// <summary>
/// One claimed stream: its subscription to the owner over the peer view route, demultiplexed into
/// a private hub, a <see cref="FrameDecoder"/> asked for pictures at the detection rate, and the
/// tracker that turns each detection into tracks and posts them back to the owner as a VMTI frame.
///
/// Only the decoder ever decodes, and only at the rate: a keyframe when keyframes come at least
/// that often, everything otherwise (FrameDecoder). Between two detections the tracker is stepped
/// once per video packet, from the packet timestamps, so a track's identity survives the frames
/// nobody decoded. The steps are taken when the next detection arrives rather than as the packets
/// do, which is the same motion model run at the same rate and needs no second thread on the
/// tracker; nothing reads a track between detections, because a VMTI frame is only posted on one.
///
/// Each stream retains its newest waiting frame and newest waiting result. Inference and result
/// delivery run independently, so a slow owner cannot stop inference for other streams.
/// </summary>
internal sealed class StreamJob : IAsyncDisposable
{
    private readonly string _name;
    private readonly HttpClient _http;
    private readonly StreamDemuxer _demuxer;
    private readonly ChannelWriter<StreamJob> _due;
    private readonly Channel<VmtiSample> _results = Channel.CreateBounded<VmtiSample>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private static readonly Meter Meter = new(LiveMetrics.MeterName);
    private static readonly Histogram<double> QueueTime = Meter.CreateHistogram<double>("live.detection.queue.duration", "ms");
    private static readonly Histogram<double> PostTime = Meter.CreateHistogram<double>("live.detection.post.duration", "ms");
    private static readonly Histogram<double> FrameTime = Meter.CreateHistogram<double>("live.detection.frame.duration", "ms");
    private readonly double _trackThreshold;
    private readonly ILogger _logger;
    private readonly StreamHub _hub;
    private readonly FrameDecoder _decoder;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _running;

    private readonly Lock _gate = new();
    private readonly List<long> _pendingPts = [];
    private ByteTracker? _tracker;
    private (int Width, int Height) _trackerSize;
    private DateTimeOffset _anchor;
    private long _lastPts = long.MinValue;
    private double _secondsPerTick;

    private IDisposable? _subscription;
    private double _rate;
    private readonly Lock _frameGate = new();
    private PendingFrame? _pending;
    private bool _stopped;

    public StreamJob(
        string name,
        string owner,
        double rate,
        HttpClient http,
        StreamDemuxer demuxer,
        LiveOptions live,
        ChannelWriter<StreamJob> due,
        double trackThreshold,
        ILogger logger)
    {
        _name = name;
        _http = http;
        _demuxer = demuxer;
        _due = due;
        _trackThreshold = trackThreshold;
        _logger = logger;
        Owner = owner;

        _hub = new StreamHub(name, live, logger);
        _decoder = new FrameDecoder(_hub, logger);
        Rate = rate;

        _running = Task.WhenAll(
            FeedAsync(_lifetime.Token),
            _decoder.RunAsync(_lifetime.Token),
            CountPacketsAsync(_lifetime.Token),
            PublishAsync(_lifetime.Token));
    }

    /// <summary>Where the stream's bytes are. Re-read from each listing, because a stream can move.</summary>
    public string Owner { get; set; }

    /// <summary>Detections per second. Changing it re-subscribes at the new rate.</summary>
    public double Rate
    {
        get => _rate;
        set
        {
            if (value == _rate)
            {
                return;
            }

            _rate = value;
            _subscription?.Dispose();
            _subscription = _decoder.Subscribe(DecodeRate.PerSecond(value), OnFrame);
        }
    }

    /// <summary>
    /// The subscription: the transport stream over HTTP, exactly what a relaying replica reads,
    /// into the demultiplexer on a thread of its own because libav reads synchronously. Reconnects
    /// after a beat when the feed ends, to whichever owner the listing named last.
    /// </summary>
    private async Task FeedAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var url = $"{Owner}/api/live/peer/view/{_name}";

                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("{Url} answered {Status}", url, (int)response.StatusCode);
                    }
                    else
                    {
                        using var reader = new AvioReader(await response.Content.ReadAsStreamAsync(cancellationToken));

                        // A blocking read inside libav only returns when bytes arrive, so a stream
                        // that is interrupted would hold the shutdown until it resumed. Closing the
                        // response underneath it turns the read into an error the demuxer reports.
                        using var unblock = cancellationToken.Register(response.Dispose);

                        var outcome = await Task.Factory.StartNew(
                            () => Demux(reader, cancellationToken),
                            CancellationToken.None,
                            TaskCreationOptions.LongRunning,
                            TaskScheduler.Default);

                        _logger.LogInformation("The feed of '{Name}' from {Owner} ended: {Outcome}", _name, Owner, outcome);
                    }
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not read '{Name}' from {Url}", _name, url);
                }

                await Task.Delay(DetectionWorker.Beat, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>The transport is a pointer, and a lambda cannot be unsafe; a method can.</summary>
    private unsafe DemuxOutcome Demux(AvioReader reader, CancellationToken cancellationToken)
        => _demuxer.Run(reader.Context, _hub, cancellationToken);

    /// <summary>
    /// Every video packet's timestamp, so the tracker can be stepped once per frame it never saw.
    /// Waits for a layout and starts again on a new one, the way the decoder does.
    /// </summary>
    private async Task CountPacketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_hub.Closed)
            {
                var layout = _hub.Layout;

                if (layout is null || layout.VideoIndex < 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                    continue;
                }

                _secondsPerTick = ffmpeg.av_q2d(layout.TimeBase(layout.VideoIndex));

                using var subscription = _hub.Subscribe(capacity: 240, OverflowPolicy.SkipToLive, [layout.VideoIndex]);

                await foreach (var packet in subscription.Packets.ReadAllAsync(cancellationToken))
                {
                    if (!ReferenceEquals(_hub.Layout, layout))
                    {
                        break;
                    }

                    if (packet.Pts == ffmpeg.AV_NOPTS_VALUE)
                    {
                        continue;
                    }

                    lock (_gate)
                    {
                        // Bounded, for a stream whose detections have stalled: ten thousand steps
                        // is minutes of video, and a tracker that far behind is starting over anyway.
                        if (_pendingPts.Count < 10_000)
                        {
                            _pendingPts.Add(packet.Pts);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnFrame(IntPtr frame)
    {
        var pending = PendingFrame.Clone(this, frame);
        if (pending is null) return;
        lock (_frameGate)
        {
            if (_stopped)
            {
                pending.Dispose();
                return;
            }
            var previous = _pending;
            _pending = pending;
            previous?.Dispose();
            if (previous is null && !_due.TryWrite(this))
            {
                _pending.Dispose();
                _pending = null;
            }
        }
    }

    internal PendingFrame? TakeFrame()
    {
        lock (_frameGate)
        {
            var frame = _pending;
            _pending = null;
            if (frame is not null) QueueTime.Record(Stopwatch.GetElapsedTime(frame.QueuedAt).TotalMilliseconds);
            return frame;
        }
    }

    /// <summary>
    /// The detector's boxes for one frame: step the tracker over every packet since the last
    /// detection, correct it with these, and post the tracks as a VMTI frame on the frame's own
    /// timestamp, derived from its presentation time against a wall-clock anchor taken when the
    /// tracker started.
    ///
    /// ponytail: the anchor is this worker's clock, not the encoder's. A stream carrying
    /// synchronous KLV has the encoder's clock in ST 0601 tag 2 beside a reference PTS, and a
    /// KlvExtractor on this hub would give the VMTI frame that clock exactly; add it when the
    /// consumer that pairs the two by timestamp exists.
    /// </summary>
    public void Detected(PendingFrame frame, VmtiDetection[] detections)
    {
        VmtiFrame vmti;

        lock (_gate)
        {
            var seconds = frame.Pts * _secondsPerTick;

            if (_tracker is null || _trackerSize != (frame.Width, frame.Height) || frame.Pts < _lastPts)
            {
                // First picture, a new shape, or the clock went backwards after a reconnect:
                // identities cannot carry across any of these, so the tracker starts again.
                _tracker = new ByteTracker(frame.Width, frame.Height, _trackThreshold);
                _trackerSize = (frame.Width, frame.Height);
                _anchor = frame.DecodedAt - TimeSpan.FromSeconds(seconds);
                _pendingPts.Clear();
                _lastPts = long.MinValue;
            }

            foreach (var pts in _pendingPts)
            {
                if (pts > _lastPts && pts < frame.Pts)
                {
                    _tracker.Predict(At(pts));
                }
            }

            // Packets past this frame have already arrived when the decoder is behind; they are
            // the next detection's steps, not this one's.
            _pendingPts.RemoveAll(pts => pts <= frame.Pts);
            _lastPts = frame.Pts;

            var timestamp = At(frame.Pts);

            _tracker.Update(timestamp, detections);

            vmti = new VmtiFrame(
                timestamp,
                frame.Width,
                frame.Height,
                _name,
                [.. _tracker.Tracks.Select(track => track.Box)]);
        }

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "'{Name}' frame {Frame}: {Detections} boxes, tracking [{Tracks}]",
                _name,
                _tracker.Frame,
                detections.Length,
                string.Join(", ", vmti.Detections.Select(d => $"#{d.Id} {d.OntologyClass} {d.ConfidencePercent}")));
        }

        FrameTime.Record(Stopwatch.GetElapsedTime(frame.QueuedAt).TotalMilliseconds);
        _results.Writer.TryWrite(new VmtiSample(vmti, Misb0903.Encode(vmti)));
    }

    private async Task PublishAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var sample in _results.Reader.ReadAllAsync(cancellationToken))
            {
                var started = Stopwatch.GetTimestamp();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(TimeSpan.FromSeconds(2));
                try
                {
                    using var response = await _http.PostAsJsonAsync(
                        $"{Owner}/api/live/peer/detections/{_name}", sample, deadline.Token);
                    if (!response.IsSuccessStatusCode)
                        _logger.LogWarning("The owner of '{Name}' answered {Status} to a VMTI frame", _name, (int)response.StatusCode);
                }
                catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning("The owner of '{Name}' did not accept its VMTI frame within 2 seconds; the next result replaces it", _name);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning(ex, "Could not post a VMTI frame for '{Name}'", _name);
                }
                finally
                {
                    PostTime.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private DateTimeOffset At(long pts) => _anchor + TimeSpan.FromSeconds(pts * _secondsPerTick);

    public async ValueTask DisposeAsync()
    {
        lock (_frameGate)
        {
            _stopped = true;
            _pending?.Dispose();
            _pending = null;
        }
        _results.Writer.TryComplete();
        await _lifetime.CancelAsync();
        _hub.Close();

        await Task.WhenAny(_running, Task.Delay(TimeSpan.FromSeconds(10)));

        _subscription?.Dispose();
        _decoder.Dispose();
        _hub.Dispose();
        _lifetime.Dispose();
    }
}
