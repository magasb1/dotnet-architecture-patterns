using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <param name="Packets">Packets copied across.</param>
/// <param name="Bytes">Payload bytes copied, excluding container overhead.</param>
public sealed record RemuxResult(long Packets, long Bytes);

/// <summary>
/// The multiplexer. It copies packets from one container into another without touching the encoded
/// frames, which is what makes it cheap enough to run per live stream: no decode, no encode, just
/// demux, restamp, mux.
///
/// Both directions of live streaming are this one loop. Ingest reads a network URL and writes a
/// file; egress reads a file and writes a network URL. The transport is only ever the scheme on
/// the URL, so MPEG-TS over UDP today and over SRT on a build with libsrt are the same code.
/// </summary>
public sealed unsafe class LibavRemuxer(ILogger<LibavRemuxer> logger)
{
    /// <param name="input">A file path, or a URL such as udp://0.0.0.0:9000 or srt://host:9000.</param>
    /// <param name="output">A file path, or a URL to send to.</param>
    /// <param name="outputFormat">
    /// Container to write, when the output name does not imply one. "mpegts" for a live transport:
    /// it needs no seeking and a receiver can join mid-stream.
    /// </param>
    /// <param name="onProgress">Called as packets are copied, for session statistics.</param>
    public RemuxResult Remux(
        string input,
        string output,
        string? outputFormat,
        IReadOnlyDictionary<string, string>? inputOptions,
        Action<long, long>? onProgress,
        CancellationToken cancellationToken)
    {
        FfmpegLibrary.EnsureLoaded();

        Require(input, forOutput: false);
        Require(output, forOutput: true);

        AVFormatContext* source = null;
        AVFormatContext* target = null;
        AVPacket* packet = null;
        AVDictionary* options = null;

        // Input stream index to output stream index. A stream we do not carry maps to -1.
        int[] mapping;

        try
        {
            foreach (var (key, value) in inputOptions ?? new Dictionary<string, string>())
            {
                ffmpeg.av_dict_set(&options, key, value, 0);
            }

            Check(ffmpeg.avformat_open_input(&source, input, null, &options), $"opening {input}");
            Check(ffmpeg.avformat_find_stream_info(source, null), "reading stream information");

            Check(
                ffmpeg.avformat_alloc_output_context2(&target, null, outputFormat, output),
                $"preparing {outputFormat ?? "output"} for {output}");

            mapping = MapStreams(source, target);

            // Write through rather than buffering. A recording that only reaches disk when the
            // muxer closes cannot be previewed or watched while it runs, which for a live stream
            // is the entire point. Costs a flush per packet, which next to a network read is noise.
            target->flush_packets = 1;

            if ((target->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
            {
                Check(ffmpeg.avio_open(&target->pb, output, ffmpeg.AVIO_FLAG_WRITE), $"opening {output}");
            }

            Check(ffmpeg.avformat_write_header(target, null), "writing the container header");

            packet = ffmpeg.av_packet_alloc();
            if (packet is null)
            {
                throw new InvalidOperationException("Could not allocate a packet.");
            }

            var packets = 0L;
            var bytes = 0L;

            while (!cancellationToken.IsCancellationRequested)
            {
                var read = ffmpeg.av_read_frame(source, packet);
                if (read < 0)
                {
                    // End of file, or the sender went away.
                    break;
                }

                try
                {
                    var outputIndex = packet->stream_index < mapping.Length
                        ? mapping[packet->stream_index]
                        : -1;

                    if (outputIndex < 0)
                    {
                        continue;
                    }

                    // Timestamps are in the input stream's time base and mean nothing in the
                    // output's. Rescaling them is the whole job of a remuxer.
                    ffmpeg.av_packet_rescale_ts(
                        packet,
                        source->streams[packet->stream_index]->time_base,
                        target->streams[outputIndex]->time_base);

                    packet->stream_index = outputIndex;
                    packet->pos = -1;

                    bytes += packet->size;
                    packets++;

                    var written = ffmpeg.av_interleaved_write_frame(target, packet);
                    if (written < 0)
                    {
                        logger.LogWarning("Dropped a packet: {Error}", Error(written));
                    }

                    onProgress?.Invoke(packets, bytes);
                }
                finally
                {
                    ffmpeg.av_packet_unref(packet);
                }
            }

            // Flushes the muxer. Skipped on a cancelled stream only if the header never went out.
            ffmpeg.av_write_trailer(target);

            return new RemuxResult(packets, bytes);
        }
        finally
        {
            if (options is not null)
            {
                ffmpeg.av_dict_free(&options);
            }

            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (target is not null)
            {
                if (target->pb is not null)
                {
                    ffmpeg.avio_closep(&target->pb);
                }

                ffmpeg.avformat_free_context(target);
            }

            if (source is not null)
            {
                ffmpeg.avformat_close_input(&source);
            }
        }
    }

    /// <summary>
    /// Recreates each input stream on the output, copying codec parameters rather than re-encoding.
    /// Streams the target container cannot carry are dropped rather than failing the whole stream.
    /// </summary>
    private int[] MapStreams(AVFormatContext* source, AVFormatContext* target)
    {
        var mapping = new int[source->nb_streams];
        var next = 0;

        for (var index = 0; index < source->nb_streams; index++)
        {
            var input = source->streams[index];
            var type = input->codecpar->codec_type;

            if (type is not (AVMediaType.AVMEDIA_TYPE_VIDEO
                or AVMediaType.AVMEDIA_TYPE_AUDIO
                or AVMediaType.AVMEDIA_TYPE_SUBTITLE))
            {
                mapping[index] = -1;
                continue;
            }

            var stream = ffmpeg.avformat_new_stream(target, null);
            if (stream is null)
            {
                mapping[index] = -1;
                continue;
            }

            if (ffmpeg.avcodec_parameters_copy(stream->codecpar, input->codecpar) < 0)
            {
                mapping[index] = -1;
                continue;
            }

            // Meaningless in the new container, and left set it confuses some muxers.
            stream->codecpar->codec_tag = 0;
            stream->time_base = input->time_base;

            mapping[index] = next++;
        }

        if (next == 0)
        {
            throw new InvalidOperationException("The input carries no stream this container can hold.");
        }

        return mapping;
    }

    private static void Require(string url, bool forOutput)
    {
        if (FfmpegLibrary.Supports(url, forOutput))
        {
            return;
        }

        var available = string.Join(
            ", ",
            forOutput ? FfmpegLibrary.OutputProtocols() : FfmpegLibrary.InputProtocols());

        throw new NotSupportedException(
            $"The loaded FFmpeg cannot use '{url}'. Available protocols: {available}. "
            + "Point Media:LibraryPath at a build that includes the one you need.");
    }

    private static void Check(int result, string what)
    {
        if (result < 0)
        {
            throw new InvalidOperationException($"libav failed {what}: {Error(result)}");
        }
    }

    private static string Error(int code)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];

        return ffmpeg.av_strerror(code, buffer, size) == 0
            ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer) ?? code.ToString()
            : code.ToString();
    }
}
