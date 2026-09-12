namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// One SRT socket, as an ordinary .NET stream.
///
/// Everything downstream of the hub keeps writing to a
/// <see cref="Stream"/> and never learns what is on the other end, so the recorder writes to a file
/// and a viewer writes to a socket through the same muxer and the same code.
///
/// Two of libsrt's rules leak through the <see cref="Stream"/> contract and cannot be hidden:
/// a read buffer smaller than the socket's payload size is refused with SRT_EINVALMSGAPI, and a
/// write larger than it is refused as a message, so writes are cut into payload-sized sends.
///
/// Reads and writes block, so this is only ever used from a thread already dedicated to one
/// connection.
/// </summary>
public sealed unsafe class SrtSocketStream : Stream
{
    /// <summary>Hands a chunk to libsrt, returning what <c>srt_sendmsg</c> returned.</summary>
    public delegate int Send(ReadOnlySpan<byte> chunk);

    /// <summary>Fills a buffer from libsrt, returning what <c>srt_recvmsg</c> returned.</summary>
    public delegate int Receive(Span<byte> buffer);

    private readonly int _socket;

    private readonly bool _writable;

    private readonly int _payloadSize;

    private readonly Send _send;

    private readonly Receive _receive;

    private bool _closed;

    public SrtSocketStream(int socket, bool writable)
        : this(socket, writable, send: null, receive: null, payloadSize: PayloadSizeOf(socket))
    {
    }

    /// <summary>
    /// The test seam. Substituting the two calls is what lets the chunking be proved on a machine
    /// with no libsrt, which is every machine until someone runs scripts/fetch-libsrt.sh.
    ///
    /// Public rather than internal only because the test project has no access to internals and
    /// giving it some would mean editing a csproj for one constructor.
    /// </summary>
    public SrtSocketStream(
        int socket,
        bool writable,
        Send? send,
        Receive? receive,
        int payloadSize = Srt.LiveDefaultPayloadSize)
    {
        _socket = socket;
        _writable = writable;
        _payloadSize = payloadSize > 0 ? payloadSize : Srt.LiveDefaultPayloadSize;

        _send = send ?? SendToSocket;
        _receive = receive ?? ReceiveFromSocket;
    }

    public override bool CanRead => !_writable && !_closed;

    public override bool CanWrite => _writable && !_closed;

    public override bool CanSeek => false;

    /// <summary>
    /// True once a send has been refused, which for a viewer means it has gone.
    ///
    /// Worth exposing, because the muxer swallows the exception a refused write throws: it treats
    /// that as "stop writing" rather than as a failure. Without a flag the caller cannot tell a
    /// stream that ended from a viewer that left, and would re-attach to a socket nobody is
    /// listening to, forever.
    /// </summary>
    public bool Faulted { get; private set; }

    /// <summary>What a write is cut into and the smallest buffer a read may be given.</summary>
    public int PayloadSize => _payloadSize;

    /// <summary>
    /// What this connection lost and dropped since the last time it was asked, or null when the
    /// socket has gone and there is nothing to ask.
    ///
    /// The interval, not the running total, and the <c>clear</c> argument is what makes it one. An
    /// operator looking at a list of a thousand streams is asking which of them is broken now: a
    /// total answers "this one lost forty packets at some point today", which is true of a healthy
    /// stream that had one bad minute and says nothing about the last two seconds. Only the
    /// heartbeat calls this, once per beat per stream, so the window is that beat and the figure
    /// reads as "per two seconds" without anything having to record when it was last cleared.
    ///
    /// The two are different failures. Lost is what never arrived and could not be retransmitted in
    /// time, which is the network or a saturated receive path; dropped is what arrived too late for
    /// the latency window, which is usually the latency window being too small for the link.
    /// </summary>
    public (int Lost, int Dropped)? Health()
    {
        if (_closed || _socket == Srt.SRT_INVALID_SOCK)
        {
            return null;
        }

        return Srt.Stats(_socket, out var stats, clear: true)
            ? (stats.pktRcvLoss, stats.pktRcvDrop)
            : null;
    }

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
        if (_closed || buffer.IsEmpty)
        {
            return 0;
        }

        if (buffer.Length < _payloadSize)
        {
            // libsrt would answer SRT_EINVALMSGAPI, which reads as a coding error rather than as
            // the too-small buffer it is. Say so here instead.
            throw new ArgumentException(
                $"An SRT read needs at least the payload size, {_payloadSize} bytes, "
                + $"and was given {buffer.Length}.",
                nameof(buffer));
        }

        while (!_closed)
        {
            var read = _receive(buffer);

            if (read > 0)
            {
                return read;
            }

            if (read == 0)
            {
                // The peer closed in an orderly way. End of stream, and nothing faulted.
                return 0;
            }

            if (Srt.LastErrorCode() == Srt.SRT_ETIMEOUT)
            {
                // SRTO_RCVTIMEO expired, which is not end of stream. It is the chance to notice
                // that the socket has been disposed underneath us and read again if it has not,
                // so a shutdown never waits on a silent sender.
                //
                // ponytail: a read can still sit one receive timeout past a Dispose, because the
                // loop only looks between reads. Pass a CancellationToken in if that second ever
                // shows up in a shutdown measurement.
                continue;
            }

            // A broken link, or a socket closed under us. Both mean nothing more is coming, and
            // both are a fault rather than an ending.
            Faulted = true;

            return 0;
        }

        return 0;
    }

    public override void Write(byte[] buffer, int offset, int count)
        => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (_closed || buffer.IsEmpty)
        {
            return;
        }

        while (!buffer.IsEmpty)
        {
            var chunk = buffer[..Math.Min(_payloadSize, buffer.Length)];

            if (_send(chunk) < 0)
            {
                // The socket's own error, which libsrt reports by return value. Without this a
                // viewer who walked away would be written to forever.
                Faulted = true;

                throw new IOException($"The socket refused {chunk.Length} bytes: {Srt.LastError()}");
            }

            buffer = buffer[chunk.Length..];
        }
    }

    /// <summary>Nothing to do: libsrt puts every message on the wire as it is sent.</summary>
    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (!_closed)
        {
            _closed = true;

            if (_socket != Srt.SRT_INVALID_SOCK)
            {
                Srt.srt_close(_socket);
            }
        }

        base.Dispose(disposing);
    }

    private int SendToSocket(ReadOnlySpan<byte> chunk)
    {
        fixed (byte* data = chunk)
        {
            // ttl -1 and inorder 0 are the live defaults; the transport-stream timeline is the
            // muxer's business, not the socket's.
            return Srt.srt_sendmsg(_socket, data, chunk.Length, -1, 0);
        }
    }

    private int ReceiveFromSocket(Span<byte> buffer)
    {
        fixed (byte* data = buffer)
        {
            return Srt.srt_recvmsg(_socket, data, buffer.Length);
        }
    }

    /// <summary>
    /// The socket's configured payload, falling back to the live default when it cannot be read,
    /// which is what a socket that has already gone answers.
    /// </summary>
    private static int PayloadSizeOf(int socket)
    {
        if (socket == Srt.SRT_INVALID_SOCK)
        {
            return Srt.LiveDefaultPayloadSize;
        }

        var size = Srt.GetInt32(socket, SRT_SOCKOPT.SRTO_PAYLOADSIZE);

        return size is > 0 and <= Srt.LiveMaxPayloadSize ? size : Srt.LiveDefaultPayloadSize;
    }
}
