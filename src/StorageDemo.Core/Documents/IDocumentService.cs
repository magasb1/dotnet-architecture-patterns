namespace StorageDemo.Core.Documents;

public sealed record DocumentContent(Stream Stream, string FileName, string? ContentType);

public interface IDocumentService
{
    Task<Document> UploadAsync(
        string fileName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken = default);

    Task<DocumentContent?> DownloadAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>The generated preview image, or null when the document has none.</summary>
    Task<DocumentContent?> DownloadThumbnailAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Document?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Probes an object that is already in the store and writes its thumbnail. Used by upload and
    /// by the reconciler, so both produce identical results.
    /// </summary>
    Task<StoredAnalysis> AnalyzeStoredObjectAsync(
        Guid id,
        string storageKey,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default);
}
