using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>Why a feed stopped, which is what decides whether the stream waits or ends.</summary>
public enum DemuxOutcome
{
    /// <summary>The sender went away or fell silent. The stream becomes interrupted, not gone.</summary>
    FeedEnded,

    /// <summary>The transport never produced a readable stream at all.</summary>
    NeverStarted,

    /// <summary>The service asked it to stop.</summary>
    Stopped,
}

/// <summary>
/// Reads one accepted connection and feeds one hub. This is the demultiplexing half of what used
/// to be a single remux loop; the multiplexing half now belongs to each consumer that writes bytes.
///
/// It takes an already-open transport rather than a URL, because the accept happened on the
/// listener's thread and that thread had to move on. libav is told the container is MPEG-TS
/// rather than left to probe it: this is a contribution ingest, and probing costs a read before
/// the first packet reaches anybody.
/// </summary>
public sealed unsafe class StreamDemuxer(
    IOptions<LiveOptions> options,
    ILogger<StreamDemuxer> logger)
{
    private readonly LiveOptions _options = options.Value;

    /// <summary>
    /// How long libav may spend working out what is arriving, and how much it may read doing it.
    ///
    /// This is dead time between a camera connecting and its stream being on air, which for a
    /// service whose purpose is being live is the number that matters after a reconnect. libav's
    /// own defaults are five seconds and five megabytes; MPEG-TS repeats its tables every hundred
    /// milliseconds, so far less is enough for a source that presents everything at once.
    /// </summary>
    private void LimitProbe(AVDictionary** options)
    {
        ffmpeg.av_dict_set(
            options,
            "analyzeduration",
            ((long)(_options.ProbeSeconds * 1_000_000)).ToString(),
            0);

        ffmpeg.av_dict_set(options, "probesize", _options.ProbeBytes.ToString(), 0);
    }

    /// <summary>
    /// Runs until the feed stops or the token is cancelled, publishing every packet to the hub.
    /// Takes ownership of <paramref name="transport"/> and closes it on the way out.
    /// </summary>
    public DemuxOutcome Run(AVIOContext* transport, StreamHub hub, CancellationToken cancellationToken)
    {
        var format = ffmpeg.avformat_alloc_context();

        if (format is null)
        {
            ffmpeg.avio_closep(&transport);
            return DemuxOutcome.NeverStarted;
        }

        format->pb = transport;

        // Tells avformat the transport is ours: avformat_close_input leaves it alone, and closing
        // it is this method's job.
        format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;

        AVDictionary* options = null;

        try
        {
            LimitProbe(&options);

            // The container is known rather than probed. This is a contribution ingest, and
            // guessing the format costs a read before the first packet can reach anybody.
            if (ffmpeg.avformat_open_input(&format, null, ffmpeg.av_find_input_format("mpegts"), &options) < 0)
            {
                logger.LogWarning("'{Name}' connected but sent nothing readable", hub.Name);
                return DemuxOutcome.NeverStarted;
            }

            return Read(format, hub, cancellationToken);
        }
        finally
        {
            ffmpeg.av_dict_free(&options);

            if (format is not null)
            {
                ffmpeg.avformat_close_input(&format);
            }

            // Ours to close, because of AVFMT_FLAG_CUSTOM_IO above.
            if (transport is not null)
            {
                ffmpeg.avio_closep(&transport);
            }
        }
    }

    /// <summary>
    /// The manual path: this replica opens the input itself, for a protocol that cannot name
    /// itself and so could never have arrived at a listening port. Everything after the open is the
    /// same, which is the point - once a demultiplexer exists the two are indistinguishable.
    /// </summary>
    public DemuxOutcome Run(
        string url,
        IReadOnlyDictionary<string, string>? inputOptions,
        StreamHub hub,
        CancellationToken cancellationToken)
    {
        AVFormatContext* format = null;
        AVDictionary* options = null;

        try
        {
            LimitProbe(&options);

            foreach (var (key, value) in inputOptions ?? new Dictionary<string, string>())
            {
                ffmpeg.av_dict_set(&options, key, value, 0);
            }

            if (ffmpeg.avformat_open_input(&format, url, null, &options) < 0)
            {
                logger.LogWarning("Nothing arrived on {Url} for '{Name}'", url, hub.Name);
                return DemuxOutcome.NeverStarted;
            }

            return Read(format, hub, cancellationToken);
        }
        finally
        {
            ffmpeg.av_dict_free(&options);

            if (format is not null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }
    }

    private DemuxOutcome Read(AVFormatContext* format, StreamHub hub, CancellationToken cancellationToken)
    {
        AVPacket* packet = null;

        try
        {
            if (ffmpeg.avformat_find_stream_info(format, null) < 0)
            {
                logger.LogWarning("'{Name}' carries no stream information", hub.Name);
                return DemuxOutcome.NeverStarted;
            }

            var layout = StreamLayout.From(format);

            if (!hub.Adopt(layout))
            {
                // The encoder was reconfigured while it was away. Anything writing a file has to
                // close it: a container whose codec configuration changes halfway is not something
                // that will reliably play.
                logger.LogInformation(
                    "'{Name}' came back with a different layout ({Layout}), so its buffer starts fresh",
                    hub.Name,
                    layout.Describe());
            }

            packet = ffmpeg.av_packet_alloc();

            return packet is null
                ? DemuxOutcome.NeverStarted
                : Pump(format, packet, hub, cancellationToken);
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }
        }
    }

    private DemuxOutcome Pump(
        AVFormatContext* format,
        AVPacket* packet,
        StreamHub hub,
        CancellationToken cancellationToken)
    {
        var layout = hub.Layout!;
        var reference = layout.ReferenceTimeBase;
        var lastReferencePts = 0L;

        // ponytail: cancellation is noticed between reads rather than during one, so a shutdown
        // can wait out the transport's read timeout. An AVIOInterruptCB would cut that short;
        // worth adding if a slow shutdown ever matters.
        while (!cancellationToken.IsCancellationRequested)
        {
            var read = ffmpeg.av_read_frame(format, packet);

            if (read < 0)
            {
                // The sender went away, or nothing arrived within the transport's read timeout.
                // Either way this feed has stopped; whether the stream has is not decided here.
                return DemuxOutcome.FeedEnded;
            }

            try
            {
                if (packet->stream_index < 0 || packet->stream_index >= layout.Count)
                {
                    continue;
                }

                var data = new byte[packet->size];
                Marshal.Copy((IntPtr)packet->data, data, 0, packet->size);

                if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
                {
                    // Every stream carries its own clock; the buffer measures itself on one, so
                    // each packet is placed on the reference stream's scale as it arrives.
                    lastReferencePts = ffmpeg.av_rescale_q(
                        packet->pts,
                        layout.TimeBase(packet->stream_index),
                        reference);
                }

                hub.Publish(
                    new MediaPacket(
                        packet->stream_index,
                        data,
                        packet->pts,
                        packet->dts,
                        packet->duration,
                        (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0),
                    lastReferencePts);
            }
            finally
            {
                ffmpeg.av_packet_unref(packet);
            }
        }

        return DemuxOutcome.Stopped;
    }
}
