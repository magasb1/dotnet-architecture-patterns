using FFmpeg.AutoGen.Abstractions;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// The frame subscriber that is always attached, keeping a stream's preview current.
///
/// A preview is the picture right now. That is a different question from a poster frame, which is
/// the picture a fixed number of seconds into a piece of media, and asking the wrong one is
/// precisely what made an earlier implementation serve a fixed frame for minutes while every
/// component reported success. Here there is no seeking and no window: the decoder hands over
/// whatever it just decoded, and the newest one wins.
///
/// It throttles rather than encoding every picture it is handed. A tile in a grid does not need a
/// new JPEG per keyframe, and the encode is the only cost in this path worth avoiding.
/// </summary>
public sealed class Harvester(int size, int quality, TimeSpan cadence)
{
    private readonly Lock _gate = new();

    private byte[]? _preview;
    private DateTimeOffset _encodedAt = DateTimeOffset.MinValue;

    /// <summary>The newest picture, or null before the first one has been decoded.</summary>
    public byte[]? Preview
    {
        get { lock (_gate) { return _preview; } }
    }

    public DateTimeOffset? UpdatedAt => _encodedAt == DateTimeOffset.MinValue ? null : _encodedAt;

    public unsafe void OnFrame(IntPtr frame)
    {
        if (DateTimeOffset.UtcNow - _encodedAt < cadence)
        {
            return;
        }

        var encoded = JpegEncoder.Encode((AVFrame*)frame, size, quality);

        if (encoded is null)
        {
            return;
        }

        lock (_gate)
        {
            _preview = encoded;
            _encodedAt = DateTimeOffset.UtcNow;
        }
    }
}
