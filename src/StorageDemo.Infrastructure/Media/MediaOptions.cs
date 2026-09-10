using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Media;

public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>Turn off to skip probing and thumbnails entirely.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Where the libav libraries live. Empty means the ones bundled with the application, which is
    /// what every normal deployment uses. Point it at a build compiled with libsrt to get SRT.
    /// </summary>
    public string? LibraryPath { get; init; }

    /// <summary>JPEG quality, on libav's scale where 1 is best and 31 is worst.</summary>
    [Range(1, 31)]
    public int ThumbnailQuality { get; init; } = 4;

    /// <summary>Long edge of the generated thumbnail, in pixels.</summary>
    [Range(32, 2048)]
    public int ThumbnailSize { get; init; } = 320;

    /// <summary>
    /// How far into a video to grab the preview frame. The very first frame is often black,
    /// a fade-in or a slate.
    /// </summary>
    [Range(0, 600)]
    public double VideoFrameSeconds { get; init; } = 3;

}
