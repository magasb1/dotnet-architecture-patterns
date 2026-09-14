using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Monitoring;

/// <summary>
/// How long what a live stream produced is kept, and when a registry entry counts as abandoned.
///
/// Off unless configured, for the same reason live streaming is: a thousand camera-rate streams
/// record about 43 TB a day and something has to expire, but a service that silently deletes a
/// customer's recordings the first time it is upgraded is indefensible. Switching this on is a
/// deliberate act.
/// </summary>
public sealed class RetentionOptions
{
    public const string SectionName = "Retention";

    public bool Enabled { get; init; }

    /// <summary>How often the pass runs. One replica runs it and the others skip, as with the monitor.</summary>
    [Range(1, 86_400)]
    public int IntervalSeconds { get; init; } = 3600;

    /// <summary>
    /// How long a recording or a snapshot is kept, from when it was created. Zero keeps them
    /// forever, which is what someone who wants only the registry swept asks for.
    ///
    /// ponytail: age is the whole policy, and it is blunt. A recording is aged from its first part
    /// rather than its last, so a six-hour one expires six hours early, and every stream is treated
    /// alike. Per-stream rules, per-size rules and "keep the last N from this camera" all belong on
    /// the document rather than here, and nothing has asked for them.
    /// </summary>
    [Range(0, 3650)]
    public int MaxAgeDays { get; init; }

    /// <summary>
    /// How stale an owner's heartbeat may be before its registry entry is treated as abandoned and
    /// removed. Nothing else in the service ever removes an entry whose owner was force-killed.
    ///
    /// Deliberately far longer than anything the live side uses. The beat is two seconds and the
    /// grace period is thirty, so ten minutes is three hundred missed beats and twenty times the
    /// window that already declares a stream gone. Removing a living stream's entry would unlock
    /// its name to a second publisher and drop it out of every replica's view of the cluster, which
    /// is much worse than leaving a dead entry in Redis for another pass to find.
    /// </summary>
    [Range(1, 1440)]
    public int AbandonedEntryMinutes { get; init; } = 10;
}
