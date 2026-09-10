#!/bin/sh
# A test pattern pushed into the multiplexer over SRT, so the live path can be exercised without
# an encoder or a camera. The service listens and this calls it, which is how SRT contribution
# works in the field: the encoder reaches the receiver, not the other way round.
set -eu

API=${API:-http://api:8080}
TOKEN=${TOKEN:-local-demo-token}
NAME=${NAME:-srt-test-pattern}
INGEST=${INGEST:-srt://0.0.0.0:9000?mode=listener}
TARGET=${TARGET:-srt://api:9000?mode=caller}

apk add --no-cache ffmpeg curl >/dev/null

echo "Waiting for $API"
until curl -sf "$API/health/ready" >/dev/null 2>&1; do sleep 2; done

# Reconnects on its own, so restarting the API does not leave a dead sender behind.
while true; do
    echo "Asking the service to listen on $INGEST"
    if ! curl -sf -X POST "$API/api/live/ingest" \
        -H "X-Storage-Token: $TOKEN" \
        -H 'Content-Type: application/json' \
        -d "{\"name\":\"$NAME\",\"url\":\"$INGEST\"}" >/dev/null
    then
        echo "The service would not start a session; retrying"
        sleep 5
        continue
    fi

    # The listener needs a moment to bind before anything calls it.
    sleep 2

    echo "Pushing to $TARGET"
    # -re paces the sender at wall-clock speed. Without it the whole thing arrives as one burst
    # and the far end sees a connection that hangs up mid-handshake rather than a stream.
    # mpeg2video keeps this cheap: the point is a stream, not picture quality.
    ffmpeg -hide_banner -loglevel warning -re \
        -f lavfi -i "testsrc=size=640x360:rate=25" \
        -f lavfi -i "sine=frequency=440:sample_rate=48000" \
        -c:v mpeg2video -b:v 1500k -g 25 \
        -c:a mp2 -b:a 128k \
        -f mpegts "$TARGET" || true

    echo "Sender stopped; starting over"
    sleep 5
done
