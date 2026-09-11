#!/bin/sh
# A test pattern pushed into the service over SRT, so the live path can be exercised without an
# encoder or a camera.
#
# Nothing is requested first. The sender connects to the ingest port and names itself in the
# stream identifier, which is the whole shape of the design: the service does the rest.
set -eu

NAME=${NAME:-srt-test-pattern}
TARGET=${TARGET:-srt://api:9000}

apk add --no-cache ffmpeg >/dev/null

# The SRT Access Control convention. The '#' has to be percent-escaped, because it starts a
# fragment in a URL and a strict parser would truncate the identifier there. A box with a plain
# Stream ID field takes the bare name instead: just "srt-test-pattern".
STREAMID="%23!::r=${NAME},m=publish"

# Reconnects on its own, so restarting the service, or losing the pod holding this stream, does
# not leave a dead sender behind. Real encoders retry by nature, which is what the design counts
# on: the accept backlog is one, so a burst of senders is absorbed by retrying rather than by the
# service.
while true; do
    echo "Pushing '$NAME' to $TARGET"

    # -re paces the sender at wall-clock speed. Without it the whole thing arrives as one burst
    # and the far end sees a connection that hangs up mid-handshake rather than a stream.
    # mpeg2video keeps this cheap: the point is a stream, not picture quality.
    #
    # A one second keyframe interval, and all three flags are needed to get one: -g asks for it,
    # -keyint_min stops the encoder shortening it, and -sc_threshold 0 stops a scene change
    # inserting a keyframe of its own, which the test pattern's hard cuts would otherwise do. It
    # decides two things: the buffer is cut into thirty fine segments rather than three coarse
    # ones, and a viewer can only start at a keyframe, so this is also the floor on how long
    # joining takes however low the SRT latency goes.
    ffmpeg -hide_banner -loglevel warning -re \
        -f lavfi -i "testsrc=size=640x360:rate=25" \
        -f lavfi -i "sine=frequency=440:sample_rate=48000" \
        -c:v mpeg2video -b:v 1500k -g 25 -keyint_min 25 -sc_threshold 0 \
        -c:a mp2 -b:a 128k \
        -f mpegts "${TARGET}?mode=caller&streamid=${STREAMID}" || true

    echo "Sender stopped; reconnecting"
    sleep 2
done
