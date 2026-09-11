using System.Collections.Concurrent;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Coordination;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// Owns every stream this replica holds, and keeps the shared registry telling the truth about
/// them.
///
/// The split that matters: the connection and everything hanging off it are local, because they
/// cannot be anything else, while what a caller asks about is in the registry, because a caller
/// may reach any replica. A request that needs the actual bytes is forwarded to the owner; the
/// rest are answered from the registry by whoever received them.
///
/// A stream never becomes a document by itself. Documents come only from snapshots and recordings
/// someone asked for, which is what makes unattended ingest safe to leave running.
/// </summary>
public sealed class LiveStreamCoordinator(
    StreamDemuxer demuxer,
    ILiveStreamRegistry registry,
    IDistributedLock coordination,
    IMediaAnalyzer analyzer,
    IServiceScopeFactory scopeFactory,
    IOptions<LiveOptions> options,
    IOptions<MediaOptions> mediaOptions,
    ILogger<LiveStreamCoordinator> logger) : ILiveStreamService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, LiveStreamEntry> _local = new(StringComparer.Ordinal);
    private readonly LiveOptions _options = options.Value;
    private readonly CancellationTokenSource _shutdown = new();

    private bool _disposed;

    public string Owner { get; } = options.Value.NodeName is { Length: > 0 } name
        ? name
        : Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;

    public IReadOnlyList<string> Transports
    {
        get
        {
            var inputs = FfmpegLibrary.InputProtocols();

            return [.. FfmpegLibrary.OutputProtocols().Where(inputs.Contains)];
        }
    }

    private TimeSpan Grace => TimeSpan.FromSeconds(_options.GracePeriodSeconds);

    public async Task<IReadOnlyList<LiveStream>> StreamsAsync(CancellationToken cancellationToken = default)
    {
        var streams = await registry.ListAsync(cancellationToken);

        // A stream whose owner has stopped heartbeating is gone, and after that it leaves no
        // trace: the registry is a picture of what is live now, and the documents a stream
        // produced are what outlives it.
        return [.. streams.Where(stream => !LiveStreamStaleness.IsGone(stream, Grace))];
    }

    public async Task<LiveStream?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        var stream = await registry.GetAsync(name, cancellationToken);

        return stream is null || LiveStreamStaleness.IsGone(stream, Grace) ? null : stream;
    }

    public bool Owns(string name) => _local.ContainsKey(name);

    public byte[]? Preview(string name)
        => _local.TryGetValue(name, out var entry) ? entry.Harvester.Preview : null;

    /// <summary>
    /// Takes an accepted socket off the accept thread. Everything real happens on another thread,
    /// because every millisecond spent here is a millisecond the ingest port is not listening.
    ///
    /// A <see cref="SrtSocketStream"/> and not yet an <see cref="AvioReader"/>: the reader's
    /// context is freed by the demultiplexer and by nothing else, so it is built only where
    /// <c>demuxer.Run</c> is certain to be called. Everything up to that point carries the stream.
    /// </summary>
    public void OnAccepted(AcceptedSocket socket)
    {
        var name = socket.Name;
        var transport = new SrtSocketStream(socket.Release(), writable: false);
        var connectionId = Guid.NewGuid().ToString("N")[..8];

        _ = Task.Run(() => AttachAsync(name, transport, connectionId));
    }

    private async Task AttachAsync(string name, Stream transport, string connectionId)
    {
        LiveStreamEntry? entry = null;

        try
        {
            entry = _local.GetOrAdd(name, Create);

            if (!await ClaimAsync(entry, cancellationToken: CancellationToken.None))
            {
                // Another replica took the name between the accept and the claim. It is the newer
                // connection, so this one stands down rather than fighting for it.
                logger.LogInformation("'{Name}' was claimed elsewhere while it was being attached", name);

                transport.Dispose();

                return;
            }

            var feed = await entry.TakeOverAsync(connectionId);
            var running = entry;

            entry.Feeds(Task.Factory.StartNew(
                () => Feed(running, transport, feed),
                TaskCreationOptions.LongRunning));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Attaching '{Name}' failed", name);

            transport.Dispose();
        }
    }

    /// <summary>
    /// Wraps the socket as a libav transport and demultiplexes it until the feed ends.
    ///
    /// The reader is built here, a line before the call that consumes it, and never earlier. Only
    /// <see cref="StreamDemuxer.Run(AVIOContext*, StreamHub, CancellationToken)"/> frees an
    /// <c>AVIOContext</c>, so one allocated on a path that can still bail - a claim lost while
    /// attaching - would be leaked. Disposing the reader afterwards closes the socket and nothing
    /// else, the context being already gone.
    /// </summary>
    private unsafe void Feed(LiveStreamEntry entry, Stream transport, CancellationToken feed)
    {
        using var reader = new AvioReader(transport);

        var outcome = demuxer.Run(reader.Context, entry.Hub, feed);

        logger.LogInformation(
            "The feed for '{Name}' ({Connection}) ended: {Outcome}",
            entry.Name,
            entry.ConnectionId,
            outcome);
    }

    private LiveStreamEntry Create(string name) => Create(name, manual: false, manualUrl: null);

    private LiveStreamEntry Create(string name, bool manual, string? manualUrl)
    {
        var hub = new StreamHub(name, _options, logger);

        var harvester = new Harvester(
            mediaOptions.Value.ThumbnailSize,
            mediaOptions.Value.ThumbnailQuality,
            TimeSpan.FromSeconds(_options.PreviewIntervalSeconds));

        logger.LogInformation("Stream '{Name}' is now on air", name);

        return new LiveStreamEntry(hub, harvester, logger, manual, manualUrl);
    }

    /// <summary>
    /// Records this replica as the owner of the name.
    ///
    /// The newest connection wins. A replica finding the name already held takes it anyway; the
    /// previous owner discovers on its next heartbeat that it has lost the claim, shuts its hub
    /// down and closes any recording as a complete document. Refusing the newcomer was rejected
    /// because it makes recovery wait on a timeout this service does not control: an encoder
    /// actively pushing bytes is more real than a socket that has not yet noticed its peer is gone.
    ///
    /// The claim is the owner field of the registry entry, and the distributed lock serialises the
    /// moment of taking it rather than being held for the stream's life. See the map: the design
    /// said the claim was held on the lock and renewed by the heartbeat, and the lock this
    /// repository has can neither be taken over nor renewed. Nothing waits on it.
    /// </summary>
    private async Task<bool> ClaimAsync(LiveStreamEntry entry, CancellationToken cancellationToken)
    {
        var gate = await coordination.TryAcquireAsync(
            $"live-claim:{entry.Name}",
            TimeSpan.FromSeconds(10),
            cancellationToken);

        try
        {
            var existing = await registry.GetAsync(entry.Name, cancellationToken);

            if (existing is not null
                && existing.Owner != Owner
                && !LiveStreamStaleness.IsGone(existing, Grace))
            {
                logger.LogInformation(
                    "Taking '{Name}' over from {Previous}, which will stand down on its next heartbeat",
                    entry.Name,
                    existing.Owner);
            }

            await registry.UpsertAsync(Describe(entry, LiveStreamState.Live), cancellationToken);

            return true;
        }
        finally
        {
            if (gate is not null)
            {
                await gate.DisposeAsync();
            }
        }
    }

    public async Task<LiveStream> CreateManualAsync(
        string name,
        string url,
        CancellationToken cancellationToken = default)
    {
        if (!StreamName.TryParse(name, out var parsed, out var rejection))
        {
            throw new ArgumentException(rejection, nameof(name));
        }

        RequireAllowed(url);

        // One namespace and one claim. A manual stream is simply one that claimed its name early,
        // and an encoder presenting that name is the same conflict as any other.
        var entry = _local.GetOrAdd(parsed, _ => Create(parsed, manual: true, manualUrl: url));

        await ClaimAsync(entry, cancellationToken);

        var feed = await entry.TakeOverAsync(Guid.NewGuid().ToString("N")[..8]);
        var running = entry;

        entry.Feeds(Task.Factory.StartNew(
            () => demuxer.Run(url, _options.ManualInputOptions, running.Hub, feed),
            TaskCreationOptions.LongRunning));

        return Describe(entry, LiveStreamState.Live);
    }

    public async Task<Guid?> SnapshotAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_local.TryGetValue(name, out var entry))
        {
            return null;
        }

        var (bytes, note) = await CaptureAsync(entry, cancellationToken);

        if (bytes is null)
        {
            return null;
        }

        var takenAt = DateTimeOffset.UtcNow;

        var metadata = new Dictionary<string, string>
        {
            ["Live stream"] = name,
            ["Captured"] = takenAt.ToString("u"),
        };

        if (note is not null)
        {
            metadata["Snapshot"] = note;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

        using var content = new MemoryStream(bytes);

        // Named by stream and wall-clock capture time, matching recordings, so the two sit
        // together and read as related. A live feed has no beginning, so an offset into it would
        // mean nothing to a person.
        var document = await documents.UploadAsync(
            $"{FileName(name)}-{takenAt:yyyyMMdd-HHmmss}.jpg",
            content,
            "image/jpeg",
            cancellationToken,
            metadata);

        logger.LogInformation("Snapshot of '{Name}' stored as {DocumentId}", name, document.Id);

        return document.Id;
    }

    /// <summary>
    /// A snapshot is a fresh decode of the newest segment, not the harvester's frame.
    ///
    /// The harvester decodes keyframes only at the rate a preview needs, so what it holds is stale
    /// by up to a keyframe interval plus the preview cadence: about three seconds for a fine sender
    /// and about twelve for a coarse one. A snapshot is a deliberate act performed once, and one
    /// decode is nothing next to being twelve seconds wrong about the moment somebody meant to
    /// capture.
    ///
    /// When the stream has nowhere to start, the harvester's picture is all there is, and the
    /// document says so rather than quietly being older than it looks.
    /// </summary>
    private async Task<(byte[]? Bytes, string? Note)> CaptureAsync(
        LiveStreamEntry entry,
        CancellationToken cancellationToken)
    {
        var packets = Newest(entry);

        if (packets is null)
        {
            return entry.Harvester.Preview is { } preview
                ? (preview, "Taken from the live preview, because this feed has sent no keyframe "
                    + "recently enough to decode from. It is smaller and older than a snapshot "
                    + "normally is.")
                : (null, null);
        }

        using var container = new MemoryStream();

        Mux(entry, packets, container);

        container.Position = 0;

        return (await analyzer.LatestFrameAsync(container, "snapshot.ts", cancellationToken), null);
    }

    private static MediaPacket[]? Newest(LiveStreamEntry entry)
        => entry.Hub.Layout is null ? null : entry.Hub.NewestStartablePackets();

    private static void Mux(LiveStreamEntry entry, MediaPacket[] packets, Stream destination)
    {
        using var muxer = new PacketMuxer(destination, entry.Hub.Layout!);

        foreach (var packet in packets)
        {
            muxer.Write(packet);
        }

        muxer.Close();
    }

    public Task<RecordingStatus?> RecordAsync(
        string name,
        TimeSpan? duration,
        CancellationToken cancellationToken = default)
    {
        if (!_local.TryGetValue(name, out var entry))
        {
            return Task.FromResult<RecordingStatus?>(null);
        }

        // One recording at a time per stream. A trigger arriving while one runs extends its end
        // rather than starting a second, so continuous detection produces one clip covering the
        // whole event instead of a drift of overlapping near-duplicates.
        if (entry.Recorder is { Finished: false } running)
        {
            running.Extend(duration);

            return Task.FromResult<RecordingStatus?>(running.Status);
        }

        var recorder = new StreamRecorder(entry.Hub, _options, scopeFactory, logger, duration);

        entry.Records(recorder, recorder.RunAsync(entry.Lifetime.Token));

        logger.LogInformation(
            "Recording '{Name}' as {RecordingId}, due to end at {EndsAt}",
            name,
            recorder.Id,
            recorder.EndsAt);

        // Returns immediately. The recording then runs here and has no further relationship with
        // whoever asked for it: closing the client, losing it, or never having had one changes
        // nothing.
        return Task.FromResult<RecordingStatus?>(recorder.Status);
    }

    public Task<bool> StopRecordingAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_local.TryGetValue(name, out var entry) || entry.Recorder is not { Finished: false } recorder)
        {
            return Task.FromResult(false);
        }

        recorder.Stop();

        return Task.FromResult(true);
    }

    public async Task<bool> StopAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!_local.TryRemove(name, out var entry))
        {
            return await GetAsync(name, cancellationToken) is not null;
        }

        await EndAsync(entry, "it was stopped");

        return true;
    }

    /// <summary>
    /// What a viewer asking to start this far back would actually get, which is at least what it
    /// asked for. Answered before a byte is written, so the response can say so in a header:
    /// asking for twenty seconds and receiving twenty-six is normal rather than an error.
    /// </summary>
    public double ResolvePreroll(string name, double seconds)
        => _local.TryGetValue(name, out var entry) ? entry.Hub.ResolvePreroll(seconds) : 0;

    public Task WriteToViewerAsync(
        ViewerRequest request,
        Stream destination,
        CancellationToken cancellationToken = default)
        => WriteToViewerAsync(request, destination, continueFromSeconds: 0, cancellationToken);

    /// <param name="continueFromSeconds">
    /// Where this viewer's timeline has already reached, when it is being handed on from another
    /// replica mid-connection. Zero for a viewer that has just arrived.
    /// </param>
    /// <returns>How far the timeline reached, so whatever serves this viewer next can carry on.</returns>
    public Task<double> WriteToViewerAsync(
        ViewerRequest request,
        Stream destination,
        double continueFromSeconds,
        CancellationToken cancellationToken = default)
        => _local.TryGetValue(request.Name, out var entry)
            ? Serve(entry, request, destination, continueFromSeconds, cancellationToken)
            : throw new InvalidOperationException($"'{request.Name}' is not running on {Owner}.");

    /// <summary>
    /// Feeds one viewer until it leaves or the stream ends.
    ///
    /// During an interruption this simply has nothing to write, and the connection stays open. The
    /// client already knows the stream is interrupted from its state, and closing would push every
    /// viewer into reconnecting at the exact moment a reconnect storm is under way on ingest.
    /// </summary>
    private async Task<double> Serve(
        LiveStreamEntry entry,
        ViewerRequest request,
        Stream destination,
        double continueFromSeconds,
        CancellationToken cancellationToken)
    {
        var layout = entry.Hub.Layout;

        if (layout is null)
        {
            return continueFromSeconds;
        }

        using var subscription = entry.Hub.Subscribe(
            _options.ViewerQueuePackets,
            OverflowPolicy.SkipToLive,
            streamIndexes: [],
            request.Preroll);

        using var muxer = new PacketMuxer(destination, layout, "mpegts", continueFromSeconds);

        try
        {
            await foreach (var packet in subscription.Packets.ReadAllAsync(cancellationToken))
            {
                muxer.Write(packet);

                if (muxer.Fault is not null)
                {
                    break;
                }
            }

        }
        catch (OperationCanceledException)
        {
            // The viewer closed the player. Normal.
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "A viewer of '{Name}' went away", entry.Name);
        }

        // No trailer: this connection may be handed straight on to another replica, and a trailer
        // would tell the player the stream had ended when it has not.
        muxer.Abandon();

        return muxer.TimelineSeconds;
    }

    /// <summary>
    /// One pass of the heartbeat: republish what is running here, stand down where this replica
    /// has lost a name, retire streams whose grace period has expired, and close recordings that
    /// have reached their end.
    /// </summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        foreach (var entry in _local.Values.ToList())
        {
            try
            {
                await TickAsync(entry, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The heartbeat for '{Name}' failed", entry.Name);
            }
        }
    }

    private async Task TickAsync(LiveStreamEntry entry, CancellationToken cancellationToken)
    {
        if (entry.Recorder is { } recorder && (recorder.Finished || recorder.Due()))
        {
            if (!recorder.Finished)
            {
                recorder.Stop();
            }
            else
            {
                entry.RecordingEnded();
            }
        }

        var shared = await registry.GetAsync(entry.Name, cancellationToken);

        if (shared is not null && shared.Owner != Owner && !LiveStreamStaleness.IsGone(shared, Grace))
        {
            // Displaced. The newest connection won the name somewhere else, so everything here
            // shuts down and any recording closes as a complete document rather than moving.
            logger.LogInformation("'{Name}' now belongs to {Owner}; standing down", entry.Name, shared.Owner);

            _local.TryRemove(entry.Name, out _);
            entry.StandingDown = true;

            await entry.DisposeAsync();

            return;
        }

        var silent = entry.Hub.LastPacketAt is { } last ? DateTimeOffset.UtcNow - last : (TimeSpan?)null;
        var interrupted = !entry.FeedRunning || silent > TimeSpan.FromSeconds(_options.FeedTimeoutSeconds);

        if (interrupted && Expired(entry, silent))
        {
            _local.TryRemove(entry.Name, out _);

            await EndAsync(entry, "its grace period expired");

            return;
        }

        await registry.UpsertAsync(
            Describe(entry, interrupted ? LiveStreamState.Interrupted : LiveStreamState.Live),
            cancellationToken);
    }

    /// <summary>
    /// Whether an interrupted stream has waited long enough. A manual stream that has never
    /// received anything is given the same window from when it was created, so one created a
    /// moment before its sender starts is not swept away in between.
    /// </summary>
    private bool Expired(LiveStreamEntry entry, TimeSpan? silent)
        => silent is { } quiet ? quiet > Grace : DateTimeOffset.UtcNow - entry.StartedAt > Grace;

    private async Task EndAsync(LiveStreamEntry entry, string why)
    {
        logger.LogInformation("Stream '{Name}' is gone because {Why}", entry.Name, why);

        await entry.DisposeAsync();

        // Removed rather than left as finished. After the grace period the stream leaves nothing
        // behind: no entry, no history. The documents it produced are its trace.
        await registry.RemoveAsync(entry.Name, CancellationToken.None);
    }

    private LiveStream Describe(LiveStreamEntry entry, LiveStreamState state)
    {
        var buffer = entry.Hub.BufferState();

        return new LiveStream(
            entry.Name,
            state,
            entry.StartedAt,
            DateTimeOffset.UtcNow,
            Owner,
            _options.PeerBaseUrl,
            _options.PeerConsumptionBaseUrl,
            entry.Hub.Packets,
            entry.Hub.Bytes,
            entry.Harvester.Preview is not null,
            buffer.Startable,
            buffer.CeilingBinding,
            buffer.HeldSeconds,
            entry.Hub.Layout?.Describe(),
            entry.Recorder is { Finished: false } recorder ? recorder.Status : null,
            entry.ConnectionId,
            entry.Manual);
    }

    private void RequireAllowed(string url)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator > 0 ? url[..separator].ToLowerInvariant() : "file";

        if (!_options.AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
        {
            // Without this, a caller could make the service read anywhere libav can reach, local
            // files included.
            throw new NotSupportedException(
                $"Transport '{scheme}' is not allowed. Allowed: {string.Join(", ", _options.AllowedSchemes)}.");
        }

        if (!FfmpegLibrary.Supports(url, forOutput: false))
        {
            throw new NotSupportedException(
                $"The loaded FFmpeg has no '{scheme}' support. Available: {string.Join(", ", Transports)}.");
        }
    }

    private static string FileName(string name)
    {
        var flattened = name.Replace('/', '-');

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            flattened = flattened.Replace(invalid, '_');
        }

        return flattened;
    }

    public async ValueTask DisposeAsync()
    {
        // Registered twice, as itself and as the port, so the container disposes it twice.
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _shutdown.CancelAsync();

        foreach (var entry in _local.Values)
        {
            try
            {
                await entry.DisposeAsync();
                await registry.RemoveAsync(entry.Name, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Shutting '{Name}' down was untidy", entry.Name);
            }
        }

        _local.Clear();
        _shutdown.Dispose();
    }
}
