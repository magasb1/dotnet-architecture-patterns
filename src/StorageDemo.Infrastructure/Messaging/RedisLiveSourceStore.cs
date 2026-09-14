using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Messaging;

/// <summary>
/// Configured sources in a Redis hash, one field per name, so every replica reads the same list and
/// a source outlives the pod that was serving it.
///
/// Same shape as <see cref="RedisLiveStreamRegistry"/> and for the same reason: listing is the
/// common operation and one HGETALL beats scanning a keyspace. The difference from the registry is
/// what lives here - nothing is ever removed by a stream ending, because these rows are what
/// somebody asked for rather than what is happening.
///
/// Reads and writes deliberately do not treat a Redis outage the same way, which is the one thing
/// to keep in mind when editing this file. A failed read degrades to an empty list and a warning,
/// exactly as the registry does: the caller is a heartbeat that will ask again in a second, and
/// telling it "no sources right now" costs a cycle of doing nothing. A failed write must not degrade
/// at all. The caller there is an operator saving a URL, and swallowing the exception would report
/// success for a change that never happened - the worst outcome available, because they walk away
/// believing the system is configured. So the write throws and the UI says so.
/// </summary>
public sealed class RedisLiveSourceStore(
    IConnectionMultiplexer connection,
    ILogger<RedisLiveSourceStore> logger) : ILiveSourceStore
{
    private const string Key = "storagedemo:live:sources";

    public async Task SaveAsync(LiveSource source, CancellationToken cancellationToken = default)
    {
        // Not swallowed. See the note on the class: a save that quietly vanishes is a lie to the
        // operator, and the exception reaching them as a failed request is the honest answer.
        //
        // Translated rather than rethrown, though. A raw RedisException is unmapped and becomes a
        // 500, which says "this service is broken"; PersistenceException is already mapped to 503
        // alongside every other backing-store outage, which says "try again" and is the truth.
        // Naming the operation matters here too: an operator who cannot save wants to know whether
        // it was their row or the whole store.
        await Write(
            () => connection.GetDatabase().HashSetAsync(Key, source.Name, JsonSerializer.Serialize(source)),
            $"save live source '{source.Name}'");
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        await Write(
            () => connection.GetDatabase().HashDeleteAsync(Key, name),
            $"remove live source '{name}'");
    }

    private static async Task Write(Func<Task> write, string what)
    {
        try
        {
            await write();
        }
        catch (RedisException ex)
        {
            throw new PersistenceException($"Could not {what}.", ex);
        }
    }

    public async Task<LiveSource?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        try
        {
            var value = await connection.GetDatabase().HashGetAsync(Key, name);

            return value.IsNullOrEmpty ? null : Deserialize(value);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not read live source '{Name}'", name);
            return null;
        }
    }

    public async Task<IReadOnlyList<LiveSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        HashEntry[] entries;

        try
        {
            entries = await connection.GetDatabase().HashGetAllAsync(Key);
        }
        catch (RedisException ex)
        {
            logger.LogWarning(ex, "Could not list live sources");
            return [];
        }

        var sources = new List<LiveSource>(entries.Length);
        var unreadable = new List<RedisValue>();

        foreach (var entry in entries)
        {
            var source = Deserialize(entry.Value);

            if (source is null)
            {
                unreadable.Add(entry.Name);
                continue;
            }

            sources.Add(source);
        }

        if (unreadable.Count > 0)
        {
            // Tidying on read, so nothing has to run a sweeper for a handful of records.
            _ = connection.GetDatabase().HashDeleteAsync(Key, [.. unreadable]);
        }

        return [.. sources.OrderBy(source => source.Name, StringComparer.Ordinal)];
    }

    private LiveSource? Deserialize(RedisValue value)
    {
        if (value.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LiveSource>(value.ToString());
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Discarded an unreadable live source record");
            return null;
        }
    }
}
