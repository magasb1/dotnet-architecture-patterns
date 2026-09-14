using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Tests.Infrastructure;

namespace StorageDemo.Tests.Integration;

/// <summary>
/// The name lock across replicas, which is the half that cannot be seen on one host.
///
/// Two applications in one process sharing one registry and one lock; see <see cref="LiveReplicas"/>.
/// Everything here asserts against the shared registry directly, because the registry is the claim.
/// </summary>
public sealed class LiveNameLockTests : IAsyncLifetime
{
    private readonly LiveReplicas _replicas = new();

    private int _aIngest;
    private int _bIngest;

    public ValueTask InitializeAsync()
    {
        _aIngest = SrtSenders.FreePort();
        _bIngest = SrtSenders.FreePort();

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => _replicas.DisposeAsync();

    /// <summary>
    /// The rule the owner asked for, seen from the other pod: while a name is live on A, B refuses
    /// it at the handshake. Fails if the handshake cache is never populated or is consulted wrongly,
    /// which on one host would be hidden by the claim check behind it.
    /// </summary>
    [Fact]
    public async Task A_name_live_on_one_pod_is_refused_on_the_other()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "shared-camera";

        _replicas.Start("pod-a", _aIngest);
        _replicas.Start("pod-b", _bIngest);

        _replicas.Send(_aIngest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { State: LiveStreamState.Live, Packets: > 0 },
            TimeSpan.FromSeconds(40),
            "A never went live");

        // One full beat, which is how long B's copy of the registry may be behind it.
        await Task.Delay(LiveStreamCoordinator.Beat + TimeSpan.FromSeconds(1));

        var second = _replicas.Send(_bIngest, name);

        Assert.True(
            await SrtSenders.WasRefused(second, TimeSpan.FromSeconds(10)),
            $"B admitted a name A holds: {SrtSenders.Complaints([second])}");

        Assert.Equal("pod-a", (await _replicas.Registry.GetAsync(name))?.Owner);
    }

    /// <summary>
    /// The lock lets go three beats after a pod stops saying it is alive, not thirty seconds later
    /// when the stream is finally delisted. A force-killed pod's encoders reconnect in one to seven
    /// seconds, and a lock on the grace period would turn that into half a minute of black screen.
    ///
    /// The dead owner is a registry entry rather than a real host, because a pod that is killed
    /// outright is exactly an entry nothing will ever update again.
    /// </summary>
    [Fact]
    public async Task A_name_whose_owner_has_stopped_heartbeating_is_free()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "abandoned-camera";

        _replicas.Start("pod-b", _bIngest);

        await _replicas.Registry.UpsertAsync(LiveReplicas.Entry(name, "ghost", DateTimeOffset.UtcNow.AddSeconds(-10)));

