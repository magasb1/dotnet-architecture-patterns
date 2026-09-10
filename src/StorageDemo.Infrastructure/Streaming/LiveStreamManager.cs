using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// Runs live sessions on this replica and keeps the shared registry current.
///
/// The split that matters: the socket and the recording are local, because they cannot be
/// anything else, while everything a caller asks about is in the registry, because a caller may
/// reach any replica. Stopping is therefore a flag another replica sets and this one acts on.
///
/// Ingest records to a local file and hands it to the ordinary upload path once the stream ends,
/// so a recording becomes a document with a thumbnail and metadata like any other file.
/// </summary>
public sealed class LiveStreamManager(
    LibavRemuxer remuxer,
    ILiveSessionRegistry registry,
    IMediaAnalyzer analyzer,
    IServiceScopeFactory scopeFactory,
    IOptions<LiveOptions> options,
    ILogger<LiveStreamManager> logger) : ILiveStreamService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<Guid, Running> _local = new();
    private readonly LiveOptions _options = options.Value;
    private readonly CancellationTokenSource _shutdown = new();

    public string Owner { get; } = options.Value.NodeName is { Length: > 0 } name
        ? name
        : Environment.GetEnvironmentVariable("POD_NAME") ?? Environment.MachineName;

    public IReadOnlyList<string> Transports
    {
        get
        {
            // Intersected, because a transport is only useful here if it works both ways.
            var inputs = FfmpegLibrary.InputProtocols();

            return [.. FfmpegLibrary.OutputProtocols().Where(inputs.Contains)];
        }
    }

    public async Task<IReadOnlyList<LiveSession>> SessionsAsync(CancellationToken cancellationToken = default)
    {
        // Only what is still running. A stream that has stopped is not a stream any more, and a
        // client showing it as live has no way to tell it apart from one that is on air. Its
        // recording arrives as a document instead, which is the thing that outlives it.
        //
        // ponytail: filtered on read rather than reaped. A session whose replica died leaves a row
        // that nothing removes, so it stays in the registry, marked failed and hidden. Add a sweep
        // if a long-lived cluster starts accumulating them.
        var sessions = await registry.ListAsync(cancellationToken);

        return [.. sessions.Where(session => !session.IsFinished)];
    }

    public Task<LiveSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default)
        => registry.GetAsync(sessionId, cancellationToken);

    public async Task<LiveSession> StartIngestAsync(
        string name,
        string url,
        CancellationToken cancellationToken = default)
    {
        RequireAllowed(url);

        var id = Guid.NewGuid();
        var safeName = SanitizeName(name);

        var recording = Path.Combine(
            Path.GetTempPath(),
            "storagedemo-live",
            $"{id:N}",
            $"{safeName}.ts");

        Directory.CreateDirectory(Path.GetDirectoryName(recording)!);

        var running = new Running(
            new LiveSession(
                id,
                safeName,
                LiveDirection.Ingest,
                url,
                LiveSessionState.Waiting,
                DateTimeOffset.UtcNow,
                null,
                0,
                0,
                null,
                null,
                Owner,
                DateTimeOffset.UtcNow,
                PlaybackUrl: PlaybackUrl(id)),
            recording);

        _local[id] = running;
        await registry.UpsertAsync(running.Session, cancellationToken);

        logger.LogInformation("Live ingest {SessionId} listening on {Url}", id, url);

        running.Task = Task.Run(async () =>
        {
            StartThumbnailSampling(running);

            try
            {
                var result = remuxer.Remux(
                    url,
                    recording,
                    "mpegts",
                    _options.IngestOptions,
                    (packets, bytes) => running.Report(packets, bytes),
                    running.Cancellation.Token);

                logger.LogInformation(
                    "Live ingest {SessionId} ended after {Packets} packets",
                    id,
                    result.Packets);

                await StoreRecordingAsync(running);
            }
            catch (Exception ex)
            {
                await FailAsync(running, ex);
            }
            finally
            {
                // Stops the preview sampler. The session is over either way, and this is a signal
                // rather than a disposal so nothing can pull the token out from under it.
                await running.Cancellation.CancelAsync();

                // Removed rather than published as finished, so every replica stops reporting
                // it at once and the tile disappears from every client watching.
                await registry.RemoveAsync(id, CancellationToken.None);
                Cleanup(recording);
                _local.TryRemove(id, out _);
            }
        });

        return running.Session;
    }

    public async Task<LiveSession> StartEgressAsync(
        Guid documentId,
        string url,
        CancellationToken cancellationToken = default)
    {
        RequireAllowed(url);

        // Downloaded first: libav seeks its input, and an object store hands back a forward stream.
        await using var scope = scopeFactory.CreateAsyncScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

        var content = await documents.DownloadAsync(documentId, cancellationToken)
            ?? throw new InvalidOperationException($"Document {documentId} has no stored object.");

        var id = Guid.NewGuid();
        var source = Path.Combine(Path.GetTempPath(), "storagedemo-live", $"{id:N}", content.FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);

        await using (var file = File.Create(source))
        await using (var stream = content.Stream)
        {
            await stream.CopyToAsync(file, cancellationToken);
        }

        var running = new Running(
            new LiveSession(
                id,
                content.FileName,
                LiveDirection.Egress,
                url,
                LiveSessionState.Waiting,
                DateTimeOffset.UtcNow,
                null,
                0,
                0,
                documentId,
                null,
                Owner,
                DateTimeOffset.UtcNow),
            source);

        _local[id] = running;
        await registry.UpsertAsync(running.Session, cancellationToken);

        running.Task = Task.Run(async () =>
        {
            try
            {
                var result = remuxer.Remux(
                    source,
                    url,
                    "mpegts",
                    null,
                    (packets, bytes) => running.Report(packets, bytes),
                    running.Cancellation.Token);

                running.Complete(LiveSessionState.Completed);

                logger.LogInformation(
                    "Live egress {SessionId} sent {Packets} packets to {Url}",
                    id,
                    result.Packets,
                    url);
            }
            catch (Exception ex)
            {
                await FailAsync(running, ex);
            }
            finally
            {
                await registry.RemoveAsync(id, CancellationToken.None);
                Cleanup(source);
                _local.TryRemove(id, out _);
            }
        });

        return running.Session;
    }

    public async Task<bool> StopAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        if (_local.TryGetValue(sessionId, out var running))
        {
            running.Cancellation.Cancel();
            return true;
        }

        // Owned by another replica: leave a request it will pick up on its next heartbeat.
        var session = await registry.GetAsync(sessionId, cancellationToken);
        if (session is null)
        {
            return false;
        }

        if (session.IsFinished)
        {
            return true;
        }

        await registry.UpsertAsync(session with { StopRequested = true }, cancellationToken);

        logger.LogInformation(
            "Asked {Owner} to stop live session {SessionId}",
            session.Owner,
            sessionId);

        return true;
    }

    public byte[]? Thumbnail(Guid sessionId)
        => _local.TryGetValue(sessionId, out var running) ? running.Thumbnail : null;

    public async Task StreamAsync(
        Guid sessionId,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        if (!_local.TryGetValue(sessionId, out var running))
        {
            throw new InvalidOperationException($"Live session {sessionId} is not running on {Owner}.");
        }

        // A viewer can attach before the first packet arrives, and the recording file does not
        // exist until libav writes to it. Waiting beats failing: the stream is starting, not absent.
        while (!File.Exists(running.RecordingPath))
        {
            if (running.Session.IsFinished || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        // Follows the recording as it is written, which is what makes this live rather than a
        // download of whatever existed when the request arrived.
        await using var source = new FileStream(
            running.RecordingPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        // Joining at the live edge rather than at byte zero. A viewer who opens a stream that has
        // been running for an hour wants what is happening now, not an hour-old recording played
        // from the start, and the player would never catch up anyway.
        source.Seek(LiveEdge(source.Length, running.Session.StartedAt), SeekOrigin.Begin);

        var buffer = new byte[64 * 1024];
        var idle = TimeSpan.FromMilliseconds(200);

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);

            if (read > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await destination.FlushAsync(cancellationToken);
                continue;
            }

            if (running.Session.IsFinished || !_local.ContainsKey(sessionId))
            {
                return;
            }

            // Caught up with the writer; wait for more rather than spinning on end of file.
            await Task.Delay(idle, cancellationToken);
        }
    }

    /// <summary>How much of the stream a viewer gets before the live edge, in seconds.</summary>
    private const double LiveEdgeSeconds = 3;

    /// <summary>
    /// Where a new viewer starts reading. A few seconds of backlog rather than none: a decoder
    /// needs a table and a keyframe before it can show anything, and MPEG-TS carries those
    /// periodically rather than on demand.
    /// </summary>
    private static long LiveEdge(long length, DateTimeOffset startedAt)
    {
        var elapsed = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

        // Measured from this recording rather than assumed, so a 500 kbps feed and a 20 Mbps one
        // both hand back about the same amount of time instead of the same number of bytes.
        if (elapsed <= LiveEdgeSeconds)
        {
            return 0;
        }

        var start = Math.Max(0, length - (long)(length / elapsed * LiveEdgeSeconds));

        // Transport stream packets are 188 bytes counted from the start of the file. Landing
        // mid-packet leaves the demuxer hunting for a sync byte, which is time it does not have
        // when the whole point was to join without delay.
        return start - (start % 188);
    }

    /// <summary>
    /// Keeps the registry current for this replica's sessions, samples a preview frame, and acts
    /// on a stop another replica asked for.
    /// </summary>
    private void StartThumbnailSampling(Running running)
    {
        // Read once. A token stays usable after its source is cancelled, where reaching back
        // through the source would not: a disposed source throws on every access, which turned
        // this loop into a hot spin logging one exception per iteration.
        var token = running.Cancellation.Token;

        _ = Task.Run(async () =>
        {
            var interval = TimeSpan.FromSeconds(_options.ThumbnailIntervalSeconds);

            // The first frame comes quickly so a viewer sees a picture rather than a placeholder;
            // after that the configured interval is plenty.
            var wait = TimeSpan.FromSeconds(1);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(wait, token);
                    wait = interval;

                    await SampleThumbnailAsync(running, token);
                    await PublishAsync(running);
                    await HonourStopRequestAsync(running, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // A preview is decoration, but silently never producing one is the kind of
                    // failure that goes unnoticed for months.
                    logger.LogWarning(ex, "Live preview pass failed for {SessionId}", running.Session.Id);
                }
            }
        }, CancellationToken.None);
    }

    /// <summary>
    /// Decodes a frame from the tail of the recording. MPEG-TS resynchronises from anywhere, so a
    /// chunk off the end decodes on its own without reading the whole file back.
    /// </summary>
    private async Task SampleThumbnailAsync(Running running, CancellationToken cancellationToken)
    {
        // Enough to hold a keyframe at a modest bitrate. Too high a floor means a low-bitrate
        // stream shows no preview for its first several seconds, which is exactly when someone is
        // watching to see whether it started.
        const int minimumBytes = 16 * 1024;

        FileStream source;

        try
        {
            source = new FileStream(
                running.RecordingPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            // Nothing has arrived yet, so libav has not created the recording.
            return;
        }

        // Size read through the open handle, not from FileInfo. While another handle is still
        // writing, Windows leaves the size in the directory entry stale, so a path-based query
        // reports zero for a file that is being written to right now.
        await using (source)
        {
            var available = source.Length;
            if (available < minimumBytes)
            {
                return;
            }

            var length = Math.Min(available, _options.ThumbnailTailBytes);
            source.Seek(-length, SeekOrigin.End);

            using var tail = new MemoryStream();
            await source.CopyToAsync(tail, cancellationToken);
            tail.Position = 0;

            var analysis = await analyzer.AnalyzeAsync(tail, "live.ts", "video/mp2t", cancellationToken);

            if (analysis.Thumbnail is { Length: > 0 })
            {
                running.SetThumbnail(analysis.Thumbnail);
                return;
            }

            logger.LogDebug(
                "Live preview found no frame in {Bytes} bytes for {SessionId}",
                length,
                running.Session.Id);
        }
    }

    private async Task HonourStopRequestAsync(Running running, CancellationToken cancellationToken)
    {
        var shared = await registry.GetAsync(running.Session.Id, cancellationToken);

        if (shared is { StopRequested: true })
        {
            logger.LogInformation("Stopping live session {SessionId} at another replica's request", running.Session.Id);
            await running.Cancellation.CancelAsync();
        }
    }

    /// <summary>Hands the recording to the ordinary upload path, so it becomes a document.</summary>
    private async Task StoreRecordingAsync(Running running)
    {
        var cancelled = running.Cancellation.IsCancellationRequested;
        var file = new FileInfo(running.RecordingPath);

        if (!file.Exists || file.Length == 0)
        {
            // A listener that timed out without a sender: nothing arrived, so nothing is stored.
            running.Complete(cancelled ? LiveSessionState.Cancelled : LiveSessionState.Completed);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var documents = scope.ServiceProvider.GetRequiredService<IDocumentService>();

        await using var stream = File.OpenRead(running.RecordingPath);

        var document = await documents.UploadAsync(
            Path.GetFileName(running.RecordingPath),
            stream,
            ContentTypes.Guess(running.RecordingPath),
            CancellationToken.None);

        running.Complete(
            cancelled ? LiveSessionState.Cancelled : LiveSessionState.Completed,
            document.Id);

        logger.LogInformation(
            "Live recording {SessionId} stored as {DocumentId}",
            running.Session.Id,
            document.Id);
    }

    private async Task FailAsync(Running running, Exception ex)
    {
        if (ex is OperationCanceledException)
        {
            running.Complete(LiveSessionState.Cancelled);
            return;
        }

        logger.LogError(ex, "Live session {SessionId} failed", running.Session.Id);
        running.Fail(ex.Message);

        await Task.CompletedTask;
    }

    private Task PublishAsync(Running running) => registry.UpsertAsync(running.Session);

    private string? PlaybackUrl(Guid id)
        => string.IsNullOrWhiteSpace(_options.PublicBaseUrl)
            ? null
            : $"{_options.PublicBaseUrl.TrimEnd('/')}/api/live/{id}/stream";

    private void RequireAllowed(string url)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        var scheme = separator > 0 ? url[..separator].ToLowerInvariant() : "file";

        if (!_options.AllowedSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase))
        {
            // Without this, a caller could make the service read or write anywhere libav can reach,
            // local files included.
            throw new NotSupportedException(
                $"Transport '{scheme}' is not allowed. Allowed: {string.Join(", ", _options.AllowedSchemes)}.");
        }

        if (!FfmpegLibrary.Supports(url, forOutput: true) && !FfmpegLibrary.Supports(url, forOutput: false))
        {
            throw new NotSupportedException(
                $"The loaded FFmpeg has no '{scheme}' support. Available: {string.Join(", ", Transports)}.");
        }
    }

    private static string SanitizeName(string name)
    {
        var trimmed = string.IsNullOrWhiteSpace(name) ? "live" : name.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            trimmed = trimmed.Replace(invalid, '_');
        }

        return trimmed;
    }

    private static void Cleanup(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);

            if (directory is not null && Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp files; the operating system will get them.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _shutdown.CancelAsync();

        foreach (var running in _local.Values)
        {
            await running.Cancellation.CancelAsync();
        }

        foreach (var running in _local.Values.Where(r => r.Task is not null))
        {
            try
            {
                await running.Task!;
            }
            catch (Exception)
            {
                // Shutting down; a session's outcome no longer matters.
            }
        }
    }

    /// <summary>A session running here, plus the machinery to stop it and report on it.</summary>
    private sealed class Running(LiveSession session, string recordingPath)
    {
        private readonly Lock _gate = new();

        public LiveSession Session { get; private set; } = session;

        public string RecordingPath { get; } = recordingPath;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Task { get; set; }

        public byte[]? Thumbnail { get; private set; }

        public void Report(long packets, long bytes)
        {
            lock (_gate)
            {
                Session = Session with
                {
                    State = LiveSessionState.Active,
                    Packets = packets,
                    Bytes = bytes,
                    Heartbeat = DateTimeOffset.UtcNow,
                };
            }
        }

        public void SetThumbnail(byte[] thumbnail)
        {
            lock (_gate)
            {
                Thumbnail = thumbnail;
                Session = Session with { HasThumbnail = true, Heartbeat = DateTimeOffset.UtcNow };
            }
        }

        public void Complete(LiveSessionState state, Guid? documentId = null)
        {
            lock (_gate)
            {
                Session = Session with
                {
                    State = state,
                    EndedAt = DateTimeOffset.UtcNow,
                    Heartbeat = DateTimeOffset.UtcNow,
                    DocumentId = documentId ?? Session.DocumentId,
                };
            }
        }

        public void Fail(string error)
        {
            lock (_gate)
            {
                Session = Session with
                {
                    State = LiveSessionState.Failed,
                    EndedAt = DateTimeOffset.UtcNow,
                    Heartbeat = DateTimeOffset.UtcNow,
                    Error = error,
                };
            }
        }
    }
}
