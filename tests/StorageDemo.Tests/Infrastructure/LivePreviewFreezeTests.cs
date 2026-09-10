using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// Replays the live preview sampler against prefixes of a real growing recording. A prefix of a
/// file that is being appended to is exactly what that file looked like at an earlier moment, so
/// this reproduces "the preview froze" without a running stream and without a clock.
///
/// Point the fixture at a captured MPEG-TS ingest recording of at least 24 MB:
///   set LIVE_RECORDING=C:\path\to\recording.ts
/// </summary>
public sealed class LivePreviewFreezeTests
{
    /// <summary>LiveOptions.ThumbnailTailBytes, the sampler's window.</summary>
    private const long TailBytes = 4 * 1024 * 1024;

    private static string? Recording()
    {
        var path = Environment.GetEnvironmentVariable("LIVE_RECORDING");

        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
    }

    private static LibavMediaAnalyzer Analyzer(double videoFrameSeconds = 3) => new(
        Options.Create(new MediaOptions { VideoFrameSeconds = videoFrameSeconds }),
        NullLogger<LibavMediaAnalyzer>.Instance);

    /// <summary>The bytes in [offset, offset + length), analyzed exactly as the sampler does.</summary>
    private static async Task<byte[]?> SampleWindowAsync(
        string path,
        long offset,
        long length,
        double videoFrameSeconds = 3)
    {
        await using var file = File.OpenRead(path);
        file.Seek(offset, SeekOrigin.Begin);

        var buffer = new byte[length];
        await file.ReadExactlyAsync(buffer);

        using var window = new MemoryStream(buffer);
        var analysis = await Analyzer(videoFrameSeconds).AnalyzeAsync(window, "live.ts", "video/mp2t");

        return analysis.Thumbnail;
    }

    /// <summary>What SampleThumbnailAsync would produce if the file were <paramref name="available"/> bytes long.</summary>
    private static Task<byte[]?> SampleAtAsync(string path, long available)
    {
        var length = Math.Min(available, TailBytes);

        return SampleWindowAsync(path, available - length, length);
    }

