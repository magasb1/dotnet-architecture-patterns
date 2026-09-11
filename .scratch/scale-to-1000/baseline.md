# Phase 0 baseline, measured

Measured 2026-09-11 against `docker/Dockerfile` built from `srt-listener-libsrt`. Phase 1 is in.

**Every absolute figure here came through a WSL2 virtual machine on Docker Desktop for Windows,**
14 vCPU and 15.5 GiB, with the service and the load generator in two containers on the same Docker
bridge. Treat absolutes as indicative and ratios between two rows as sound. A ratio is what most
of the conclusions below rest on.

## How to repeat it

```bash
docker build -f docker/Dockerfile -t storagedemo-api .
docker network create loadnet
docker run -d --name srt-api --network loadnet -e Live__Enabled=true -e Live__Token=t \
  -e Storage__FileSystem__RootPath=/data/files -e Database__LiteDb__Path=/data/database/app.db \
  -p 8085:8080 storagedemo-api

# Senders in a second container, using the image's own FFmpeg (the one with libsrt).
docker run -d --name senders --network loadnet -v "$PWD/scripts:/app/scripts:ro" \
  --entrypoint sleep storagedemo-api infinity
docker exec senders /app/ffmpeg/linux-x64/ffmpeg -f lavfi -i "testsrc2=size=640x360:rate=25" \
  -t 120 -c:v libx264 -preset ultrafast -tune zerolatency -g 25 -keyint_min 25 -sc_threshold 0 \
  -b:v 500k -f mpegts /tmp/pattern.ts
docker exec -d -e PATTERN=/tmp/pattern.ts senders bash /app/scripts/load-senders.sh 150 srt-api 9000
docker exec -e Live__Token=t senders bash /app/scripts/count-live.sh http://srt-api:8080 t
```

Both rig scripts ran; `count-live.sh` was not changed. `load-senders.sh` gained two things and is
otherwise the same command line (see **Rig changes**).

Per-thread CPU is `(utime+stime)` deltas from `/proc/1/task/*/stat` in the API container, as a
percentage of **one** core; `docker exec -it srt-api top -H` shows the same thread. Wire rate and
kernel drops are `/proc/net/dev` and `/proc/net/snmp` deltas inside the container. **Delivered**
bitrate is what the service itself reports: the `"bytes"` deltas in `GET /api/live` over ten
seconds, divided by the stream count. That last column is the one that matters; see the first
defect.

## Time to N on air, and accepts per second

Test pattern, 0.57 Mbit/s, one container.

| N | Sender | All on air | Accepts/s | Plan expected |
| --- | --- | --- | --- | --- |
| 50 | encode per sender, as the script was written | 7.2 s | 6.9 | 20 to 25 s, 2 to 3/s |
| 50 | pre-encoded replay | **2.5 s** | **20** | |
| 100 | pre-encoded replay | **1.7 s** | **59** | |
| 200 | pre-encoded replay | 6.6 s to 193, 38.4 s to 200 | 29 over the first 193 | |
| 250 | pre-encoded replay | 8.2 s to 222, 28.4 s to 250 | 27 over the first 222 | |

**The senders are the limit at every step, not the listener.** The 50-sender figure halves and
then thirds purely by making the sender cheaper, which it would not do if the accept path were
binding. Past roughly 200 the tail is `ffmpeg` process starts contending for the same 14 cores:
the load container sits at 6 to 11 cores while the service uses 2 to 3. Read the accept rates as
a floor, not a ceiling. The plan's "about 20 to 25 s for 50" is disproved by an order of
magnitude; Phase 1's done-when ("under five seconds") is met with the rig as written and met four
times over with a rig that is not its own bottleneck.

## Capacity: where one container stops delivering

Streams were held for 35 to 100 s after the ramp, then sampled. **Clean** means the service is
demultiplexing the source bitrate; **collapsed** means it is not, while still reporting every
stream live.

