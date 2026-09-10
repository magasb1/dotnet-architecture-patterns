using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace StorageDemo.Infrastructure.Media;

/// <summary>
/// Points the libav bindings at the libraries bundled with this application and loads them once.
///
/// Dynamically loaded rather than statically bound, so the search path is ours to set. Without
/// that the loader would fall back to whatever FFmpeg the machine happens to have, which is the
/// thing this project deliberately never uses.
/// </summary>
public static unsafe partial class FfmpegLibrary
{
    private static readonly Lock Gate = new();

    private static bool _loaded;

    /// <summary>Throws if the bundled libraries cannot be loaded, naming where it looked.</summary>
    public static void EnsureLoaded()
    {
        lock (Gate)
        {
            if (_loaded)
            {
                return;
            }

            DynamicallyLoadedBindings.LibrariesPath = Ffmpeg.Directory;

            // Fail loudly here rather than at the first null function pointer.
            DynamicallyLoadedBindings.ThrowErrorIfFunctionNotFound = true;
            DynamicallyLoadedBindings.Initialize();

            // Required before any network protocol is opened, and harmless otherwise.
            ffmpeg.avformat_network_init();

            _loaded = true;
        }
    }

    /// <summary>
    /// Which transport protocols the loaded libraries actually support, lowercase and sorted.
    /// Worth asking rather than assuming: an LGPL build has udp and rtp but not srt, and the
    /// difference is invisible until a stream fails to open.
    /// </summary>
    public static IReadOnlyList<string> InputProtocols() => Enumerate(output: 0);

    public static IReadOnlyList<string> OutputProtocols() => Enumerate(output: 1);

    public static bool Supports(string url, bool forOutput)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0)
        {
            // No scheme means a plain file path, which needs no protocol.
            return true;
        }

        var scheme = url[..separator].ToLowerInvariant();

        return (forOutput ? OutputProtocols() : InputProtocols()).Contains(scheme);
    }

    private static unsafe List<string> Enumerate(int output)
    {
        EnsureLoaded();

        var names = new List<string>();
        void* state = null;

        while (true)
        {
            var name = ffmpeg.avio_enum_protocols(&state, output);
            if (name is null)
            {
                break;
            }

            names.Add(name);
        }

        names.Sort(StringComparer.Ordinal);

        return names;
    }
}
