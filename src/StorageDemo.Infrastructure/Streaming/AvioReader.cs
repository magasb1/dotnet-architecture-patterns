using FFmpeg.AutoGen.Abstractions;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// An ordinary .NET stream, as a libav transport.
///
/// The same trick <see cref="PacketMuxer"/> plays for writing, mirrored:
/// an <c>avio_alloc_context</c> whose callback crosses the boundary, so the demultiplexer keeps
/// taking an <c>AVIOContext*</c> and never learns that the accept was done by libsrt rather than by
/// libav.
///
/// Ownership is split, because <see cref="StreamDemuxer.Run(AVIOContext*, StreamHub, System.Threading.CancellationToken)"/>
/// already closes the context it is handed: the demuxer frees the <c>AVIOContext</c> and its buffer,
/// and this class disposes only the managed <see cref="Stream"/>.
///
/// Reads block, so this is only ever used from a thread already dedicated to one connection.
/// </summary>
public sealed unsafe class AvioReader : IDisposable
{
    /// <summary>
    /// libav's buffer between the read callback and the demuxer, the same size the muxer uses.
    ///
    /// It is also a floor: the callback hands this buffer straight to the stream underneath, and an
    /// <see cref="SrtSocketStream"/> refuses a read smaller than one SRT payload (1316 bytes by
    /// default), because libsrt delivers whole messages or nothing.
    /// </summary>
    private const int IoBufferSize = 64 * 1024;

    private readonly Stream _source;

    /// <summary>Held so the garbage collector cannot free the thunk libav reads through.</summary>
    private readonly avio_alloc_context_read_packet _read;

    private AVIOContext* _io;

    public AvioReader(Stream source)
    {
        _source = source;
        _read = OnRead;

        var buffer = (byte*)ffmpeg.av_malloc(IoBufferSize);

        _io = ffmpeg.avio_alloc_context(
            buffer,
            IoBufferSize,
            write_flag: 0,
            // Null, and the stream is reached through the captured delegate instead. avio_close
            // treats a non-null opaque as a URLContext of its own and closes it, which for a
            // pointer libav did not hand out is a crash on the way to a clean shutdown.
            opaque: null,
            read_packet: _read,
            write_packet: null,
            seek: null);

        if (_io is null)
        {
            ffmpeg.av_free(buffer);

            throw new InvalidOperationException("Could not allocate an IO context for the demuxer.");
        }

        // Not seekable. An accepted socket cannot be rewound, and a live container must not need to
        // be: MPEG-TS is chosen precisely because a receiver can join mid-stream.
        _io->seekable = 0;
    }

    /// <summary>
    /// The transport to hand to <see cref="StreamDemuxer"/>, which takes ownership of it and closes
    /// it; disposing this reader closes the underlying <see cref="Stream"/> and nothing else.
    /// </summary>
    ///
    /// <remarks>
    /// ponytail: the context leaks if it is allocated and then never handed to the demuxer. There
    /// is one caller and it always runs, so the alternative - a handover flag, or freeing here and
    /// teaching the demuxer not to - is bookkeeping for a path that does not exist. Give this class
    /// a second caller and it needs one of the two.
    /// </remarks>
    public AVIOContext* Context => _io;

    private int OnRead(void* opaque, byte* buffer, int size)
    {
        try
        {
            var read = _source.Read(new Span<byte>(buffer, size));

            // A stream that has ended reads zero forever, and libav reads zero as "nothing yet, ask
            // again", so the ending has to be spelled out or av_read_frame spins on it.
            return read > 0 ? read : ffmpeg.AVERROR_EOF;
        }
        catch (Exception)
        {
            // A sender that vanished mid-read is the common case and is not an error here; the
            // demuxer has to be told to stop either way, and it reports the feed as ended.
            //
            // EIO. The bindings expose AVERROR but not errno, so the number is spelled out.
            return ffmpeg.AVERROR(5);
        }
    }

    public void Dispose()
    {
        // The context and its buffer are the demuxer's to free, by way of avio_closep. Only the
        // pointer is dropped here, so a reader disposed twice cannot hand out a freed transport.
        _io = null;

        _source.Dispose();
    }
}
