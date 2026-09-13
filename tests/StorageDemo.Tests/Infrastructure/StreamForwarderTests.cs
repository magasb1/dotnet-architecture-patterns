using System.Net;
using System.Net.Sockets;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Coordination;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Tests.Application;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// Forwarding, in the two halves it actually has.
///
/// One is bytes: a hub, a real MPEG-TS layout, a socket at the other end, and an assertion that
/// what arrives is a transport stream. That is the only test that proves the whole path, since
/// every part of it - the subscription, the muxer, the AVIOContext, the protocol - is something
/// that can be wrong without any other test noticing.
///
/// The other is the decision, which has no bytes in it at all: <see cref="ForwardPlan"/> takes what
/// was configured, what is running and the time, and says what to start and stop. Proving "a
/// disabled forward stops" that way costs nothing, where proving it through a socket would cost a
/// far end and a wait.
/// </summary>
public sealed class StreamForwarderTests
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    private static readonly DateTimeOffset Now = new(2026, 9, 13, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The smallest layout a muxer will accept, built without a real transport.</summary>
    private static unsafe StreamLayout OneVideoStream()
    {
        var format = ffmpeg.avformat_alloc_context();

        try
        {
            var stream = ffmpeg.avformat_new_stream(format, null);

            stream->time_base = new AVRational { num = 1, den = 90000 };
            stream->codecpar->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            stream->codecpar->codec_id = AVCodecID.AV_CODEC_ID_MPEG2VIDEO;
            stream->codecpar->width = 320;
            stream->codecpar->height = 240;

            return StreamLayout.From(format);
        }
        finally
        {
            ffmpeg.avformat_free_context(format);
        }
    }

    /// <summary>
    /// The whole path, with real bytes: packets into a hub, a forward pointed at a socket this test
    /// holds, and a transport stream coming out of it.
    ///
    /// The sync byte is the assertion worth making. Bytes arriving proves a socket was opened;
    /// 0x47 at the start of a datagram proves the muxer built a transport stream and that the RTP
    /// question - which container this scheme wants - was answered correctly for udp.
    /// </summary>
    [Fact]
    public async Task A_forward_pushes_a_transport_stream_to_the_far_end()
    {
        Assert.SkipUnless(Ffmpeg.IsPresent, "No FFmpeg. Run scripts/fetch-ffmpeg.sh.");

        FfmpegLibrary.EnsureLoaded();

        var options = new LiveOptions();

        using var hub = new StreamHub("forwarded-camera", options, NullLogger.Instance);
        using var layout = OneVideoStream();

        hub.Adopt(layout);

        var port = SrtSenders.FreePort();

        using var far = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));

        using var forwarder = new StreamForwarder(
            "forward-1",
            $"udp://127.0.0.1:{port}",
            hub,
            options,
            NullLogger.Instance);

        forwarder.Start(CancellationToken.None);

        // Published in a loop rather than all at once, because the forward subscribes at the live
        // edge: anything sent before its thread has attached is gone, and the thread has a socket to
        // open first.
        var received = far.ReceiveAsync(CancellationToken.None).AsTask();
        var pts = 0L;

        for (var burst = 0; burst < 100 && !received.IsCompleted; burst++)
        {
            for (var step = 0; step < 10; step++)
            {
                hub.Publish(
                    new MediaPacket(0, new byte[1400], pts, pts, Duration: 3600, IsKeyframe: step == 0),
                    pts);

                pts += 3600;
            }

            await Task.Delay(50);
        }

        Assert.True(
            received.IsCompleted,
            $"nothing reached the far end in five seconds; the forward said: {forwarder.Error}");

        var datagram = (await received).Buffer;

        Assert.NotEmpty(datagram);
        Assert.Equal(0x47, datagram[0]);

        Assert.True(forwarder.Connected, $"the forward reported itself disconnected: {forwarder.Error}");
        Assert.True(forwarder.Bytes > 0, "the forward counted no bytes");
        Assert.NotNull(forwarder.ConnectedAt);

        var status = forwarder.Status;

        Assert.Equal("forward-1", status.Id);
        Assert.True(status.Connected);
        Assert.True(status.Bytes > 0);
    }

    /// <summary>
    /// A forward whose far end cannot be opened stops and says why, rather than retrying on its own.
    /// The retry lives in the reconcile pass, which is the only place that knows whether the
    /// operator still wants this forward at all.
    /// </summary>
    [Fact]
    public async Task A_forward_that_cannot_open_stops_and_leaves_the_reason()
    {
        Assert.SkipUnless(Ffmpeg.IsPresent, "No FFmpeg. Run scripts/fetch-ffmpeg.sh.");

        FfmpegLibrary.EnsureLoaded();

        var options = new LiveOptions();

        using var hub = new StreamHub("unopenable", options, NullLogger.Instance);
        using var layout = OneVideoStream();

        hub.Adopt(layout);

        // A scheme libav has no output protocol for, so the open fails rather than hanging on a
        // handshake this test would then have to wait out.
        using var forwarder = new StreamForwarder(
            "forward-1",
            "nonsense://127.0.0.1:1/x",
            hub,
            options,
            NullLogger.Instance);

        forwarder.Start(CancellationToken.None);

        await SrtSenders.WaitUntilAsync(
            () => forwarder.Finished,
            TimeSpan.FromSeconds(10),
            () => "the forward never gave up on a URL it cannot open");

        Assert.False(forwarder.Connected);
        Assert.NotNull(forwarder.Error);
        Assert.Contains("nonsense", forwarder.Error);
    }

    [Fact]
    public void A_configured_forward_that_is_not_running_is_started()
    {
        var (start, stop) = ForwardPlan.Decide(
            [new ForwardTarget("a", "udp://far:5000")],
            new Dictionary<string, RunningForward>(),
            Now,
            Retry);

        Assert.Equal("a", Assert.Single(start).Id);
        Assert.Empty(stop);
    }

    [Fact]
    public void A_forward_that_was_removed_from_the_source_is_stopped()
    {
        var (start, stop) = ForwardPlan.Decide(
            [],
            Running(("a", "udp://far:5000", false)),
            Now,
            Retry);

        Assert.Empty(start);
        Assert.Equal("a", Assert.Single(stop));
    }

    /// <summary>
    /// Disabling is the same decision as removing, from here. The difference matters to the store,
    /// which keeps the URL so switching it back on costs nobody a retype, and not to this.
    /// </summary>
    [Fact]
    public void A_forward_that_was_disabled_is_stopped()
    {
        var source = new LiveSource(
            "camera",
            null,
            Enabled: true,
            [new ForwardTarget("a", "udp://far:5000", Enabled: false)],
            Now);

        var (start, stop) = ForwardPlan.Decide(
            [.. source.Forwards.Where(target => target.Enabled)],
            Running(("a", "udp://far:5000", false)),
            Now,
            Retry);

        Assert.Empty(start);
        Assert.Equal("a", Assert.Single(stop));
    }

    /// <summary>
    /// An edited URL is a redial on the same forward, not a forward disappearing and another
    /// appearing, which is the whole reason the id is stable across edits.
    /// </summary>
    [Fact]
    public void A_forward_whose_url_changed_is_stopped_and_started_again()
    {
        var (start, stop) = ForwardPlan.Decide(
            [new ForwardTarget("a", "udp://elsewhere:5000")],
            Running(("a", "udp://far:5000", false)),
            Now,
            Retry);

        Assert.Equal("udp://elsewhere:5000", Assert.Single(start).Url);
        Assert.Equal("a", Assert.Single(stop));
    }

    [Fact]
    public void A_running_forward_is_left_alone()
    {
        var (start, stop) = ForwardPlan.Decide(
            [new ForwardTarget("a", "udp://far:5000")],
            Running(("a", "udp://far:5000", false)),
            Now,
            Retry);

        Assert.Empty(start);
        Assert.Empty(stop);
    }

    /// <summary>
    /// The back-off. A far end that is down is dialled on a schedule rather than on every beat,
    /// which at a thousand streams is the difference between a retry and a flood.
    /// </summary>
    [Fact]
    public void A_failed_forward_is_left_alone_until_its_retry_is_due()
    {
        var running = new Dictionary<string, RunningForward>(StringComparer.Ordinal)
        {
            ["a"] = new("udp://far:5000", Finished: true, LastAttemptAt: Now - TimeSpan.FromSeconds(2)),
        };

        var (start, stop) = ForwardPlan.Decide([new ForwardTarget("a", "udp://far:5000")], running, Now, Retry);

        Assert.Empty(start);
        Assert.Empty(stop);
    }

    [Fact]
    public void A_failed_forward_is_started_again_once_its_retry_is_due()
    {
        var running = new Dictionary<string, RunningForward>(StringComparer.Ordinal)
        {
            ["a"] = new("udp://far:5000", Finished: true, LastAttemptAt: Now - TimeSpan.FromSeconds(30)),
        };

        var (start, stop) = ForwardPlan.Decide([new ForwardTarget("a", "udp://far:5000")], running, Now, Retry);

        Assert.Equal("a", Assert.Single(start).Id);
        Assert.Equal("a", Assert.Single(stop));
    }

    /// <summary>
    /// A forward that failed and has been disabled since is stopped and not retried, which is the
    /// case that would go wrong if the forwarder retried itself: it has no idea the operator has
    /// changed their mind.
    /// </summary>
    [Fact]
    public void A_failed_forward_that_was_disabled_is_not_retried()
    {
        var running = new Dictionary<string, RunningForward>(StringComparer.Ordinal)
        {
            ["a"] = new("udp://far:5000", Finished: true, LastAttemptAt: Now - TimeSpan.FromSeconds(30)),
        };

        var (start, stop) = ForwardPlan.Decide([], running, Now, Retry);

        Assert.Empty(start);
        Assert.Equal("a", Assert.Single(stop));
    }

    private static Dictionary<string, RunningForward> Running(params (string Id, string Url, bool Finished)[] forwards)
        => forwards.ToDictionary(
            forward => forward.Id,
            forward => new RunningForward(forward.Url, forward.Finished, Now),
            StringComparer.Ordinal);
}

