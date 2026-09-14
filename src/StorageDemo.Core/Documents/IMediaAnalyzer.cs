namespace StorageDemo.Core.Documents;

/// <param name="Metadata">Everything the probe could read, ready to display as-is.</param>
/// <param name="Thumbnail">Encoded preview image, or null when one could not be produced.</param>
public sealed record MediaAnalysis(
    IReadOnlyDictionary<string, string> Metadata,
    byte[]? Thumbnail);

/// <summary>
/// Reads dimensions, duration and codecs out of a media file and renders a preview image.
/// The implementation shells out to ffmpeg; nothing about that leaks into this contract.
/// </summary>
public interface IMediaAnalyzer
{
    /// <summary>True when this file is worth probing at all: an image, a video or audio.</summary>
    bool CanAnalyze(string? contentType, string fileName);

    Task<MediaAnalysis> AnalyzeAsync(
        Stream content,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The last picture in the media, at source resolution, or null when it holds none.
    ///
    /// Deliberately not the same question as the thumbnail above. That one is the poster frame,
    /// a fixed number of seconds in, which is what a stored file wants. This one is "the picture
    /// now", which is what a live preview and a snapshot want. One verb answering both is how a
    /// live preview came to serve a fixed frame while everything reported success.
    /// </summary>
    Task<byte[]?> LatestFrameAsync(
        Stream content,
        string fileName,
        CancellationToken cancellationToken = default);
}
