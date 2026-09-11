using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// Reads the stream identifier a caller presented, out of libav's log stream.
///
/// This is the one genuinely brittle thing in the ingest path, and it is brittle because libav
/// leaves no other way. <c>libsrt_listen</c> reads SRTO_STREAMID off the freshly accepted socket
/// and logs it at verbose level without storing it anywhere reachable: the option is set-only, and
/// the SRTSOCKET lives in a private context that is not public API. See
/// <c>.scratch/server-side-ingest/issues/01-srt-listener-streamid.md</c>.
///
/// Two properties make it workable. A custom callback sees every message, because av_vlog hands
/// the line over and the level filtering happens inside the default callback, so nothing else in
/// the process gets noisier. And the line is emitted on every accept including an unnamed one,
/// which is what lets "the capture broke" and "the sender sent no name" stay distinguishable.
///
/// The self-test is not optional decoration. Without it an FFmpeg upgrade that reworded one log
/// line would leave every stream arriving unnamed, in production, with nothing reporting a fault.
/// </summary>
public static unsafe class StreamIdCapture
{
    /// <summary>The literal from libavformat/libsrt.c. Changing it is what the self-test catches.</summary>
    private const string Marker = "accept streamid [";

    /// <summary>
    /// Held in a field so the garbage collector cannot move or free the thunk libav is calling
    /// through. A local would be collected and the next accept would land in freed memory.
    /// </summary>
    private static readonly av_log_set_callback_callback Callback = OnLog;

    private static readonly Lock Gate = new();

    /// <summary>
    /// The protocol open runs on the thread that called it, so the identifier a callback captures
    /// belongs to whoever is accepting on that thread. That is the whole correlation.
    /// </summary>
    [ThreadStatic]
    private static string? _captured;

    [ThreadStatic]
    private static bool _armed;

    /// <summary>
    /// Suppresses forwarding on this thread. The self-test's own caller is torn down as soon as
    /// the accept has happened, so libav reports a broken connection every boot. An expected error
    /// in the log at startup is worse than no error: it teaches whoever reads it to ignore them.
    /// </summary>
    [ThreadStatic]
    private static bool _quiet;

    private static ILogger? _logger;
    private static bool _installed;

    /// <summary>Installs the callback once. Safe to call from anywhere; only the first one acts.</summary>
    public static void Install(ILogger logger)
    {
        lock (Gate)
        {
            _logger = logger;

            if (_installed)
            {
                return;
            }

            ffmpeg.av_log_set_callback(Callback);
            _installed = true;
        }
    }

    /// <summary>Call immediately before an accept, on the thread that will perform it.</summary>
    public static void Arm()
    {
        _captured = null;
        _armed = true;
    }

    /// <summary>
    /// The identifier the last accept on this thread saw. Null means no line was captured at all,
    /// which is a broken capture rather than an unnamed sender; an unnamed sender gives an empty
    /// string, because libav logs the line either way.
    /// </summary>
    public static string? Take()
    {
        var captured = _captured;
        _captured = null;
        _armed = false;

        return captured;
    }

    /// <summary>
    /// Opens a listener on a loopback port, calls it with a known identifier, and checks the name
    /// comes back. Throws when it does not, so an FFmpeg upgrade fails at boot rather than turning
    /// every stream anonymous.
    /// </summary>
    /// <remarks>
    /// The caller sends no media, so the listener's own open never completes and is not expected
    /// to: the identifier is logged during the accept, well before anything is read.
    /// </remarks>
    public static void SelfTest(int port, ILogger logger)
    {
        const string expected = "storagedemo-selftest";

        Install(logger);

        AVIOContext* accepted = null;

        // A caller on its own thread, because the accept below blocks until one arrives. It holds
        // the connection open for a moment so the accept has certainly happened before it goes.
        var calling = Task.Run(() =>
        {
            AVIOContext* caller = null;
            AVDictionary* options = null;

            _quiet = true;

            try
            {
                ffmpeg.av_dict_set(&options, "timeout", "3000000", 0);

                if (ffmpeg.avio_open2(
                        &caller,
                        $"srt://127.0.0.1:{port}?mode=caller&streamid={expected}",
                        ffmpeg.AVIO_FLAG_WRITE,
                        null,
                        &options) >= 0)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(500));
                }
            }
            finally
            {
                ffmpeg.av_dict_free(&options);

                if (caller is not null)
                {
                    ffmpeg.avio_closep(&caller);
                }
            }
        });

        AVDictionary* listenerOptions = null;

        try
        {
            // Short: nothing is going to be read, so this is how long the test takes to fail.
            ffmpeg.av_dict_set(&listenerOptions, "listen_timeout", "5000000", 0);
            ffmpeg.av_dict_set(&listenerOptions, "timeout", "1000000", 0);

            Arm();

            ffmpeg.avio_open2(
                &accepted,
                $"srt://127.0.0.1:{port}?mode=listener",
                ffmpeg.AVIO_FLAG_READ,
                null,
                &listenerOptions);

            var captured = Take();

            if (captured != expected)
            {
                throw new InvalidOperationException(
                    $"The SRT stream identifier could not be read back from libav's log. Expected "
                    + $"'{expected}', got {(captured is null ? "no log line at all" : $"'{captured}'")}. "
                    + "Every stream would arrive unnamed. This almost always means FFmpeg was "
                    + "upgraded and the line in libavformat/libsrt.c was reworded; see "
                    + ".scratch/server-side-ingest/issues/01-srt-listener-streamid.md.");
            }

            logger.LogInformation("SRT stream identifier capture verified against the loaded FFmpeg");
        }
        finally
        {
            ffmpeg.av_dict_free(&listenerOptions);

            if (accepted is not null)
            {
                ffmpeg.avio_closep(&accepted);
            }

            calling.Wait(TimeSpan.FromSeconds(10));
        }
    }

    private static void OnLog(void* context, int level, string format, byte* arguments)
    {
        const int size = 1024;
        var line = stackalloc byte[size];
        var prefix = 1;

        ffmpeg.av_log_format_line2(context, level, format, arguments, line, size, &prefix);

        var text = Marshal.PtrToStringAnsi((IntPtr)line);
        if (text is null)
        {
            return;
        }

        if (_armed && text.IndexOf(Marker, StringComparison.Ordinal) is var start and >= 0)
        {
            var open = start + Marker.Length;
            var close = text.IndexOf(']', open);

            if (close >= 0)
            {
                _captured = text[open..close];
            }
        }

        Forward(level, text);
    }

    /// <summary>
    /// libav's own messages, at the level it would have printed them. Without this, installing a
    /// callback would silently swallow every warning the demuxer produces, which is a poor trade
    /// for reading one line.
    /// </summary>
    private static void Forward(int level, string text)
    {
        if (_quiet || _logger is not { } logger || level > ffmpeg.av_log_get_level())
        {
            return;
        }

        var message = text.TrimEnd('\n', '\r');
        if (message.Length == 0)
        {
            return;
        }

        if (level <= ffmpeg.AV_LOG_ERROR)
        {
            logger.LogError("libav: {Message}", message);
        }
        else if (level <= ffmpeg.AV_LOG_WARNING)
        {
            logger.LogWarning("libav: {Message}", message);
        }
        else
        {
            logger.LogDebug("libav: {Message}", message);
        }
    }
}
