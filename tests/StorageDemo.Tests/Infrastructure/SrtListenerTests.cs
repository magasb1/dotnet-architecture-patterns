using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// The listener this service owns, against real callers.
///
/// Everything above it rests on three properties of libsrt that libav's listener did not have: a
/// backlog deep enough that callers arriving together are all accepted, a hook that sees the name
/// while the answer can still be no, and an accept path that never reads from what it accepted. Each
/// is load-bearing - the first is why a thousand encoders can cold-start, the second is what makes
/// refusing anything possible at all, the third is what keeps one silent caller from stalling a pod.
///
/// These tests exist to fail loudly if a libsrt or FFmpeg upgrade takes any of the three away,
/// because each would come back as a capacity ceiling that looks like a slow network.
/// </summary>
public sealed class SrtListenerTests(ITestOutputHelper output) : IDisposable
{
    private const string NoLibsrt = "libsrt is not installed. Run scripts/fetch-libsrt.sh.";

    private readonly List<Process> _callers = [];

    private readonly List<int> _held = [];

    public void Dispose()
    {
        foreach (var caller in _callers)
        {
            SrtSenders.Kill(caller);
        }

        foreach (var socket in _held)
        {
            Srt.srt_close(socket);
        }
    }

    /// <summary>
    /// The proof the whole phase exists for.
    ///
    /// Its predecessor started three senders two seconds apart and explained that the backlog was
    /// one, so two handshakes finishing while a third sat unaccepted was the measured limit rather
    /// than a bug. That limit is exactly what owning the listener removes, so this one starts twenty
    /// together and fails if the backlog is not real.
    ///
    /// Starting the processes is outside the five seconds. Twenty <c>Process.Start</c> calls are the
    /// harness's cost, not the listener's, and a listener that serialises accepts misses this
    /// deadline by half a minute rather than by a margin.
    /// </summary>
    [Fact]
    public async Task Twenty_senders_started_at_once_are_all_accepted_within_five_seconds()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var names = Enumerable.Range(1, 20).Select(index => $"cam-{index:00}").ToArray();
        var accepted = new ConcurrentDictionary<string, bool>();

        await ListeningAsync(
            Listener(socket => accepted[socket.Name] = true),
            SrtSenders.FreePort(),
            async port =>
            {
                // Nothing between them, which is the point.
                foreach (var name in names)
                {
                    Sender(port, name);
                }

                await SrtSenders.WaitUntilAsync(
                    () => accepted.Count == names.Length,
                    TimeSpan.FromSeconds(5),
                    () => $"only {accepted.Count} of {names.Length} senders were accepted on port {port} "
                        + $"({string.Join(", ", accepted.Keys.Order())}): {SrtSenders.Complaints(_callers)}");
            });