        _replicas.Send(_bIngest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { Owner: "pod-b", State: LiveStreamState.Live },
            TimeSpan.FromSeconds(40),
            "B never took a name whose owner was gone");
    }

    /// <summary>
    /// A stream that moves to another replica is the same stream resuming, so it keeps its start
    /// time. On one host that is free, because the entry the replica already holds is reused; across
    /// two it has to be carried through the registry, and it was not. The symptom is a stream whose
    /// start time jumps forward every time its owner is replaced, which makes "this feed has been up
    /// for two hours" a lie told once per rolling update.
    ///
    /// docs/replica-failover.md states the start time survives a move. This is that claim, tested.
    /// </summary>
    [Fact]
    public async Task A_name_resumed_on_another_pod_keeps_its_start_time()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "moved-camera";
        var began = DateTimeOffset.UtcNow.AddMinutes(-7);

        // The production grace period rather than this fixture's five seconds, because the window
        // this test lives in does not exist at five: a name frees three beats after its owner stops
        // heartbeating, which is six seconds, and a stream whose heartbeat is six seconds old is
        // already gone under a five second grace. A stream that is gone leaves no trace and its name
        // is genuinely new the next time somebody publishes it, which is why the claim only carries
        // a start time forward for an entry that is still listed.
        _replicas.Start("pod-b", _bIngest, graceSeconds: 30);

        // A pod that was killed outright: an entry nothing will ever update again. Eight seconds
        // frees the name, and leaves the stream listed as interrupted rather than gone.
        await _replicas.Registry.UpsertAsync(LiveReplicas.Entry(name, "ghost", DateTimeOffset.UtcNow.AddSeconds(-8), began));

        _replicas.Send(_bIngest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { Owner: "pod-b", State: LiveStreamState.Live },
            TimeSpan.FromSeconds(40),
            "B never took a name whose owner was gone");

        Assert.Equal(began, (await _replicas.Registry.GetAsync(name))!.StartedAt);
    }

    /// <summary>
    /// The graceful twin of the test above, and the one a rolling update exercises. A pod that is
    /// stopped cleanly used to remove its entries, so the replica taking the name over found nothing
    /// to resume from and the stream came back new: a crash kept the start time and a clean shutdown
    /// lost it. Now a stopping pod leaves each entry interrupted, which is the same thing a reconnect
    /// finds after a dropped feed, and the same claim carries the start time forward.
    ///
    /// The fixture's dispose is the graceful path: the host stops, the heartbeat with it, and the
    /// coordinator is disposed by the container as it would be by SIGTERM.
    /// </summary>
    [Fact]
    public async Task A_name_resumed_after_a_graceful_shutdown_keeps_its_start_time()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "updated-camera";

        // The production grace period, for the same reason as above: at five seconds the entry A
        // leaves behind would be gone before B's encoder reconnects.
        var a = _replicas.Start("pod-a", _aIngest, graceSeconds: 30);
        _replicas.Start("pod-b", _bIngest, graceSeconds: 30);

        var first = _replicas.Send(_aIngest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { Owner: "pod-a", State: LiveStreamState.Live, Packets: > 0 },
            TimeSpan.FromSeconds(40),
            "A never went live");

        var began = (await _replicas.Registry.GetAsync(name))!.StartedAt;

        await a.DisposeAsync();
        SrtSenders.Kill(first);

        var left = await _replicas.Registry.GetAsync(name);

        Assert.NotNull(left);
        Assert.Equal(LiveStreamState.Interrupted, left.State);
        Assert.Equal("pod-a", left.Owner);

        // One full beat, which is how long B's handshake copy of the registry may still say A holds
        // the name live. A real encoder retries through that window; ffmpeg here does not.
        await Task.Delay(LiveStreamCoordinator.Beat + TimeSpan.FromSeconds(1));

        var second = _replicas.Send(_bIngest, name);

        await LiveReplicas.Until(
            async () => await _replicas.Registry.GetAsync(name) is { Owner: "pod-b", State: LiveStreamState.Live },
            TimeSpan.FromSeconds(40),
            $"B never took the name A left behind ({SrtSenders.Complaints([second])})");

        Assert.Equal(began, (await _replicas.Registry.GetAsync(name))!.StartedAt);
        Assert.Single(await _replicas.Registry.ListAsync());
    }

    /// <summary>
    /// The second enforcement point, on its own.
    ///
    /// The handshake answers from a copy of the registry taken once a beat, so for up to a beat two
    /// replicas can both admit the same name. B's copy is made blind to this name here, which is
    /// that window held open rather than raced for: B admits the caller, and the claim behind the
    /// handshake is what refuses it. The encoder sees a closed socket rather than a rejection,
    /// because by then the connection exists.
    ///
    /// A cache bug would hide behind the handshake check in every other test in this file. This one
    /// is the reason the claim is still authoritative.
    /// </summary>
    [Fact]
    public async Task The_race_window_resolves_to_one_owner()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "raced-camera";

        _replicas.Start("pod-b", _bIngest, new HiddenFromListing(_replicas.Registry, name));

        await _replicas.Registry.UpsertAsync(LiveReplicas.Entry(name, "pod-a", DateTimeOffset.UtcNow));

        var sender = _replicas.Send(_bIngest, name);

        // Accepted and then dropped, which is what a claim-time refusal looks like from the caller.
        Assert.True(
            await LiveReplicas.Exited(sender, TimeSpan.FromSeconds(20)),
            $"B kept a connection for a name it does not own: {SrtSenders.Complaints([sender])}");

        Assert.Equal("pod-a", (await _replicas.Registry.GetAsync(name))?.Owner);
    }

    /// <summary>
    /// A registry that keeps one name out of its listing and answers for it normally otherwise.
    ///
    /// That is precisely the shape of the window this rule has to survive: the handshake reads a
    /// copy of the listing and cannot see the name, while the claim reads the entry itself and can.
    /// Holding the window open beats racing for it, which would be a test that passes by luck.
    /// </summary>
    private sealed class HiddenFromListing(ILiveStreamRegistry inner, string hidden) : ILiveStreamRegistry
    {
        public Task UpsertAsync(LiveStream stream, CancellationToken cancellationToken = default)
            => inner.UpsertAsync(stream, cancellationToken);

        public Task RemoveAsync(string name, CancellationToken cancellationToken = default)
            => inner.RemoveAsync(name, cancellationToken);

        public Task<LiveStream?> GetAsync(string name, CancellationToken cancellationToken = default)
            => inner.GetAsync(name, cancellationToken);

        public async Task<IReadOnlyList<LiveStream>> ListAsync(CancellationToken cancellationToken = default)
            => [.. (await inner.ListAsync(cancellationToken)).Where(stream => stream.Name != hidden)];
    }
}
