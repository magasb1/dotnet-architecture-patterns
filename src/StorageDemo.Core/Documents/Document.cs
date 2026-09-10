namespace StorageDemo.Core.Documents;

/// <summary>Metadata for a stored file. The bytes live in <see cref="Storage.IFileStorage"/>.</summary>
public sealed class Document
{
    public Guid Id { get; init; }

    public required string FileName { get; init; }

    public required string StorageKey { get; init; }

    public string? ContentType { get; init; }

    public long Size { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Key of the generated preview image, or null when the type has no thumbnail.</summary>
    public string? ThumbnailKey { get; init; }

    /// <summary>
    /// Whatever the media probe could read: dimensions, duration, codecs, bitrate, embedded tags.
    /// Empty for types that are not media.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();
}
