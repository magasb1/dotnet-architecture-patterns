using System.IO;
using FlyleafLib;
using FlyleafLib.MediaPlayer;

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

    /// <summary>
    /// Points the player at a live stream or at a stored file, which want opposite things.
    ///
    /// A stored file is opened once and scrubbed, so libav should take its time and find every
    /// track. A camera is opened again every time the stream moves between replicas, and the wait
    /// before the first picture is the whole of what somebody watching experiences as downtime.
    /// libav's own default is to read five seconds of a transport stream before deciding what is
    /// in it, and that default is most of a viewer's reconnect.
    ///
    /// MPEG-TS repeats its tables every hundred milliseconds or so, so a second is generous for a
    /// camera that presents everything at once. A source that starts its audio late would need
    /// more: the symptom is a stream that plays without sound.
    /// </summary>
    /// <param name="streamId">
    /// The SRT stream identifier naming the stream to pull, for a live address.
    ///
    /// It is set here rather than put in the URL, and that is not a stylistic choice. The
    /// identifier begins with <c>#</c>, which starts a fragment in a URL, so it has to be written
    /// <c>%23</c> there - and FFmpeg versions disagree about when they decode that. 8.1 decodes
    /// per option after splitting the query and works. 7.1, which is what this client loads,
    /// decodes first, so the <c>#</c> reappears and truncates the query: the identifier arrives
    /// empty and the server drops the connection with nothing to say. Older builds do not decode
    /// at all and send the escape through literally. As a demuxer option no URL parser touches it,
    /// and all three behave the same.
    /// </param>
    public static void TuneFor(Player player, bool live, string? streamId = null)
    {
        var demuxer = player.Config.Demuxer;

        if (live)
        {
            demuxer.FormatOpt["analyzeduration"] = LiveProbeMicroseconds.ToString();
            demuxer.FormatOpt["probesize"] = LiveProbeBytes.ToString();

            // Hand packets on rather than holding them: this is a camera, and there is nothing to
            // be gained by being a second behind it.
            demuxer.FormatOpt["fflags"] = "nobuffer";

            if (streamId is { Length: > 0 })
            {
                demuxer.FormatOpt["streamid"] = streamId;
            }
            else
            {
                demuxer.FormatOpt.Remove("streamid");
            }

            player.Config.Decoder.LowDelay = true;

            return;
        }

        // A stored file gets libav's own judgement back. Left set, the live limits would make a
        // long recording open having missed tracks that appear later in it, and a stale identifier
        // would be carried into a request that has no use for one.
        demuxer.FormatOpt.Remove("analyzeduration");
        demuxer.FormatOpt.Remove("probesize");
        demuxer.FormatOpt.Remove("fflags");
        demuxer.FormatOpt.Remove("streamid");

        player.Config.Decoder.LowDelay = false;
    }

    /// <summary>One second. libav's default for a transport stream is five.</summary>
    private const long LiveProbeMicroseconds = 1_000_000;

    /// <summary>One megabyte, the other half of the same limit, which binds on a high bitrate feed.</summary>
    private const long LiveProbeBytes = 1024 * 1024;
}