        Assert.Equal(names.Order(), accepted.Keys.Order());
    }

    /// <summary>
    /// The name comes off the socket now, not out of a log line, and nothing is installed to
    /// intercept it. Fails if the option read breaks, if the handshake's zero padding starts coming
    /// through, or if a future libsrt changes what it hands back.
    ///
    /// The identifier is asserted by its tail rather than whole: FFmpeg 7 and later percent-decode
    /// the leading hash and older builds do not, and which one ran is not what is being pinned here.
    /// </summary>
    [Fact]
    public async Task The_stream_identifier_is_read_off_the_accepted_socket_byte_for_byte()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var accepted = new TaskCompletionSource<(string Name, string StreamId)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await ListeningAsync(
            Listener(socket => accepted.TrySetResult((socket.Name, socket.StreamId))),
            SrtSenders.FreePort(),
            async port =>
            {
                Sender(port, "#!::r=live/cam-1,m=publish");

                await SrtSenders.WaitUntilAsync(
                    () => accepted.Task.IsCompleted,
                    TimeSpan.FromSeconds(15),
                    () => $"the sender was never accepted on port {port}: {SrtSenders.Complaints(_callers)}");
            });

        var (name, streamId) = await accepted.Task;

        Assert.Equal("live/cam-1", name);
        Assert.EndsWith("r=live/cam-1,m=publish", streamId, StringComparison.Ordinal);
    }

    /// <summary>
    /// The property Phases 1b, 4 and 5 are all built on: an answer of no that arrives before a
    /// connection exists.
    ///
    /// The discriminator is that the accept handler never fired. A caller that was admitted and then
    /// dropped would have fired it, and would complain to stderr in exactly the same words, because
    /// FFmpeg's caller path never asks libsrt for the rejection reason. The stderr check is
    /// corroboration and lives in <see cref="SrtSenders.WasRefused"/> for that reason.
    /// </summary>
    [Fact]
    public async Task A_sender_with_an_unparseable_name_is_rejected_during_the_handshake_and_not_after()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var accepted = new ConcurrentBag<string>();

        await ListeningAsync(
            Listener(socket => accepted.Add(socket.Name)),
            SrtSenders.FreePort(),
            async port =>
            {
                var sender = Sender(port, "../../etc/passwd");

                var refused = await SrtSenders.WasRefused(sender);

                Assert.Empty(accepted);

                Assert.True(
                    refused,
                    $"the sender was not turned away quickly: {SrtSenders.Complaints(_callers)}");
            });
    }

    /// <summary>
    /// The same mechanism in both directions. A port that took callers going the wrong way would
    /// give an encoder a stream nobody can watch and a player a stream nobody is sending, and both
    /// would look like a broken feed rather than a misdirected caller.
    /// </summary>
    [Fact]
    public async Task A_publisher_on_the_consumption_port_and_a_subscriber_on_the_ingest_port_are_both_rejected()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var accepted = new ConcurrentBag<string>();
        var ingestPort = SrtSenders.FreePort();

        await ListeningAsync(
            Listener(socket => accepted.Add(socket.Name)),
            ingestPort,
            async _ => await ListeningAsync(
                Listener(socket => accepted.Add(socket.Name), StreamIntent.Subscribe),
                SrtSenders.FreePort(),
                async consumptionPort =>
                {
                    var offering = Sender(consumptionPort, "#!::r=cam-1,m=publish");
                    var asking = Player(ingestPort, "#!::r=cam-1,m=request");

                    var refused = await SrtSenders.WasRefused(offering) && await SrtSenders.WasRefused(asking);

                    Assert.Empty(accepted);

                    Assert.True(
                        refused,
                        $"a caller going the wrong way was not turned away: {SrtSenders.Complaints(_callers)}");
                }));
    }

    /// <summary>
    /// The bug the old carousel opened the transport on a separate thread to avoid: one caller that
    /// says nothing holding up everybody behind it.
    ///
    /// The first caller is an FFmpeg reading rather than writing, which connects and then waits to be
    /// sent something, so it genuinely never sends a byte. Its socket is taken off the handler and
    /// never read from, which is the other half of the claim: accept does not depend on anything
    /// arriving on what it has already accepted.
    ///
    /// Three seconds rather than the one the plan names, because starting an FFmpeg is inside the
    /// window. An accept that waits on a read does not finish late, it does not finish at all.
    /// </summary>
    [Fact]
    public async Task A_caller_that_never_sends_a_byte_does_not_block_the_next_accept()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var accepted = new ConcurrentDictionary<string, bool>();

        await ListeningAsync(
            Listener(socket =>
            {
                accepted[socket.Name] = true;

                // Held open and unread until the test is over.
                _held.Add(socket.Release());
            }),
            SrtSenders.FreePort(),
            async port =>
            {
                Player(port, "silent");

                await SrtSenders.WaitUntilAsync(
                    () => accepted.ContainsKey("silent"),
                    TimeSpan.FromSeconds(15),
                    () => $"the silent caller was never accepted: {SrtSenders.Complaints(_callers)}");

                Sender(port, "after-the-silent-one");

                await SrtSenders.WaitUntilAsync(
                    () => accepted.ContainsKey("after-the-silent-one"),
                    TimeSpan.FromSeconds(3),
                    () => "the silent caller blocked the next accept: " + SrtSenders.Complaints(_callers));
            });
    }

    /// <summary>
    /// What a shutdown depends on. libsrt documents closing a socket from another thread as what
    /// unblocks <c>srt_accept</c> and says nothing about <c>srt_recvmsg</c>, so the one-second
    /// receive timeout is there to bound it either way.
    ///
    /// Which of the two actually fired is reported rather than asserted, and <c>Faulted</c> is what
    /// tells them apart: the close comes back as a socket error and the timeout does not. If it is
    /// always the close, the receive timeout can be lengthened and a silent sender costs less.
    /// </summary>
    [Fact]
    public async Task Closing_the_socket_from_another_thread_ends_a_blocked_read()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var accepted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await ListeningAsync(
            Listener(socket => accepted.TrySetResult(socket.Release()), StreamIntent.Subscribe),
            SrtSenders.FreePort(),
            async port =>
            {
                // A viewer sends nothing, so the read below has nothing to return and blocks.
                Player(port, "viewer-1");

                await SrtSenders.WaitUntilAsync(
                    () => accepted.Task.IsCompleted,
                    TimeSpan.FromSeconds(15),
                    () => $"the viewer was never accepted on port {port}: {SrtSenders.Complaints(_callers)}");

                using var stream = new SrtSocketStream(await accepted.Task, writable: false);

                var reading = Task.Run(() => stream.Read(new byte[4096], 0, 4096));

                // Long enough to be inside srt_recvmsg, and past the first receive timeout, so what
                // ends the read is the close and not a coincidence of timing.
                await Task.Delay(TimeSpan.FromMilliseconds(1500));

                Assert.False(reading.IsCompleted, "the read returned before anything closed the socket");

                stream.Dispose();

                var finished = await Task.WhenAny(reading, Task.Delay(TimeSpan.FromSeconds(2)));

                Assert.True(finished == reading, "a blocked read outlived the close by over two seconds");
                Assert.Equal(0, await reading);

                output.WriteLine(stream.Faulted
                    ? "srt_close unblocked the read: it came back as a socket error"
                    : "the receive timeout ended the read, not the close");
            });
    }

    private SrtListener Listener(Action<AcceptedSocket> onAccepted, StreamIntent intent = StreamIntent.Publish)
        => new(intent, new LiveOptions(), _ => null, onAccepted, NullLogger<SrtListener>.Instance);

    /// <summary>
    /// Runs a listener on its own thread for as long as the body takes, and stops it afterwards
    /// whatever the body did. Every test here needs that and none of them needs anything else.
    /// </summary>
    private static async Task ListeningAsync(SrtListener listener, int port, Func<int, Task> body)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));

        var listening = Task.Factory.StartNew(
            () => listener.Run(port, stop.Token),
            TaskCreationOptions.LongRunning);

        try
        {
            await body(port);
        }
        finally
        {
            await stop.CancelAsync();
            await listening;
        }
    }

    private Process Sender(int port, string? streamId)
    {
        var caller = SrtSenders.StartSender(port, streamId);
        _callers.Add(caller);

        return caller;
    }

    private Process Player(int port, string streamId)
    {
        var caller = SrtSenders.StartPlayer(port, streamId);
        _callers.Add(caller);

        return caller;
    }
}
