namespace StorageDemo.Core.Streaming;

/// <param name="Preroll">How far back the caller asked to start, in seconds. Zero means live.</param>
public sealed record ViewerRequest(string Name, double Preroll);

/// <summary>
/// Live streaming, as everything outside the pipeline sees it.
///
/// Every call names a stream rather than an identifier, because the registry is keyed by name and
/// no identifier survives a reconnect. Only the replica that owns a stream can serve the calls
/// that need its bytes; the rest are answered from the shared registry by any replica.
/// </summary>
public interface ILiveStreamService
{
    /// <summary>This replica's name, as it records itself when it claims a stream.</summary>
    string Owner { get; }

    /// <summary>Transport schemes the loaded libraries can carry, such as udp or srt.</summary>
    IReadOnlyList<string> Transports { get; }

    /// <summary>Every stream on air anywhere, not only this replica's.</summary>
    Task<IReadOnlyList<LiveStream>> StreamsAsync(CancellationToken cancellationToken = default);

    Task<LiveStream?> GetAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>True when this replica holds the connection, so it can serve the bytes.</summary>
    bool Owns(string name);

    /// <summary>
    /// The current preview, when this replica owns the stream and has one. The picture now, not a
    /// poster frame: it is what the harvester last decoded.
    /// </summary>
    byte[]? Preview(string name);

    /// <summary>
    /// Creates a stream by request, for a protocol that cannot name itself, and opens its input.
    /// It sits in the registry waiting for bytes and is indistinguishable from an automatic stream
    /// once a demultiplexer exists.
    /// </summary>
    Task<LiveStream> CreateManualAsync(string name, string url, CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes a picture of the stream now and stores it as a document.
    ///
    /// A fresh decode of the newest part of the buffer, at full source resolution, rather than the
    /// preview, which is both smaller and up to a keyframe interval older. Served while a stream is
    /// interrupted, refused once it is gone.
    /// </summary>
    Task<Guid?> SnapshotAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a recording, or extends the one already running.
    ///
    /// Returns immediately with the recording's identity and state; the recording then has no
    /// further relationship with whoever asked for it. Closing the client, losing the client, or
    /// never having had one changes nothing. A person pressing record and a detector firing are
    /// the same caller on this one path.
    /// </summary>
    /// <param name="duration">
    /// When given, the stop time is fixed at the start, which is what would otherwise be called a
    /// clip. Two operations for one thing would drift apart.
    /// </param>
    Task<RecordingStatus?> RecordAsync(
        string name,
        TimeSpan? duration,
        CancellationToken cancellationToken = default);

    /// <summary>Ends the recording now. It becomes a document, as it would have anyway.</summary>
    Task<bool> StopRecordingAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>Drops the connection and ends the stream. Any recording closes as a document.</summary>
    Task<bool> StopAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the stream to a viewer as MPEG-TS until they leave or it ends.
    ///
    /// During an interruption the connection is held open and nothing is sent. The client already
    /// knows the stream is interrupted from its state, and closing would push every viewer into
    /// reconnecting at the exact moment a reconnect storm is under way on the ingest side.
    /// </summary>
    Task WriteToViewerAsync(
        ViewerRequest request,
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// How far back a viewer asking for this much would actually start, in seconds. Answered
    /// before anything is written, because the response says what it gave: a caller asking for
    /// twenty seconds and receiving twenty-six is normal rather than an error, since a stream can
    /// only be joined at a position a decoder can start from.
    /// </summary>
    double ResolvePreroll(string name, double seconds);
}