/// <summary>
/// The allowlist, on the output side.
///
/// It has always covered pulled inputs, and a forward is the same hazard pointed the other way: a
/// URL an operator types that libav will happily open. "Write this local file" is as much a way out
/// of this service as "read this local file" was a way in.
/// </summary>
public sealed class ForwardTargetAllowlistTests : IAsyncDisposable
{
    private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

    private LiveStreamCoordinator? _coordinator;

    [Fact]
    public async Task A_file_forward_target_is_refused_and_the_reason_is_on_the_stream()
    {
        Assert.SkipUnless(Ffmpeg.IsPresent, "No FFmpeg. Run scripts/fetch-ffmpeg.sh.");

        FfmpegLibrary.EnsureLoaded();

        const string name = "allowlisted-camera";

        var (coordinator, sources) = Coordinator();

        await coordinator.CreateManualAsync(name, $"udp://127.0.0.1:{SrtSenders.FreePort()}");

        sources.Save(new LiveSource(
            name,
            null,
            Enabled: true,
            [new ForwardTarget("leak", "file:///tmp/somewhere-it-should-not-go.ts")],
            DateTimeOffset.UtcNow));

        await coordinator.TickAsync(CancellationToken.None);

        var stream = await coordinator.GetAsync(name);

        Assert.NotNull(stream);
        Assert.NotNull(stream.Forwards);

        var forward = Assert.Single(stream.Forwards);

        Assert.False(forward.Connected);
        Assert.Equal(0, forward.Bytes);
        Assert.NotNull(forward.Error);
        Assert.Contains("file", forward.Error);
    }

