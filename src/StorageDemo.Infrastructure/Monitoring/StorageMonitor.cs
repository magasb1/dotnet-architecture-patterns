using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Coordination;
using StorageDemo.Core.Documents;

namespace StorageDemo.Infrastructure.Monitoring;

/// <summary>
/// Rescans the store so changes made outside this application still reach the database.
///
/// It runs on whichever comes first: a notification, or the interval. Events make it prompt, and
/// the interval is the safety net for everything events miss, which is plenty. FileSystemWatcher
/// drops events when its buffer overflows, and an S3 notification can be lost in delivery. Since
/// a pass is a full diff, a missed event costs latency and nothing else.
/// </summary>
public sealed class StorageMonitor(
    IServiceScopeFactory scopeFactory,
    StorageChangeSignal signal,
    IDistributedLock scanLock,
    IOptions<StorageMonitorOptions> options,
    ILogger<StorageMonitor> logger) : BackgroundService
{
    private readonly StorageMonitorOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Storage monitor is disabled");
            return;
        }

        logger.LogInformation(
            "Storage monitor watching {Prefix}, on notification or every {IntervalSeconds}s",
            _options.Prefix,
            _options.IntervalSeconds);

        var interval = TimeSpan.FromSeconds(_options.IntervalSeconds);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var notified = await signal.WaitAsync(interval, stoppingToken);

                if (notified && _options.DebounceMilliseconds > 0)
                {
                    // Copying a folder fires an event per file. Letting the burst settle turns
                    // hundreds of notifications into one scan.
                    await Task.Delay(_options.DebounceMilliseconds, stoppingToken);
                }

                try
                {
                    // One replica scans at a time. The others skip this pass rather than pay for
                    // the same listing and announce the same changes to their own clients.
                    await using var held = await scanLock.TryAcquireAsync(
                        "storage-scan",
                        LockTtl(interval),
                        stoppingToken);

                    if (held is null)
                    {
                        logger.LogDebug("Another replica is scanning; skipping this pass");
                        continue;
                    }

                    // A scope per pass: the repository and DbContext are scoped services.
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var reconciler = scope.ServiceProvider.GetRequiredService<StorageReconciler>();

                    var result = await reconciler.ReconcileAsync(_options.Prefix, stoppingToken);

                    if (result.AnyChanges)
                    {
                        logger.LogInformation(
                            "Reconciled storage {Added} added {Updated} updated {Removed} removed",
                            result.Added,
                            result.Updated,
                            result.Removed);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    // A failed pass must not kill the monitor; the next one retries.
                    logger.LogError(ex, "Storage reconciliation pass failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>
    /// Long enough to outlast a slow scan, since a lock that expires mid-pass lets a second
    /// replica start one. Short enough that a replica killed while holding it frees it soon.
    /// </summary>
    private static TimeSpan LockTtl(TimeSpan interval)
        => TimeSpan.FromSeconds(Math.Max(120, interval.TotalSeconds * 5));
}
