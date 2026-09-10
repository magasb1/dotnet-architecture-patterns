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
}
