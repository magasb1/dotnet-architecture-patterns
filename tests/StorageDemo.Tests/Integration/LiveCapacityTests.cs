using Microsoft.Extensions.DependencyInjection;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Tests.Infrastructure;

namespace StorageDemo.Tests.Integration;

/// <summary>
/// What a full replica does, through the real application with real encoders.
///
/// One replica rather than two, because capacity is entirely local: the limit counts what this pod
/// holds, and the answer is given on libsrt's thread from that count alone. The fixture is
/// <see cref="LiveReplicas"/> all the same, since starting an application with its own ports and a
/// registry a test can read is exactly what it does.
///
/// A pod with no limit accepts until it falls over, and the baseline measured what that looks like:
/// 250 streams reported live and contented while a fifth of the media arrived. The limit is how a
/// pod says no in one round trip instead, cheaply enough that the encoder lands somewhere else.
/// </summary>
public sealed class LiveCapacityTests : IAsyncLifetime
{
    private readonly LiveReplicas _replicas = new();

    private int _ingest;

    public ValueTask InitializeAsync()
    {
        _ingest = SrtSenders.FreePort();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _replicas.DisposeAsync();

    /// <summary>
    /// The refusal, and that it is the overload refusal rather than any other.
    ///
    /// FFmpeg never asks libsrt why it was turned away, so the caller cannot tell an overload from a
    /// conflict or a dead port. The meter can: the reject is tagged with the port and with a word
    /// from a closed set, and "overload" here is what an encoder's operator would eventually be
    /// shown. Fails if the count is read from the wrong place, or if the answer comes after the
    /// connection exists rather than during the handshake, which the empty registry entry catches.
    /// </summary>
    [Fact]
    public async Task A_full_pod_refuses_a_new_name_at_the_handshake()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        var host = _replicas.Start("pod-a", _ingest, maxStreams: 1);

        // Before the refusal it is measuring: a counter is an event, and a listener started
        // afterwards sees nothing of it.
        using var meters = new Meters(host.Services.GetRequiredService<LiveMetrics>());

        _replicas.Send(_ingest, "first-camera");

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync("first-camera") is { State: LiveStreamState.Live },
            TimeSpan.FromSeconds(40),
            "the first camera never went live");

        var second = _replicas.Send(_ingest, "second-camera");

        Assert.True(
            await SrtSenders.WasRefused(second, TimeSpan.FromSeconds(10)),
            $"a full pod admitted a second name: {SrtSenders.Complaints([second])}");

        // Refused at the handshake means nothing was ever claimed. A pod that accepted and then
        // dropped the connection would have left an entry behind.
        Assert.Null(await _replicas.Registry.GetAsync("second-camera"));

        var reject = Assert.Single(meters.Read(), measurement => measurement.Instrument == "live.rejects");

        Assert.Equal(
            [
                new KeyValuePair<string, object?>("port", _ingest),
                new KeyValuePair<string, object?>("reason", "overload"),
            ],
            reject.Tags);
    }

    /// <summary>
    /// The exception that matters more than the rule.
    ///
    /// A pod at its limit still owns the streams it took, and an encoder whose socket dropped is one
    /// of them coming back. Without this the pod refuses its own encoders after a blip and they land
    /// on a pod that has never heard of them, which for a full cluster means they land nowhere and
    /// the streams this pod was responsible for simply end.
    ///
    /// The limit is one and the pod is holding one, so the reconnect is admitted only because the
    /// name is already here: delete the owned-name clause and this times out. The start time is
    /// asserted too, because resuming the same stream is what makes it a reconnect rather than a
    /// second stream that happens to share a name.
    /// </summary>
    [Fact]
    public async Task A_full_pod_accepts_a_reconnect_of_a_name_it_already_owns()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "blinking-camera";

        // The production grace period, so the interrupted entry is still here when the encoder
        // comes back. At the fixture's five seconds the stream would be gone rather than resumed,
        // and a gone name is free for anybody, which is not what this test is about.
        _replicas.Start("pod-a", _ingest, graceSeconds: 30, maxStreams: 1);

        var first = _replicas.Send(_ingest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { State: LiveStreamState.Live, Packets: > 0 },
            TimeSpan.FromSeconds(40),
            "the camera never went live");

        var began = (await _replicas.Registry.GetAsync(name))!.StartedAt;

        SrtSenders.Kill(first);

        // The feed timeout declares it interrupted, and the same heartbeat pass refreshes the copy
        // of the registry the handshake reads, so this is also when the name lock lets go.
        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { State: LiveStreamState.Interrupted },
            TimeSpan.FromSeconds(40),
            "the feed never went interrupted");

        var again = _replicas.Send(_ingest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { State: LiveStreamState.Live, Packets: > 0 },
            TimeSpan.FromSeconds(40),
            $"a full pod refused its own encoder reconnecting: {SrtSenders.Complaints([again])}");

        Assert.Equal(began, (await _replicas.Registry.GetAsync(name))!.StartedAt);
    }

    /// <summary>
    /// The number an autoscaler would be trusting, against the dictionary it is meant to describe.
    ///
    /// <see cref="StorageDemo.Tests.Infrastructure.LiveMetricsTests"/> pins that the instrument reports what it was
    /// last told; this pins that what it is told is what the coordinator actually holds, through the
    /// real heartbeat, as a stream arrives and as it is swept away again. The gauge is republished
    /// once a beat, so it is at worst one beat behind the claim, which is the same freshness as
    /// everything else a replica publishes about itself.
    /// </summary>
    [Fact]
    public async Task The_owned_gauge_reports_what_the_coordinator_holds()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        var host = _replicas.Start("pod-a", _ingest);

        using var meters = new Meters(host.Services.GetRequiredService<LiveMetrics>());

        var sender = _replicas.Send(_ingest, "counted-camera");

        await LiveReplicas.Until(
            () => Task.FromResult(meters.Value("live.streams.owned") == 1),
            TimeSpan.FromSeconds(40),
            "the gauge never counted the stream the pod had taken");

        SrtSenders.Kill(sender);

        // Interrupted after the feed timeout, then dropped when the grace period expires. The
        // coordinator lets go of it in the same pass that republishes the gauge.
        await LiveReplicas.Until(
            () => Task.FromResult(meters.Value("live.streams.owned") == 0),
            TimeSpan.FromSeconds(40),
            "the gauge still counted a stream the pod had let go of");
    }
}
