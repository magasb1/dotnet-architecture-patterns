using System.IO;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using StorageDemo.Core.Documents;
using StorageDemo.Grpc;

namespace StorageDemo.Client;

/// <summary>
/// Everything this client knows about the server. gRPC only: no REST call is made from here,
/// the REST surface exists for curl and Swagger.
/// </summary>
public sealed class DocumentsApi : IDisposable
{
    private const int ChunkSize = 64 * 1024;

    private readonly GrpcChannel _channel;
    private readonly StorageDemo.Grpc.Documents.DocumentsClient _client;

    public DocumentsApi(string address)
    {
        Address = address;
        _channel = GrpcChannel.ForAddress(address);
        _client = new StorageDemo.Grpc.Documents.DocumentsClient(_channel);
    }

    public string Address { get; }

    public async Task<IReadOnlyList<DocumentMessage>> ListAsync(CancellationToken cancellationToken)
        => (await _client.ListAsync(new Empty(), cancellationToken: cancellationToken)).Documents;

    /// <summary>Returns null when the document is gone, which is a normal race with a delete.</summary>
    public async Task<DocumentMessage?> GetAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            return await _client.GetAsync(new DocumentId { Id = id }, cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<ProviderResponse> GetProvidersAsync(CancellationToken cancellationToken)
        => await _client.GetProvidersAsync(new Empty(), cancellationToken: cancellationToken);

    public async Task<DocumentMessage> UploadAsync(string path, CancellationToken cancellationToken)
    {
        using var call = _client.Upload(cancellationToken: cancellationToken);

        await call.RequestStream.WriteAsync(
            new UploadRequest
            {
                Metadata = new UploadMetadata
                {
                    FileName = Path.GetFileName(path),
                    ContentType = ContentTypes.Guess(path),
                },
            },
            cancellationToken);

        await using var source = File.OpenRead(path);
        var buffer = new byte[ChunkSize];

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await call.RequestStream.WriteAsync(
                new UploadRequest { Chunk = UnsafeByteOperations.UnsafeWrap(buffer.AsMemory(0, read)) },
                cancellationToken);
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    /// <summary>
    /// Streams a document to a local file and returns its path. WPF media playback and the system
    /// PDF viewer both need a real file, and caching one per document keeps repeat views instant.
    /// </summary>
    public async Task<string> DownloadToCacheAsync(
        DocumentMessage document,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "StorageDemoClient", document.Id);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, document.FileName);

        // Size is the cheap cache key: the monitor reports a size change when a file is replaced.
        if (File.Exists(path) && new FileInfo(path).Length == document.Size && document.Size > 0)
        {
            return path;
        }

        using var call = _client.Download(
            new DocumentId { Id = document.Id },
            cancellationToken: cancellationToken);

        await using (var target = File.Create(path))
        {
            await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                await target.WriteAsync(chunk.Data.Memory, cancellationToken);
            }
        }

        return path;
    }

    /// <summary>
    /// The server-rendered preview image. Returns null when the document has none, which is the
    /// normal answer for a text file or a PDF.
    /// </summary>
    public async Task<byte[]?> DownloadThumbnailAsync(string id, CancellationToken cancellationToken)
    {
        using var call = _client.DownloadThumbnail(
            new DocumentId { Id = id },
            cancellationToken: cancellationToken);

        using var buffer = new MemoryStream();

        try
        {
            await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                chunk.Data.WriteTo(buffer);
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            return null;
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>Live streams on air anywhere in the cluster.</summary>
    public async Task<LiveListResponse> ListLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _client.ListLiveAsync(new Empty(), cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
        {
            // An older server. Live simply does not exist as far as this client is concerned.
            return new LiveListResponse { Enabled = false };
        }
    }

    /// <summary>Null before the first picture of a stream has been decoded.</summary>
    public async Task<byte[]?> DownloadLivePreviewAsync(string name, CancellationToken cancellationToken)
    {
        using var call = _client.DownloadLivePreview(
            new LiveStreamName { Name = name },
            cancellationToken: cancellationToken);

        using var buffer = new MemoryStream();

        try
        {
            await foreach (var chunk in call.ResponseStream.ReadAllAsync(cancellationToken))
            {
                chunk.Data.WriteTo(buffer);
            }
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.Unimplemented)
        {
            return null;
        }

        return buffer.Length == 0 ? null : buffer.ToArray();
    }

    /// <summary>Takes a picture of a stream now. Null when the server would not or could not.</summary>
    public async Task<string?> SnapshotLiveAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var document = await _client.SnapshotLiveAsync(
                new LiveStreamName { Name = name },
                cancellationToken: cancellationToken);

            return document.Id;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.Unimplemented)
        {
            return null;
        }
    }

    /// <summary>
    /// Starts a recording, or extends the one already running. It continues whether or not this
    /// client stays connected, which is the point: closing the window does not stop it.
    /// </summary>
    public async Task<LiveRecordingMessage?> RecordLiveAsync(
        string name,
        double seconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _client.RecordLiveAsync(
                new RecordLiveRequest { Name = name, Seconds = seconds },
                cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.Unimplemented)
        {
            return null;
        }
    }

    public async Task StopLiveRecordingAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            await _client.StopLiveRecordingAsync(
                new LiveStreamName { Name = name },
                cancellationToken: cancellationToken);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.Unimplemented)
        {
        }
    }

    public async Task DeleteAsync(string id, CancellationToken cancellationToken)
        => await _client.DeleteAsync(new DocumentId { Id = id }, cancellationToken: cancellationToken);

    /// <summary>
    /// Long-lived server stream. It reports uploads and deletes from any client, and the changes
    /// the server's storage monitor picks up from outside the application entirely.
    /// </summary>
    public async IAsyncEnumerable<ChangeEvent> WatchAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var call = _client.Watch(new Empty(), cancellationToken: cancellationToken);

        while (true)
        {
            ChangeEvent change;
            try
            {
                if (!await call.ResponseStream.MoveNext(cancellationToken))
                {
                    yield break;
                }

                change = call.ResponseStream.Current;
            }
            catch (RpcException)
            {
                // Connection lost. The caller reconnects rather than tearing down the window.
                yield break;
            }

            yield return change;
        }
    }

    public void Dispose() => _channel.Dispose();
}
