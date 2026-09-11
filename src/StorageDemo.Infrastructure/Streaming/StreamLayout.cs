using FFmpeg.AutoGen.Abstractions;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// What the streams inside one transport are: their codecs, their parameters and their clocks.
///
/// It is held apart from the demultiplexer that produced it because it outlives one. A stream
/// whose feed stops keeps its hub, its buffer and any recording alive through the grace period,
/// and everything attached to it still needs to know what it is carrying. It is also what decides
/// whether a returning encoder can be appended to or has to start a new document: a file whose
/// codec configuration changes halfway is not something anything will reliably play.
///
/// The parameters are libav's own, copied and owned here, because that is the form both the
/// muxers and the decoder want them in.
/// </summary>
public sealed unsafe class StreamLayout : IDisposable
{
    private readonly IntPtr[] _parameters;
    private readonly AVRational[] _timeBases;

    private StreamLayout(IntPtr[] parameters, AVRational[] timeBases, int videoIndex)
    {
        _parameters = parameters;
        _timeBases = timeBases;
        VideoIndex = videoIndex;
    }

    /// <summary>The stream a keyframe means something on, or -1 when there is no picture.</summary>
    public int VideoIndex { get; }

    public int Count => _parameters.Length;

    /// <summary>
    /// The clock everything is measured against. The video stream when there is one, so a segment
    /// boundary and a rollback position are on the same scale as the pictures they refer to.
    /// </summary>
    public AVRational ReferenceTimeBase => _timeBases[VideoIndex >= 0 ? VideoIndex : 0];

    public double SecondsPerTick => ffmpeg.av_q2d(ReferenceTimeBase);

    public AVRational TimeBase(int index) => _timeBases[index];

    public AVCodecParameters* Parameters(int index) => (AVCodecParameters*)_parameters[index];

    public static StreamLayout From(AVFormatContext* format)
    {
        var count = (int)format->nb_streams;
        var parameters = new IntPtr[count];
        var timeBases = new AVRational[count];
        var videoIndex = -1;

        for (var index = 0; index < count; index++)
        {
            var stream = format->streams[index];
            var copy = ffmpeg.avcodec_parameters_alloc();

            ffmpeg.avcodec_parameters_copy(copy, stream->codecpar);

            // Meaningless once these parameters are used to build a different container, and left
            // set it confuses some muxers.
            copy->codec_tag = 0;

            parameters[index] = (IntPtr)copy;
            timeBases[index] = stream->time_base;

            if (videoIndex < 0 && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                videoIndex = index;
            }
        }

        return new StreamLayout(parameters, timeBases, videoIndex);
    }

    /// <summary>
    /// Recreates these streams on a container being written, so a consumer's muxer carries the
    /// same tracks the sender presented.
    /// </summary>
    /// <returns>Input stream index to output stream index; -1 for a stream this container refused.</returns>
    public int[] ApplyTo(AVFormatContext* output)
    {
        var mapping = new int[Count];
        var next = 0;

        for (var index = 0; index < Count; index++)
        {
            var stream = ffmpeg.avformat_new_stream(output, null);

            if (stream is null || ffmpeg.avcodec_parameters_copy(stream->codecpar, Parameters(index)) < 0)
            {
                mapping[index] = -1;
                continue;
            }

            stream->time_base = _timeBases[index];
            mapping[index] = next++;
        }

        if (next == 0)
        {
            throw new InvalidOperationException("The stream carries nothing this container can hold.");
        }

        return mapping;
    }

    /// <summary>
    /// Whether a returning feed is the same shape as the one that went away.
    ///
    /// Compared field by field rather than by identity, because the encoder that comes back opened
    /// a new connection and libav built a fresh context for it. What matters is whether the bytes
    /// still fit the file already being written.
    /// </summary>
    public bool Matches(StreamLayout other)
    {
        if (Count != other.Count)
        {
            return false;
        }

        for (var index = 0; index < Count; index++)
        {
            var mine = Parameters(index);
            var theirs = other.Parameters(index);

            if (mine->codec_id != theirs->codec_id
                || mine->codec_type != theirs->codec_type
                || mine->width != theirs->width
                || mine->height != theirs->height
                || mine->format != theirs->format
                || mine->sample_rate != theirs->sample_rate
                || mine->ch_layout.nb_channels != theirs->ch_layout.nb_channels
                || mine->extradata_size != theirs->extradata_size)
            {
                return false;
            }

            // Extradata carries the decoder configuration itself: the sequence header, the SPS and
            // PPS. Two streams agreeing on codec and size but not on this are not interchangeable.
            if (mine->extradata_size > 0
                && new ReadOnlySpan<byte>(mine->extradata, mine->extradata_size)
                    .SequenceEqual(new ReadOnlySpan<byte>(theirs->extradata, theirs->extradata_size)) is false)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A short description for a log line or a client's metadata panel.</summary>
    public string Describe()
    {
        var parts = new List<string>(Count);

        for (var index = 0; index < Count; index++)
        {
            var parameters = Parameters(index);
            var codec = ffmpeg.avcodec_get_name(parameters->codec_id);

            parts.Add(parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO
                ? $"{codec} {parameters->width}x{parameters->height}"
                : codec);
        }

        return string.Join(", ", parts);
    }

    public void Dispose()
    {
        for (var index = 0; index < _parameters.Length; index++)
        {
            if (_parameters[index] == IntPtr.Zero)
            {
                continue;
            }

            var parameters = (AVCodecParameters*)_parameters[index];
            ffmpeg.avcodec_parameters_free(&parameters);

            _parameters[index] = IntPtr.Zero;
        }
    }
}
