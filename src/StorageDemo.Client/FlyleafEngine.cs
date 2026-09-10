using System.IO;
using FlyleafLib;

namespace StorageDemo.Client;

/// <summary>
/// Starts FlyleafLib once, pointed at the libav libraries that ship with this application.
/// The machine's own FFmpeg install is never used, so playback behaves the same everywhere.
/// </summary>
public static class FlyleafEngine
{
    private static readonly Lock Gate = new();

    private static bool _started;

    /// <summary>Where the runtime package puts the native libraries for this platform.</summary>
    public static string FFmpegDirectory { get; } =
        Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");

    /// <summary>Throws if the engine cannot start, so the caller can fall back to a still image.</summary>
    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            Engine.Start(new EngineConfig
            {
                FFmpegPath = FFmpegDirectory,
                LogLevel = LogLevel.Quiet,

                // Lets the player raise property changes for CurTime and friends on the UI thread.
                UIRefresh = true,
                UIRefreshInterval = 200,
            });

            _started = true;
        }
    }
}
