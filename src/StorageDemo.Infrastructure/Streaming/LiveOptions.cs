using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Streaming;

public sealed class LiveOptions
{
    public const string SectionName = "Live";

    /// <summary>
    /// Off by default. Switching this on opens a port that anybody who can reach it may push a
    /// stream into, so it should be a decision rather than an inheritance.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Required in the X-Storage-Token header when set. Set it if the API is reachable.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Where encoders push. Reaching it lets you push a stream and nothing else, which is the
    /// whole reason it is not the API port.
    /// </summary>
    [Range(1, 65535)]
    public int IngestPort { get; init; } = 9000;

    /// <summary>
    /// Where viewers pull live streams, over SRT, symmetric with ingest. Separate from ingest so a
    /// deployment can expose one to the internet and keep the other on a private network, each
    /// with its own rules.
    /// </summary>
    [Range(1, 65535)]
    public int ConsumptionPort { get; init; } = 9010;

    /// <summary>
    /// The address to listen on. Every interface by default, which is what a container wants.
    /// </summary>
    public string IngestAddress { get; init; } = "0.0.0.0";

    /// <summary>
    /// How long a sender may fall silent before the transport gives up on it. The stream then
    /// becomes interrupted rather than gone, and the grace period below decides the rest.
    /// </summary>
    [Range(1, 300)]
    public int FeedTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// How long an interrupted stream is kept before it is considered gone: still claimed, still
    /// listed, hub and buffer and any recording all still alive. A tile that vanishes and returns
    /// is worse than one showing a state, and after a resume it would be the same stream on both
    /// sides of the gap.
    /// </summary>
    [Range(1, 3600)]
    public int GracePeriodSeconds { get; init; } = 30;

    /// <summary>
    /// The receiver's buffering delay on both media ports, in milliseconds. It is the floor on
    /// end-to-end latency and the budget out of which a lost packet is retransmitted, so it is the
    /// one number to turn down on a link that does not need it.
    ///
    /// Haivision's deployment guide sizes it as a multiple of the round trip: about four times the
    /// RTT as a rule of thumb, three on a link losing under one percent, and never below 60 ms.
    /// A LAN is therefore 60. The internet is whatever the internet is that day.
    ///
    /// libsrt's own live default, kept so that a deployment which sets nothing behaves exactly as
    /// it did before this option existed.
    /// </summary>
    [Range(0, 8000)]
    public int SrtLatencyMs { get; init; } = 120;

    /// <summary>
    /// Identifies this replica. Defaults to POD_NAME, which Kubernetes supplies from the downward
    /// API, and to the machine name elsewhere.
    /// </summary>
    public string? NodeName { get; init; }

    /// <summary>
    /// The address other replicas reach this one on, recorded with the name claim. A replica
    /// holding no stream of its own forwards a viewer here, because only the owner has the bytes.
    /// </summary>
    public string? PeerBaseUrl { get; init; }

    /// <summary>
    /// The SRT address other replicas reach this one's consumption port on, for example
    /// "srt://storage-demo-abc123:9010". A viewer that lands on a replica which does not own the
    /// stream is relayed through this, because SRT has no redirect.
    /// </summary>
    public string? PeerConsumptionBaseUrl { get; init; }

    /// <summary>
    /// The SRT address to hand to a player, when this instance knows its own, for example
    /// "srt://live.example.com:9010". Empty means the client builds one from the host it is
    /// already talking to.
    /// </summary>
    public string? PublicConsumptionUrl { get; init; }

    /// <summary>
    /// How often the preview is refreshed. Short, because a live tile that updates every few
    /// seconds reads as live and one that does not reads as a stuck image.
    /// </summary>
    [Range(1, 300)]
    public int PreviewIntervalSeconds { get; init; } = 2;

    /// <summary>
    /// How much recent stream every hub keeps, as a promise of at least this many seconds. The
    /// actual window moves in whole segments, so a sender with a coarse keyframe interval gives
    /// more than this and in coarser steps.
    /// </summary>
    [Range(5, 300)]
    public double BufferWindowSeconds { get; init; } = 30;

    /// <summary>
    /// The hard memory bound per stream, which is what stops one careless encoder evicting the
    /// service. Thirty seconds is roughly six megabytes for a modest feed and seventy-five for a
    /// contribution one, so this has to be forgiving enough to hold a couple of segments from a
    /// coarse sender or it will bind before the window does and quietly shorten every pre-roll.
    /// </summary>
    [Range(1024 * 1024, 2L * 1024 * 1024 * 1024)]
    public long BufferByteCeiling { get; init; } = 96L * 1024 * 1024;