    /// <summary>
    /// Parking a source stops this service dialling out.
    ///
    /// It is the only reading of "disabled" an operator who has just switched a source off will
    /// accept. Leaving the pull running would make the toggle mean "stop trying again later", and
    /// the row would sit there disabled while its camera carried on arriving.
    /// </summary>
    [Fact]
    public async Task A_pulled_stream_stops_when_its_source_is_switched_off()
    {
        Assert.SkipUnless(Ffmpeg.IsPresent, "No FFmpeg. Run scripts/fetch-ffmpeg.sh.");

        FfmpegLibrary.EnsureLoaded();

        const string name = "parked-camera";

        var (coordinator, sources) = Coordinator();

        await coordinator.CreateManualAsync(name, $"udp://127.0.0.1:{SrtSenders.FreePort()}");

        sources.Save(new LiveSource(name, "udp://127.0.0.1:1", Enabled: true, [], DateTimeOffset.UtcNow));
        await coordinator.TickAsync(CancellationToken.None);

        Assert.True(coordinator.Owns(name));

        sources.Save(new LiveSource(name, "udp://127.0.0.1:1", Enabled: false, [], DateTimeOffset.UtcNow));
        await coordinator.TickAsync(CancellationToken.None);

        Assert.False(coordinator.Owns(name));
        Assert.Null(await coordinator.GetAsync(name));
    }

