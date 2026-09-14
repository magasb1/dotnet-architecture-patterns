namespace StorageDemo.Infrastructure.Monitoring;

/// <summary>
/// Tells the monitor that something in the store probably changed, so it can rescan now instead
/// of at the next tick. Poked by the filesystem watcher and by the S3 notification endpoint.
///
/// It carries no detail about what changed, on purpose: reconciliation is a full diff of the store
/// against the database, so the only useful information an event carries is "look again". That
/// also means a missed or duplicated event costs nothing.
/// </summary>
public sealed class StorageChangeSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Coalescing: a hundred events between two passes still mean one pass.</summary>
    public void Trigger()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled and not yet consumed. Nothing to add.
        }
    }

    /// <summary>True when something signalled, false when the wait timed out.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
        => _signal.WaitAsync(timeout, cancellationToken);
}
