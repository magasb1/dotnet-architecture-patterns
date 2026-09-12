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

/// <param name="Id">
/// The recording in progress, unique per recording. It is <b>not</b> a document id and never
/// becomes one: the recorder mints this when it starts and mints nothing else, and the document id
/// is a separate value that does not exist until the recording ends and its file has been stored.
/// Nothing in this record carries it, so a caller holding a running recording cannot name the
/// document it will become - it can only wait for the recording to finish and find the document by
/// what it was written with.
/// </param>
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
/// replica are forwarded to it. Writing a different owner here is how a name moves, and it is
/// allowed only once the name is free - the feed interrupted, or the owner no longer heartbeating.
/// The replica losing it reads this on its next heartbeat and stands down.
/// </param>
/// <param name="OwnerAddress">
/// Where that replica can be reached, recorded when it claimed the name. Both a forwarded control
/// call and a relayed viewer's media go over it, because inside the cluster the hop is HTTP.
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
/// <param name="PacketsLost">
/// Packets the transport never received on this feed during the last heartbeat, as libsrt counts
/// them. Zero on a healthy stream, and the first thing to look at on one that is not: a stream can
/// be listed as live, with packets and bytes rising, while most of what was sent to it is missing.
/// An interval rather than a total, so it answers "is this stream broken now"; see
/// <c>SrtSocketStream.Health</c>.
/// </param>
/// <param name="PacketsDropped">
/// Packets that did arrive but too late for the latency window to play them, over the same
/// interval. Distinct from lost, and usually means the window is too small for the link rather
/// than that the link is failing.
/// </param>
public sealed record LiveStream(
    string Name,
    LiveStreamState State,
    DateTimeOffset StartedAt,
    DateTimeOffset Heartbeat,
    string Owner,
    string? OwnerAddress,
    long Packets,
    long Bytes,
    bool HasPreview,
    bool Startable,
    bool CeilingBinding,
    double BufferedSeconds,
    string? Layout,
    RecordingStatus? Recording,
    string? ConnectionId,
    bool Manual = false,
    int PacketsLost = 0,
    int PacketsDropped = 0);

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

    /// <summary>
    /// Whether the replica that owns this entry is still there, on a much shorter fuse than
    /// <see cref="IsGone"/>.
    ///
    /// The two windows answer different questions and are deliberately not the same number. What a
    /// viewer sees is decided by the grace period: a tile that vanishes and returns is worse than
    /// one showing a state, so an entry stays listed as interrupted for thirty seconds. Who may
    /// publish the name is decided here: a dead pod's encoders are already reconnecting, and making
    /// them wait out the grace period would cost half a minute of black screen to protect a pod
    /// that is not coming back.
    ///
    /// Three beats, because one missed heartbeat is a scheduling hiccup and three is a pod that has
    /// stopped. At a two-second beat a name frees about six seconds after its owner dies.
    /// </summary>
    public static bool OwnerAlive(LiveStream stream, TimeSpan beat)
        => DateTimeOffset.UtcNow - stream.Heartbeat <= beat * 3;
}
