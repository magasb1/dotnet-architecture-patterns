using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StorageDemo.Core.Coordination;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Tests.Infrastructure;

namespace StorageDemo.Tests.Integration;

/// <summary>
/// The name lock across replicas, which is the half that cannot be seen on one host.
///
/// Two applications in one process, each with its own ingest port and node name, sharing one
/// <see cref="InMemoryLiveStreamRegistry"/> and one <see cref="InMemoryLock"/>. That is what two
/// pods are: the same program, the same shared state, no Redis needed to prove it. Everything here
/// asserts against the shared registry directly, because the registry is the claim.
/// </summary>
public sealed class LiveNameLockTests : IAsyncLifetime
{
    private const string NoLibsrt = "libsrt is not installed. Run scripts/fetch-libsrt.sh.";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "storage-demo-name-lock-tests",
        Guid.NewGuid().ToString("N"));

    private readonly InMemoryLiveStreamRegistry _shared = new();
    private readonly InMemoryLock _coordination = new();
    private readonly List<Process> _senders = [];
    private readonly List<WebApplicationFactory<Program>> _hosts = [];
    private readonly List<HttpClient> _clients = [];

    private int _aIngest;
    private int _bIngest;

    private static bool HasSrt() => FfmpegLibrary.InputProtocols().Contains("srt");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _aIngest = SrtSenders.FreePort();
        _bIngest = SrtSenders.FreePort();

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders)
        {
            SrtSenders.Kill(sender);
        }

        foreach (var client in _clients)
        {
            client.Dispose();
        }

        foreach (var host in _hosts)
        {
            await host.DisposeAsync();
        }

        for (var attempt = 0; attempt < 3 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }
    }

    /// <summary>
    /// The rule the owner asked for, seen from the other pod: while a name is live on A, B refuses
    /// it at the handshake. Fails if the handshake cache is never populated or is consulted wrongly,
    /// which on one host would be hidden by the claim check behind it.
    /// </summary>
    [Fact]
    public async Task A_name_live_on_one_pod_is_refused_on_the_other()
    {
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        const string name = "shared-camera";

        Start("pod-a", _aIngest, _shared);
        Start("pod-b", _bIngest, _shared);

        Send(_aIngest, name);

        await Until(
            async () => await _shared.GetAsync(name) is { State: LiveStreamState.Live, Packets: > 0 },
            TimeSpan.FromSeconds(40),
            "A never went live");

        // One full beat, which is how long B's copy of the registry may be behind it.
        await Task.Delay(LiveStreamCoordinator.Beat + TimeSpan.FromSeconds(1));

        var second = Send(_bIngest, name);

        Assert.True(
            await SrtSenders.WasRefused(second, TimeSpan.FromSeconds(10)),
            $"B admitted a name A holds: {SrtSenders.Complaints([second])}");

        Assert.Equal("pod-a", (await _shared.GetAsync(name))?.Owner);
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
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        const string name = "abandoned-camera";

        Start("pod-b", _bIngest, _shared);

        await _shared.UpsertAsync(Entry(name, "ghost", DateTimeOffset.UtcNow.AddSeconds(-10)));

        Send(_bIngest, name);

        await Until(
            async () => await _shared.GetAsync(name) is { Owner: "pod-b", State: LiveStreamState.Live },
            TimeSpan.FromSeconds(40),
            "B never took a name whose owner was gone");
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
        Assert.SkipUnless(Srt.IsAvailable, NoLibsrt);
        Assert.SkipUnless(HasSrt(), "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh.");

        const string name = "raced-camera";

        Start("pod-b", _bIngest, new HiddenFromListing(_shared, name));

        await _shared.UpsertAsync(Entry(name, "pod-a", DateTimeOffset.UtcNow));

        var sender = Send(_bIngest, name);

        // Accepted and then dropped, which is what a claim-time refusal looks like from the caller.
        Assert.True(
            await Exited(sender, TimeSpan.FromSeconds(20)),
            $"B kept a connection for a name it does not own: {SrtSenders.Complaints([sender])}");

        Assert.Equal("pod-a", (await _shared.GetAsync(name))?.Owner);
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

    /// <summary>One replica: its own ports, its own name, somebody else's registry.</summary>
    private void Start(string node, int ingestPort, ILiveStreamRegistry registry)
    {
        var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Provider", "FileSystem");
            builder.UseSetting("Storage:FileSystem:RootPath", Path.Combine(_root, node, "files"));
            builder.UseSetting("Database:Provider", "LiteDb");
            builder.UseSetting("Database:LiteDb:Path", Path.Combine(_root, node, "db", "app.db"));
            builder.UseSetting("StorageMonitor:Enabled", "false");
            builder.UseSetting("Live:Enabled", "true");
            builder.UseSetting("Live:NodeName", node);
            builder.UseSetting("Live:IngestPort", ingestPort.ToString());
            builder.UseSetting("Live:ConsumptionPort", (ingestPort + 1).ToString());
            builder.UseSetting("Live:GracePeriodSeconds", "5");
            builder.UseSetting("Live:FeedTimeoutSeconds", "2");
            builder.UseEnvironment("Production");

            // Registered last, so these instances are what the application resolves. Sharing them is
            // the whole fixture: two replicas differ only in what they hold locally.
            builder.ConfigureServices(services =>
            {
                services.AddSingleton(registry);
                services.AddSingleton<IDistributedLock>(_coordination);
            });
        });

        _hosts.Add(host);

        // The host starts on the first client, and with it the ingest port and the heartbeat.
        _clients.Add(host.CreateClient());
    }

    private Process Send(int port, string name)
    {
        var sender = SrtSenders.StartSender(port, $"#!::r={name},m=publish");

        _senders.Add(sender);

        return sender;
    }

    private static LiveStream Entry(string name, string owner, DateTimeOffset heartbeat)
        => new(
            name,
            LiveStreamState.Live,
            heartbeat,
            heartbeat,
            owner,
            null,
            null,
            1,
            1,
            false,
            true,
            false,
            0,
            null,
            null,
            "aaaaaaaa");

    private static async Task<bool> Exited(Process caller, TimeSpan within)
    {
        using var deadline = new CancellationTokenSource(within);

        try
        {
            await caller.WaitForExitAsync(deadline.Token);

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task Until(Func<Task<bool>> condition, TimeSpan timeout, string describe)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        Assert.Fail($"{describe} within {timeout}");
    }
}
