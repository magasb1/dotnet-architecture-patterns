using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// The live preview samples the tail of a recording that is still being written. Two samples taken
/// as the stream advances have to show different moments, or the tile shows a stream that has been
/// running for an hour as it looked in its first second.
/// </summary>
public sealed class LivePreviewSamplingTests
{
    private static readonly string Fixtures =
        Path.Combine(Path.GetTempPath(), "livetest");

    private static LibavMediaAnalyzer CreateAnalyzer() => new(
        Options.Create(new MediaOptions()),
        NullLogger<LibavMediaAnalyzer>.Instance);

    /// <summary>The last few megabytes, which is what the sampler hands the analyzer.</summary>
    private static async Task<byte[]?> SampleAsync(string path, long tailBytes)
    {
        await using var file = File.OpenRead(path);

        var length = Math.Min(file.Length, tailBytes);
        file.Seek(-length, SeekOrigin.End);

        using var tail = new MemoryStream();
        await file.CopyToAsync(tail);
        tail.Position = 0;

        var analysis = await CreateAnalyzer().AnalyzeAsync(tail, "live.ts", "video/mp2t");

        return analysis.Thumbnail;
    }

    [Fact]
    public async Task Two_samples_of_a_growing_recording_show_different_moments()
    {
        var earlier = Path.Combine(Fixtures, "a.ts");
        var later = Path.Combine(Fixtures, "b.ts");

        Assert.SkipUnless(File.Exists(earlier) && File.Exists(later), "No captured recording pair.");

        var first = await SampleAsync(earlier, 4 * 1024 * 1024);
        var second = await SampleAsync(later, 4 * 1024 * 1024);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.False(first.SequenceEqual(second), "the preview did not advance with the stream");
    }
}
