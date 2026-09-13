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
    ILiveSourceStore sources,
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

    public override async Task<ListResponse> ListByDetection(
        DetectionReferenceMessage request,
        ServerCallContext context)
    {
        var response = new ListResponse();
        response.Documents.AddRange(
            (await documents.FindByDetectionAsync(Reference(request)!, context.CancellationToken))
                .Select(ToMessage));

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
            response.Streams.Add(ToMessage(stream));
        }

        return response;
    }

    private static LiveStreamMessage ToMessage(LiveStream stream)
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
            HasKlv = stream.HasKlv,
            DetectionEnabled = stream.DetectionEnabled,
            DetectionRate = stream.DetectionRate,
        };

        if (stream.Recording is { } recording)
        {
            message.Recording = ToMessage(recording);
        }

        if (stream.KlvAt is { } klvAt)
        {
            message.LastKlvAt = Timestamp.FromDateTimeOffset(klvAt);
        }

        // Null stays absent: unmarked is not the same answer as an empty marking.
        if (stream.Classification is { } classification)
        {
            message.Classification = classification;
        }

        if (stream.DetectionWorker is { } worker)
        {
            message.DetectionWorker = worker;
        }

        foreach (var forward in stream.Forwards ?? [])
        {
            message.Forwards.Add(ToMessage(forward));
        }

        return message;
    }

    /// <summary>
    /// Configuration rather than state, so nothing is asked of an owning replica: the store is
    /// shared and whichever replica this call reached can answer it. Each source carries the
    /// stream of its name when one is on air, so a client showing both makes one call.
    /// </summary>
    public override async Task<LiveSourceListResponse> ListLiveSources(Empty request, ServerCallContext context)
    {
        RequireLive();

        var response = new LiveSourceListResponse();
        var streams = (await live.StreamsAsync(context.CancellationToken)).ToDictionary(stream => stream.Name);

        foreach (var source in await sources.ListAsync(context.CancellationToken))
        {
            response.Sources.Add(ToMessage(source, streams.GetValueOrDefault(source.Name)));
        }

        return response;
    }

    /// <summary>
    /// Creates the source or replaces it whole. The same rules the REST route applies, from the
    /// same place: a row this port could save and that one refuses would make the allowlist a
    /// suggestion.
    /// </summary>
    public override async Task<LiveSourceMessage> SaveLiveSource(
        LiveSourceMessage request,
        ServerCallContext context)
    {
        RequireLive();

        var source = new LiveSource(
            request.Name,
            string.IsNullOrWhiteSpace(request.Url) ? null : request.Url,
            request.Enabled,
            LiveSourceRules.WithIds([.. request.Forwards.Select(forward =>
                new ForwardTarget(forward.Id, forward.Url, forward.Enabled))]),
            DateTimeOffset.UtcNow);

        if (LiveSourceRules.Refuse(source, liveOptions.Value.AllowedSchemes) is { } rejection)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, rejection));
        }

        await sources.SaveAsync(source, context.CancellationToken);

        // Returned rather than acknowledged, because the ids minted above are on it.
        return ToMessage(source);
    }

    public override async Task<Empty> DeleteLiveSource(LiveStreamName request, ServerCallContext context)
    {
        RequireLive();

        // The configuration only. A stream already on air under this name belongs to whoever is
        // watching it until an operator stops it themselves.
        await sources.RemoveAsync(request.Name, context.CancellationToken);

        return new Empty();
    }

    private static LiveSourceMessage ToMessage(LiveSource source, LiveStream? stream = null)
    {
        var message = new LiveSourceMessage
        {
            Name = source.Name,
            Url = source.Url ?? string.Empty,
            Enabled = source.Enabled,
            UpdatedAt = Timestamp.FromDateTimeOffset(source.UpdatedAt),
        };

        foreach (var forward in source.Forwards)
        {
            message.Forwards.Add(new ForwardTargetMessage
            {
                Id = forward.Id,
                Url = forward.Url,
                Enabled = forward.Enabled,
            });
        }

        if (stream is not null)
        {
            message.Stream = ToMessage(stream);
        }

        return message;
    }

    private static ForwardStatusMessage ToMessage(ForwardStatus forward)
    {
        var message = new ForwardStatusMessage
        {
            Id = forward.Id,
            Url = forward.Url,
            Connected = forward.Connected,
            Bytes = forward.Bytes,
        };

        if (forward.ConnectedAt is { } connectedAt)
        {
            message.ConnectedAt = Timestamp.FromDateTimeOffset(connectedAt);
        }

        // Null stays absent: a forward that has never run is not one that failed without a reason.
        if (forward.Error is { } error)
        {
            message.Error = error;
        }

        return message;
    }

    /// <summary>
    /// Set through the owner, which is what publishes the entry a worker reads. Landing elsewhere
    /// it is forwarded over the REST route with the caller's token, as KLV is fetched.
    /// </summary>
    public override async Task<LiveStreamMessage> SetLiveDetection(
        SetLiveDetectionRequest request,
        ServerCallContext context)
    {
        RequireLive();

        var stream = live.Owns(request.Name)
            ? await live.SetDetectionAsync(request.Name, request.Enabled, request.Rate, context.CancellationToken)
            : await FetchFromOwnerAsync<LiveStream>(
                request.Name,
                $"api/live/detect/{request.Name}",
                context,
                HttpMethod.Put,
                new DetectRequest(request.Enabled, request.Rate));

        return stream is null
            ? throw new RpcException(new Status(StatusCode.NotFound, "No such live stream."))
            : ToMessage(stream);
    }

    public override async Task<LiveDetectionsMessage> GetLiveDetections(LiveStreamName request, ServerCallContext context)
    {
        RequireLive();

        var sample = live.Owns(request.Name)
            ? live.Detections(request.Name)
            : await FetchFromOwnerAsync<VmtiSample>(request.Name, $"api/live/detections/{request.Name}", context);

        return sample is null
            ? throw new RpcException(new Status(StatusCode.NotFound, "No detections on that stream."))
            : ToMessage(sample);
    }

    private static LiveDetectionsMessage ToMessage(VmtiSample sample)
    {
        var message = new LiveDetectionsMessage
        {
            Timestamp = Timestamp.FromDateTimeOffset(sample.Frame.Timestamp),
            FrameWidth = sample.Frame.FrameWidth,
            FrameHeight = sample.Frame.FrameHeight,
            Raw = ByteString.CopyFrom(sample.Raw),
        };

        foreach (var detection in sample.Frame.Detections)
        {
            var target = new VmtiTargetMessage
            {
                Id = detection.Id,
                Left = detection.Left,
                Top = detection.Top,
                Right = detection.Right,
                Bottom = detection.Bottom,
            };

            if (detection.ConfidencePercent is { } confidence) target.ConfidencePercent = confidence;
            if (detection.OntologyClass is { } ontologyClass) target.OntologyClass = ontologyClass;

            if (detection.Track is { } track)
            {
                target.TrackId = track.Id.ToString();
                target.TrackStatus = (StorageDemo.Grpc.VmtiTrackStatus)track.Status;
            }

            message.Targets.Add(target);
        }

        return message;
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

    public override async Task<DocumentId> SnapshotLive(
        SnapshotLiveRequest request,
        ServerCallContext context)
    {
        RequireLive();

        var id = await live.SnapshotAsync(request.Name, Reference(request.Detection), context.CancellationToken)
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

        var status = await live.RecordAsync(
                request.Name,
                duration,
                Reference(request.Detection),
                context.CancellationToken)
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                "That stream is not running on this replica."));

        return ToMessage(status);
    }

    /// <summary>
    /// The wire form of a detection reference, as the rest of the service knows it. A message with
    /// no timestamp names no frame, so it matches nothing, which is the right answer to a caller
    /// that sent half a reference.
    /// </summary>
    private static DetectionReference? Reference(DetectionReferenceMessage? detection)
        => detection is null
            ? null
            : new DetectionReference(
                detection.Stream,
                detection.Timestamp?.ToDateTimeOffset() ?? default,
                detection.TargetId);

    public override async Task<Empty> StopLiveRecording(LiveStreamName request, ServerCallContext context)
    {
        RequireLive();

        await live.StopRecordingAsync(request.Name, context.CancellationToken);

        return new Empty();
    }

    public override async Task<LiveKlvMessage> GetLiveKlv(LiveStreamName request, ServerCallContext context)
    {
        RequireLive();

        var sample = live.Owns(request.Name)
            ? live.Klv(request.Name)
            : await FetchFromOwnerAsync<KlvSample>(request.Name, $"api/live/klv/{request.Name}", context);

        return sample is null
            ? throw new RpcException(new Status(StatusCode.NotFound, "No KLV on that stream."))
            : ToMessage(sample);
    }

    /// <summary>
    /// Only the owner has the packets, so a call landing elsewhere is routed to it over the REST
    /// route, as the controller's own forwarding does. The token the caller presented travels
    /// with it, because the owner guards that route too.
    /// </summary>
    private async Task<T?> FetchFromOwnerAsync<T>(
        string name,
        string path,
        ServerCallContext context,
        HttpMethod? method = null,
        object? body = null)
        where T : class
    {
        var stream = await live.GetAsync(name, context.CancellationToken);

        return stream is null
            ? null
            : await peers.FetchAsync<T>(
                stream,
                path,
                context.RequestHeaders.GetValue(LiveTokenInterceptor.Header),
                context.CancellationToken,
                method,
                body);
    }

    private static LiveKlvMessage ToMessage(KlvSample sample)
    {
        var message = new LiveKlvMessage
        {
            Alignment = sample.Alignment switch
            {
                Core.Streaming.KlvAlignment.PresentationTimestamp => StorageDemo.Grpc.KlvAlignment.PresentationTimestamp,
                Core.Streaming.KlvAlignment.Timestamp => StorageDemo.Grpc.KlvAlignment.Timestamp,
                _ => StorageDemo.Grpc.KlvAlignment.Unspecified,
            },
            ReceivedAt = Timestamp.FromDateTimeOffset(sample.ReceivedAt),
            Raw = ByteString.CopyFrom(sample.Raw),
        };

        if (sample.ReferencePts is { } pts)
        {
            message.ReferencePts = pts;
        }

        if (sample.Fields is { } set)
        {
            message.Fields = ToMessage(set);
        }

        return message;
    }

    /// <summary>
    /// Every null stays absent rather than becoming zero: an error indicator from the sensor and
    /// a platform on the equator are different answers, and this is the one place they could be
    /// confused.
    /// </summary>
    private static Misb0601Message ToMessage(Misb0601Set set)
    {
        var message = new Misb0601Message();

        if (set.Timestamp is { } timestamp) message.Timestamp = Timestamp.FromDateTimeOffset(timestamp);
        if (set.MissionId is { } missionId) message.MissionId = missionId;
        if (set.PlatformHeading is { } heading) message.PlatformHeading = heading;
        if (set.PlatformPitch is { } pitch) message.PlatformPitch = pitch;
        if (set.PlatformRoll is { } roll) message.PlatformRoll = roll;
        if (set.PlatformDesignation is { } designation) message.PlatformDesignation = designation;
        if (set.ImageSourceSensor is { } sensor) message.ImageSourceSensor = sensor;
        if (set.ImageCoordinateSystem is { } coordinates) message.ImageCoordinateSystem = coordinates;
        if (set.SensorLatitude is { } latitude) message.SensorLatitude = latitude;
        if (set.SensorLongitude is { } longitude) message.SensorLongitude = longitude;
        if (set.SensorTrueAltitude is { } altitude) message.SensorTrueAltitude = altitude;
        if (set.SensorHorizontalFov is { } horizontalFov) message.SensorHorizontalFov = horizontalFov;
        if (set.SensorVerticalFov is { } verticalFov) message.SensorVerticalFov = verticalFov;
        if (set.SensorRelativeAzimuth is { } azimuth) message.SensorRelativeAzimuth = azimuth;
        if (set.SensorRelativeElevation is { } elevation) message.SensorRelativeElevation = elevation;
        if (set.SensorRelativeRoll is { } relativeRoll) message.SensorRelativeRoll = relativeRoll;
        if (set.SlantRange is { } range) message.SlantRange = range;
        if (set.FrameCenterLatitude is { } centreLatitude) message.FrameCenterLatitude = centreLatitude;
        if (set.FrameCenterLongitude is { } centreLongitude) message.FrameCenterLongitude = centreLongitude;
        if (set.FrameCenterElevation is { } centreElevation) message.FrameCenterElevation = centreElevation;
        if (set.Classification is { } classification) message.Classification = classification;
        if (set.Version is { } version) message.Version = version;

        foreach (var (tag, value) in set.Unparsed)
        {
            message.Unparsed[tag] = ByteString.CopyFrom(value);
        }

        return message;
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
