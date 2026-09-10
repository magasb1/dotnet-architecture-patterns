namespace StorageDemo.Core.Storage;

/// <summary>Provider-neutral failure. Infrastructure translates S3/filesystem errors into this.</summary>
public sealed class StorageException(string message, Exception? inner = null)
    : Exception(message, inner);
