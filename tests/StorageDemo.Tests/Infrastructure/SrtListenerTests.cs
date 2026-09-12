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
///
/// The last three are about the fourth thing a listener decides and a viewer feels: how much
/// latency a connection ends up with, which is negotiated rather than configured.
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

        using var metrics = new LiveMetrics();

        // Before anything is accepted: a counter is an event, so a listener started afterwards sees
        // none of them.
        using var meters = new Meters(metrics);

        var listenPort = SrtSenders.FreePort();

        await ListeningAsync(
            Listener(socket => accepted[socket.Name] = true, metrics: metrics),
            listenPort,
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

        // The same event, counted. Twenty accepts on one port is one time series and not twenty:
        // the port is the tag, the name is not, and this is what stops that being changed quietly.
        var counted = meters
            .Read()
            .Where(measurement => measurement.Instrument == "live.accepts")
            .ToArray();

        Assert.Equal(names.Length, counted.Sum(measurement => measurement.Value));
        Assert.All(
            counted,
            measurement => Assert.Equal(
                [new KeyValuePair<string, object?>("port", listenPort)],
                measurement.Tags));
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

        using var metrics = new LiveMetrics();
        using var meters = new Meters(metrics);

        var listenPort = SrtSenders.FreePort();

        await ListeningAsync(
            Listener(socket => accepted.Add(socket.Name), metrics: metrics),
            listenPort,
            async port =>
            {
                var sender = Sender(port, "../../etc/passwd");

                var refused = await SrtSenders.WasRefused(sender);

                Assert.Empty(accepted);

                Assert.True(
                    refused,
                    $"the sender was not turned away quickly: {SrtSenders.Complaints(_callers)}");
            });

        // The other half of "an operator can find out why an encoder cannot connect". The reason is
        // a word from a closed set and never the identifier that was refused, which a caller chooses
        // and could therefore use to choose this metric's cost.
        var reject = Assert.Single(
            meters.Read(),
            measurement => measurement.Instrument == "live.rejects");

        Assert.Equal(
            [
                new KeyValuePair<string, object?>("port", listenPort),
                new KeyValuePair<string, object?>("reason", "bad-request"),
            ],
            reject.Tags);
    }

    /// <summary>
    /// The one thing that cannot be reasoned about: that the fields this service reads out of
    /// <c>srt_bstats</c> are the fields libsrt wrote.
    ///
    /// <c>SRT_TRACEBSTATS</c> is eighty-two members of mixed width, and libsrt takes no length to
    /// bound what it writes. A layout that is wrong by one member does not fail: it returns success
    /// and hands back a plausible number from the wrong offset, which would be reported as the
    /// health of a stream and believed. So two fields deep inside the struct are checked against
    /// values known from somewhere else entirely - the MTU, which is libsrt's own default of 1500,
    /// and the receiver's delivery delay, which has to be the latency the connection negotiated and
    /// is read here through a socket option instead.
    ///
    /// Their offsets are 360 and 392 bytes in, so a struct that is wrong anywhere before them is
    /// wrong here too.
    /// </summary>
    [Fact]
    public async Task The_statistics_libsrt_writes_land_in_the_fields_this_service_reads()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var connected = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stats = default(SRT_TRACEBSTATS);

        await ListeningAsync(
            Listener(
                socket =>
                {
                    var connection = socket.Release();
                    _held.Add(connection);

                    connected.TrySetResult(connection);
                },
                latencyMs: 60),
            SrtSenders.FreePort(),
            async port =>
            {
                Sender(port, "live/cam-1");

                await SrtSenders.WaitUntilAsync(
                    () => connected.Task.IsCompleted,
                    TimeSpan.FromSeconds(15),
                    () => $"the sender was never accepted on port {port}: {SrtSenders.Complaints(_callers)}");

                var socket = await connected.Task;

                // Sampled once media has actually moved, so the receive counters mean something.
                await SrtSenders.WaitUntilAsync(
                    () => Srt.Stats(socket, out stats, clear: false) && stats.pktRecvTotal > 0,
                    TimeSpan.FromSeconds(15),
                    () => $"libsrt never reported a received packet: {SrtSenders.Complaints(_callers)}");
            });

        Assert.True(stats.msTimeStamp > 0, "the connection reported no elapsed time");
        Assert.Equal(1500, stats.byteMSS);

        Assert.Equal(
            Srt.GetInt32(await connected.Task, SRT_SOCKOPT.SRTO_RCVLATENCY),
            stats.msRcvTsbPdDelay);
    }

    /// <summary>
    /// A stream that is fine says so, which is the half of the health signal that has to be quiet or
    /// none of it means anything.
    ///
    /// Two samples rather than one, because the figure is an interval and the first one covers the
    /// connection's whole life. The second covers only the seconds between the two calls, which is
    /// what the heartbeat reads and what an operator is shown.
    ///
    /// The socket is drained throughout. A receiver that never reads accumulates drops of its own
    /// once packets outlive the latency window, and that would be this test measuring itself.
    /// </summary>
    [Fact]
    public async Task A_healthy_stream_reports_no_loss_and_no_drops()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var connected = new TaskCompletionSource<SrtSocketStream>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        (int Lost, int Dropped)? health = null;

        await ListeningAsync(
            Listener(socket => connected.TrySetResult(new SrtSocketStream(socket.Release(), writable: false))),
            SrtSenders.FreePort(),
            async port =>
            {
                Sender(port, "live/cam-1");

                await SrtSenders.WaitUntilAsync(
                    () => connected.Task.IsCompleted,
                    TimeSpan.FromSeconds(15),
                    () => $"the sender was never accepted on port {port}: {SrtSenders.Complaints(_callers)}");

                using var transport = await connected.Task;
                using var draining = new CancellationTokenSource();

                var drain = Task.Factory.StartNew(
                    () => Drain(transport, draining.Token),
                    TaskCreationOptions.LongRunning);

                await Task.Delay(TimeSpan.FromSeconds(2));

                Assert.NotNull(transport.Health());

                await Task.Delay(TimeSpan.FromSeconds(2));

                health = transport.Health();

                await draining.CancelAsync();
                await drain;
            });

        Assert.Equal((0, 0), health);
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

    /// <summary>
    /// What the listener asks for is what an accepted socket ends up with, as long as nobody at the
    /// far end asks for more. Fails if the option is set on the wrong socket or after srt_listen,
    /// either of which leaves libsrt's 120 ms default in place and nothing else to notice it by.
    /// </summary>
    [Fact]
    public async Task The_configured_latency_is_what_an_accepted_socket_negotiates()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var negotiated = await NegotiatedAsync(
            StreamIntent.Publish,
            latencyMs: 60,
            SRT_SOCKOPT.SRTO_RCVLATENCY,
            port => Sender(port, "cam-1"));

        Assert.Equal(60, negotiated);
    }

    /// <summary>
    /// The direction of the negotiation, pinned so nobody "fixes" it later. Each side names a
    /// figure and the larger one wins, which is correct: an encoder on a link that loses packets
    /// knows something this service does not, and its 200 ms has to survive our 60.
    ///
    /// FFmpeg's <c>latency</c> is microseconds and libsrt's is milliseconds, which is the other
    /// thing this test would catch: a unit slip here reads as a stream that buffers a thousand
    /// times too much or not at all.
    /// </summary>
    [Fact]
    public async Task The_encoders_higher_latency_wins()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var negotiated = await NegotiatedAsync(
            StreamIntent.Publish,
            latencyMs: 60,
            SRT_SOCKOPT.SRTO_RCVLATENCY,
            port => Sender(port, "cam-1", "latency=200000"));

        Assert.Equal(200, negotiated);
    }

    /// <summary>
    /// The half a plain SRTO_RCVLATENCY would have missed. On the consumption port this service is
    /// the sender, so the buffer that matters is the player's, and it is SRTO_PEERLATENCY that
    /// carries our figure into it.
    ///
    /// The player asks for 20 ms rather than for nothing, which is where this departs from the
    /// plan. libsrt takes the maximum of our peer latency and the player's own receive latency, and
    /// a player that asks for nothing is asking for libsrt's 120 ms default, so it would win and
    /// the assertion would pass at 120 whether or not this service had set anything at all. Against
    /// a player asking for less, 60 can only have come from SRTO_LATENCY: with SRTO_RCVLATENCY
    /// alone the peer half would still be its default of zero and the answer would be the player's
    /// own 20.
    /// </summary>
    [Fact]
    public async Task The_consumption_side_inherits_the_latency_too()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);

        var negotiated = await NegotiatedAsync(
            StreamIntent.Subscribe,
            latencyMs: 60,
            SRT_SOCKOPT.SRTO_PEERLATENCY,
            port => Player(port, "cam-1", "latency=20000"));

        Assert.Equal(60, negotiated);
    }

    /// <summary>
    /// Runs one caller against a listener at <paramref name="latencyMs"/> and answers with the
    /// option read off the accepted socket. Post-handshake that is the negotiated figure rather
    /// than the configured one, which is the whole reason these three tests read it from there and
    /// not from the options object.
    /// </summary>
    private async Task<int> NegotiatedAsync(
        StreamIntent intent,
        int latencyMs,
        SRT_SOCKOPT option,
        Func<int, Process> caller)
    {
        var negotiated = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await ListeningAsync(
            Listener(
                socket =>
                {
                    // Taken off the handler rather than read through it: an option is only worth
                    // anything on a live connection, and the handler closes what it is given.
                    var connection = socket.Release();
                    _held.Add(connection);

                    negotiated.TrySetResult(Srt.GetInt32(connection, option));
                },
                intent,
                latencyMs),
            SrtSenders.FreePort(),
            async port =>
            {
                caller(port);

                await SrtSenders.WaitUntilAsync(
                    () => negotiated.Task.IsCompleted,
                    TimeSpan.FromSeconds(15),
                    () => $"the caller was never accepted on port {port}: {SrtSenders.Complaints(_callers)}");
            });

        return await negotiated.Task;
    }

    private SrtListener Listener(
        Action<AcceptedSocket> onAccepted,
        StreamIntent intent = StreamIntent.Publish,
        int latencyMs = 120,
        LiveMetrics? metrics = null)
        => new(
            intent,
            new LiveOptions { SrtLatencyMs = latencyMs },
            _ => null,
            onAccepted,
            NullLogger<SrtListener>.Instance,
            listeners: null,
            metrics);

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

    /// <summary>Reads until cancelled, so the receiver is never why a packet went stale.</summary>
    private static void Drain(SrtSocketStream transport, CancellationToken cancellationToken)
    {
        var buffer = new byte[transport.PayloadSize];

        while (!cancellationToken.IsCancellationRequested && transport.Read(buffer) > 0)
        {
        }
    }

    private Process Sender(int port, string? streamId, string? callerOptions = null)
    {
        var caller = SrtSenders.StartSender(port, streamId, callerOptions);
        _callers.Add(caller);

        return caller;
    }

    private Process Player(int port, string streamId, string? callerOptions = null)
    {
        var caller = SrtSenders.StartPlayer(port, streamId, callerOptions);
        _callers.Add(caller);

        return caller;
    }
}
