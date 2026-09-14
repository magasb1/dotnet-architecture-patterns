namespace StorageDemo.Core.Documents;

/// <summary>Provider-neutral failure. Infrastructure translates LiteDB/Npgsql errors into this.</summary>
public sealed class PersistenceException(string message, Exception? inner = null)
    : Exception(message, inner);