    /// <summary>
    /// Deliberately narrow, in the direction that matters: an absent row sweeps nothing.
    ///
    /// A stream created straight through the manual endpoint has no configured row at all, and
    /// deleting a configuration is not a licence to yank a live feed away from its viewers. If this
    /// ever fails, every manually created stream disappears two seconds after it starts.
    /// </summary>
    [Fact]
    public async Task A_pulled_stream_with_no_configured_row_is_left_alone()
    {
        Assert.SkipUnless(Ffmpeg.IsPresent, "No FFmpeg. Run scripts/fetch-ffmpeg.sh.");

        FfmpegLibrary.EnsureLoaded();

        const string name = "unconfigured-camera";

        var (coordinator, _) = Coordinator();

        await coordinator.CreateManualAsync(name, $"udp://127.0.0.1:{SrtSenders.FreePort()}");

        await coordinator.TickAsync(CancellationToken.None);

        Assert.True(coordinator.Owns(name));
    }

    /// <summary>
    /// A real coordinator over fakes, kept for disposal. One per test: the coordinator owns threads
    /// and a lifetime token, and sharing one between tests would let a stream from the first decide
    /// what the second sees.
    /// </summary>
    private (LiveStreamCoordinator Coordinator, FakeLiveSourceStore Sources) Coordinator()
    {
        var sources = new FakeLiveSourceStore();

        var options = Options.Create(new LiveOptions
        {
            NodeName = "pod-a",
            // A second rather than thirty, so the pulled input a stream needs in order to exist
            // gives up promptly and the test is not held open by a port nothing is sending to.
            ManualInputOptions = new Dictionary<string, string> { ["timeout"] = "1000000" },
        });

        _coordinator = new LiveStreamCoordinator(
            new StreamDemuxer(options, NullLogger<StreamDemuxer>.Instance),
            new InMemoryLiveStreamRegistry(),
            sources,
            new InMemoryLock(),
            new FakeMediaAnalyzer(),
            _services.GetRequiredService<IServiceScopeFactory>(),
            options,
            Options.Create(new MediaOptions()),
            new LiveMetrics(),
            NullLogger<LiveStreamCoordinator>.Instance);

        return (_coordinator, sources);
    }

    public async ValueTask DisposeAsync()
    {
        if (_coordinator is not null)
        {
            await _coordinator.DisposeAsync();
        }

        await _services.DisposeAsync();
    }
}

/// <summary>The configuration half, with no store behind it. Reads are all the coordinator makes.</summary>
internal sealed class FakeLiveSourceStore : ILiveSourceStore
{
    private readonly Dictionary<string, LiveSource> _sources = new(StringComparer.Ordinal);

    public void Save(LiveSource source) => _sources[source.Name] = source;

    public Task<IReadOnlyList<LiveSource>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LiveSource>>([.. _sources.Values]);

    public Task<LiveSource?> GetAsync(string name, CancellationToken cancellationToken = default)
        => Task.FromResult(_sources.GetValueOrDefault(name));

    public Task SaveAsync(LiveSource source, CancellationToken cancellationToken = default)
    {
        Save(source);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        _sources.Remove(name);

        return Task.CompletedTask;
    }
}
