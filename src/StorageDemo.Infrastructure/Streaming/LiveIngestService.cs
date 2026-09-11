using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// Runs the ingest port for the life of the process: the boot-time self-test, then the accept
/// carousel, then the heartbeat that keeps the registry honest.
///
/// Nothing is requested before it exists. An encoder connects to one address, names itself in the
/// stream identifier, and the stream is on air from that moment.
/// </summary>
public sealed class LiveIngestService(
    SrtAcceptLoop acceptLoop,
    LiveStreamCoordinator coordinator,
    LiveListeners listeners,
    IOptions<LiveOptions> options,
    ILogger<LiveIngestService> logger) : BackgroundService
{
    /// <summary>
    /// How often the registry is refreshed and the local streams reconsidered. Short next to the
    /// grace period, so an interruption is noticed well inside it.
    /// </summary>
    private static readonly TimeSpan Beat = TimeSpan.FromSeconds(2);

    private readonly LiveOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Live streaming is switched off, so no ingest port is opened");
            return;
        }

        listeners.Enabled = true;

        FfmpegLibrary.EnsureLoaded();

        if (!FfmpegLibrary.InputProtocols().Contains("srt"))
        {
            // Saying so plainly beats a port that opens and never accepts anything. The fix is a
            // build with libsrt; see scripts/fetch-ffmpeg.sh.
            listeners.Fault = "the loaded FFmpeg has no SRT";

            logger.LogError(
                "The loaded FFmpeg has no SRT, so nothing can be ingested. Point Media:LibraryPath "
                + "at a build compiled with libsrt.");

            return;
        }

        // Before the port opens, not after. A reworded log line in a new FFmpeg would otherwise
        // leave every stream arriving unnamed, in production, with nothing reporting a fault.
        //
        // A replica that fails this cannot name anything it accepts, so it takes itself out of the
        // Service rather than swallowing encoders it will never serve.
        try
        {
            StreamIdCapture.SelfTest(SelfTestPort(), logger);
        }
        catch (Exception ex)
        {
            listeners.Fault = "the stream identifier self-test failed";

            logger.LogCritical(ex, "This replica cannot name the streams it accepts");

            return;
        }

        var listening = Task.Factory.StartNew(
            () => acceptLoop.Run(
                $"srt://{_options.IngestAddress}:{_options.IngestPort}?mode=listener"
                + $"&timeout={_options.FeedTimeoutSeconds * 1_000_000}",
                TimeSpan.FromSeconds(1),
                StreamIntent.Publish,
                coordinator.OnAccepted,
                stoppingToken),
            TaskCreationOptions.LongRunning);

        await HeartbeatAsync(stoppingToken);
        await listening;
    }

    private async Task HeartbeatAsync(CancellationToken stoppingToken)
    {
        using var beats = new PeriodicTimer(Beat);

        while (await Safe(() => beats.WaitForNextTickAsync(stoppingToken).AsTask()))
        {
            try
            {
                await coordinator.TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // A registry that is briefly unreachable must not take the ingest port down.
                logger.LogWarning(ex, "A heartbeat pass failed");
            }
        }
    }

    private static async Task<bool> Safe(Func<Task<bool>> wait)
    {
        try
        {
            return await wait();
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>
    /// A port of its own for the self-test, so it never collides with the ingest port it is about
    /// to open, and a fixed offset so an operator can recognise it in a firewall log.
    /// </summary>
    private int SelfTestPort() => _options.IngestPort >= 65500
        ? _options.IngestPort - 1
        : _options.IngestPort + 1;
}
