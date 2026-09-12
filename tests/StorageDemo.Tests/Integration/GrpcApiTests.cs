using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StorageDemo.Core.Documents;
using StorageDemo.Core.Streaming;
using StorageDemo.Api.Grpc;
using StorageDemo.Grpc;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;
using StorageDemo.Tests.Infrastructure;

namespace StorageDemo.Tests.Integration;

/// <summary>
/// Drives the real application over a real gRPC channel: filesystem storage, LiteDB metadata and
/// the full upload, download, watch and delete path. Nothing is stubbed out.
/// </summary>
public sealed class GrpcApiTests : IAsyncLifetime
{
    private const string Token = "grpc-test-token";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "storage-demo-grpc-tests",
        Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> _factory = null!;
    private GrpcChannel _channel = null!;
    private StorageDemo.Grpc.Documents.DocumentsClient _client = null!;

    public ValueTask InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Provider", "FileSystem");
            builder.UseSetting("Storage:FileSystem:RootPath", Path.Combine(_root, "files"));
            builder.UseSetting("Database:Provider", "LiteDb");
            builder.UseSetting("Database:LiteDb:Path", Path.Combine(_root, "db", "app.db"));
            // The monitor is exercised directly in the reconciler tests; a timer here only adds flake.
            builder.UseSetting("StorageMonitor:Enabled", "false");

