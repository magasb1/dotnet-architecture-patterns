using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using StorageDemo.Api.Controllers;
using StorageDemo.Api.Uploads;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Monitoring;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Grpc;
using StorageDemo.Infrastructure;

namespace StorageDemo.Api.Grpc;

/// <summary>
/// The primary API surface. It is a thin translation layer: every decision lives in
/// <see cref="IDocumentService"/>, which knows nothing about gRPC.
/// </summary>
public sealed class DocumentsGrpcService(
    IDocumentService documents,
    ILiveStreamService live,
    LivePeerProxy peers,
    IOptions<LiveOptions> liveOptions,
    IOptions<RetentionOptions> retention,
    ContentTypeSniffer sniffer,
    IChangeFeed changeFeed,
    ProviderInfo providers,
    ILogger<DocumentsGrpcService> logger) : StorageDemo.Grpc.Documents.DocumentsBase
{
    private const int ChunkSize = 64 * 1024;

    public override async Task<ListResponse> List(Empty request, ServerCallContext context)
    {
        var response = new ListResponse();
        response.Documents.AddRange(
            (await documents.GetAllAsync(context.CancellationToken)).Select(ToMessage));

        return response;
    }

    public override async Task<DocumentMessage> Get(DocumentId request, ServerCallContext context)
    {
        var document = await documents.GetAsync(ParseId(request.Id), context.CancellationToken);

        return document is null
            ? throw new RpcException(new Status(StatusCode.NotFound, "Document not found."))
            : ToMessage(document);
    }

    public override async Task Download(
        DocumentId request,
        IServerStreamWriter<Chunk> responseStream,
        ServerCallContext context)
    {
        var content = await documents.DownloadAsync(ParseId(request.Id), context.CancellationToken);

        await StreamAsync(content?.Stream, responseStream, context, "Document or object not found.");
    }

    public override async Task DownloadThumbnail(
        DocumentId request,
        IServerStreamWriter<Chunk> responseStream,
        ServerCallContext context)
    {
        var content = await documents.DownloadThumbnailAsync(
            ParseId(request.Id),
            context.CancellationToken);

        await StreamAsync(content?.Stream, responseStream, context, "No thumbnail for this document.");
    }

    private static async Task StreamAsync(
        Stream? source,
        IServerStreamWriter<Chunk> responseStream,
        ServerCallContext context,
        string missingMessage)
    {
        if (source is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, missingMessage));
        }

        await using var stream = source;
        var buffer = new byte[ChunkSize];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, context.CancellationToken);
            if (read == 0)
            {
                break;
            }

            await responseStream.WriteAsync(
                new Chunk { Data = UnsafeByteOperations.UnsafeWrap(buffer.AsMemory(0, read)) },
                context.CancellationToken);
        }
    }

    public override async Task<DocumentMessage> Upload(
        IAsyncStreamReader<UploadRequest> requestStream,
        ServerCallContext context)
    {
        if (!await requestStream.MoveNext(context.CancellationToken)
            || requestStream.Current.PayloadCase != UploadRequest.PayloadOneofCase.Metadata)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "The first message must carry upload metadata."));
        }

        var metadata = requestStream.Current.Metadata;

        // Pull the first data message so the type can be read from the bytes. It is handed back
        // to the stream below, so nothing is lost and nothing is read twice.
        ReadOnlyMemory<byte> head = default;
        while (await requestStream.MoveNext(context.CancellationToken))
        {
            if (requestStream.Current.PayloadCase == UploadRequest.PayloadOneofCase.Chunk)
            {
                head = requestStream.Current.Chunk.Memory;
                break;
            }
        }

        var contentType = await sniffer.ResolveAsync(
            string.IsNullOrWhiteSpace(metadata.ContentType) ? null : metadata.ContentType,
            metadata.FileName,
            head,
            context.CancellationToken);

        // The chunks are handed to the service as a stream, so nothing buffers the whole file.
        await using var content = new ChunkStream(requestStream, context.CancellationToken, head);

        var document = await documents.UploadAsync(
            metadata.FileName,
            content,
            contentType,
            context.CancellationToken);

        return ToMessage(document);
    }

    public override async Task<Empty> Delete(DocumentId request, ServerCallContext context)
    {
        await documents.DeleteAsync(ParseId(request.Id), context.CancellationToken);
        return new Empty();
    }

    public override async Task Watch(
        Empty request,
        IServerStreamWriter<ChangeEvent> responseStream,
        ServerCallContext context)
    {
        logger.LogInformation("Client subscribed to the change feed {Peer}", context.Peer);

        // Registered before the headers go out, so a client that waits for them cannot miss an
        // event published between its call arriving and the stream starting.
        using var subscription = changeFeed.Subscribe();
        await context.WriteResponseHeadersAsync(Metadata.Empty);

        try
        {
            await foreach (var change in subscription.ReadAllAsync(context.CancellationToken))
            {
                await responseStream.WriteAsync(
                    new ChangeEvent
                    {
                        Kind = change.Kind switch
                        {
                            ChangeKind.Added => ChangeEvent.Types.Kind.Added,
                            ChangeKind.Updated => ChangeEvent.Types.Kind.Updated,
                            ChangeKind.Removed => ChangeEvent.Types.Kind.Removed,
                            _ => ChangeEvent.Types.Kind.Unspecified,
                        },
                        DocumentId = change.DocumentId.ToString(),
                        StorageKey = change.StorageKey,
                        FileName = change.FileName,
                    },
                    context.CancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Client left the change feed {Peer}", context.Peer);
        }
    }

    public override async Task<LiveListResponse> ListLive(Empty request, ServerCallContext context)
    {
        var response = new LiveListResponse { Enabled = liveOptions.Value.Enabled };

        if (!response.Enabled)
        {
            return response;
        }

        response.Transports.AddRange(live.Transports);
        response.ConsumptionUrl = liveOptions.Value.PublicConsumptionUrl ?? string.Empty;
        response.ConsumptionPort = liveOptions.Value.ConsumptionPort;

        foreach (var stream in await live.StreamsAsync(context.CancellationToken))
        {
            var message = new LiveStreamMessage
            {
                Name = stream.Name,
                State = stream.State.ToString(),
                HasPreview = stream.HasPreview,
                Packets = stream.Packets,
                Bytes = stream.Bytes,
                StartedAt = Timestamp.FromDateTimeOffset(stream.StartedAt),
                Owner = stream.Owner,
                Layout = stream.Layout ?? string.Empty,
                Startable = stream.Startable,
                CeilingBinding = stream.CeilingBinding,
                BufferedSeconds = stream.BufferedSeconds,
                Manual = stream.Manual,
                PacketsLost = stream.PacketsLost,
                PacketsDropped = stream.PacketsDropped,
            };

            if (stream.Recording is { } recording)
            {
                message.Recording = ToMessage(recording);
            }

            response.Streams.Add(message);
        }

        return response;
    }

    public override async Task DownloadLivePreview(
        LiveStreamName request,
        IServerStreamWriter<Chunk> responseStream,
        ServerCallContext context)
    {
        if (!liveOptions.Value.Enabled)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "No such live stream."));
        }

        if (live.Preview(request.Name) is { } local)
        {
            await responseStream.WriteAsync(
                new Chunk { Data = UnsafeByteOperations.UnsafeWrap(local) },
                context.CancellationToken);

            return;
        }

        // Owned by another replica, so fetched from it, as the REST route does. Answering
        // NOT_FOUND here instead was a documented trade-off with one replica; in a cluster it
        // turns most of a client's grid into icons.
        var stream = await live.GetAsync(request.Name, context.CancellationToken);

        if (stream is null || !stream.HasPreview || live.Owns(request.Name))
        {
            throw new RpcException(new Status(StatusCode.NotFound, "No preview for this stream."));
        }

        await StreamAsync(
            await peers.OpenAsync(stream, $"api/live/preview/{request.Name}", context.CancellationToken),
            responseStream,
            context,
            "No preview for this stream.");
    }

    public override async Task<DocumentId> SnapshotLive(LiveStreamName request, ServerCallContext context)
    {
        RequireLive();

        var id = await live.SnapshotAsync(request.Name, context.CancellationToken)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                "That stream is not running on this replica, or it has nothing to capture."));

        return new DocumentId { Id = id.ToString() };
    }

    public override async Task<LiveRecordingMessage> RecordLive(
        RecordLiveRequest request,
        ServerCallContext context)
    {
        RequireLive();

        var duration = request.Seconds > 0 ? TimeSpan.FromSeconds(request.Seconds) : (TimeSpan?)null;

        var status = await live.RecordAsync(request.Name, duration, context.CancellationToken)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                "That stream is not running on this replica."));

        return ToMessage(status);
    }

    public override async Task<Empty> StopLiveRecording(LiveStreamName request, ServerCallContext context)
    {
        RequireLive();

        await live.StopRecordingAsync(request.Name, context.CancellationToken);

        return new Empty();
    }

    private void RequireLive()
    {
        if (!liveOptions.Value.Enabled)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Live streaming is disabled."));
        }
    }

    private static LiveRecordingMessage ToMessage(StorageDemo.Core.Streaming.RecordingStatus recording)
    {
        var message = new LiveRecordingMessage
        {
            Id = recording.Id.ToString(),
            StartedAt = Timestamp.FromDateTimeOffset(recording.StartedAt),
            Bytes = recording.Bytes,
            Truncated = recording.Truncated,
        };

        if (recording.EndsAt is { } endsAt)
        {
            message.EndsAt = Timestamp.FromDateTimeOffset(endsAt);
        }

        return message;
    }

    public override Task<ProviderResponse> GetProviders(Empty request, ServerCallContext context)
        => Task.FromResult(new ProviderResponse
        {
            Storage = providers.Storage,
            Database = providers.Database,
            ContentBaseUrl = providers.ContentBaseUrl ?? string.Empty,
            RetentionEnabled = retention.Value.Enabled,
            RetentionMaxAgeDays = retention.Value.Enabled ? retention.Value.MaxAgeDays : 0,
        });

    private static Guid ParseId(string id)
        => Guid.TryParse(id, out var parsed)
            ? parsed
            : throw new RpcException(new Status(StatusCode.InvalidArgument, "Malformed document id."));

    private static DocumentMessage ToMessage(Document document)
    {
        var message = new DocumentMessage
        {
            Id = document.Id.ToString(),
            FileName = document.FileName,
            StorageKey = document.StorageKey,
            ContentType = document.ContentType ?? string.Empty,
            Size = document.Size,
            CreatedAt = Timestamp.FromDateTimeOffset(document.CreatedAt),
            HasThumbnail = document.ThumbnailKey is not null,
        };

        message.Metadata.AddRange(
            document.Metadata.Select(entry => new MetadataEntry
            {
                Key = entry.Key,
                Value = entry.Value,
            }));

        return message;
    }
}