    /// <summary>
    /// How far either side of a trigger a recording reaches, so an event already under way when
    /// it was noticed is still caught. A floor rather than an exact figure: a recording can only
    /// begin at a position a decoder can start from.
    /// </summary>
    [Range(0, 60)]
    public double PrerollSeconds { get; init; } = 5;

    /// <summary>
    /// How long a recording runs past its trigger when no duration was given, and how much longer
    /// each further trigger extends it. Continuous detection then leaves one clip covering the
    /// whole event rather than a drift of overlapping near-duplicates.
    /// </summary>
    [Range(1, 3600)]
    public double DefaultRecordingSeconds { get; init; } = 30;

    /// <summary>
    /// How much of a recording is muxed to a local file before it is stored and the file deleted.
    ///
    /// A part, not a segment: a segment in this codebase is the buffer's unit, one keyframe to the
    /// next, and is the sender's to decide. This is ours, and it is minutes rather than seconds.
    ///
    /// It bounds disk and memory for a recording of any length, since a camera running for six
    /// hours costs one part at a time. It also decides how much is lost if the pod goes:
    /// everything up to the last completed part is already in storage. Short enough to keep both
    /// small, long enough that a day of recording is not a hundred thousand objects.
    /// </summary>
    [Range(0.1, 60)]
    public double RecordingPartMinutes { get; init; } = 5;

    /// <summary>
    /// The ceiling on one recording, so a stream nobody stops still ends somewhere. Disk no longer
    /// needs a ceiling, because segments are stored and deleted as they complete; this bounds the
    /// document instead. On reaching it the recording closes and a still-firing trigger starts the
    /// next one.
    /// </summary>
    [Range(1, 7 * 24 * 60)]
    public int MaxRecordingMinutes { get; init; } = 12 * 60;

    /// <summary>
    /// Queue depth for a viewer, in packets. Overflowing costs a viewer a skip forward to live,
    /// which is the right answer for something that must never accumulate delay.
    /// </summary>
    [Range(64, 100_000)]
    public int ViewerQueuePackets { get; init; } = 2_000;

    /// <summary>
    /// Queue depth for a recorder. Larger, because overflowing here is not a skip: it ends the
    /// recording and marks the document truncated, and that must be genuinely rare.
    /// </summary>
    [Range(64, 1_000_000)]
    public int RecorderQueuePackets { get; init; } = 20_000;

    /// <summary>
    /// How long libav may spend working out what an arriving stream contains, in seconds.
    ///
    /// This is dead time between a camera connecting and its stream being on air, and libav's own
    /// default is five seconds. MPEG-TS repeats its tables every hundred milliseconds or so, so a
    /// second is generous for a camera that presents everything at once.
    ///
    /// Raise it for a source that starts its audio late: a probe that ends before the audio track
    /// appears produces a stream, and recordings from it, with no sound.
    /// </summary>
    [Range(0.1, 30)]
    public double ProbeSeconds { get; init; } = 1;

    /// <summary>
    /// How many bytes libav may read while working that out. The other half of the same limit,
    /// and the one that binds on a high bitrate source.
    /// </summary>
    [Range(32 * 1024, 64 * 1024 * 1024)]
    public long ProbeBytes { get; init; } = 1024 * 1024;

    /// <summary>Where recordings are written before they are uploaded as documents.</summary>
    public string? RecordingDirectory { get; init; }

    /// <summary>
    /// Transports a manual stream may name. Anything outside this list is refused, which is what
    /// stops "create a stream" from turning into "read this local file". Automatic ingest needs no
    /// such list: it is one port and one protocol, and nobody chooses a URL.
    /// </summary>
    public string[] AllowedSchemes { get; init; } = ["udp", "rtp", "srt"];

    /// <summary>
    /// Demuxer options for a manual input. The timeout gives up on a source nothing arrives from,
    /// rather than holding a socket until the process restarts, and the buffer absorbs a burst on
    /// a busy link.
    /// </summary>
    public Dictionary<string, string> ManualInputOptions { get; init; } = new()
    {
        ["timeout"] = "30000000",
        ["fifo_size"] = "1000000",
    };
}
