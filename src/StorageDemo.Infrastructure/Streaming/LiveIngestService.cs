using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// Runs the ingest port for the life of the process: the libsrt listener, and beside it the
/// heartbeat that keeps the registry honest.
///
/// Nothing is requested before it exists. An encoder connects to one address, names itself in the
/// stream identifier, and the stream is on air from that moment.
/// </summary>
public sealed class LiveIngestService(
    LiveStreamCoordinator coordinator,
    LiveListeners listeners,
    IOptions<LiveOptions> options,
    ILogger<LiveIngestService> logger) : BackgroundService
{
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

        if (!Srt.IsAvailable)
        {
            // Both listening ports are libsrt's, so without it this replica can accept nothing and
            // takes itself out of the Service rather than swallowing encoders it cannot serve.
            listeners.Fault = "libsrt is not loaded";

            logger.LogError(
                "libsrt is not loaded, or is older than 1.5, so no media port can be opened. Run "
                + "scripts/fetch-libsrt.sh, or install libsrt1.5 on the host.");

            return;
        }

        if (!FfmpegLibrary.InputProtocols().Contains("srt"))
        {
            // Not a fault: the listening ports are libsrt's now. libav is still the SRT caller for
            // a pulled stream and for relaying a viewer to the replica that owns its stream, and
            // both of those fail without it, so an operator has to hear which half is missing.
            logger.LogWarning(
                "The loaded FFmpeg has no SRT. Encoders can still push here, but pulled streams "
                + "and relaying a viewer to another replica both dial with libav and will fail. "
                + "Point Media:LibraryPath at a build compiled with libsrt.");
        }

        var listener = new SrtListener(
            StreamIntent.Publish,
            _options,
            coordinator.AdmitPublisher,
            coordinator.OnAccepted,
            logger,
            listeners);

        var listening = Task.Factory.StartNew(
            () => listener.Run(_options.IngestPort, stoppingToken),
            TaskCreationOptions.LongRunning);

        await HeartbeatAsync(stoppingToken);
        await listening;
    }

    /// <summary>
    /// The same beat the coordinator counts heartbeats in, and the same pass refreshes the copy of
    /// the registry the handshake reads, so a name is locked here within one beat of being claimed
    /// anywhere.
    /// </summary>
    private async Task HeartbeatAsync(CancellationToken stoppingToken)
    {
        using var beats = new PeriodicTimer(LiveStreamCoordinator.Beat);

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
}
