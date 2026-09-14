using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Monitoring;

public sealed class StorageMonitorOptions
{
    public const string SectionName = "StorageMonitor";

    public bool Enabled { get; init; } = true;

    /// <summary>How often the store is rescanned.</summary>
    [Range(1, 3600)]
    public int IntervalSeconds { get; init; } = 30;

    /// <summary>Only objects under this prefix are reconciled.</summary>
    public string Prefix { get; init; } = "documents/";

    /// <summary>
    /// How long to let a burst of notifications settle before rescanning. Copying a folder fires
    /// one event per file; without this each would start its own pass.
    /// </summary>
    [Range(0, 60_000)]
    public int DebounceMilliseconds { get; init; } = 1000;

    /// <summary>
    /// Shared secret for the S3 notification endpoint, sent as the X-Storage-Token header. The
    /// endpoint stays closed while this is empty.
    /// </summary>
    public string? WebhookToken { get; init; }
}
