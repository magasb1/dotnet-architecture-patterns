using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Coordination;
using StorageDemo.Core.Documents;

namespace StorageDemo.Infrastructure.Monitoring;

/// <summary>
/// Runs the retention pass on an interval, on one replica at a time.
///
/// The same shape as <see cref="StorageMonitor"/> and for the same reason: the work is correct but
/// wasteful to do twice, and two replicas deleting the same documents would race each other through
/// storage for no gain. Nothing here schedules; the lock decides who works this pass.
/// </summary>
public sealed class RetentionService(
    IServiceScopeFactory scopeFactory,
    IDistributedLock sweepLock,
    IOptions<RetentionOptions> options,
    ILogger<RetentionService> logger) : BackgroundService
{
    private readonly RetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        logger.LogInformation(
            "Retention is on: recordings and snapshots are kept for {Days} days, abandoned registry "
                + "entries removed after {Minutes} minutes, swept every {IntervalSeconds}s",
            _options.MaxAgeDays,
            _options.AbandonedEntryMinutes,
            _options.IntervalSeconds);

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);
        using var passes = new PeriodicTimer(interval);

        try
        {
            while (await passes.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var held = await sweepLock.TryAcquireAsync(
                        "retention-sweep",
                        LockTtl(interval),
                        stoppingToken);

                    if (held is null)
                    {
                        logger.LogDebug("Another replica is sweeping; skipping this pass");
                        continue;
                    }

                    await using var scope = scopeFactory.CreateAsyncScope();
                    var sweeper = scope.ServiceProvider.GetRequiredService<RetentionSweeper>();

                    var result = await sweeper.SweepAsync(
                        TimeSpan.FromDays(_options.MaxAgeDays),
                        TimeSpan.FromMinutes(_options.AbandonedEntryMinutes),
                        stoppingToken);

                    if (result.AnyChanges)
                    {
                        logger.LogInformation(
                            "Retention removed {Documents} documents and {Entries} abandoned registry entries",
                            result.Documents,
                            result.Entries);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // A failed pass must not kill the sweeper; the next one picks up where this
                    // stopped, because a half-done delete leaves bytes gone and the row to find.
                    logger.LogError(ex, "Retention pass failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// Long enough to outlast a slow pass, since a lock that expires mid-pass lets a second replica
    /// start one. Short enough that a replica killed while holding it frees it soon.
    /// </summary>
    private static TimeSpan LockTtl(TimeSpan interval)
        => TimeSpan.FromSeconds(Math.Max(300, interval.TotalSeconds * 2));
}
