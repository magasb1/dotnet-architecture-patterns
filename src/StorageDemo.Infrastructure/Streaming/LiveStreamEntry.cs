using Microsoft.Extensions.Logging;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// One stream this replica owns: its hub, the decoder and harvester behind its preview, whatever
/// connection is currently feeding it, and any recording that is running.
///
/// The hub outlives the connection, which is the whole point of the interrupted state. A feed that
/// stops leaves everything here alive for the grace period, and a reconnect under the same name
/// attaches a new demultiplexer to this same hub rather than creating a second stream.
/// </summary>
public sealed class LiveStreamEntry : IAsyncDisposable
{
    private readonly Lock _gate = new();

    public LiveStreamEntry(StreamHub hub, Harvester harvester, ILogger logger, bool manual, string? manualUrl)
    {
        Hub = hub;
        Harvester = harvester;
        Decoder = new FrameDecoder(hub, logger);
        Manual = manual;
        ManualUrl = manualUrl;

        PreviewSubscription = SubscribePreview(Decoder, harvester);
        Decoding = Decoder.RunAsync(Lifetime.Token);
    }

    /// <summary>The harvester's handler takes a libav frame, so binding it needs an unsafe context.</summary>
    private static unsafe IDisposable SubscribePreview(FrameDecoder decoder, Harvester harvester)
        => decoder.Subscribe(DecodeRate.Keyframes, harvester.OnFrame);

    public StreamHub Hub { get; }

    public string Name => Hub.Name;

    public Harvester Harvester { get; }

    public FrameDecoder Decoder { get; }

    /// <summary>Ends the stream: the decoder, the current feed and any recording.</summary>
    public CancellationTokenSource Lifetime { get; } = new();

    public Task Decoding { get; }

    private IDisposable PreviewSubscription { get; }

    /// <summary>
    /// When the stream began, which is not when this entry was built: a stream that moves to
    /// another replica is the same stream resuming, and its start time has to move with it or the
    /// name is the only thing that survived. Set from the registry by <c>Resumes</c> under the
    /// claim lock, before anything reads it.
    /// </summary>
    public DateTimeOffset StartedAt { get; private set; } = DateTimeOffset.UtcNow;

    /// <summary>Adopts the start time of the stream this entry is taking over.</summary>
    public void Resumes(DateTimeOffset startedAt) => StartedAt = startedAt;

    public bool Manual { get; }

    public string? ManualUrl { get; }

    /// <summary>Tells one connection attempt from the next in a log. Nothing looks a stream up by it.</summary>
    public string? ConnectionId { get; private set; }

    /// <summary>Cancels only the current connection, leaving the hub and the recording alive.</summary>
    public CancellationTokenSource? Feed { get; private set; }

    public Task? Feeding { get; private set; }

    /// <summary>
    /// The socket the current feed is arriving on, so the heartbeat can ask libsrt how that
    /// connection is actually doing rather than inferring it from a byte count. Null for a pulled
    /// stream, which libav dials and which therefore has no socket this service holds.
    /// </summary>
    public SrtSocketStream? Transport { get; private set; }

    public StreamRecorder? Recorder { get; private set; }

    public Task<Guid?>? Recording { get; private set; }

    /// <summary>Set when this replica has been displaced and is standing down.</summary>
    public bool StandingDown { get; set; }

    public bool FeedRunning => Feeding is { IsCompleted: false };

    /// <summary>
    /// Hands the entry a new connection, cancelling whatever was feeding it.
    ///
    /// The cancellation is a guard rather than a take-over. A live name is locked now, so a second
    /// connection only reaches here once the feed it replaces has already stopped and there is
    /// nothing left to cancel. It stays because the alternative to a no-op cancel is two feeds
    /// writing into one hub, and that would be discovered as corrupted output.
    /// </summary>
    public async Task<CancellationToken> TakeOverAsync(string connectionId)
    {
        CancellationTokenSource? previous;
        Task? previousTask;

        lock (_gate)
        {
            previous = Feed;
            previousTask = Feeding;

            Feed = CancellationTokenSource.CreateLinkedTokenSource(Lifetime.Token);
            ConnectionId = connectionId;
        }

        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        if (previousTask is not null)
        {
            // Bounded: the old demultiplexer notices cancellation between reads, so it can be
            // waiting out a socket timeout. Nothing here depends on it having finished.
            await Task.WhenAny(previousTask, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        previous?.Dispose();

        lock (_gate)
        {
            return Feed!.Token;
        }
    }

    public void Feeds(Task feeding, SrtSocketStream? transport = null)
    {
        lock (_gate)
        {
            Feeding = feeding;
            Transport = transport;
        }
    }

    public void Records(StreamRecorder recorder, Task<Guid?> running)
    {
        lock (_gate)
        {
            Recorder = recorder;
            Recording = running;
        }
    }

    /// <summary>Forgets a finished recording, so the next trigger starts a new one.</summary>
    public void RecordingEnded()
    {
        lock (_gate)
        {
            Recorder = null;
            Recording = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Recorder?.Stop();

        await Lifetime.CancelAsync();

        if (Feeding is not null)
        {
            await Task.WhenAny(Feeding, Task.Delay(TimeSpan.FromSeconds(10)));
        }

        // The recording is awaited rather than abandoned: it still has a file to upload, and that
        // is what turns it into the document somebody asked for.
        if (Recording is not null)
        {
            await Task.WhenAny(Recording, Task.Delay(TimeSpan.FromSeconds(60)));
        }

        Hub.Close();

        await Task.WhenAny(Decoding, Task.Delay(TimeSpan.FromSeconds(10)));

        PreviewSubscription.Dispose();
        Decoder.Dispose();
        Feed?.Dispose();
        Lifetime.Dispose();
        Hub.Dispose();
    }
}
