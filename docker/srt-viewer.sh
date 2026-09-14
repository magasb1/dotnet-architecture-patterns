#!/bin/sh
# Pulls a live stream back off the consumption port and reports what it receives, so the cost of a
# replica disappearing can be watched rather than guessed at.
#
# A viewer connects the same way an encoder does: one address, the stream named in the stream
# identifier. Add a position to start further back, which is the only rollback there is:
#
#   FROM=20 ./srt-viewer.sh
set -eu

NAME=${NAME:-srt-test-pattern}
TARGET=${TARGET:-srt://api:9010}
FROM=${FROM:-}

apk add --no-cache ffmpeg >/dev/null

# A bare name unless a position is asked for. The convention's form starts with '#', which has to
# be written %23 in a URL, and FFmpeg versions disagree about when they decode that: one sends the
# escape through literally, one restores the '#' and truncates the query there, and only the newest
# gets it right. A bare name has no '#' and works on all of them. See README, "Watching a stream".
if [ -n "$FROM" ]; then
    STREAMID="%23!::r=${NAME},user_from=${FROM},m=request"
else
    STREAMID="${NAME}"
fi

# Retries, because a viewer that gives up on the first refusal cannot tell "not on air yet" from
# "gone". The service holds a viewer's connection open across a stream moving between replicas, so
# a reconnect here means the pod holding this viewer went away, not the pod holding the stream.
while true; do
    echo "Watching '$NAME' from $TARGET"

    # Decodes and discards, printing a progress line a second. A frozen frame count is a stalled
    # stream; a gap in the timestamps is what a replica disappearing costs somebody watching.
    ffmpeg -hide_banner -loglevel warning -stats \
        -i "${TARGET}?mode=caller&streamid=${STREAMID}" \
        -f null - || true

    echo "Viewer disconnected; reconnecting"
    sleep 2
done
