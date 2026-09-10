using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using StorageDemo.Infrastructure.Media;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// The multiplexer, exercised for real: a file into MPEG-TS, and then the same code sending over a
/// network transport with a listener reading it back. Nothing is mocked, because what is worth
/// testing here is whether libav actually carries the packets.
/// </summary>
public sealed class LibavRemuxerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "storage-demo-remux-tests",
        Guid.NewGuid().ToString("N"));

    public LibavRemuxerTests() => Directory.CreateDirectory(_directory);

    private static LibavRemuxer CreateRemuxer() => new(NullLogger<LibavRemuxer>.Instance);

    [Fact]
    public void The_loaded_libraries_report_which_transports_they_have()
    {
        var inputs = FfmpegLibrary.InputProtocols();

        // These come with any build; asserting them proves the enumeration works at all.
        Assert.Contains("file", inputs);
        Assert.Contains("udp", inputs);
        Assert.Contains("udp", FfmpegLibrary.OutputProtocols());
    }

    [Fact]
    public void An_unavailable_transport_is_refused_with_the_list_of_available_ones()
    {
        // The bundled LGPL build has no libsrt. Asking for it should say so plainly rather than
        // failing somewhere inside libav with a numeric code.
        if (FfmpegLibrary.OutputProtocols().Contains("srt"))
        {
            return;
        }

        var failure = Assert.Throws<NotSupportedException>(() => CreateRemuxer().Remux(
            SampleFile(),
            "srt://127.0.0.1:9999",
            "mpegts",
            null,
            null,
            CancellationToken.None));

        Assert.Contains("srt://127.0.0.1:9999", failure.Message);
        Assert.Contains("Media:LibraryPath", failure.Message);
    }

    [Fact]
    public void A_file_is_multiplexed_into_a_transport_stream_without_re_encoding()
    {
        var output = Path.Combine(_directory, "out.ts");

        var result = CreateRemuxer().Remux(SampleFile(), output, "mpegts", null, null, CancellationToken.None);

        Assert.True(result.Packets > 0, "no packets were copied");
        Assert.True(result.Bytes > 0, "no payload was copied");
        Assert.True(new FileInfo(output).Length > 0, "the transport stream is empty");

        // Same codec on the far side: a remux copies encoded frames rather than re-encoding them.
        Assert.Equal(CodecOf(SampleFile()), CodecOf(output));
    }

    [Fact]
    public void Progress_is_reported_as_packets_are_carried()
    {
        var reported = new List<long>();

        var result = CreateRemuxer().Remux(
            SampleFile(),
            Path.Combine(_directory, "progress.ts"),
            "mpegts",
            null,
            (packets, _) => reported.Add(packets),
            CancellationToken.None);

        Assert.NotEmpty(reported);
        Assert.Equal(result.Packets, reported[^1]);
    }

    [Fact]
    public async Task A_stream_sent_over_the_network_arrives_and_can_be_recorded()
    {
        // Ingest and egress at once, which is the whole live path: one side multiplexes a file out
        // over a transport, the other demuxes it off the wire and records it.
        var port = FreePort();
        var recorded = Path.Combine(_directory, "received.ts");
        var remuxer = CreateRemuxer();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // The listener has to be up before the sender starts, or the first packets land nowhere.
        var receiving = Task.Run(
            () => remuxer.Remux(
                $"udp://127.0.0.1:{port}",
                recorded,
                "mpegts",
                new Dictionary<string, string> { ["timeout"] = "10000000", ["fifo_size"] = "1000000" },
                null,
                timeout.Token),
            timeout.Token);

        await Task.Delay(TimeSpan.FromMilliseconds(700), timeout.Token);

        var sent = await Task.Run(
            () => remuxer.Remux(
                SampleFile(),
                $"udp://127.0.0.1:{port}?pkt_size=1316",
                "mpegts",
                null,
                null,
                timeout.Token),
            timeout.Token);

        Assert.True(sent.Packets > 0, "nothing was sent");

        var received = await receiving;

        Assert.True(received.Packets > 0, "nothing arrived over the transport");
        Assert.True(new FileInfo(recorded).Length > 0, "the recording is empty");
    }

    [Fact]
    public async Task A_stream_carried_over_SRT_arrives_and_can_be_recorded()
    {
        // SRT is a build choice: the LGPL bundle has no libsrt, scripts/fetch-ffmpeg.sh brings one
        // that does. Skipped rather than failed on a build without it, since the code is identical
        // either way and only the transport differs.
        Assert.SkipUnless(
            FfmpegLibrary.OutputProtocols().Contains("srt"),
            "This FFmpeg has no SRT. Run scripts/fetch-ffmpeg.sh to get a build that does.");

        var port = FreePort();
        var recorded = Path.Combine(_directory, "received-srt.ts");
        var sample = SampleFile();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // An encoder pushes, the service listens, which is how SRT contribution actually works.
        // The sender is paced with -re: SRT is a connection, so a burst that ends immediately is
        // not a stream, and the receiver would see the far end hang up mid-handshake.
        var receiving = Task.Run(
            () => CreateRemuxer().Remux(
                $"srt://0.0.0.0:{port}?mode=listener&listen_timeout=20000000",
                recorded,
                "mpegts",
                null,
                null,
                timeout.Token),
            timeout.Token);

        await Task.Delay(TimeSpan.FromSeconds(1), timeout.Token);

        Run(
            Ffmpeg.ExecutablePath,
            [
                "-hide_banner", "-loglevel", "error",
                "-re", "-i", sample,
                "-c", "copy", "-f", "mpegts",
                $"srt://127.0.0.1:{port}?mode=caller",
            ]);

        var received = await receiving;

        Assert.True(received.Packets > 0, "nothing arrived over SRT");
        Assert.True(new FileInfo(recorded).Length > 0, "the SRT recording is empty");
    }

    /// <summary>A short clip, generated by the bundled ffmpeg so no fixture lives in the repo.</summary>
    private string SampleFile()
    {
        var path = Path.Combine(_directory, "sample.ts");
        if (File.Exists(path))
        {
            return path;
        }

        Run(
            Ffmpeg.ExecutablePath,
            [
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi", "-i", "testsrc=size=320x240:duration=3:rate=15",
                "-c:v", "mpeg2video", "-b:v", "600k",
                "-y", path,
            ]);

        Assert.True(File.Exists(path), "ffmpeg did not generate the sample stream");

        return path;
    }

    private static string CodecOf(string path)
        => Run(
            Ffmpeg.ProbePath,
            [
                "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=codec_name",
                "-of", "csv=p=0",
                path,
            ]).Trim();

    /// <summary>
    /// A free port from a low, fixed range rather than an ephemeral one.
    ///
    /// Binding to port zero hands back something in the dynamic range, and Windows reserves
    /// stretches of that range for Hyper-V and friends. A port can be handed out that way and then
    /// refuse an explicit bind moments later, which shows up as a listener that will not start.
    /// </summary>
    private static int _nextPort = 9100;

    private static int FreePort()
    {
        // Never hand out the same port twice in one run. A socket libav has just finished with can
        // linger a moment, and the next bind on that port fails, which looks exactly like a
        // listener that refuses to start.
        var start = Interlocked.Add(ref _nextPort, 10);

        for (var port = start; port < start + 200; port++)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(
                    System.Net.Sockets.AddressFamily.InterNetwork,
                    System.Net.Sockets.SocketType.Dgram,
                    System.Net.Sockets.ProtocolType.Udp);

                socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port));

                return port;
            }
            catch (System.Net.Sockets.SocketException)
            {
                // In use or reserved; try the next one.
            }
        }

        throw new InvalidOperationException("No free port in the test range.");
    }

    private static string Run(string executable, string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
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

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(executable)} failed: {error}");

        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
