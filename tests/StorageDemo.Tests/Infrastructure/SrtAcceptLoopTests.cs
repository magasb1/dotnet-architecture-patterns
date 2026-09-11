using System.Diagnostics;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// The accept carousel, against real senders. Everything else in the ingest design rests on this
/// working, and it works in a way nobody would guess from the API: one libav listener accepts a
/// single caller and closes its own listening socket, so concurrency comes from re-opening the
/// listener rather than from a backlog.
///
/// These tests exist to fail loudly if an FFmpeg upgrade takes any of that away.
/// </summary>
public sealed class SrtAcceptLoopTests
{
    private static readonly TimeSpan ListenTimeout = TimeSpan.FromMilliseconds(500);

    private static bool HasSrt() => FfmpegLibrary.InputProtocols().Contains("srt");

    private static readonly LiveListeners Listeners = new();

    private static SrtAcceptLoop Loop() => new(Listeners, NullLogger<SrtAcceptLoop>.Instance);

    /// <summary>
    /// The claim the whole design rests on: three senders, three names, one port, all at once.
    ///
    /// Each accepted transport is read from on its own thread, which is what proves the earlier
    /// connections survive the listening socket being torn down and rebound underneath them.
    /// </summary>
    [Fact]
    public async Task Several_named_senders_are_accepted_concurrently_on_one_port()
    {
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        string[] names = ["cam-alpha", "cam-bravo", "cam-charlie"];
        var port = FreePort();

        var accepted = new System.Collections.Concurrent.ConcurrentDictionary<string, Task<long>>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listening = Task.Factory.StartNew(
            () => Loop().Run(
                Listener(port),
                ListenTimeout,
                StreamIntent.Publish,
                connection =>
                {
                    // Taken over here, so the carousel is free to re-listen immediately. This is
                    // exactly what the service does with a real stream.
                    var transport = Handover(connection);
                    accepted[connection.Name] = Task.Run(() => Drain(transport, stop.Token));
                },
                stop.Token),
            TaskCreationOptions.LongRunning);

        // Staggered, because the backlog is one: two handshakes finishing while a third sits
        // unaccepted is the measured limit, not a bug to work around here.
        var senders = new List<Process>();

        foreach (var name in names)
        {
            senders.Add(StartSender(port, name));
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        try
        {
            await WaitUntilAsync(
                () => accepted.Count == names.Length,
                TimeSpan.FromSeconds(30),
                () => $"only {accepted.Count} of {names.Length} senders were accepted "
                    + $"({string.Join(", ", accepted.Keys.Order())}) on port {port}: "
                    + Complaints(senders));

            Assert.Equal(names.Order(), accepted.Keys.Order());
        }
        finally
        {
            foreach (var sender in senders)
            {
                Kill(sender);
            }

            await stop.CancelAsync();
            await listening;
        }

        foreach (var (name, reading) in accepted)
        {
            Assert.True(await reading > 0, $"nothing arrived on {name}");
        }
    }

    /// <summary>
    /// A sender that presents nothing is accepted, because it has to be, and then dropped. The
    /// carousel must survive that: an unnamed caller is the cheapest denial of service there is.
    /// </summary>
    [Fact]
    public async Task An_unnamed_sender_is_dropped_and_the_listener_keeps_going()
    {
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        var port = FreePort();
        var accepted = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var listening = Task.Factory.StartNew(
            () => Loop().Run(
                Listener(port),
                ListenTimeout,
                StreamIntent.Publish,
                connection =>
                {
                    accepted.Add(connection.Name);
                    connection.Dispose();
                },
                stop.Token),
            TaskCreationOptions.LongRunning);

        var anonymous = StartSender(port, streamId: null);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Kill(anonymous);

        var named = StartSender(port, "still-here");

        try
        {
            await WaitUntilAsync(() => accepted.Contains("still-here"), TimeSpan.FromSeconds(30));
        }
        finally
        {
            Kill(named);
            await stop.CancelAsync();
            await listening;
        }

        Assert.DoesNotContain(string.Empty, accepted);
        Assert.Contains("still-here", accepted);
    }

    /// <summary>
    /// The guard on the whole scheme. If this fails, the stream identifier is no longer reachable
    /// through libav's log and every stream would arrive unnamed.
    /// </summary>
    [Fact]
    public void The_boot_time_self_test_confirms_the_identifier_can_still_be_captured()
    {
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        FfmpegLibrary.EnsureLoaded();

        StreamIdCapture.SelfTest(FreePort(), NullLogger.Instance);
    }

    /// <summary>A thread that has not accepted anything holds nothing, so the capture cannot
    /// pass by returning a leftover from somebody else's connection.</summary>
    [Fact]
    public void Nothing_is_captured_on_a_thread_that_never_accepted()
        => Assert.Null(StreamIdCapture.Take());

    /// <summary>
    /// Readiness is about the port, not the process. A carousel that is waiting for a caller has
    /// its port bound and is serving; one that has never got that far is not, and a replica in
    /// that state belongs out of the Service rather than swallowing encoders it cannot name.
    /// </summary>
    [Fact]
    public async Task A_listener_waiting_for_a_caller_reports_itself_as_accepting()
    {
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        var listeners = new LiveListeners { Enabled = true };

        // Nothing has been opened, so nothing is being served.
        Assert.Equal("the ingest port is not accepting", listeners.NotServing());

        var loop = new SrtAcceptLoop(listeners, NullLogger<SrtAcceptLoop>.Instance);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var listening = Task.Factory.StartNew(
            () => loop.Run(
                Listener(FreePort()),
                ListenTimeout,
                StreamIntent.Publish,
                connection => connection.Dispose(),
                stop.Token),
            TaskCreationOptions.LongRunning);

        try
        {
            await WaitUntilAsync(() => listeners.IsListening(StreamIntent.Publish), TimeSpan.FromSeconds(20));
        }
        finally
        {
            await stop.CancelAsync();
            await listening;
        }

        // And it stops claiming to serve once the loop is gone.
        Assert.False(listeners.IsListening(StreamIntent.Publish));
    }

    /// <summary>Switched off is not a fault: there is nothing to gate readiness on.</summary>
    [Fact]
    public void A_replica_with_live_streaming_off_is_serving_by_definition()
        => Assert.Null(new LiveListeners { Enabled = false }.NotServing());

    /// <summary>
    /// A replica that cannot read the identifiers it is handed would name every stream wrong, so
    /// it takes itself out of the Service rather than accepting them.
    /// </summary>
    [Fact]
    public void A_replica_that_cannot_name_streams_reports_itself_as_not_serving()
    {
        var listeners = new LiveListeners { Enabled = true, Fault = "the self-test failed" };

        listeners.Bound(StreamIntent.Publish);
        listeners.Bound(StreamIntent.Subscribe);

        Assert.Equal("the self-test failed", listeners.NotServing());
    }

    private static string Listener(int port) => $"srt://0.0.0.0:{port}?mode=listener";

    /// <summary>Takes the open transport off the accept thread, as the service itself does.</summary>
    private static unsafe IntPtr Handover(AcceptedConnection connection) => (IntPtr)connection.Release();

    /// <summary>Reads the accepted transport until it ends, and closes it. Bytes carried.</summary>
    private static unsafe long Drain(IntPtr handle, CancellationToken cancellationToken)
    {
        var transport = (AVIOContext*)handle;

        const int size = 32 * 1024;
        var buffer = stackalloc byte[size];
        var carried = 0L;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = ffmpeg.avio_read(transport, buffer, size);
                if (read <= 0)
                {
                    break;
                }

                carried += read;
            }
        }
        finally
        {
            ffmpeg.avio_closep(&transport);
        }