    private static string Digest(byte[]? bytes)
        => bytes is null ? "none" : Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(bytes))[..12];

    /// <summary>
    /// The symptom. Every sample taken while the recording is still smaller than the sampler's
    /// tail window shows the same picture, however much the recording has grown in between.
    /// </summary>
    [Fact]
    public async Task Preview_advances_while_the_recording_is_smaller_than_the_tail_window()
    {
        var path = Recording();
        Assert.SkipUnless(path is not null, "Set LIVE_RECORDING to a captured ingest recording.");

        long[] sizes = [1 << 20, 2 << 20, 3 << 20, TailBytes];
        var digests = new List<string>();

        foreach (var size in sizes)
        {
            var thumbnail = await SampleAtAsync(path!, size);
            digests.Add(Digest(thumbnail));
        }

        var report = string.Join(", ", sizes.Zip(digests, (s, d) => $"{s >> 20}MB={d}"));

        Assert.True(digests.Distinct().Count() == digests.Count, $"preview froze: {report}");
    }

    /// <summary>
    /// The mechanism. With the window's end fixed at the live edge, halving the window changes
    /// where it starts and nothing else - and the picture changes with it. Growing the window
    /// backwards moves the preview backwards in time, which is the opposite of what a live
    /// preview should do, and shows the frame is taken from the window's start, not its end.
    /// </summary>
    [Fact]
    public async Task Preview_frame_is_fixed_by_the_window_start_not_the_live_edge()
    {
        var path = Recording();
        Assert.SkipUnless(path is not null, "Set LIVE_RECORDING to a captured ingest recording.");

        const long edge = 20L << 20;

        var wide = await SampleWindowAsync(path!, edge - TailBytes, TailBytes);
        var narrow = await SampleWindowAsync(path!, edge - (TailBytes / 4), TailBytes / 4);
        var sameStart = await SampleWindowAsync(path!, edge - TailBytes, TailBytes / 4);

        Assert.NotNull(wide);
        Assert.NotNull(narrow);
        Assert.NotNull(sameStart);

        // Same live edge, different window start: different picture.
        Assert.False(
            wide.SequenceEqual(narrow),
            $"windows ending at the same byte agree: 4MB={Digest(wide)} 1MB={Digest(narrow)}");

        // Same window start, different live edge: same picture. The end is not read at all.
        Assert.True(
            wide.SequenceEqual(sameStart),
            $"windows starting at the same byte differ: 4MB={Digest(wide)} 1MB={Digest(sameStart)}");
    }

    /// <summary>
    /// Why. Seek() targets VideoFrameSeconds as an ABSOLUTE timestamp, so it only ever reaches
    /// anything in a window whose own timestamps start near zero.
    ///
    /// At the head of the recording the seek steers: moving the target moves the picture. In a
    /// window taken from the middle - which is every window once the recording outgrows the tail
    /// - no target reaches a later frame at all. Below the window's start_time the seek clamps to
    /// its first frame; above the window's duration the seekable guard skips the seek and decoding
    /// starts at that same first frame. Both roads end on the oldest frame in the window.
    /// </summary>
    [Fact]
    public async Task An_absolute_seek_steers_only_in_a_window_that_starts_at_the_stream_start()
    {
        var path = Recording();
        Assert.SkipUnless(path is not null, "Set LIVE_RECORDING to a captured ingest recording.");

        double[] targets = [1, 3, 10, 30, 60, 120, 180, 240, 300];

        async Task<Dictionary<double, string>> ScanAsync(long start)
        {
            var digests = new Dictionary<double, string>();

            foreach (var target in targets)
            {
                digests[target] = Digest(await SampleWindowAsync(path!, start, TailBytes, target));
            }

            return digests;
        }

        var atHead = await ScanAsync(0);
        var midStream = await ScanAsync((20L << 20) - TailBytes);

        Assert.True(
            atHead.Values.Distinct().Count() > 1,
            $"the seek never steered even at the head: {Report(atHead)}");

        Assert.True(
            midStream.Values.Distinct().Count() == 1,
            $"the seek steered mid-stream after all: {Report(midStream)}");

        static string Report(Dictionary<double, string> digests)
            => string.Join(", ", digests.Select(pair => $"{pair.Key}s={pair.Value}"));
    }

    /// <summary>
    /// The fix, demonstrated through the one knob that reaches the seek target from outside.
    ///
    /// A preview wants the newest frame in the window, not a poster frame a fixed distance from
    /// its start. Aim near the window's end instead and the same growing recording produces a
    /// different picture on every pass - which is what the frozen test above is asking for.
    /// </summary>
    [Fact]
    public async Task Targeting_the_end_of_the_window_advances_the_preview()
    {
        var path = Recording();
        Assert.SkipUnless(path is not null, "Set LIVE_RECORDING to a captured ingest recording.");

        long[] sizes = [1 << 20, 2 << 20, 3 << 20, TailBytes];
        var digests = new List<string>();

        foreach (var size in sizes)
        {
            // These windows all start at the head of the stream, where an absolute seek still
            // steers, so the option alone is enough to aim at the live edge.
            var span = await SpanSecondsAsync(path!, size);
            digests.Add(Digest(await SampleWindowAsync(path!, 0, size, span - 2)));
        }

        var report = string.Join(", ", sizes.Zip(digests, (s, d) => $"{s >> 20}MB={d}"));

        Assert.True(digests.Distinct().Count() == digests.Count, $"still frozen: {report}");
    }

    /// <summary>How many seconds of media a window holds, as libav reports it.</summary>
    private static async Task<double> SpanSecondsAsync(string path, long length)
    {
        await using var file = File.OpenRead(path);

        var buffer = new byte[length];
        await file.ReadExactlyAsync(buffer);

        using var window = new MemoryStream(buffer);
        var analysis = await Analyzer().AnalyzeAsync(window, "live.ts", "video/mp2t");

        // "12.34 s" below a minute, which is every window this test uses.
        var duration = analysis.Metadata["Duration"];

        return double.Parse(duration.Replace(" s", string.Empty), System.Globalization.CultureInfo.InvariantCulture);
    }
}
