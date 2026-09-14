namespace StorageDemo.Core.Documents;

/// <summary>
/// One piece of a document that was written in pieces, and how big it is.
///
/// The size is kept rather than looked up because it is what makes the whole thing seekable: to
/// reach a point five hours in, the reader adds part sizes until it finds the piece that holds it
/// and opens only that one.
/// </summary>
public sealed record DocumentPart(string Key, long Size);

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
    /// Empty for anything that is not media.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; init; } =
        new Dictionary<string, string>();

    /// <summary>
    /// The pieces this document is made of, in order, when it was written in pieces rather than in
    /// one go. Empty for an ordinary upload, whose bytes are the single object at
    /// <see cref="StorageKey"/>.
    ///
    /// A recording that runs for hours cannot wait until it ends to be stored: the bytes would sit
    /// on one pod's disk the whole time and die with it. So it is written a few minutes at a time
    /// and each piece is uploaded as it completes, through the same ordinary storage path any file
    /// uses. Nothing here needs a feature only one provider has.
    ///
    /// None of that is visible to whoever opens it. One row, one name, one size, one download: the
    /// pieces are joined on the way out.
    /// </summary>
    public IReadOnlyList<DocumentPart> Parts { get; init; } = [];

    /// <summary>
    /// True when the bytes are the pieces rather than a single object. Worth asking about in a few
    /// places, because such a document has no object at its own storage key: that key names the
    /// document, and the pieces carry the bytes.
    /// </summary>
    public bool Segmented => Parts.Count > 0;
}
