using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Messaging;

/// <summary>
/// Sessions in a Redis hash, one field per session, so any replica can list, find and ask to stop
/// a session that another replica is running.
///
/// A hash rather than one key per session: listing is the common operation and a single HGETALL
/// beats scanning a keyspace. Finished sessions expire on their own, so the hash does not grow
/// forever with records nobody will read again.
/// </summary>
public sealed class RedisLiveSessionRegistry(
    IConnectionMultiplexer connection,
    ILogger<RedisLiveSessionRegistry> logger) : ILiveSessionRegistry
{
    private const string Key = "storagedemo:live:sessions";

    /// <summary>How long a finished session stays visible before it is tidied away.</summary>
    private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(10);

    public async Task UpsertAsync(LiveSession session, CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.GetDatabase().HashSetAsync(
                Key,
                session.Id.ToString(),
                JsonSerializer.Serialize(session));
        }
        catch (RedisException ex)
        {
            // The session keeps running; it is only invisible to the other replicas.
            logger.LogWarning(ex, "Could not record live session {SessionId}", session.Id);
        }
    }

    public async Task RemoveAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.GetDatabase().HashDeleteAsync(Key, id.ToString());
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not remove live session {SessionId}", id);
        }
    }

    public async Task<LiveSession?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await connection.GetDatabase().HashGetAsync(Key, id.ToString());

            return value.IsNullOrEmpty ? null : LiveSessionStaleness.Apply(Deserialize(value)!);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not read live session {SessionId}", id);
            return null;
        }
    }

    public async Task<IReadOnlyList<LiveSession>> ListAsync(CancellationToken cancellationToken = default)
    {
        HashEntry[] entries;

        try
        {
            entries = await connection.GetDatabase().HashGetAllAsync(Key);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not list live sessions");
            return [];
        }

        var sessions = new List<LiveSession>(entries.Length);
        var expired = new List<RedisValue>();

        foreach (var entry in entries)
        {
            var session = Deserialize(entry.Value);
            if (session is null)
            {
                expired.Add(entry.Name);
                continue;
            }

            var current = LiveSessionStaleness.Apply(session);

            if (current.IsFinished && current.EndedAt is { } ended && DateTimeOffset.UtcNow - ended > KeepFinished)
            {
                expired.Add(entry.Name);
                continue;
            }

            sessions.Add(current);
        }

        if (expired.Count > 0)
        {
            // Tidying on read, so nothing has to run a sweeper for a handful of records.
            _ = connection.GetDatabase().HashDeleteAsync(Key, [.. expired]);
        }

        return [.. sessions.OrderByDescending(s => s.StartedAt)];
    }

    private LiveSession? Deserialize(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LiveSession>(value.ToString());
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarded an unreadable live session record");
            return null;
        }
    }
}
