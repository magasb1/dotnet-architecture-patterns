namespace StorageDemo.Core.Streaming;

public enum LiveStreamState
{
    /// <summary>Packets are arriving.</summary>
    Live,

    /// <summary>
    /// The feed has stopped arriving but the grace period has not expired. The stream is still
    /// claimed, still listed, and its hub, buffer and any recording are all still alive. A
    /// reconnect inside this window resumes the same stream rather than creating a second one.
    /// </summary>
    Interrupted,
}

/// <param name="Id">The document this recording will become, once it ends and there is a file.</param>
/// <param name="EndsAt">
/// When it is due to stop. A further trigger moves this later rather than starting a second
/// recording, so continuous detection leaves one clip covering the whole event.
/// </param>
/// <param name="Truncated">
/// Set when the recording ended because its queue overflowed. The document is real but short, and
/// saying so is the point: dropping packets to keep going would write a hole into a file that
/// claims to be a recording.
/// </param>
public sealed record RecordingStatus(
    Guid Id,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndsAt,
    long Bytes,
    bool Truncated = false);

/// <summary>
/// A named live feed, as every replica sees it.
///
/// The name is the identity, not an incidental label. A feed that drops and reconnects under the
/// same name is the same stream resuming, which is why nothing here is keyed by an opaque
/// identifier. The connection identifier below survives only to tell one attempt from the next in
/// a log; nothing looks a stream up by it.
/// </summary>
/// <param name="Owner">
/// The replica holding the connection. Only the owner has the bytes, so requests reaching another
/// replica are forwarded to it. Writing a different owner here is how a name is taken over: the
/// previous owner reads it on its next heartbeat and stands down.
/// </param>
/// <param name="OwnerAddress">Where that replica's API can be reached, recorded when it claimed the name.</param>
/// <param name="ConsumptionAddress">
/// Where that replica's consumption port can be reached. Kept apart from the API address because
/// media never travels over the API port, on either hop of a forwarded viewer.
/// </param>
/// <param name="Heartbeat">
/// Last time the owner said it was still alive. A pod that dies holding a socket cannot report its
/// own death, so a stale heartbeat is how the rest of the cluster finds out.
/// </param>
/// <param name="Startable">
/// False while no keyframe has arrived for longer than the buffer can hold. Every pre-roll from
/// this stream is then empty and every snapshot of it is second-hand, and the fix is at the
/// encoder rather than here.
/// </param>
/// <param name="CeilingBinding">
/// True when the buffer's byte ceiling is evicting before its time window is reached, which
/// silently shortens every pre-roll taken from it.
/// </param>
public sealed record LiveStream(
    string Name,
    LiveStreamState State,
    DateTimeOffset StartedAt,
    DateTimeOffset Heartbeat,
    string Owner,
    string? OwnerAddress,
    string? ConsumptionAddress,
    long Packets,
    long Bytes,
    bool HasPreview,
    bool Startable,
    bool CeilingBinding,
    double BufferedSeconds,
    string? Layout,
    RecordingStatus? Recording,
    string? ConnectionId,
    bool Manual = false);

/// <summary>
/// Where live streams are recorded so every replica can see them, not just the one holding the
/// connection.
///
/// Keyed by name. The socket cannot be shared, but knowing that a stream exists, who owns it and
/// how to reach them can be, and that is the difference between a cluster where the API tells the
/// truth about what is on air and one where the answer depends on which pod you happened to hit.
///
/// Entries are removed when a stream ends, so this stays a picture of what is live now. That is
/// the only thing every replica needs to agree on: the documents a stream produced are its trace.
/// </summary>
public interface ILiveStreamRegistry
{
    Task UpsertAsync(LiveStream stream, CancellationToken cancellationToken = default);

    Task RemoveAsync(string name, CancellationToken cancellationToken = default);

    Task<LiveStream?> GetAsync(string name, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LiveStream>> ListAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Decides when a stream whose owner has gone quiet should stop being reported.
///
/// A pod killed mid-stream leaves an entry saying "live" that nothing will ever update. Rather
/// than show a stream that stopped minutes ago, an entry whose owner has stopped heartbeating for
/// longer than the grace period is treated as gone. The reader decides this rather than the
/// registry rewriting anything, because the owner might yet come back.
/// </summary>
public static class LiveStreamStaleness
{
    public static bool IsGone(LiveStream stream, TimeSpan grace)
        => DateTimeOffset.UtcNow - stream.Heartbeat > grace;
}
