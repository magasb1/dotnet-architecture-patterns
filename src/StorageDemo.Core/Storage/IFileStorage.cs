namespace StorageDemo.Core.Storage;

/// <summary>One object in a store, as reported by <see cref="IFileStorage.ListAsync"/>.</summary>
public sealed record StorageObject(string Key, long Size, DateTimeOffset LastModified);

/// <summary>Object/blob storage. Keys are logical ("documents/{id}/{name}"), never physical paths.</summary>
public interface IFileStorage
{
    Task SaveAsync(string key, Stream content, string? contentType, CancellationToken cancellationToken = default);

    Task<Stream?> OpenReadAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Idempotent: deleting a missing key succeeds.</summary>
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Enumerates every object under a key prefix. Used by the reconciler.</summary>
    IAsyncEnumerable<StorageObject> ListAsync(string prefix, CancellationToken cancellationToken = default);
}
