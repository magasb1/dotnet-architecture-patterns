using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// The consumption port: SRT out, live only.
///
/// Symmetric with ingest, down to running the same kind of listener. A player calls this port and
/// names the stream it wants in its stream identifier, exactly as an encoder names the stream it is
/// sending. One address, any replica behind it, nothing to configure on the player beyond a URL.
///
/// Its own port rather than a route on the API, so a deployment can expose one network to viewers
/// and keep the other private. Reaching this port lets you watch live streams and nothing else,
/// which is the whole reason the ports are split. Finished recordings and snapshots are documents
/// and stay on the API.
///
/// One cost comes with the symmetry and is worth stating plainly: a viewer reaching a replica that
/// does not own the stream cannot be redirected, since SRT has no such thing, so that replica calls
/// the owner and relays the bytes.
/// </summary>
public sealed class LiveConsumptionService(
    LiveStreamCoordinator coordinator,
    LiveListeners listeners,
    IOptions<LiveOptions> options,
    ILogger<LiveConsumptionService> logger) : BackgroundService
{
    /// <summary>
    /// How long to wait for the replica that owns a stream to answer. A peer inside a cluster
    /// answers at once or is gone, and waiting longer only makes a viewer wait longer.
    /// </summary>
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(1);

    private readonly LiveOptions _options = options.Value;

    private CancellationToken _stopping;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        // The same gate ingest sets its fault on, asked directly rather than read off that service,
        // so neither port depends on which of the two started first.
        if (!_options.Enabled || !Srt.IsAvailable)
        {
            return Task.CompletedTask;
        }

        var listener = new SrtListener(
            StreamIntent.Subscribe,
            _options,
            Admit,
            OnAccepted,
            logger,
            listeners);

        return Task.Factory.StartNew(
            () => listener.Run(_options.ConsumptionPort, stoppingToken),
            TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Everything the listener could parse is admitted: Phase 1b refuses a name that is live and
    /// held, Phase 4 refuses when the pod is full, and neither exists yet.
    /// </summary>
    private static int? Admit(Admission admission) => null;

    /// <summary>
    /// Takes an accepted viewer off the accept thread. As on ingest, everything real happens
    /// elsewhere: time spent here is time the consumption port is not listening.
    /// </summary>
    private void OnAccepted(AcceptedSocket socket)
    {
        var name = socket.Name;

        // The position rides in the identifier's user_from key, so returning to live is a new
        // connection rather than a control message and the connection stays one-way.
        var from = StreamName.Position(socket.StreamId) ?? 0;
        var viewer = new SrtSocketStream(socket.Release(), writable: true);

        _ = Task.Factory.StartNew(
            () => ServeAsync(name, from, viewer, _stopping),
            TaskCreationOptions.LongRunning);
    }

    /// <summary>
    /// Serves one viewer for as long as it stays connected, whatever happens to the stream behind
    /// it.
    ///
    /// This is a loop rather than a single attach, because that is where a viewer's downtime
    /// actually comes from. When the replica owning a stream disappears, the encoder reconnects
    /// somewhere else and the name moves; a viewer served by a single attach would have its socket
    /// closed and would have to reconnect, which for a player means a black screen and a fresh
    /// handshake. Holding the socket open and re-attaching to wherever the stream went costs the
    /// viewer only the gap in the feed itself.
    ///
    /// The timeline carries across each re-attach, so the player is never asked to accept
    /// timestamps jumping back to zero in the middle of one connection.
    /// </summary>
    private async Task ServeAsync(
        string name,
        double from,
        SrtSocketStream viewer,
        CancellationToken stopping)
    {
        var timeline = 0d;
        var waitingSince = DateTimeOffset.UtcNow;

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                var stream = await coordinator.GetAsync(name, stopping);

                if (stream is null)
                {
                    // Gone, or not there yet. A viewer is given the same grace an interrupted
                    // stream gets, because a stream moving between replicas looks exactly like
                    // this from here and dropping the player would be the more disruptive answer.
                    if (DateTimeOffset.UtcNow - waitingSince > TimeSpan.FromSeconds(_options.GracePeriodSeconds))
                    {
                        logger.LogInformation("Giving up on '{Name}' for a viewer; it is not on air", name);
                        return;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(250), stopping);
                    continue;
                }

                waitingSince = DateTimeOffset.UtcNow;

                if (coordinator.Owns(name))
                {
                    // Asking for twenty seconds and receiving twenty-six is normal, since a stream
                    // can only be joined where a decoder can start. There is no response header on
                    // this transport, so it is logged and the stream reports what it holds.
                    logger.LogInformation(
                        "Serving '{Name}' to a viewer from {Given:0.#}s back (asked for {Asked:0.#}s)",
                        name,
                        coordinator.ResolvePreroll(name, from),
                        from);

                    timeline = await coordinator.WriteToViewerAsync(
                        new ViewerRequest(name, from),
                        viewer,
                        timeline,
                        stopping);
                }
                else
                {
                    await RelayAsync(stream, from, viewer, stopping);
                }

                if (viewer.Faulted)
                {
                    // The viewer left. Without this the loop would re-attach to a socket nobody is
                    // listening to and do it again every quarter second, forever, because the
                    // muxer treats a refused write as "stop writing" rather than as a failure.
                    logger.LogInformation("A viewer of '{Name}' went away", name);

                    return;
                }

                // Whatever was feeding this viewer stopped. Only a rollback is a chosen position;
                // resuming after a gap wants the live edge rather than the same twenty seconds again.
                from = 0;

                await Task.Delay(TimeSpan.FromMilliseconds(250), stopping);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            // The viewer closed the player. Normal.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Serving '{Name}' to a viewer failed", name);
        }
        finally
        {
            await viewer.DisposeAsync();
        }
    }

    /// <summary>
    /// Calls the owner's consumption port and pumps its bytes to this viewer.
    ///
    /// A media relay, which an HTTP consumption port would not have needed. It is the price of a
    /// player speaking one protocol to one address: SRT has no redirect, so the replica the load
    /// balancer picked either serves the viewer or fetches for it.
    /// </summary>
    private async Task RelayAsync(LiveStream stream, double from, Stream viewer, CancellationToken stopping)
    {
        if (stream.ConsumptionAddress is not { Length: > 0 } address)
        {
            logger.LogWarning(
                "'{Name}' is owned by {Owner}, which recorded no consumption address, so a viewer "
                + "here cannot be served. Set Live:PeerConsumptionBaseUrl on every replica.",
                stream.Name,
                stream.Owner);

            await Task.Delay(TimeSpan.FromSeconds(1), stopping);

            return;
        }

        var identifier = from > 0
            ? $"#!::r={stream.Name},user_from={from.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)},m=request"
            : $"#!::r={stream.Name},m=request";

        // Bounded, because the address may belong to a replica that has already gone. A pod that
        // is force-killed leaves its registry entry behind until its heartbeat goes stale, so
        // every viewer arriving here in the meantime dials an address nobody is answering. libav's
        // own connect timeout is several seconds; inside a cluster a peer answers in milliseconds
        // or not at all, and the difference is the whole of what a viewer waits for.
        var url = $"{address.TrimEnd('/')}?mode=caller&connect_timeout={(int)DialTimeout.TotalMilliseconds}";

        logger.LogInformation("Relaying '{Name}' from its owner {Owner}", stream.Name, stream.Owner);

        // The identifier goes as an option rather than in the URL. It begins with '#', which starts
        // a fragment in a URL, so in one it has to be written %23 - and FFmpeg versions disagree
        // about whether they decode that before or after splitting the query. One of them puts the
        // '#' back and truncates everything after it, which arrives as an empty identifier and is
        // refused. An option is read by nothing that parses URLs.
        using var upstream = Open(url, identifier);

        if (upstream is null)
        {
            // Straight round the loop: the registry is re-read every pass, so the moment the
            // encoder reconnects somewhere this viewer follows it.
            await Task.Delay(TimeSpan.FromMilliseconds(250), stopping);
            return;
        }

        await upstream.CopyToAsync(viewer, stopping);
    }

    private AvioStream? Open(string url, string streamId)
    {
        var opened = OpenTransport(url, streamId);

        if (opened == IntPtr.Zero)
        {
            logger.LogWarning("Could not reach {Url} to relay a viewer", url);
            return null;
        }

        return new AvioStream(opened, writable: false);
    }

    private static unsafe IntPtr OpenTransport(string url, string streamId)
    {
        AVIOContext* transport = null;
        AVDictionary* options = null;

        try
        {
            ffmpeg.av_dict_set(&options, "streamid", streamId, 0);

            return ffmpeg.avio_open2(&transport, url, ffmpeg.AVIO_FLAG_READ, null, &options) < 0
                ? IntPtr.Zero
                : (IntPtr)transport;
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }
}
