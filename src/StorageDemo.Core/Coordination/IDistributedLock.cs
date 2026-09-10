namespace StorageDemo.Core.Coordination;

/// <summary>
/// Mutual exclusion across every replica of the service.
///
/// It exists for work that is correct but wasteful to do twice: reconciling the store is a full
/// diff, so two replicas doing it at once converge on the same answer while paying twice for the
/// listing and telling every connected client about the same change twice.
/// </summary>
public interface IDistributedLock
{
    /// <param name="ttl">
    /// How long the lock survives without being released, so a replica that dies holding it does
    /// not block the others forever. Longer than the work is expected to take.
    /// </param>
    /// <returns>A handle to release it, or null when somebody else holds it.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan ttl, CancellationToken cancellationToken = default);
}
