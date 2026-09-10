using System.Runtime.CompilerServices;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Storage;

namespace StorageDemo.Tests.Application;

public sealed class FakeFileStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = [];

    public bool FailOnSave { get; set; }

    public bool FailOnDelete { get; set; }

    public Task SaveAsync(string key, Stream content, string? contentType, CancellationToken ct = default)
    {
        if (FailOnSave)
        {
            throw new StorageException("save failed");
        }

        using var buffer = new MemoryStream();
        content.CopyTo(buffer);
        Objects[key] = buffer.ToArray();
        return Task.CompletedTask;
    }

    public Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Objects.TryGetValue(key, out var bytes)
            ? (Stream?)new MemoryStream(bytes)
            : null);

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        if (FailOnDelete)
        {
            throw new StorageException("delete failed");
        }

        Objects.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default)
        => Task.FromResult(Objects.ContainsKey(key));

    public async IAsyncEnumerable<StorageObject> ListAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var (key, bytes) in Objects.Where(o => o.Key.StartsWith(prefix, StringComparison.Ordinal)))
        {
            yield return new StorageObject(key, bytes.Length, DateTimeOffset.UnixEpoch);
        }

        await Task.CompletedTask;
    }
}

/// <summary>Stands in for ffmpeg: records what was asked for, returns a fixed fake thumbnail.</summary>
public sealed class FakeMediaAnalyzer : IMediaAnalyzer
{
    public static readonly byte[] ThumbnailBytes = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];

    public List<string> Analyzed { get; } = [];

    public bool ProduceThumbnail { get; set; } = true;

    public Dictionary<string, string> Metadata { get; set; } = new() { ["Container"] = "fake" };

    public bool CanAnalyze(string? contentType, string fileName)
        => contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true
            || contentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true;

    public Task<MediaAnalysis> AnalyzeAsync(
        Stream content,
        string fileName,
        string? contentType,
        CancellationToken ct = default)
    {
        Analyzed.Add(fileName);

        return Task.FromResult(new MediaAnalysis(
            Metadata,
            ProduceThumbnail ? ThumbnailBytes : null));
    }
}

public sealed class FakeDocumentRepository : IDocumentRepository
{
    public Dictionary<Guid, Document> Documents { get; } = [];

    public bool FailOnAdd { get; set; }

    public Task<Document?> GetAsync(Guid id, CancellationToken ct = default)
        => Task.FromResult(Documents.GetValueOrDefault(id));

    public Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Document>>(Documents.Values.ToList());

    public Task AddAsync(Document document, CancellationToken ct = default)
    {
        if (FailOnAdd)
        {
            throw new PersistenceException("add failed");
        }

        Documents[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task UpsertAsync(Document document, CancellationToken ct = default)
    {
        Documents[document.Id] = document;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        Documents.Remove(id);
        return Task.CompletedTask;
    }
}
