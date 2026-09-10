using System.Collections.Concurrent;

namespace StorageDemo.Core.Streaming;

/// <summary>
/// One replica's view, which is the whole cluster when there is only one. Staleness is still
/// applied, so a single instance behaves the way a cluster does rather than only appearing to.
/// </summary>
public sealed class InMemoryLiveSessionRegistry : ILiveSessionRegistry
{
    private readonly ConcurrentDictionary<Guid, LiveSession> _sessions = new();

    public Task UpsertAsync(LiveSession session, CancellationToken cancellationToken = default)
    {
        _sessions[session.Id] = session;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        _sessions.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<LiveSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => Task.FromResult(_sessions.TryGetValue(id, out var session)
            ? LiveSessionStaleness.Apply(session)
            : null);

    public Task<IReadOnlyList<LiveSession>> ListAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<LiveSession>>(
            [.. _sessions.Values.Select(LiveSessionStaleness.Apply).OrderByDescending(s => s.StartedAt)]);
}
