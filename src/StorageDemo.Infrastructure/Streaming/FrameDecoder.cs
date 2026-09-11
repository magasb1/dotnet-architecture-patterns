using Microsoft.Extensions.Logging;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>How often a frame subscriber needs a picture.</summary>
public enum DecodeRate
{
    /// <summary>
    /// One picture per position a decoder can start from, which is what a preview needs and what
    /// a detector would raise. Keyframes decode standalone, so this costs one decode per segment.
    /// </summary>
    Keyframes,

    /// <summary>Everything. Nothing asks for this yet; detection or tracking would.</summary>
    Everything,
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
/// Today the only subscriber is the harvester and the only rate is keyframes, which is one decode
/// per keyframe interval for a preview that needs one picture every couple of seconds.
/// </summary>
public sealed class FrameDecoder(StreamHub hub, ILogger logger) : IDisposable
{
    private readonly Lock _gate = new();

    private (DecodeRate Rate, FrameHandler Handler)[] _subscribers = [];

    /// <summary>The rate actually decoded at, which is the highest anybody asked for.</summary>
    public DecodeRate Rate => _subscribers.Any(subscriber => subscriber.Rate == DecodeRate.Everything)
        ? DecodeRate.Everything
        : DecodeRate.Keyframes;

    public IDisposable Subscribe(DecodeRate rate, FrameHandler handler)
    {
        lock (_gate)
        {
            _subscribers = [.. _subscribers, (rate, handler)];
        }

        return new Subscription(() =>
        {
            lock (_gate)
            {
                _subscribers = [.. _subscribers.Where(existing => existing.Handler != handler)];
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

        await foreach (var media in subscription.Packets.ReadAllAsync(cancellationToken))
        {
            if (!ReferenceEquals(hub.Layout, layout))
            {
                // The encoder came back as something else; this decoder is for the old shape.
                return;
            }

            if (Rate == DecodeRate.Keyframes && !media.IsKeyframe)
            {
                continue;
            }

            decoder.Decode(media, Publish);
        }
    }

    private void Publish(IntPtr frame)
    {
        foreach (var (rate, handler) in _subscribers)
        {
            // Everyone gets every picture the decoder produced. A subscriber asking for keyframes
            // while something else has raised the rate simply sees more of them, which is free;
            // filtering it back down would be work for nobody's benefit.
            _ = rate;

            handler(frame);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _subscribers = [];
        }
    }

    private sealed class Subscription(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