| Streams x source | Delivered/stream | | `SRT:RcvQ:w` | API CPU | API memory | Wire |
| --- | --- | --- | --- | --- | --- | --- |
| 100 x 0.57 Mbit/s | 0.51 | clean | 51 % | 302 % | 1.13 GiB | 62 Mbit/s |
| **150 x 0.57** | **0.60** | **clean** | **60 %** | 336 % | 1.38 GiB | 96 Mbit/s |
| 200 x 0.57 | 0.25 | collapsed | 56 % | 232 % | 1.48 GiB | 192 Mbit/s |
| 250 x 0.57 | 0.10 | collapsed | 54 % | 275 % | 1.58 GiB | 236 Mbit/s |
| 25 x 4.17 Mbit/s | 4.03 | clean | 15 % | 72 % | 1.29 GiB | 109 Mbit/s |
| 50 x 4.17 | 4.03 | clean | 44 % | 166 % | 1.93 GiB | 218 Mbit/s |
| **60 x 4.17** | **3.93** | **clean, 94 %** | **56 %** | 259 % | 2.86 GiB | 265 Mbit/s |
| 75 x 4.17 | 2.66 | collapsed | 55 % | 289 % | 3.03 GiB | 538 Mbit/s |
| 100 x 4.17 | 2.89 | collapsed | 63 % | 442 % | 3.58 GiB | 674 Mbit/s |
| 150 x 4.17 | 0.96 | collapsed | 51 % | 257 % | 3.11 GiB | 1242 Mbit/s |
| 10 x 15.5 Mbit/s | 15.04 | clean | 21 % | 84 % | 1.11 GiB | 162 Mbit/s |
| 20 x 15.5 | 15.06 | clean | 32 % | 81 % | 2.29 GiB | 323 Mbit/s |

Wire rate above the source rate is the SRT retransmit storm that follows the collapse: at 150 x
4.17 the container took 1242 Mbit/s off the wire to deliver 144.

**The knee is at roughly 60 % of one core on `SRT:RcvQ:w`, and 70 % is never reached.** Highest
ever observed, at any load, is 64.8 %. Past the knee the thread's share *falls*: the work it
cannot do turns into kernel UDP drops and retransmits, not into more CPU. So Phase 4's
"set `MaxStreams` where that thread sits at about 70 percent" has no solution as written on this
hardware; 60 % is the number, and delivered bitrate is the honest signal.

**The knee is not a stream count and not a throughput.** 20 streams carrying 300 Mbit/s is clean
at 32 % of the thread; 75 streams carrying 313 Mbit/s collapses. 150 streams at 13 kpkt/s is
clean; 60 streams at 26 kpkt/s is clean; 200 streams at 22 kpkt/s collapses. Both socket count and
packet rate buy from the same budget, roughly `streams/175 + packets-per-second/45000 < 1`. A
one-line version for sizing:

| Per-stream bitrate | Streams per container at the knee |
| --- | --- |
| 0.5 Mbit/s (this test pattern) | about 150 |
| 4 Mbit/s (a real camera) | about 60 |
| 15 Mbit/s | above 20, not measured to its knee |

**Scale the test-pattern numbers by about a quarter for a 4 Mbit/s camera**, and expect memory to
move by the full ratio (below), not by a quarter.

## Memory per stream

| Per-stream bitrate | Measured per stream | 30 s x bitrate | Plan's prediction |
| --- | --- | --- | --- |
| 0.57 Mbit/s | 9.4 MB | 2.1 MB | 96 MiB |
| 4.17 Mbit/s | 39 MB | 15.6 MB | 96 MiB |
| 15.5 Mbit/s | 115 MB | 58 MB | 96 MiB |

Measured is `docker stats` at a clean load, minus about 50 MiB of fixed process, divided by the
stream count. The rule that fits all three is **about 2 to 2.5 times `BufferWindowSeconds` times
the wire bitrate** - call it 70 seconds of stream at wire rate - which is the rolling buffer plus
the .NET heap that has not been given back yet.

The plan's prediction, `MaxStreams` times `LiveOptions.BufferByteCeiling` (96 MiB), over-predicts
by ten times at 0.5 Mbit/s and by two and a half at 4 Mbit/s. It only becomes the right number
above roughly 25 Mbit/s per stream, where the ceiling actually binds before the time window does.
**Size memory from bitrate, not from the ceiling.**

## Not measured

