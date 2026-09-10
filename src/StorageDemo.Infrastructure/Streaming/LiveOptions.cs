using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Streaming;

public sealed class LiveOptions
{
    public const string SectionName = "Live";

    /// <summary>
    /// Off by default. These endpoints make the service open sockets and send data to addresses a
    /// caller chooses, so switching them on should be a decision rather than an inheritance.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Required in the X-Storage-Token header when set. Set it if the API is reachable.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Transports a caller may name. Anything outside this list is refused, which is what stops a
    /// request from turning into "read this local file" or "post my bytes to that host".
    /// </summary>
    public string[] AllowedSchemes { get; init; } = ["udp", "rtp", "srt"];

    /// <summary>
    /// Identifies this replica in the shared registry. Defaults to POD_NAME, which Kubernetes can
    /// supply from the downward API, and to the machine name elsewhere.
    /// </summary>
    public string? NodeName { get; init; }

    /// <summary>
    /// The address viewers reach this replica on, used to build playback URLs. In a cluster this
    /// is the pod's own address, not the shared Service, because only this pod holds the stream.
    /// </summary>
    public string? PublicBaseUrl { get; init; }

    /// <summary>
    /// How to reach another replica, with {node} standing in for its name. In Kubernetes that is
    /// the headless Service, "http://{node}.storage-demo:8080". Empty means no peer can be reached,
    /// which is correct for a single instance since there are none.
    /// </summary>
    public string? PeerAddressTemplate { get; init; }

    /// <summary>
    /// How often a running stream gets a fresh preview frame. Short, because a live tile that
    /// updates every few seconds reads as live, and one that does not reads as a stuck image.
    /// </summary>
    [Range(1, 300)]
    public int ThumbnailIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// How much of the recording's tail to decode for that preview. Enough to contain a keyframe,
    /// small enough that sampling costs nothing next to the stream itself.
    /// </summary>
    [Range(64 * 1024, 32 * 1024 * 1024)]
    public long ThumbnailTailBytes { get; init; } = 4 * 1024 * 1024;

    /// <summary>
    /// Demuxer options for ingest. The default gives up on a listener that nothing connects to,
    /// rather than holding a port open until the process restarts, and buffers enough to absorb a
    /// burst on a busy link.
    /// </summary>
    public Dictionary<string, string> IngestOptions { get; init; } = new()
    {
        ["timeout"] = "30000000",
        ["fifo_size"] = "1000000",
    };
}
