using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Documents;

namespace StorageDemo.Infrastructure.Media;

/// <summary>
/// Probes media and renders a thumbnail by calling libav in this process, through the FFmpeg.AutoGen
/// bindings, against the libraries bundled with the application.
///
/// One open of the file answers both questions. The previous implementation launched ffprobe and
/// then ffmpeg, which meant two process starts and two reads of the same bytes.
///
/// Everything unmanaged is allocated and freed in the same method, in a finally, and no pointer
/// outlives the call. That discipline is the whole reason this file is as verbose as it is.
/// </summary>
public sealed class LibavMediaAnalyzer(
    IOptions<MediaOptions> options,
    ILogger<LibavMediaAnalyzer> logger) : IMediaAnalyzer
{
    /// <summary>swscale's flag for bilinear scaling. The bindings expose the colourspace
    /// constants but not the algorithm ones, so it is spelled out here.</summary>
    private const int Bilinear = 2;

    private readonly MediaOptions _options = options.Value;

    public bool CanAnalyze(string? contentType, string fileName)
    {
        if (!_options.Enabled)
        {
            return false;
        }

        var type = string.IsNullOrWhiteSpace(contentType) || contentType == ContentTypes.Unknown
            ? ContentTypes.Guess(fileName)
            : contentType;

        // SVG is markup, not a raster image, and libav cannot decode it.
        if (type.Contains("svg", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
            || type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MediaAnalysis> AnalyzeAsync(
        Stream content,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        FfmpegLibrary.EnsureLoaded();

        // libav seeks, and an upload does not, so the bytes land in a temp file first.
        var workingDirectory = Path.Combine(Path.GetTempPath(), "storagedemo-media");
        Directory.CreateDirectory(workingDirectory);

        var sourcePath = Path.Combine(
            workingDirectory,
            $"{Guid.NewGuid():N}{Path.GetExtension(fileName)}");

        try
        {
            await using (var temp = File.Create(sourcePath))
            {
                await content.CopyToAsync(temp, cancellationToken);
            }

            // Decoding is blocking, CPU-bound work; it does not belong on a request thread.
            return await Task.Run(() => Analyze(sourcePath), cancellationToken);
        }
        finally
        {
            try
            {
                File.Delete(sourcePath);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing an upload over.
            }
        }
    }

    private unsafe MediaAnalysis Analyze(string path)
    {
        AVFormatContext* format = null;

        var opened = ffmpeg.avformat_open_input(&format, path, null, null);
        if (opened < 0)
        {
            logger.LogDebug("libav could not open {Path}: {Error}", path, Describe(opened));
            return new MediaAnalysis(new Dictionary<string, string>(), null);
        }

        try
        {
            if (ffmpeg.avformat_find_stream_info(format, null) < 0)
            {
                logger.LogDebug("libav found no stream information in {Path}", path);
                return new MediaAnalysis(new Dictionary<string, string>(), null);
            }

            var metadata = DescribeFormat(format);
            var thumbnail = TryRenderThumbnail(format);

            return new MediaAnalysis(metadata, thumbnail);
        }
        finally
        {
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static unsafe Dictionary<string, string> DescribeFormat(AVFormatContext* format)
    {
        var metadata = new Dictionary<string, string>();

        MediaMetadataFormat.Add(
            metadata,
            "Container",
            ToString(format->iformat->long_name) ?? ToString(format->iformat->name));

        if (format->duration != ffmpeg.AV_NOPTS_VALUE && format->duration > 0)
        {
            MediaMetadataFormat.Add(
                metadata,
                "Duration",
                MediaMetadataFormat.Duration(format->duration / (double)ffmpeg.AV_TIME_BASE));
        }

        if (format->bit_rate > 0)
        {
            MediaMetadataFormat.Add(
                metadata,
                "Overall bitrate",
                MediaMetadataFormat.Bitrate(format->bit_rate));
        }

        AddTags(metadata, format->metadata, prefix: string.Empty);

        var counts = new Dictionary<string, int>();

        for (var index = 0; index < format->nb_streams; index++)
        {
            var stream = format->streams[index];
            var type = ffmpeg.av_get_media_type_string(stream->codecpar->codec_type) ?? "data";

            counts[type] = counts.GetValueOrDefault(type) + 1;

            DescribeStream(metadata, stream, type, MediaMetadataFormat.StreamLabel(type, counts[type]));
        }

        return metadata;
    }

    private static unsafe void DescribeStream(
        Dictionary<string, string> metadata,
        AVStream* stream,
        string type,
        string label)
    {
        var parameters = stream->codecpar;
        var descriptor = ffmpeg.avcodec_descriptor_get(parameters->codec_id);

        MediaMetadataFormat.Add(
            metadata,
            $"{label} codec",
            descriptor is not null
                ? ToString(descriptor->long_name) ?? ToString(descriptor->name)
                : ffmpeg.avcodec_get_name(parameters->codec_id));

        switch (type)
        {
            case "video":
                // A corrupt file can still be opened with a codec guessed from the extension, and
                // then reports no dimensions. Showing "0 x 0" is worse than showing nothing.
                if (parameters->width > 0 && parameters->height > 0)
                {
                    MediaMetadataFormat.Add(
                        metadata,
                        $"{label} dimensions",
                        MediaMetadataFormat.Dimensions(parameters->width, parameters->height));

                    AddAspectRatio(metadata, label, parameters);
                }

                MediaMetadataFormat.Add(
                    metadata,
                    $"{label} pixel format",
                    ffmpeg.av_get_pix_fmt_name((AVPixelFormat)parameters->format));

                MediaMetadataFormat.Add(
                    metadata,
                    $"{label} frame rate",
                    MediaMetadataFormat.FrameRate(stream->avg_frame_rate.num, stream->avg_frame_rate.den));

                if (stream->nb_frames > 0)
                {
                    MediaMetadataFormat.Add(metadata, $"{label} frames", stream->nb_frames.ToString());
                }

                break;

            case "audio":
                if (parameters->ch_layout.nb_channels > 0)
                {
                    MediaMetadataFormat.Add(
                        metadata,
                        $"{label} channels",
                        parameters->ch_layout.nb_channels.ToString());
                }

                if (parameters->sample_rate > 0)
                {
                    MediaMetadataFormat.Add(
                        metadata,
                        $"{label} sample rate",
                        MediaMetadataFormat.SampleRate(parameters->sample_rate));
                }

                break;

            case "subtitle":
                MediaMetadataFormat.Add(metadata, $"{label} language", Tag(stream->metadata, "language"));
                break;
        }

        if (parameters->bit_rate > 0)
        {
            MediaMetadataFormat.Add(
                metadata,
                $"{label} bitrate",
                MediaMetadataFormat.Bitrate(parameters->bit_rate));
        }

        AddTags(metadata, stream->metadata, prefix: $"{label} ");
    }

    /// <summary>Display aspect is the stored size scaled by the pixel aspect, reduced.</summary>
    private static unsafe void AddAspectRatio(
        Dictionary<string, string> metadata,
        string label,
        AVCodecParameters* parameters)
    {
        var sample = parameters->sample_aspect_ratio;
        if (sample.num == 0 || sample.den == 0)
        {
            return;
        }

        int numerator;
        int denominator;

        ffmpeg.av_reduce(
            &numerator,
            &denominator,
            parameters->width * (long)sample.num,
            parameters->height * (long)sample.den,
            1024 * 1024);

        MediaMetadataFormat.Add(
            metadata,
            $"{label} aspect ratio",
            MediaMetadataFormat.AspectRatio(numerator, denominator));
    }

    /// <summary>Embedded tags: EXIF on images, creation time and encoder on video, ID3 on audio.</summary>
    private static unsafe void AddTags(
        Dictionary<string, string> metadata,
        AVDictionary* dictionary,
        string prefix)
    {
        if (dictionary is null)
        {
            return;
        }

        AVDictionaryEntry* entry = null;

        while ((entry = ffmpeg.av_dict_get(dictionary, string.Empty, entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) is not null)
        {
            var key = ToString(entry->key);
            if (string.IsNullOrEmpty(key))
            {
                continue;
            }

            // Language already has its own row on a subtitle stream.
            if (prefix.Length > 0 && key.Equals("language", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            MediaMetadataFormat.Add(
                metadata,
                $"{prefix}{MediaMetadataFormat.Capitalise(key.Replace('_', ' '))}",
                ToString(entry->value));
        }
    }

    private static unsafe string? Tag(AVDictionary* dictionary, string key)
    {
        if (dictionary is null)
        {
            return null;
        }

        var entry = ffmpeg.av_dict_get(dictionary, key, null, 0);

        return entry is null ? null : ToString(entry->value);
    }

    private unsafe byte[]? TryRenderThumbnail(AVFormatContext* format)
    {
        AVCodec* decoder = null;
        var streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, &decoder, 0);

        if (streamIndex < 0 || decoder is null)
        {
            // Audio with no cover art, or a file with no picture in it at all.
            return null;
        }

        var stream = format->streams[streamIndex];
        AVCodecContext* codec = null;
        AVFrame* frame = null;
        AVPacket* packet = null;

        try
        {
            codec = ffmpeg.avcodec_alloc_context3(decoder);
            if (codec is null || ffmpeg.avcodec_parameters_to_context(codec, stream->codecpar) < 0)
            {
                return null;
            }

            // 0 lets libav pick a thread count; decoding one frame barely uses them either way.
            codec->thread_count = 0;

            if (ffmpeg.avcodec_open2(codec, decoder, null) < 0)
            {
                return null;
            }

            frame = ffmpeg.av_frame_alloc();
            packet = ffmpeg.av_packet_alloc();

            if (frame is null || packet is null)
            {
                return null;
            }

            // A still image has one frame, so seeking into it would land past the end.
            var seekable = format->duration != ffmpeg.AV_NOPTS_VALUE
                && format->duration > _options.VideoFrameSeconds * ffmpeg.AV_TIME_BASE;

            var moved = seekable && Seek(format, stream, streamIndex, codec);

            if (moved && Decode(format, codec, packet, frame, streamIndex))
            {
                return Encode(frame);
            }

            if (moved)
            {
                // The seek landed somewhere undecodable, so start over from the beginning.
                // Only rewind if we actually moved: a single-image demuxer does not take kindly
                // to being seeked at all, and rewinding one left it with nothing to read.
                ffmpeg.av_seek_frame(format, streamIndex, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(codec);
            }

            if (Decode(format, codec, packet, frame, streamIndex))
            {
                return Encode(frame);
            }

            logger.LogDebug("No frame could be decoded for a thumbnail");

            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Thumbnail rendering failed");
            return null;
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (frame is not null)
            {
                ffmpeg.av_frame_free(&frame);
            }

            if (codec is not null)
            {
                ffmpeg.avcodec_free_context(&codec);
            }
        }
    }

    private unsafe bool Seek(
        AVFormatContext* format,
        AVStream* stream,
        int streamIndex,
        AVCodecContext* codec)
    {
        var timestamp = (long)(_options.VideoFrameSeconds / ffmpeg.av_q2d(stream->time_base));

        if (ffmpeg.av_seek_frame(format, streamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
        {
            return false;
        }

        // Anything buffered belongs to the old position.
        ffmpeg.avcodec_flush_buffers(codec);

        return true;
    }

    /// <summary>Reads until the decoder hands back one frame from the stream we care about.</summary>
    private static unsafe bool Decode(
        AVFormatContext* format,
        AVCodecContext* codec,
        AVPacket* packet,
        AVFrame* frame,
        int streamIndex)
    {
        while (ffmpeg.av_read_frame(format, packet) >= 0)
        {
            try
            {
                if (packet->stream_index != streamIndex)
                {
                    continue;
                }

                if (ffmpeg.avcodec_send_packet(codec, packet) < 0)
                {
                    continue;
                }

                if (ffmpeg.avcodec_receive_frame(codec, frame) == 0)
                {
                    return true;
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(packet);
            }
        }

        // Flush: the frame may still be sitting inside the decoder at end of file.
        ffmpeg.avcodec_send_packet(codec, null);

        return ffmpeg.avcodec_receive_frame(codec, frame) == 0;
    }

    /// <summary>Scales the frame into the box and encodes it as a single JPEG.</summary>
    private unsafe byte[]? Encode(AVFrame* source)
    {
        if (source->width <= 0 || source->height <= 0)
        {
            return null;
        }

        var encoder = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
        if (encoder is null)
        {
            logger.LogWarning("The bundled libav has no MJPEG encoder, so thumbnails are unavailable");
            return null;
        }

        // Ask the encoder what it accepts rather than assuming. JPEG's full-range 4:2:0 is what
        // every mjpeg encoder offers first, but reading it beats hardcoding a deprecated constant.
        var target = PreferredPixelFormat(encoder);

        var (width, height) = Fit(source->width, source->height, _options.ThumbnailSize);

        AVFrame* scaled = null;
        SwsContext* scaler = null;
        AVCodecContext* codec = null;
        AVPacket* packet = null;

        try
        {
            scaled = ffmpeg.av_frame_alloc();
            if (scaled is null)
            {
                return null;
            }

            scaled->format = (int)target;
            scaled->width = width;
            scaled->height = height;

            if (ffmpeg.av_frame_get_buffer(scaled, 32) < 0)
            {
                return null;
            }

            scaler = ffmpeg.sws_getContext(
                source->width,
                source->height,
                (AVPixelFormat)source->format,
                width,
                height,
                target,
                Bilinear,
                null,
                null,
                null);

            if (scaler is null)
            {
                return null;
            }

            ffmpeg.sws_scale(
                scaler,
                source->data.ToArray(),
                source->linesize.ToArray(),
                0,
                source->height,
                scaled->data.ToArray(),
                scaled->linesize.ToArray());

            codec = ffmpeg.avcodec_alloc_context3(encoder);
            if (codec is null)
            {
                return null;
            }

            codec->width = width;
            codec->height = height;
            codec->pix_fmt = target;

            // A single still, so the time base is a formality the encoder still requires.
            codec->time_base = new AVRational { num = 1, den = 1 };

            // Fixed quality rather than a bitrate target: one frame has no bitrate.
            codec->flags |= ffmpeg.AV_CODEC_FLAG_QSCALE;
            codec->global_quality = ffmpeg.FF_QP2LAMBDA * _options.ThumbnailQuality;

            if (ffmpeg.avcodec_open2(codec, encoder, null) < 0)
            {
                return null;
            }

            scaled->pts = 0;
            scaled->quality = codec->global_quality;

            if (ffmpeg.avcodec_send_frame(codec, scaled) < 0)
            {
                return null;
            }

            // Tell the encoder that was the only frame, so it emits the picture now.
            ffmpeg.avcodec_send_frame(codec, null);

            packet = ffmpeg.av_packet_alloc();
            if (packet is null || ffmpeg.avcodec_receive_packet(codec, packet) < 0 || packet->size <= 0)
            {
                return null;
            }

            var bytes = new byte[packet->size];
            Marshal.Copy((IntPtr)packet->data, bytes, 0, packet->size);

            return bytes;
        }
        finally
        {
            if (packet is not null)
            {
                ffmpeg.av_packet_free(&packet);
            }

            if (codec is not null)
            {
                ffmpeg.avcodec_free_context(&codec);
            }

            if (scaler is not null)
            {
                ffmpeg.sws_freeContext(scaler);
            }

            if (scaled is not null)
            {
                ffmpeg.av_frame_free(&scaled);
            }
        }
    }

    private static unsafe AVPixelFormat PreferredPixelFormat(AVCodec* encoder)
    {
        void* configs = null;
        var count = 0;

        var queried = ffmpeg.avcodec_get_supported_config(
            null,
            encoder,
            AVCodecConfig.AV_CODEC_CONFIG_PIX_FORMAT,
            0,
            &configs,
            &count);

        return queried >= 0 && configs is not null && count > 0
            ? ((AVPixelFormat*)configs)[0]
            : AVPixelFormat.AV_PIX_FMT_YUVJ420P;
    }

    /// <summary>Fits inside a square box without distorting, and keeps both sides even.</summary>
    private static (int Width, int Height) Fit(int width, int height, int box)
    {
        var scale = Math.Min(1.0, Math.Min((double)box / width, (double)box / height));

        return (Even((int)Math.Round(width * scale)), Even((int)Math.Round(height * scale)));
    }

    private static int Even(int value) => Math.Max(2, value % 2 == 0 ? value : value - 1);

    private static unsafe string? ToString(byte* value)
        => value is null ? null : Marshal.PtrToStringAnsi((IntPtr)value);

    /// <summary>Turns a libav negative return code into its message.</summary>
    private static unsafe string Describe(int error)
    {
        const int size = 256;
        var buffer = stackalloc byte[size];

        return ffmpeg.av_strerror(error, buffer, size) == 0
            ? Marshal.PtrToStringAnsi((IntPtr)buffer) ?? error.ToString()
            : error.ToString();
    }
}