Join latency and glass-to-glass (rows three and four of the plan's table) need the
`k8s/live/failover-test.yaml` rig and a cluster. Nothing here touches them.

## What to expect on real hardware

- **Absolute throughput should improve, probably a lot.** Everything above crosses a WSL2 virtual
  NIC and a Docker bridge, both ends of the traffic sharing 14 vCPU. The receive thread's cost per
  packet here is several times what a bare-metal kernel would charge.
- **The shape should not change.** One receive thread per bound UDP port per process is a
  structural fact, so the ceiling stays a single core and the collapse stays sudden and silent.
  The knee moves right; it does not stop being a knee.
- **Check `net.core.rmem_max` and `rmem_default` on the node.** The container's defaults are
  4 MiB and 208 KiB, and `Udp: RcvbufErrors` is non-zero even in the clean rows - SRT retransmits
  them away, at a cost. A pod sysctl raising the default is worth trying before concluding
  anything about a node's real ceiling.
- **The memory rule should hold**, being arithmetic on a buffer, and the CPU shares should fall.

## Defects found, not fixed

**1. Overload is silent, and the autoscaling signal will not see it.** At 250 x 0.57 Mbit/s,
`GET /api/live` reported `names=250 live=250 notlive=0`, every stream `"state":0` with `packets`
and `bytes` rising, while the service was demultiplexing **0.10 Mbit/s of each 0.57 Mbit/s
source** - a fifth of the media - with 319,000 kernel UDP input errors in a fifteen-second window
and 236 Mbit/s coming off the wire to deliver 26. Nothing in the registry, the API, or
`CeilingBinding` distinguishes that pod from a healthy one. Phase 4 plans to scale on
`live.streams.owned`, which would read 250 contented streams at the exact moment every one of them
is broken. A stream is "live" iff bytes are arriving; nothing asks whether *enough* are. Any
capacity signal built on stream count alone will let a pod take load it cannot carry.
*Evidence: `docker exec senders bash /rig/bitrate.sh 10` against `/rig/health.sh 15`, both in the
scratchpad, at each row of the table above.*

**2. `k8s/live/deployment.yaml` is short on both resources by about an order of magnitude.**
It requests `cpu: 200m` / `memory: 512Mi` and limits memory to `1Gi`, with a comment calling the
memory "a calculation rather than a guess". Measured at the knee: 1.38 GiB for 150 low-bitrate
streams, 2.86 GiB for 60 camera-rate ones, and 2.5 to 3.4 cores. **That 1Gi limit OOM-kills the
pod at roughly 100 test-pattern streams or 20 camera streams** - well before anything measured
here becomes the constraint, and far below the 250 the plan's last row assumes. The `200m` CPU
request is about a fifteenth of what a loaded replica uses, so on a contended node it is
throttled long before its receive thread is.

**3. Every stream decodes and JPEG-encodes a preview whether or not anyone is looking.**
`LiveStreamEntry` opens a `FrameDecoder` and subscribes `Harvester.OnFrame` at `DecodeRate.Keyframes`
unconditionally in its constructor (`LiveStreamEntry.cs:21-31`, `Harvester.cs:33-52`), so with the
one-second GOP this plan prescribes that is one H.264 keyframe decode per stream per second plus a
JPEG encode every two. **This, not the receive thread, is what the container's 250 to 440 % CPU
is.** It is a deliberate feature - the tile that "reads as live" - but at a thousand streams it is
the dominant cost in the whole design and there is no way to switch it off per stream or to fall
back to on-demand. Worth a `Live__PreviewIntervalSeconds: 0` before Phase 4 sizes anything.

**4. About 2.2 OS threads per stream.** 94 threads at 25 streams, 250 at 100, 549 at 250, 803 at
about 380: one `LongRunning` demultiplexer thread per feed plus libsrt's per-socket `SRT:TsbPd`.
A pod holding 250 streams runs 550 threads; the plan's thousand-in-one-cluster is fine, a
thousand-in-one-pod would not be. It bounds `MaxStreams` independently of everything above.

## Rig changes

`scripts/count-live.sh`: unchanged, ran as written, including its "unreachable" path.

`scripts/load-senders.sh`: two additions, same command line otherwise.

- **`FFMPEG` may be overridden by the environment.** The script resolved `ffmpeg` from
  `$(dirname $0)/..`, which is wrong in a container where the scripts are mounted somewhere that
  is not the repository root, and fell through to a bare `ffmpeg` that the runtime image does not
  have.
- **`PATTERN` replays a pre-encoded copy of the same pattern with `-c copy`.** Encoding
  `testsrc2` per sender costs about a third of a core each, so the script as written measures the
  load machine's processor rather than the listener at anything past fifty senders - which is
  exactly what the 7.2 s versus 2.5 s row above shows. The bytes on the wire are identical; the
  comment in the script carries the one-line command that makes the file. Make it longer than the
  measurement: `-re` stops pacing at the loop point, which is its own source of bad numbers.