            // Live is on with a token, so the guard is exercised; nothing is sent to the ports.
            builder.UseSetting("Live:Enabled", "true");
            builder.UseSetting("Live:Token", Token);
            builder.UseSetting("Live:IngestPort", SrtSenders.FreePort().ToString());
            builder.UseSetting("Live:ConsumptionPort", SrtSenders.FreePort().ToString());
            builder.UseSetting("Retention:Enabled", "true");
            builder.UseSetting("Retention:MaxAgeDays", "30");
            builder.UseEnvironment("Production");
        });

        // The in-memory test server speaks HTTP/2 to gRPC through this handler.
        _channel = GrpcChannel.ForAddress(
            _factory.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });

        _client = new StorageDemo.Grpc.Documents.DocumentsClient(_channel);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Dispose();
        await _factory.DisposeAsync();

        // LiteDB releases its log file just after the host goes away. Cleaning up a temp directory
        // is housekeeping, so a couple of retries and then let it be; failing the test over it
        // would report a problem that does not exist.
        for (var attempt = 0; attempt < 3 && Directory.Exists(_root); attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
        }
    }

    [Fact]
    public async Task GetProviders_reports_the_configured_combination()
    {
        var providers = await _client.GetProvidersAsync(new Empty());

        Assert.Equal("FileSystem", providers.Storage);
        Assert.Equal("LiteDb", providers.Database);
    }

    [Fact]
    public async Task GetProviders_says_when_documents_expire()
    {
        var providers = await _client.GetProvidersAsync(new Empty());

        Assert.True(providers.RetentionEnabled);
        Assert.Equal(30, providers.RetentionMaxAgeDays);
    }

    [Fact]
    public async Task Live_rpcs_refuse_a_missing_or_wrong_token_and_document_rpcs_ignore_it()
    {
        var name = new LiveStreamName { Name = "guarded" };

        var missing = await Assert.ThrowsAsync<RpcException>(() => _client.SnapshotLiveAsync(name).ResponseAsync);
        var wrong = await Assert.ThrowsAsync<RpcException>(
            () => _client.RecordLiveAsync(new RecordLiveRequest { Name = "guarded" }, WithToken("nope")).ResponseAsync);
        var listing = await Assert.ThrowsAsync<RpcException>(() => _client.ListLiveAsync(new Empty()).ResponseAsync);

        Assert.Equal(StatusCode.Unauthenticated, missing.StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, wrong.StatusCode);
        Assert.Equal(StatusCode.Unauthenticated, listing.StatusCode);

        // The token is a live concern only; documents never asked for one.
        await _client.ListAsync(new Empty());
    }

    [Fact]
    public async Task Live_rpcs_accept_the_configured_token()
    {
        var listed = await _client.ListLiveAsync(new Empty(), WithToken(Token));
        Assert.True(listed.Enabled);

        // Past the guard and into the service, which has no such stream to act on.
        var failure = await Assert.ThrowsAsync<RpcException>(
            () => _client.SnapshotLiveAsync(new LiveStreamName { Name = "absent" }, WithToken(Token)).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, failure.StatusCode);
    }

    [Fact]
    public async Task ListLive_carries_the_health_figures()
    {
        // Written straight into the registry, which is what another replica's heartbeat is.
        var registry = _factory.Services.GetRequiredService<ILiveStreamRegistry>();
        var entry = LiveReplicas.Entry("lossy", "elsewhere", DateTimeOffset.UtcNow) with
        {
            PacketsLost = 3,
            PacketsDropped = 7,
        };
        await registry.UpsertAsync(entry);

        var listed = await _client.ListLiveAsync(new Empty(), WithToken(Token));
        var stream = Assert.Single(listed.Streams, s => s.Name == "lossy");

        Assert.Equal(3, stream.PacketsLost);
        Assert.Equal(7, stream.PacketsDropped);
    }

    /// <summary>
    /// The picture from a replica that does not own the stream, which in a cluster is most of
    /// them. Two applications in one process sharing a registry; see <see cref="LiveReplicas"/>.
    /// </summary>
    [Fact]
    public async Task DownloadLivePreview_is_answered_by_a_replica_that_does_not_own_the_stream()
    {
        Assert.SkipUnless(Srt.IsAvailable, LiveReplicas.NoLibsrt);
        Assert.SkipUnless(LiveReplicas.HasSrt(), LiveReplicas.NoFfmpegSrt);

        const string name = "proxied-camera";

        await using var replicas = new LiveReplicas();
        var aIngest = SrtSenders.FreePort();
        var a = replicas.Start("pod-a", aIngest);
        var b = replicas.Start("pod-b", SrtSenders.FreePort(), peer: a);

        replicas.Send(aIngest, name);

        await LiveReplicas.Until(
            async () => await replicas.Registry.GetAsync(name) is { HasPreview: true },
            TimeSpan.FromSeconds(40),
            "A never decoded a preview");

        using var channel = GrpcChannel.ForAddress(
            b.Server.BaseAddress,
            new GrpcChannelOptions { HttpHandler = b.Server.CreateHandler() });
        var viaB = new StorageDemo.Grpc.Documents.DocumentsClient(channel);

        using var call = viaB.DownloadLivePreview(new LiveStreamName { Name = name });
        var picture = await ReadAllAsync(call.ResponseStream);

        Assert.True(picture.Length > 2 && picture[0] == 0xFF && picture[1] == 0xD8, "not a JPEG");
    }

    private static Metadata WithToken(string token) => new() { { LiveTokenInterceptor.Header, token } };

    [Fact]
    public async Task Upload_then_download_round_trips_the_bytes()
    {
        var payload = "the quick brown fox"u8.ToArray();

        var uploaded = await UploadAsync("notes.txt", "text/plain", payload);

        Assert.Equal("notes.txt", uploaded.FileName);
        Assert.Equal(payload.Length, uploaded.Size);
        Assert.Equal($"documents/{uploaded.Id}/notes.txt", uploaded.StorageKey);

        Assert.Equal(payload, await DownloadAsync(uploaded.Id));
    }

    [Fact]
    public async Task Upload_streams_a_file_larger_than_one_chunk()
    {
        var payload = new byte[300 * 1024];
        Random.Shared.NextBytes(payload);

        var uploaded = await UploadAsync("big.bin", "application/octet-stream", payload);

        Assert.Equal(payload.Length, uploaded.Size);
        Assert.Equal(payload, await DownloadAsync(uploaded.Id));
    }

    [Fact]
    public async Task Uploaded_documents_appear_in_the_listing_and_disappear_after_delete()
    {
        var uploaded = await UploadAsync("listed.txt", "text/plain", "hello"u8.ToArray());

        var listed = await _client.ListAsync(new Empty());
        Assert.Contains(listed.Documents, d => d.Id == uploaded.Id);

        await _client.DeleteAsync(new DocumentId { Id = uploaded.Id });

        var afterDelete = await _client.ListAsync(new Empty());
        Assert.DoesNotContain(afterDelete.Documents, d => d.Id == uploaded.Id);
    }

    [Fact]
    public async Task A_lying_content_type_is_replaced_by_what_the_bytes_say()
    {
        // A JPEG announced as HTML. Believing the client would mean serving it back as markup.
        var jpeg = await File.ReadAllBytesAsync(await SampleJpegAsync());

        var uploaded = await UploadAsync("not-really.html", "text/html", jpeg);

        Assert.Equal("image/jpeg", uploaded.ContentType);
    }

    [Fact]
    public async Task A_format_with_no_signature_keeps_the_declared_type()
    {
        // Plain text has no magic bytes; absence of a signature is not evidence of lying.
        var uploaded = await UploadAsync("notes.txt", "text/plain", "just some words"u8.ToArray());

        Assert.Equal("text/plain", uploaded.ContentType);
    }

    [Fact]
    public async Task Get_of_an_unknown_id_reports_not_found()
    {
        var failure = await Assert.ThrowsAsync<RpcException>(
            () => _client.GetAsync(new DocumentId { Id = Guid.NewGuid().ToString() }).ResponseAsync);

        Assert.Equal(StatusCode.NotFound, failure.StatusCode);
    }

    [Fact]
    public async Task A_malformed_id_is_rejected_rather_than_treated_as_missing()
    {
        var failure = await Assert.ThrowsAsync<RpcException>(
            () => _client.GetAsync(new DocumentId { Id = "not-a-guid" }).ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
    }

    [Fact]
    public async Task An_upload_that_does_not_start_with_metadata_is_rejected()
    {
        using var call = _client.Upload();
        await call.RequestStream.WriteAsync(new UploadRequest { Chunk = ByteString.CopyFrom("x"u8) });
        await call.RequestStream.CompleteAsync();

        var failure = await Assert.ThrowsAsync<RpcException>(() => call.ResponseAsync);

        Assert.Equal(StatusCode.InvalidArgument, failure.StatusCode);
    }

    [Fact]
    public async Task Watch_pushes_an_event_when_a_document_is_uploaded()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var call = _client.Watch(new Empty(), cancellationToken: timeout.Token);

        // Response headers arrive only once the server has registered the subscription.
        await call.ResponseHeadersAsync;

        var received = ReadFirstAsync(call.ResponseStream, timeout.Token);
        var uploaded = await UploadAsync("watched.txt", "text/plain", "hi"u8.ToArray());
        var change = await received;

        Assert.Equal(ChangeEvent.Types.Kind.Added, change.Kind);
        Assert.Equal(uploaded.Id, change.DocumentId);
        Assert.Equal("watched.txt", change.FileName);
    }

    private static async Task<ChangeEvent> ReadFirstAsync(
        IAsyncStreamReader<ChangeEvent> stream,
        CancellationToken cancellationToken)
    {
        await stream.MoveNext(cancellationToken);
        return stream.Current;
    }

    /// <summary>Generated by the bundled ffmpeg, so no binary fixture lives in the repository.</summary>
    private async Task<string> SampleJpegAsync()
    {
        var path = Path.Combine(_root, "sample.jpg");
        Directory.CreateDirectory(_root);

        if (File.Exists(path))
        {
            return path;
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo(Ffmpeg.ExecutablePath)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in new[]
                 {
                     "-hide_banner", "-loglevel", "error",
                     "-f", "lavfi", "-i", "testsrc=size=64x64:duration=1:rate=1",
                     "-frames:v", "1", "-y", path,
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        await process.WaitForExitAsync();

        Assert.True(File.Exists(path), "ffmpeg did not generate the sample image");

        return path;
    }

    private async Task<DocumentMessage> UploadAsync(string fileName, string contentType, byte[] payload)
    {
        using var call = _client.Upload();

        await call.RequestStream.WriteAsync(new UploadRequest
        {
            Metadata = new UploadMetadata { FileName = fileName, ContentType = contentType },
        });

        foreach (var chunk in payload.Chunk(64 * 1024))
        {
            await call.RequestStream.WriteAsync(new UploadRequest { Chunk = ByteString.CopyFrom(chunk) });
        }

        await call.RequestStream.CompleteAsync();
        return await call.ResponseAsync;
    }

    private async Task<byte[]> DownloadAsync(string id)
    {
        using var call = _client.Download(new DocumentId { Id = id });

        return await ReadAllAsync(call.ResponseStream);
    }

    private static async Task<byte[]> ReadAllAsync(IAsyncStreamReader<Chunk> chunks)
    {
        using var buffer = new MemoryStream();

        await foreach (var chunk in chunks.ReadAllAsync())
        {
            chunk.Data.WriteTo(buffer);
        }

        return buffer.ToArray();
    }
}
