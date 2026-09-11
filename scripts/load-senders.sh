#!/usr/bin/env bash
# Starts N senders at once against the ingest port, so the accept rate is a measured number rather
# than a guess. Every phase of the scale-out plan is judged against what this produces.
#
# Usage: scripts/load-senders.sh [N] [HOST] [PORT]   defaults 50 localhost 9000; Ctrl-C stops them all.
set -euo pipefail

N="${1:-50}"
HOST="${2:-localhost}"
PORT="${3:-9000}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

# The fetched build is the one with libsrt in it. The machine's own ffmpeg is the last resort and
# usually has no SRT at all.
FFMPEG="$ROOT/ffmpeg/linux-x64/ffmpeg"
[ -f "$FFMPEG" ] || FFMPEG="$ROOT/ffmpeg/win-x64/ffmpeg.exe"
[ -f "$FFMPEG" ] || FFMPEG=ffmpeg

# Without this the retry loops outlive the Ctrl-C and keep reconnecting.
trap 'kill 0' INT TERM

for i in $(seq -f '%04g' 1 "$N"); do
  # ffmpeg does not retry a refused or dropped connection, so the loop is the retry. That is
  # also what a real encoder does, and it is what a full pod in Phase 4 relies on.
  ( until "$FFMPEG" -hide_banner -loglevel error -re -f lavfi -i "testsrc2=size=640x360:rate=25" \
      -c:v libx264 -preset ultrafast -tune zerolatency -g 25 -keyint_min 25 -sc_threshold 0 \
      -b:v 500k -f mpegts "srt://$HOST:$PORT?mode=caller&streamid=load/$i" </dev/null; do
      sleep 1
    done ) &
done
wait
