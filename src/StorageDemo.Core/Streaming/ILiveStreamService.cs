namespace StorageDemo.Core.Streaming;

public enum LiveDirection
{
    /// <summary>Into the service: a sender pushes a stream, the service records it as a document.</summary>
    Ingest,

    /// <summary>Out of the service: a stored document is multiplexed to a destination.</summary>
    Egress,
}

public enum LiveSessionState
{
    /// <summary>Listening, with nothing arriving yet.</summary>
    Waiting,
    Active,
    Completed,
    Failed,
    Cancelled,
}

/// <param name="DocumentId">
/// For ingest, the document the recording became, once the stream ended. For egress, the document
/// being sent.
/// </param>
/// <param name="Owner">
/// The replica holding the socket. Every other replica needs this to route a stop or a playback
/// request to the one place that can serve it.
/// </param>
/// <param name="Heartbeat">
/// Last time the owner said it was still alive. A pod that dies holding a socket cannot report its
/// own death, so a stale heartbeat is how the rest of the cluster finds out.
/// </param>
/// <param name="StopRequested">
/// Set by any replica; acted on by the owner. Stopping is a request rather than a command because
/// only the owner can actually close the socket.
/// </param>
/// <param name="PlaybackUrl">Where a viewer can pull this stream while it is running.</param>
public sealed record LiveSession(
    Guid Id,
    string Name,
    LiveDirection Direction,
    string Url,
    LiveSessionState State,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    long Packets,
    long Bytes,
    Guid? DocumentId,
    string? Error,
    string Owner = "",
    DateTimeOffset? Heartbeat = null,
    bool StopRequested = false,
    bool HasThumbnail = false,
    string? PlaybackUrl = null)
{
    /// <summary>Nothing more will happen to a session in one of these states.</summary>
    public bool IsFinished => State is LiveSessionState.Completed
        or LiveSessionState.Failed
        or LiveSessionState.Cancelled;
}

/// <summary>
/// Live streaming in and out of the service, over whatever transports the loaded FFmpeg carries.
///
/// A session owns a socket, so it belongs to the process that started it. That is why sessions are
/// not shared between replicas the way documents are: there is nothing useful another pod could do
/// with a session whose socket it does not hold.
/// </summary>
public interface ILiveStreamService
{
    /// <summary>Transport schemes the loaded libraries can actually use, such as udp or srt.</summary>
    IReadOnlyList<string> Transports { get; }

    /// <summary>Every session in the cluster, not only this replica's.</summary>
    Task<IReadOnlyList<LiveSession>> SessionsAsync(CancellationToken cancellationToken = default);

    Task<LiveSession?> GetAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <param name="url">Where to listen, for example udp://0.0.0.0:9000 or srt://0.0.0.0:9000.</param>
    Task<LiveSession> StartIngestAsync(string name, string url, CancellationToken cancellationToken = default);

    /// <param name="url">Where to send, for example udp://receiver:9000.</param>
    Task<LiveSession> StartEgressAsync(Guid documentId, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks the owner to stop. False when no such session exists anywhere. True does not mean it
    /// has stopped yet, only that the owner has been told.
    /// </summary>
    Task<bool> StopAsync(Guid sessionId, CancellationToken cancellationToken = default);

    /// <summary>The latest preview frame, when this replica owns the session and has one.</summary>
    byte[]? Thumbnail(Guid sessionId);

    /// <summary>
    /// Streams the live recording to a viewer. Only the owner can serve this, because only the
    /// owner has the bytes; another replica proxies to it.
    /// </summary>
    Task StreamAsync(Guid sessionId, Stream destination, CancellationToken cancellationToken = default);
}
