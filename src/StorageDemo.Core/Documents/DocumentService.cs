using Microsoft.Extensions.Logging;
using StorageDemo.Core.Storage;

namespace StorageDemo.Core.Documents;

/// <summary>
/// Coordinates the two independent infrastructure concerns: bytes in <see cref="IFileStorage"/>,
/// metadata in <see cref="IDocumentRepository"/>. They share no transaction, so writes are
/// compensated rather than rolled back. See README "Consistency".
/// </summary>
public sealed class DocumentService(
    IFileStorage fileStorage,
    IDocumentRepository repository,
    IMediaAnalyzer mediaAnalyzer,
    IAnalysisQueue analysisQueue,
    IChangeFeed changeFeed,
    ILogger<DocumentService> logger) : IDocumentService
{
    /// <summary>Thumbnails live outside the documents prefix so the monitor never imports them.</summary>
    private const string ThumbnailPrefix = "thumbnails/";

    public async Task<Document> UploadAsync(
        string fileName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(content);

        var id = Guid.NewGuid();
        var safeName = SanitizeFileName(fileName);
        var storageKey = $"documents/{id}/{safeName}";

        // Counted on the way through, so an unseekable gRPC or HTTP upload still records a size.
        var counted = new CountingStream(content);
        await fileStorage.SaveAsync(storageKey, counted, contentType, cancellationToken);

        var document = new Document
        {
            Id = id,
            FileName = safeName,
            StorageKey = storageKey,
            ContentType = contentType,
            Size = counted.BytesRead,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        try
        {
            await repository.AddAsync(document, cancellationToken);
        }
        catch
        {
            // Compensate: the file is stored but nothing points at it.
            await TryDeleteAsync(storageKey, id);
            throw;
        }

        logger.LogInformation(
            "Document uploaded {DocumentId} {StorageKey} {Size}",
            document.Id,
            document.StorageKey,
            document.Size);

        // Other connected clients see the new document without polling.
        changeFeed.Publish(new DocumentChange(
            ChangeKind.Added,
            document.Id,
            document.StorageKey,
            document.FileName));

        // Probing and thumbnailing happen after the caller is answered. The upload returns as soon
        // as the bytes and the row are safe; the preview arrives moments later as an Updated event.
        if (mediaAnalyzer.CanAnalyze(contentType, safeName))
        {
            await analysisQueue.EnqueueAsync(
                new AnalysisRequest(id, storageKey, safeName, contentType),
                cancellationToken);
        }

        return document;
    }

    /// <summary>
    /// Reads the object back out of storage to probe it and render a preview. Reading it back
    /// rather than teeing the upload keeps the write path streaming, and works identically for
    /// objects the reconciler finds that were never uploaded through here.
    ///
    /// Called from the analysis worker rather than from the upload itself, so the caller is never
    /// waiting on ffmpeg.
    /// </summary>
    public async Task<StoredAnalysis> AnalyzeStoredObjectAsync(
        Guid id,
        string storageKey,
        string fileName,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        if (!mediaAnalyzer.CanAnalyze(contentType, fileName))
        {
            return StoredAnalysis.None;
        }

        MediaAnalysis analysis;
        try
        {
            await using var stored = await fileStorage.OpenReadAsync(storageKey, cancellationToken);
            if (stored is null)
            {
                return StoredAnalysis.None;
            }

            analysis = await mediaAnalyzer.AnalyzeAsync(stored, fileName, contentType, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A missing or unhappy ffmpeg must not fail an upload; the file is already stored.
            logger.LogWarning(ex, "Media analysis failed for {StorageKey}", storageKey);
            return StoredAnalysis.None;
        }

        if (analysis.Thumbnail is null || analysis.Thumbnail.Length == 0)
        {
            return new StoredAnalysis(analysis.Metadata, null);
        }

        var thumbnailKey = $"{ThumbnailPrefix}{id}.jpg";

        try
        {
            using var thumbnail = new MemoryStream(analysis.Thumbnail);
            await fileStorage.SaveAsync(thumbnailKey, thumbnail, "image/jpeg", cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not store the thumbnail for {StorageKey}", storageKey);
            return new StoredAnalysis(analysis.Metadata, null);
        }

        return new StoredAnalysis(analysis.Metadata, thumbnailKey);
    }

    public Task<Document?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => repository.GetAsync(id, cancellationToken);

    public Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default)
        => repository.GetAllAsync(cancellationToken);

    public async Task<DocumentContent?> DownloadAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await repository.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return null;
        }

        var stream = await fileStorage.OpenReadAsync(document.StorageKey, cancellationToken);
        if (stream is null)
        {
            logger.LogWarning(
                "Metadata references a missing object {DocumentId} {StorageKey}",
                document.Id,
                document.StorageKey);

            return null;
        }

        return new DocumentContent(stream, document.FileName, document.ContentType);
    }

    public async Task<DocumentContent?> DownloadThumbnailAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var document = await repository.GetAsync(id, cancellationToken);
        if (document?.ThumbnailKey is null)
        {
            return null;
        }

        var stream = await fileStorage.OpenReadAsync(document.ThumbnailKey, cancellationToken);

        return stream is null
            ? null
            : new DocumentContent(stream, $"{document.FileName}.jpg", "image/jpeg");
    }

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var document = await repository.GetAsync(id, cancellationToken);
        if (document is null)
        {
            return;
        }

        await fileStorage.DeleteAsync(document.StorageKey, cancellationToken);

        if (document.ThumbnailKey is not null)
        {
            // Idempotent, so a thumbnail that was never written is not an error.
            await fileStorage.DeleteAsync(document.ThumbnailKey, cancellationToken);
        }

        await repository.DeleteAsync(id, cancellationToken);

        logger.LogInformation("Document deleted {DocumentId} {StorageKey}", id, document.StorageKey);

        changeFeed.Publish(new DocumentChange(
            ChangeKind.Removed,
            document.Id,
            document.StorageKey,
            document.FileName));
    }

    private async Task TryDeleteAsync(string key, Guid id)
    {
        try
        {
            await fileStorage.DeleteAsync(key, CancellationToken.None);
        }
        catch (Exception cleanupFailure)
        {
            logger.LogError(
                cleanupFailure,
                "Orphaned object left behind after metadata write failed {DocumentId} {StorageKey}",
                id,
                key);
        }
    }

    /// <summary>Strips any client-supplied path. The name is display metadata, never identity.</summary>
    private static string SanitizeFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "file" : name;
    }
}

/// <summary>What analysis produced for an object that is already in the store.</summary>
public sealed record StoredAnalysis(IReadOnlyDictionary<string, string> Metadata, string? ThumbnailKey)
{
    public static readonly StoredAnalysis None = new(new Dictionary<string, string>(), null);
}
