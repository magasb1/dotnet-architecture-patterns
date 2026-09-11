using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// The rules libsrt imposes on a message socket, pinned without libsrt.
///
/// A write larger than the payload size is refused as a message rather than reported as an error,
/// so chunking that is off by one shows up as a silently dropped send somewhere downstream. That is
/// worth a test that runs everywhere, which means substituting the two native calls rather than
/// opening a socket.
/// </summary>
public sealed class SrtSocketStreamTests
{
    private static SrtSocketStream Writer(List<int> sends, int result = 1)
        => new(
            Srt.SRT_INVALID_SOCK,
            writable: true,
            send: chunk =>
            {
                sends.Add(chunk.Length);

                return result;
            },
            receive: null);

    /// <summary>
    /// The plan says four sends of 1316 and one of 736, which is 6000 bytes rather than the 5000 it
    /// also says. 5000 is three full payloads and a remainder of 1052, and that is what is asserted.
    /// </summary>
    [Fact]
    public void A_write_larger_than_the_payload_is_sent_in_payload_sized_chunks()
    {
        var sends = new List<int>();

        using var stream = Writer(sends);

        stream.Write(new byte[5000]);

        Assert.Equal([1316, 1316, 1316, 1052], sends);
        Assert.Equal(5000, sends.Sum());
        Assert.False(stream.Faulted);
    }

    /// <summary>
    /// A viewer that walked away has to be told apart from a stream that ended, or the hub
    /// re-attaches to a socket nobody is listening to, forever. The muxer swallows the exception,
    /// so the flag is the only part of this the caller can see.
    /// </summary>
    [Fact]
    public void A_refused_send_faults_the_stream_and_stops_writing()
    {
        var sends = new List<int>();

        using var stream = Writer(sends, result: Srt.SRT_ERROR);

        Assert.Throws<IOException>(() => stream.Write(new byte[5000]));

        Assert.True(stream.Faulted);
        Assert.Equal([1316], sends);
    }

    /// <summary>
    /// Zero from srt_recvmsg is the peer closing in an orderly way, which is an ending and not a
    /// fault. The broken-link case is the other error path and needs a real socket to provoke.
    /// </summary>
    [Fact]
    public void A_peer_that_closes_ends_the_stream_without_faulting_it()
    {
        using var stream = new SrtSocketStream(
            Srt.SRT_INVALID_SOCK,
            writable: false,
            send: null,
            receive: _ => 0);

        Assert.Equal(0, stream.Read(new byte[2000]));
        Assert.False(stream.Faulted);
    }
}