        return carried;
    }

    private static Process StartSender(int port, string? streamId)
    {
        var target = $"srt://127.0.0.1:{port}?mode=caller"
            + (streamId is null ? string.Empty : $"&streamid={streamId}");

        var startInfo = new ProcessStartInfo(Ffmpeg.ExecutablePath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     // Paced at wall-clock speed: SRT is a connection, and a burst that ends at
                     // once looks to the far end like a peer hanging up mid-handshake.
                     "-re",
                     "-f", "lavfi", "-i", "testsrc=size=320x240:rate=15",
                     "-c:v", "mpeg2video", "-b:v", "600k", "-g", "15",
                     "-f", "mpegts", target,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!OperatingSystem.IsWindows())
        {
            startInfo.Environment["LD_LIBRARY_PATH"] = Ffmpeg.Directory;
        }

        var sender = Process.Start(startInfo)!;

        // Drained, not merely redirected. A pipe nobody reads fills and stops the sender, and the
        // test then fails as "nothing was accepted" with the reason sitting unread in the pipe.
        var complaints = new System.Text.StringBuilder();
        SenderErrors[sender.Id] = complaints;
        sender.ErrorDataReceived += (_, line) =>
        {
            if (line.Data is not null)
            {
                lock (complaints)
                {
                    complaints.AppendLine(line.Data);
                }
            }
        };
        sender.BeginErrorReadLine();

        return sender;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, System.Text.StringBuilder>
        SenderErrors = new();

    /// <summary>Everything the senders complained about, for a failure message worth reading.</summary>
    private static string Complaints(IEnumerable<Process> senders)
    {
        var said = senders
            .Select(sender => SenderErrors.TryGetValue(sender.Id, out var text) ? text : null)
            .Where(text => text is not null)
            .Select(text => { lock (text!) { return text.ToString().Trim(); } })
            .Where(text => text.Length > 0)
            .ToArray();

        return said.Length == 0 ? "the senders said nothing" : string.Join(" | ", said);
    }

    private static void Kill(Process sender)
    {
        try
        {
            if (!sender.HasExited)
            {
                sender.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        sender.Dispose();
    }

    /// <param name="describe">
    /// What to say when it never came true. Worth passing: "the condition was false" sends the
    /// next reader back to the source to work out which half of it failed.
    /// </param>
    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        Func<string>? describe = null)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        Assert.Fail($"{describe?.Invoke() ?? "the condition was still false"} after {timeout}");
    }

    /// <summary>
    /// A free port from a low, fixed range rather than an ephemeral one. Windows reserves stretches
    /// of the dynamic range, so a port can be handed out and then refuse an explicit bind moments
    /// later, which looks exactly like a listener that will not start.
    /// </summary>
    private static int _nextPort = 9400;

    private static int FreePort()
    {
        var start = Interlocked.Add(ref _nextPort, 10);

        for (var port = start; port < start + 200; port++)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp);

                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));

                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
            }
        }

        throw new InvalidOperationException("No free port in the test range.");
    }
}
