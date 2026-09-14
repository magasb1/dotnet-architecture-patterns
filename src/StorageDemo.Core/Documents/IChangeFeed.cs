namespace StorageDemo.Core.Documents;

/// <summary>
/// Broadcasts document changes so clients do not have to poll. One implementation keeps the
/// subscribers in this process; the other goes through Redis so several replicas share one feed.
/// </summary>
public interface IChangeFeed
{
    void Publish(DocumentChange change);

    /// <summary>
    /// Registers immediately, before anything is read. That ordering is what lets a caller
    /// subscribe first and then act, without a change slipping through the gap.
    /// </summary>
    IChangeSubscription Subscribe();
}

public interface IChangeSubscription : IDisposable
{
    IAsyncEnumerable<DocumentChange> ReadAllAsync(CancellationToken cancellationToken);
}
