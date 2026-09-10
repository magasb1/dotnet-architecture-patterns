using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using StorageDemo.Api.Controllers;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Tests.Integration;

/// <summary>
/// Live streaming through the real application: the control endpoints, a stream pushed in over a
/// transport, and the recording arriving as an ordinary document with a thumbnail.
/// </summary>
public sealed class LiveStreamTests : IAsyncLifetime
{
    private const string Token = "live-test-token";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "storage-demo-live-tests",
        Guid.NewGuid().ToString("N"));

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Storage:Provider", "FileSystem");
            builder.UseSetting("Storage:FileSystem:RootPath", Path.Combine(_root, "files"));
            builder.UseSetting("Database:Provider", "LiteDb");
            builder.UseSetting("Database:LiteDb:Path", Path.Combine(_root, "db", "app.db"));
            builder.UseSetting("StorageMonitor:Enabled", "false");
            builder.UseSetting("Live:Enabled", "true");
            builder.UseSetting("Live:Token", Token);
            builder.UseSetting("Live:ThumbnailIntervalSeconds", "1");
            builder.UseEnvironment("Production");
        });

        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Storage-Token", Token);

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();

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
    public async Task The_service_reports_the_transports_it_can_actually_carry()
    {
        var status = await _client.GetFromJsonAsync<LiveStatusResponse>("/api/live");

        Assert.NotNull(status);
        Assert.Contains("udp", status.Transports);

        // SRT is a build choice, not a code one. Whichever way this instance was built, saying so
        // is the point: a caller should never have to guess.
        Assert.Equal(
            FfmpegLibrary.OutputProtocols().Contains("srt"),
            status.Transports.Contains("srt"));
    }

    [Fact]
    public async Task A_transport_outside_the_allowed_list_is_refused()
    {
        // file:// would turn "start a stream" into "read anything on the disk".
        var response = await _client.PostAsJsonAsync(
            "/api/live/ingest",
            new StartIngestRequest("nope", "file:///etc/passwd"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_endpoints_are_closed_without_the_token()
    {
        using var anonymous = _factory.CreateClient();

        var response = await anonymous.GetAsync("/api/live");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_stream_pushed_into_the_service_becomes_a_document()
    {
        var port = FreePort();

        var started = await _client.PostAsJsonAsync(
            "/api/live/ingest",
            new StartIngestRequest("match-of-the-day", $"udp://127.0.0.1:{port}"));

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);

        var session = await started.Content.ReadFromJsonAsync<LiveSession>();
        Assert.NotNull(session);
        Assert.Equal(LiveDirection.Ingest, session.Direction);

        // The listener needs to be up before anything is sent, or the first packets land nowhere.
        await Task.Delay(TimeSpan.FromSeconds(1));

        Send(SampleStream(), port);

        var document = await WaitForDocumentAsync("match-of-the-day.ts", TimeSpan.FromSeconds(45));

        Assert.NotNull(document);
        Assert.True(document.Size > 0, "the recording is empty");

        // It went through the ordinary upload path, so it is analysed like anything else.
        Assert.Equal("video/mp2t", document.ContentType);
    }

    [Fact]
    public async Task Stopping_an_unknown_session_is_reported_as_missing()
    {
        var response = await _client.DeleteAsync($"/api/live/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_listening_session_can_be_stopped_before_anything_arrives()
    {
        var started = await _client.PostAsJsonAsync(
            "/api/live/ingest",
            new StartIngestRequest("abandoned", $"udp://127.0.0.1:{FreePort()}"));

        var session = await started.Content.ReadFromJsonAsync<LiveSession>();
        Assert.NotNull(session);

        // Accepted, not NoContent: in a cluster the owner may be another replica, which acts shortly.
        var stopped = await _client.DeleteAsync($"/api/live/{session.Id}");
        Assert.Equal(HttpStatusCode.Accepted, stopped.StatusCode);
    }

    [Fact]
    public async Task A_running_stream_is_listed_by_the_registry_with_an_owner()
    {
        var started = await _client.PostAsJsonAsync(
            "/api/live/ingest",
            new StartIngestRequest("registered", $"udp://127.0.0.1:{FreePort()}"));

        var session = await started.Content.ReadFromJsonAsync<LiveSession>();
        Assert.NotNull(session);

        var status = await _client.GetFromJsonAsync<LiveStatusResponse>("/api/live");
        var listed = status!.Sessions.Single(s => s.Id == session.Id);

        // Without an owner, no other replica could route a stop or a viewer to the right place.
        Assert.False(string.IsNullOrWhiteSpace(listed.Owner));
        Assert.NotNull(listed.Heartbeat);

        await _client.DeleteAsync($"/api/live/{session.Id}");
    }

    [Fact]
    public async Task A_live_stream_can_be_watched_while_it_is_running()
    {
        var port = FreePort();

        var started = await _client.PostAsJsonAsync(
            "/api/live/ingest",
            new StartIngestRequest("watchable", $"udp://127.0.0.1:{port}"));

        var session = await started.Content.ReadFromJsonAsync<LiveSession>();
        Assert.NotNull(session);

        await Task.Delay(TimeSpan.FromSeconds(1));

        // Pushed in the background, so the viewer attaches while packets are still arriving.
        var pushing = Task.Run(() => Send(SampleStream(), port));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var watched = 0L;

        try
        {
            using var response = await _client.GetAsync(
                $"/api/live/{session.Id}/stream",
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("video/mp2t", response.Content.Headers.ContentType?.MediaType);

            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[16 * 1024];

            // Enough to prove bytes are really flowing, then walk away like a viewer would.
            while (watched < 64 * 1024)
            {
                var read = await body.ReadAsync(buffer, timeout.Token);
                if (read == 0)
                {
                    break;
                }

                watched += read;
            }
        }
        catch (OperationCanceledException)
        {
        }

        await pushing;
        await _client.DeleteAsync($"/api/live/{session.Id}");

        Assert.True(watched > 0, "no live bytes reached the viewer");
    }

    [Fact]
    public async Task The_hosts_analyzer_can_read_a_transport_stream()
    {
        // Same analyzer instance and configuration the live sampler uses.
        var analyzer = _factory.Services.GetRequiredService<StorageDemo.Core.Documents.IMediaAnalyzer>();
        var options = _factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<LiveOptions>>().Value;

        await using var content = File.OpenRead(SampleStream());
        var analysis = await analyzer.AnalyzeAsync(content, "live.ts", "video/mp2t");

        Assert.True(
            analysis.Thumbnail is not null,
            $"the host analyzer produced no frame. interval={options.ThumbnailIntervalSeconds} "
            + $"tailBytes={options.ThumbnailTailBytes}");
    }

    [Fact]
    public async Task A_running_stream_grows_a_preview_frame()
    {
        var port = FreePort();

        var started = await _client.PostAsJsonAsync(
            "/api/live/ingest",
            new StartIngestRequest("previewed", $"udp://127.0.0.1:{port}"));

        var session = await started.Content.ReadFromJsonAsync<LiveSession>();
        Assert.NotNull(session);

        await Task.Delay(TimeSpan.FromSeconds(1));
        var pushing = Task.Run(() => Send(SampleStream(), port));

        // Sampling runs on an interval, so this is the one place a wait is the behaviour.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        byte[]? thumbnail = null;

        try
        {
            while (!deadline.IsCancellationRequested && thumbnail is null)
            {
                var response = await _client.GetAsync($"/api/live/{session.Id}/thumbnail", deadline.Token);

                if (response.StatusCode == HttpStatusCode.OK)
                {
                    thumbnail = await response.Content.ReadAsByteArrayAsync(deadline.Token);
                    break;
                }

                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }

        await pushing;

        var final = (await _client.GetFromJsonAsync<LiveStatusResponse>("/api/live"))!
            .Sessions.FirstOrDefault(s => s.Id == session.Id);



        await _client.DeleteAsync($"/api/live/{session.Id}");

        Assert.True(
            thumbnail is not null,
            $"no preview appeared. session state={final?.State} packets={final?.Packets} "
            + $"bytes={final?.Bytes} hasThumbnail={final?.HasThumbnail} error={final?.Error}");
        Assert.True(thumbnail.Length > 0);

        // A real JPEG, so the client can show it without knowing anything about the stream.
        Assert.Equal(0xFF, thumbnail[0]);
        Assert.Equal(0xD8, thumbnail[1]);
    }

    private async Task<DocumentResponse?> WaitForDocumentAsync(string fileName, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);

        while (!deadline.IsCancellationRequested)
        {
            var documents = await _client.GetFromJsonAsync<List<DocumentResponse>>("/api/documents");
            var match = documents?.FirstOrDefault(d => d.FileName == fileName);

            if (match is not null)
            {
                return match;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return null;
    }

    /// <summary>Pushes a file at the listener with the bundled ffmpeg, standing in for an encoder.</summary>
    private static void Send(string path, int port)
        => Run(
            Ffmpeg.ExecutablePath,
            [
                "-hide_banner", "-loglevel", "error",
                "-re", "-i", path,
                "-c", "copy", "-f", "mpegts",
                $"udp://127.0.0.1:{port}?pkt_size=1316",
            ]);

    private string SampleStream()
    {
        var path = Path.Combine(_root, "sample.ts");
        if (File.Exists(path))
        {
            return path;
        }

        Run(
            Ffmpeg.ExecutablePath,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=size=320x240:duration=6:rate=15",
                "-c:v", "mpeg2video", "-b:v", "600k",
                "-y", path,
            ]);

        return path;
    }

    private static int FreePort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    private static void Run(string executable, string[] arguments)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo(executable)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (!OperatingSystem.IsWindows())
        {
            startInfo.Environment["LD_LIBRARY_PATH"] = Ffmpeg.Directory;
        }

        using var process = System.Diagnostics.Process.Start(startInfo)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(executable)} failed: {error}");
    }
}
