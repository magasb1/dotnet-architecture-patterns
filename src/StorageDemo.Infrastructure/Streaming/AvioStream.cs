using FFmpeg.AutoGen.Abstractions;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// An open libav transport, as an ordinary .NET stream.
///
/// It exists so that a libav transport can be read by code that only knows <see cref="Stream"/>.
/// One caller is left: relaying a viewer from the replica that owns its stream, which dials that
/// replica's consumption port with libav's SRT caller. Everything accepted on a port of ours is an
/// <see cref="SrtSocketStream"/> instead.
///
/// Reads and writes on a live transport block, so this is only ever used from a thread that is
/// already dedicated to one connection.
/// </summary>
public sealed unsafe class AvioStream(IntPtr transport, bool writable) : Stream
{
    private AVIOContext* _transport = (AVIOContext*)transport;

    public override bool CanRead => !writable && _transport is not null;

    public override bool CanWrite => writable && _transport is not null;

    public override bool CanSeek => false;

    /// <summary>
    /// True once the transport has reported an error, which for a viewer means it has gone.
    ///
    /// Worth exposing, because the muxer swallows the exception this throws: it treats a refused
    /// write as "stop writing" rather than as a failure. Without a flag the caller cannot tell a
    /// stream that ended from a viewer that left, and would re-attach to a socket nobody is
    /// listening to, forever.
    /// </summary>
    public bool Faulted { get; private set; }

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_transport is null || buffer.IsEmpty)
        {
            return 0;
        }

        fixed (byte* data = buffer)
        {
            var read = ffmpeg.avio_read(_transport, data, buffer.Length);

            if (read <= 0)
            {
                // End of stream or a broken connection; both mean nothing more is coming.
                Faulted |= _transport->error < 0;

                return 0;
            }

            return read;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_transport is null || buffer.IsEmpty)
        {
            return;
        }

        fixed (byte* data = buffer)
        {
            ffmpeg.avio_write(_transport, data, buffer.Length);
        }

        // The transport's own error, which libav reports on the context rather than by return
        // value. Without this a viewer who walked away would be written to forever.
        if (_transport->error < 0)
        {
            Faulted = true;

            throw new IOException($"The transport refused {buffer.Length} bytes.");
        }
    }

    public override void Flush()
    {
        if (_transport is not null)
        {
            ffmpeg.avio_flush(_transport);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_transport is not null)
        {
            var transport = _transport;
            _transport = null;

            ffmpeg.avio_closep(&transport);
        }

        base.Dispose(disposing);
    }
}
