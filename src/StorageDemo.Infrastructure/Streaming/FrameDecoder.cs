using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// How often a frame subscriber needs a picture, as frames per second. Two values are special:
/// <see cref="Keyframes"/> (zero) wants one picture per position a decoder can start from, which is
/// what a preview needs, and <see cref="Everything"/> wants every frame, which nothing asks for yet.
/// Anything in between is what a detector asks for, and the decoder honours it at the lowest cost
/// it can: keyframes alone when they arrive at least that often, since a keyframe decodes
/// standalone, and everything otherwise, since a predicted frame needs everything since the last
/// keyframe and there is no cheaper way to reach it.
/// </summary>
public readonly record struct DecodeRate(double FramesPerSecond)
{
    public static readonly DecodeRate Keyframes = new(0);

    public static readonly DecodeRate Everything = new(double.PositiveInfinity);

    public static DecodeRate PerSecond(double framesPerSecond) => new(Math.Max(0, framesPerSecond));

    public bool IsKeyframes => FramesPerSecond <= 0;

    public bool IsEverything => double.IsPositiveInfinity(FramesPerSecond);
}

/// <summary>Receives one decoded picture, as a pointer to libav's own frame.</summary>
public delegate void FrameHandler(IntPtr frame);

/// <summary>
/// The frame tier: one decoder, itself a packet subscriber, republishing pictures.
///
/// This is the seam detection, tracking and KLV extraction attach to later, and what makes it worth
/// having now is what it costs when nothing is attached. Exactly one decode exists however many
/// things want pictures, and consumers that only move bytes never pay for it at all. A detector
/// raises the decode rate by asking for it rather than getting it by default.
///
/// The decoder decodes at the highest rate anybody asked for and hands each subscriber only the
/// pictures its own rate is due, so a detector at one a second beside a preview at keyframes costs
/// one decode per keyframe and one detection per second, never more of either.
/// </summary>
public sealed class FrameDecoder(StreamHub hub, ILogger logger) : IDisposable
{
    private readonly Lock _gate = new();

    private Subscriber[] _subscribers = [];

    /// <summary>The rate actually decoded at, which is the highest anybody asked for.</summary>
    public DecodeRate Rate => _subscribers.Length == 0
        ? DecodeRate.Keyframes
        : new DecodeRate(_subscribers.Max(subscriber => subscriber.Rate.FramesPerSecond));

    public IDisposable Subscribe(DecodeRate rate, FrameHandler handler)
    {
        var subscriber = new Subscriber(rate, handler);

        lock (_gate)
        {
            _subscribers = [.. _subscribers, subscriber];
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _subscribers = [.. _subscribers.Where(existing => !ReferenceEquals(existing, subscriber))];
            }
        });
    }

    /// <summary>
    /// Runs until the hub closes or the token is cancelled.
    ///
    /// It waits for a layout rather than being told about one, and starts again when the layout
    /// changes, which is what a reconnect with a reconfigured encoder produces. Polling for that is
    /// smaller than an event, and the wait only ever happens before the first packet and once per
    /// reconnect.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !hub.Closed)
            {
                var layout = hub.Layout;

                if (layout is null)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                    continue;
                }

                await DecodeAsync(layout, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The decoder for '{Name}' stopped", hub.Name);
        }
    }

    private async Task DecodeAsync(StreamLayout layout, CancellationToken cancellationToken)
    {
        // Small and skip-to-live: a decoder that falls behind wants the newest picture, not the
        // backlog. Nothing downstream of it is worse off for a gap.
        using var subscription = hub.Subscribe(
            capacity: 240,
            OverflowPolicy.SkipToLive,
            streamIndexes: layout.VideoIndex >= 0 ? [layout.VideoIndex] : [],
            preroll: 0);

        using var decoder = VideoDecoder.Open(layout, hub.Name, logger);

        if (decoder is null)
        {
            // No picture in this stream, or no decoder for it. Wait rather than spin: a reconnect
            // could yet bring something decodable.
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            return;
        }

        var secondsPerTick = ffmpeg.av_q2d(layout.TimeBase(layout.VideoIndex));

        // The keyframe interval is measured off the stream rather than configured: it is the
        // sender's, and it decides whether keyframes alone satisfy the rate asked for.
        long? lastKeyframePts = null;
        var keyframeInterval = 0d;

        // Set once a packet has been skipped, and cleared by the next keyframe. A decoder handed a
        // predicted frame whose references it never saw produces garbage rather than an error, so
        // after any skip the only honest place to resume is a keyframe.
        var awaitingKeyframe = false;

        await foreach (var media in subscription.Packets.ReadAllAsync(cancellationToken))
        {
            if (!ReferenceEquals(hub.Layout, layout))
            {
                // The encoder came back as something else; this decoder is for the old shape.
                return;
            }

            var rate = Rate;

            if (media.IsKeyframe)
            {
                if (lastKeyframePts is { } previous && media.Pts > previous)
                {
                    keyframeInterval = (media.Pts - previous) * secondsPerTick;
                }

                lastKeyframePts = media.Pts;
                awaitingKeyframe = false;
            }
            else if (rate.IsKeyframes || awaitingKeyframe || (!rate.IsEverything && KeyframesSuffice(rate, keyframeInterval)))
            {
                awaitingKeyframe = true;
                continue;
            }

            decoder.Decode(media, frame => Publish(frame, secondsPerTick));
        }
    }

    /// <summary>
    /// Whether keyframes arrive at least as often as the rate asks for. Until an interval has been
    /// measured, which takes two keyframes, they are assumed to: one keyframe interval of fewer
    /// pictures than asked is the cheaper mistake, and it is made once per connection.
    /// </summary>
    private static bool KeyframesSuffice(DecodeRate rate, double keyframeInterval)
        => keyframeInterval <= 0 || rate.FramesPerSecond * keyframeInterval <= 1.0001;

    private unsafe void Publish(IntPtr frame, double secondsPerTick)
    {
        var pts = ((AVFrame*)frame)->pts;
        var seconds = pts == ffmpeg.AV_NOPTS_VALUE ? double.NaN : pts * secondsPerTick;

        foreach (var subscriber in _subscribers)
        {
            if (subscriber.Due(seconds))
            {
                subscriber.Handler(frame);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _subscribers = [];
        }
    }

    private sealed class Subscriber(DecodeRate rate, FrameHandler handler)
    {
        private double _nextDue = double.NegativeInfinity;

        public DecodeRate Rate { get; } = rate;

        public FrameHandler Handler { get; } = handler;

        /// <summary>
        /// A keyframe subscriber gets every picture the decoder produced: when something else has
        /// raised the rate it simply sees more of them, which is free, and filtering back down
        /// would be work for nobody's benefit. A rated subscriber gets the first picture at or
        /// past its next due time, measured on the stream's own clock so a decoder that falls
        /// behind does not bunch pictures up when it catches up.
        /// </summary>
        public bool Due(double seconds)
        {
            if (Rate.IsKeyframes || Rate.IsEverything || double.IsNaN(seconds))
            {
                return true;
            }

            var interval = 1 / Rate.FramesPerSecond;

            // The clock went backwards, which a reconnect does: start again from here rather than
            // waiting for the stream to reach a time it may never see again.
            if (seconds < _nextDue - interval)
            {
                _nextDue = seconds;
            }

            if (seconds < _nextDue)
            {
                return false;
            }

            _nextDue = seconds + interval;

            return true;
        }
    }

    private sealed class Subscription(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
