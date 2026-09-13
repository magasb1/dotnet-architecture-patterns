using System.Text.Json.Serialization;

namespace StorageDemo.Core.Streaming;

/// <summary>
/// Somewhere a stream is copied to, besides the local consumption port.
///
/// Every source already has a local output: any player may pull it from this cluster's consumption
/// port, and that costs nothing until somebody asks. A forward is the other direction - this
/// service dials out and pushes, whether or not anyone is watching - and it exists because the far
/// end is often another gateway in another place that cannot reach in.
/// </summary>
/// <param name="Id">
/// Stable across edits, so a target whose URL changes is the same forward rather than a new one.
/// Minted when a forward is first saved without one; callers never have to invent it.
/// </param>
/// <param name="Url">
/// Where to push, under the same scheme allowlist a pulled source is held to. The scheme picks the
/// container: RTP carries MPEG-TS in the RTP muxer, everything else is plain MPEG-TS.
///
/// Query options are libav's and are passed through, which is how one field covers all three
/// shapes the deployment asked for: <c>srt://host:9000?streamid=name</c> dials the far end,
/// <c>srt://0.0.0.0:9100?mode=listener</c> waits to be pulled from, and <c>udp://</c> or
/// <c>rtp://</c> push with no handshake at all.
/// </param>
/// <param name="Enabled">
/// False stops the copy without forgetting where it went. Switching a forward off and on again is
/// a routine operation and should not cost the operator a URL they then have to retype.
/// </param>
public sealed record ForwardTarget(string Id, string Url, bool Enabled = true);

/// <summary>
/// What a forward is actually doing, as opposed to what it was asked to do.
///
/// Carried on the stream's registry entry rather than on the source, because it is per-connection
/// and belongs to whichever replica currently holds the stream. A source is configuration and
/// outlives every pod; this is the state of one attempt.
/// </summary>
/// <param name="Error">
/// Why the last attempt stopped, when one did. Retained after the forward has given up and while
/// it is waiting to retry, because a forward that is simply not running looks identical to one
/// that has never been asked to run, and the operator needs to tell those apart.
/// </param>
public sealed record ForwardStatus(
    string Id,
    string Url,
    bool Connected,
    long Bytes,
    DateTimeOffset? ConnectedAt = null,
    string? Error = null);

/// <summary>
/// A standing instruction about one stream name: fetch it from here, and copy it to there.
///
/// The difference from <see cref="LiveStream"/> is the whole point of this type. A
/// <see cref="LiveStream"/> is what is on air now, is removed the moment it stops, and belongs to
/// the replica holding the socket. A source is what an operator configured, survives every pod
/// that ever served it, and belongs to nobody. One is a reading, the other is the setting.
///
/// Push and pull share the record. An encoder that pushes a name needs no URL, but it may well
/// need forwarding, and splitting that into two types would mean two lists in the interface for
/// what an operator thinks of as one row.
/// </summary>
/// <param name="Url">
/// Where to pull from, or null when an encoder brings the stream in by itself. Null is not
/// "unconfigured": it is the statement that this name arrives on the ingest port.
/// </param>
/// <param name="Enabled">
/// False parks the source: it stays in the list and this service stops acting on it. Deleting the
/// row is how you forget a source; this is how you park one.
///
/// Parking stops what this service itself started, and only that. A pull already running is
/// dropped, because otherwise the toggle would mean "stop trying again later" while the camera
/// carried on arriving. Every forward stops, for the same reason. A stream an encoder is pushing
/// is untouched, because stopping an encoder is not this toggle's business - refusing a name is
/// the lock's, and ending a feed is the stop call's.
/// </param>
public sealed record LiveSource(
    string Name,
    string? Url,
    bool Enabled,
    IReadOnlyList<ForwardTarget> Forwards,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// True when this service is meant to fetch the stream rather than wait for it.
    ///
    /// Not serialised. It is derived from <see cref="Url"/> and nothing reads it back, so writing
    /// it out would put a second, redundant statement of the same fact into a file an operator may
    /// open and edit by hand - and into Redis, where it would be one more thing that can disagree
    /// with itself.
    /// </summary>
    [JsonIgnore]
    public bool IsPull => !string.IsNullOrWhiteSpace(Url);
}

/// <summary>
/// Where configured sources are kept, so every replica agrees on what is meant to be running.
///
/// Deliberately a second store rather than more fields on <see cref="ILiveStreamRegistry"/>. That
/// registry is emptied as streams end, because it answers "what is on air"; this one must survive
/// exactly the events that clear it - a stream ending, a pod dying, the whole service restarting -
/// because it answers "what did somebody ask for". Putting both in one structure would mean either
/// configuration that evaporates or a registry that accumulates the dead.
///
/// It is small and rarely written: an operator adds a source, and a thousand replicas read the
/// list on their heartbeat. Every implementation may therefore be read-heavy and unclever.
/// </summary>
public interface ILiveSourceStore
{
    Task<IReadOnlyList<LiveSource>> ListAsync(CancellationToken cancellationToken = default);

    Task<LiveSource?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the whole row. Forwards are part of it, not a separate call.</summary>
    Task SaveAsync(LiveSource source, CancellationToken cancellationToken = default);

    Task RemoveAsync(string name, CancellationToken cancellationToken = default);
}
