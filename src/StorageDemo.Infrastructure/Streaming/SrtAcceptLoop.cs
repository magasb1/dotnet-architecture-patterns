using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;
using StorageDemo.Infrastructure.Media;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// A connection that has been accepted and named, with the transport still open. Whoever handles
/// it either takes the transport with <see cref="Release"/> or disposes it, which drops the caller.
/// </summary>
public sealed unsafe class AcceptedConnection(IntPtr transport, string streamId, string name) : IDisposable
{
    private IntPtr _transport = transport;

    /// <summary>The raw identifier, kept for logs and for telling one attempt from the next.</summary>
    public string StreamId { get; } = streamId;

    public string Name { get; } = name;

    /// <summary>Hands the open transport over. The caller owns closing it from then on.</summary>
    public AVIOContext* Release()
    {
        var transport = _transport;
        _transport = IntPtr.Zero;

        return (AVIOContext*)transport;
    }

    public void Dispose()
    {
        if (_transport == IntPtr.Zero)
        {
            return;
        }

        var transport = (AVIOContext*)_transport;
        _transport = IntPtr.Zero;

        ffmpeg.avio_closep(&transport);
    }
}

/// <summary>
/// The carousel. One listener URL, re-opened after every accept, which is the only shape that
/// gives many concurrent named streams on one port with the bundled libav.
///
/// A libav SRT listener accepts exactly one caller and then closes the listening socket; there is
/// no loop inside it. Re-opening works because libsrt multiplexes its sockets over one shared UDP
/// port per process and libav sets the reuse-address option, so the rebind succeeds immediately
/// while earlier connections keep running.
///
/// The transport is opened here rather than through <c>avformat_open_input</c> on purpose. That
/// call performs the accept and then reads the container header in the same breath, so a caller
/// that connects and sends nothing would hold the whole carousel until its read timeout. Opening
/// the transport alone returns the moment the handshake completes, and the demultiplexer is
/// somebody else's thread.
///
/// Two limits are inherent and are stated in the design: the backlog is one, giving roughly two or
/// three accepts a second, and the name arrives after accept so nothing can be refused by name,
/// only accepted and dropped. Both ports run one of these, so both pay them - viewers connect at
/// the same couple a second that encoders do.
/// </summary>
public sealed unsafe class SrtAcceptLoop(LiveListeners listeners, ILogger<SrtAcceptLoop> logger)
{
    /// <summary>
    /// Runs until cancelled, handing every named connection to <paramref name="onAccepted"/>.
    ///
    /// The handler must not block: it is on the accept thread, and every millisecond spent there
    /// is a millisecond the port is not listening.
    /// </summary>
    /// <param name="intent">
    /// Which way bytes will move once a caller is accepted. Ingest reads from the caller;
    /// consumption writes to it, and the same carousel serves both.
    /// </param>
    public void Run(
        string url,
        TimeSpan listenTimeout,
        StreamIntent intent,
        Action<AcceptedConnection> onAccepted,
        CancellationToken cancellationToken)
    {
        FfmpegLibrary.EnsureLoaded();
        StreamIdCapture.Install(logger);

        logger.LogInformation("Listening for SRT senders on {Url}", url);

        while (!cancellationToken.IsCancellationRequested)
        {
            AVIOContext* transport = null;
            AVDictionary* options = null;
            int opened;
            string? streamId;

            var startedAt = DateTimeOffset.UtcNow;

            try
            {
                // Bounded so cancellation is noticed. Nothing is lost by re-opening: the listening
                // socket is torn down and rebound in well under a millisecond.
                ffmpeg.av_dict_set(
                    &options,
                    "listen_timeout",
                    ((long)listenTimeout.TotalMicroseconds).ToString(),
                    0);

                StreamIdCapture.Arm();

                opened = ffmpeg.avio_open2(
                    &transport,
                    url,
                    intent == StreamIntent.Publish ? ffmpeg.AVIO_FLAG_READ : ffmpeg.AVIO_FLAG_WRITE,
                    null,
                    &options);
                streamId = StreamIdCapture.Take();
            }
            finally
            {
                ffmpeg.av_dict_free(&options);
            }

            if (opened >= 0)
            {
                listeners.Bound(intent);
            }
            else
            {
                // A failed open that waited out the window is the normal quiet case: the port was
                // bound and nobody called. One that comes back immediately is a port that will not
                // bind, and that is what readiness has to notice rather than a caller-less minute.
                //
                // ponytail: told apart by how long the attempt took, because libav reports both as
                // a negative return and the errno for a timeout differs by platform. Compare
                // against the error code if a platform ever makes that reliable.
                if (DateTimeOffset.UtcNow - startedAt >= listenTimeout / 2)
                {
                    listeners.Bound(intent);
                }
                else
                {
                    logger.LogWarning("Could not open the listener on {Url}", url);

                    // Nothing else would slow this down, and a wedged port must not become a hot
                    // loop logging thousands of lines a second.
                    Thread.Sleep(TimeSpan.FromMilliseconds(250));
                }

                if (streamId is null)
                {
                    continue;
                }

                logger.LogWarning(
                    "A caller presenting '{StreamId}' was accepted and then lost before it could be used",
                    streamId);

                continue;
            }

            using var accepted = Admit(transport, streamId, intent);

            if (accepted is null)
            {
                continue;
            }

            try
            {
                onAccepted(accepted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Handing over the accepted stream '{Name}' failed", accepted.Name);
            }
        }

        listeners.Stopped(intent);

        logger.LogInformation("Stopped listening on {Url}", url);
    }

    /// <summary>
    /// Turns an accepted transport into a named connection, or closes it and says why.
    ///
    /// The three outcomes are deliberately distinct. No log line at all means the capture itself
    /// has broken and every stream will arrive unnamed, which is an operator's problem rather than
    /// a sender's. An empty identifier means the sender simply did not set one. Anything else is
    /// the sender's name and is validated rather than cleaned up.
    /// </summary>
    private AcceptedConnection? Admit(AVIOContext* transport, string? streamId, StreamIntent intent)
    {
        if (streamId is null)
        {
            logger.LogError(
                "A sender was accepted but no stream identifier could be read from libav's log, so "
                + "it cannot be named. The capture is broken; every stream will arrive unnamed. "
                + "Check whether FFmpeg was upgraded.");

            Close(transport);

            return null;
        }

        if (!StreamName.TryParse(streamId, out var name, out var rejection, intent))
        {
            // Dropped after the fact, which is the only refusal available: the name arrives with
            // the accept, never before it.
            logger.LogWarning("Dropped a sender: {Rejection}", rejection);

            Close(transport);

            return null;
        }

        if (StreamName.CarriesSessionKey(streamId))
        {
            // Never the value. It is the slot a push token will occupy.
            logger.LogDebug("The identifier for '{Name}' carries a session key, which is ignored today", name);
        }

        logger.LogInformation(
            "Accepted '{Name}' on the {Port} port",
            name,
            intent == StreamIntent.Publish ? "ingest" : "consumption");

        return new AcceptedConnection((IntPtr)transport, streamId, name);
    }

    private static void Close(AVIOContext* transport)
    {
        if (transport is not null)
        {
            ffmpeg.avio_closep(&transport);
        }
    }
}
