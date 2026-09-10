namespace StorageDemo.Core.Streaming;

/// <summary>
/// Where live sessions are recorded so every replica can see them, not just the one holding the
/// socket.
///
/// The socket cannot be shared, but knowing that it exists, who owns it, and how to reach it can
/// be. That is the difference between a cluster where the API tells the truth about what is
/// running and one where the answer depends on which pod you happened to hit.
/// </summary>
public interface ILiveSessionRegistry
{
    Task UpsertAsync(LiveSession session, CancellationToken cancellationToken = default);

    Task RemoveAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LiveSession?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every session on every replica. Sessions whose owner has stopped writing a heartbeat come
    /// back as failed: a pod that dies holding a socket cannot report its own death.
    /// </summary>
    Task<IReadOnlyList<LiveSession>> ListAsync(CancellationToken cancellationToken = default);
}
