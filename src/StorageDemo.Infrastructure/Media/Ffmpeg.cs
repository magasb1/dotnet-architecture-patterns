using System.Runtime.InteropServices;

namespace StorageDemo.Infrastructure.Media;

/// <summary>
/// The FFmpeg that ships with the application. The host's own install is deliberately never used: probing results and thumbnails must not depend on which build someone happens to
/// have on PATH, and a container should not need FFmpeg installed at all.
///
/// The NuGet package lays the binaries out as <c>ffmpeg/&lt;rid&gt;/</c> beside the application.
/// </summary>
public static class Ffmpeg
{
    /// <summary>
    /// Directory holding the native libraries and CLI tools for this platform. Overridable, and
    /// that override is the supported way to change what FFmpeg can do: pointing it at a build
    /// compiled with libsrt is what turns on SRT, with no application change.
    /// </summary>
    public static string Directory { get; private set; } = Path.Combine(
        AppContext.BaseDirectory,
        "ffmpeg",
        RuntimeIdentifier());

    /// <summary>
    /// Must be called before anything loads the libraries.
    ///
    /// It insists on actually finding libraries, because the obvious mistake is pointing this at a
    /// static build. Those ship ffmpeg.exe and nothing else, so the directory exists, the check
    /// would pass, and the failure would surface much later as a missing function.
    /// </summary>
    public static void UseDirectory(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"No FFmpeg libraries at '{directory}'.");
        }

        // avcodec-62.dll on Windows, libavcodec.so.62 elsewhere. Matched loosely so a different
        // FFmpeg version is accepted or rejected by the bindings rather than by this check.
        var found = System.IO.Directory
            .EnumerateFiles(directory)
            .Select(Path.GetFileName)
            .Any(name => name is not null
                && name.Contains("avcodec", StringComparison.OrdinalIgnoreCase)
                && (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    || name.Contains(".so", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)));

        if (!found)
        {
            throw new FileNotFoundException(
                $"'{directory}' holds no FFmpeg libraries. This has to be a shared build: a static "
                + "build ships only ffmpeg and ffprobe executables, which cannot be loaded in "
                + "process. Look for a build whose name says 'shared'.");
        }

        Directory = directory;
    }

    public static string ExecutablePath { get; } = Executable("ffmpeg");

    public static string ProbePath { get; } = Executable("ffprobe");

    public static bool IsPresent => File.Exists(ExecutablePath) && File.Exists(ProbePath);

    private static string Executable(string name)
        => Path.Combine(Directory, OperatingSystem.IsWindows() ? $"{name}.exe" : name);

    /// <summary>
    /// Matches the folder names in the package. <see cref="RuntimeInformation.RuntimeIdentifier"/>
    /// can be more specific than that (win10-x64, ubuntu.22.04-x64), so it is narrowed here.
    /// </summary>
    private static string RuntimeIdentifier()
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "arm64"
            : "x64";

        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"osx-{architecture}";
        }

        // Alpine and friends need the musl build; glibc binaries simply will not start there.
        return RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase)
            ? "linux-musl-x64"
            : $"linux-{architecture}";
    }
}
