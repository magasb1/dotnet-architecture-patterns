# Several ingest ports in one pod

Measured 2026-09-11/12 against `srt-listener-libsrt` plus a new `Live__IngestPortCount`, on the rig
`baseline.md` built.

**The question.** libsrt runs one receive worker thread per multiplexer and one multiplexer per
bound UDP port per process; accepted sockets share their listener's. `baseline.md` measured that
single thread as the pod's ceiling - about sixty camera-rate streams - so a thousand streams needs
seventeen pods. If four bound ports give four receive threads, a pod holds roughly four times as
much and the cluster needs four or five.

**The failure mode to watch for was libsrt sharing one multiplexer anyway. It does not.** The
threads multiply and they share the load evenly. But the capacity gain is **not** four times: about
1.6 times measured, about 2.2 times after correcting for a load generator that ran out of machine
before the service did.

## 1. The receive threads multiply

With `Live__IngestPortCount=4` the process binds 9000 to 9003 and `top -H` inside the container
shows five `SRT:RcvQ:w` threads - four ingest, one consumption - where one port shows two:

```
$ docker exec srt-api sh -c 'for t in /proc/1/task/*; do cat $t/comm; done | sort | uniq -c'
      1 SRT:RcvQ:w1        # consumption, 9010
      1 SRT:RcvQ:w2        # ingest, 9000
      1 SRT:RcvQ:w3        # ingest, 9001
      1 SRT:RcvQ:w4        # ingest, 9002
      1 SRT:RcvQ:w5        # ingest, 9003
```

`SRTO_REUSEADDR` is set on every listening socket, which is what `SrtListener` has always done, and
it does not collapse the four: `CUDTUnited::updateMux` only reuses a multiplexer **for the same
port**, so four ports are four `CMultiplexer`s, four `CChannel`s, four `CRcvQueue`s and four worker
threads. That is `research/libsrt-api.md` section 6 holding up against the real library.

**And they are evenly loaded.** Senders spread round-robin across the four ports put the four
threads within 8 % of each other at every load measured - 32.2/33.9/33.4/32.3 at 150 streams,
36.1/36.2/36.1/37.9 at 100, 40.9/43.0/43.6/44.4 at 125. Nothing is pinning work to one multiplexer.

## 2. The two knees

Source is a pre-encoded 4.15 Mbit/s test pattern - the "real camera" row of `baseline.md` - replayed
with `-c copy`. **Clean** means the service is demultiplexing the source rate; the knee is the last
load still delivering about 95 %, which is the criterion `baseline.md` used. CPU is the `/proc`
integral over a fixed 30 s window as a percentage of one core, with `docker stats` beside it.

| Ports | Streams | Delivered/stream | | Busiest `SRT:RcvQ` | CPU, `/proc` | `docker stats` | Memory |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | 50 | 4.012 | clean | 30.5 % | 129 % | 130 % | 2.20 GiB |
| 1 | 65 | 4.027 | clean | 48.9 % | 220 % | 237 % | 2.43 GiB |
| **1** | **80** | **3.936** | **clean, 95 %** | **62.5 %** | **298 %** | 570 % | **3.18 GiB** |
| 1 | 100 | 2.145 | collapsed, 52 % | 61.6 % | 283 % | 221 % | 2.74 GiB |
| 4 | 100 | 4.800 | clean | 37.9 % | 465 % | 395 % | 4.48 GiB |
| **4** | **125** | **3.946** | **clean, 95 %** | **44.4 %** | **620 %** | 349 % | **6.39 GiB** |
| 4 | 150 | - | rig failed | 34.1 % | 339 % | 308 % | 4.52 GiB |
| 4 | 200 | 0.942 | rig failed | 34.1 % | 301 % | 331 % | 4.01 GiB |
| 4 | 260 | 0.000 | rig failed | 6.0 % | 195 % | 149 % | 2.92 GiB |

**The one-port control reproduces `baseline.md`'s shape.** Its knee is 80 rather than 60 - this
machine was quieter tonight than when the baseline ran - but the character is identical: the
receive thread tops out around 62 %, 70 % is never reached, and past the knee the thread's share
*falls* while delivered bitrate halves. At 100 streams on one port the service reported 100 of 100
live with bytes rising while delivering 52 % of the media, which is `baseline.md`'s defect 1
reproducing exactly.

### The ratio

| | One port | Four ports | Ratio |
| --- | --- | --- | --- |
| Knee, by delivered bitrate | **80** | **125** | **1.56** |
| Busiest receive thread at that knee | 62.5 % | 44.4 % | |
| Knee if the four threads were driven to 62.5 % too | 80 | ~176 | ~2.2 |

**1.56 is what was measured. About 2.2 is the honest estimate**, and the difference is the rig, not
the design: at the four-port knee the receive threads still have a third of their headroom left,
and what stopped the run was the machine. See section 4.

The cleanest single comparison in the whole exercise needs no extrapolation. **At exactly 100
streams, same senders, same container, same pattern: one ingest port delivers 2.145 Mbit/s of each
4.15 Mbit/s source and four ports deliver 4.800.** One configuration is broken and the other is
not, and the only difference is the port count.

## 3. What it costs

At the four-port knee the container wants **about 6.2 cores and 6.4 GiB** for 125 camera-rate
streams, against about 3.0 cores and 3.2 GiB for the 80 streams one port carries. Per stream that
is 5.0 % of a core and 52 MB against 3.7 % and 40 MB - so four ports costs roughly **a third more
CPU and a third more memory per stream** than one does.

Some of that is real and some is the contended host (section 4); the measurement cannot separate
them. Either way the sizing consequence is concrete: **a four-port pod is not a one-port pod with
more streams in it, it is a bigger pod.** A manifest at two cores and 3 GiB fits a one-port pod at
about sixty camera-rate streams. The same pod with four ports wants four to six cores and six to
seven gigabytes, and if it does not get them the CPU limit binds before any receive thread does.

Thread count scales the way `baseline.md` predicted and is unaffected by this change: still about
2.2 OS threads per stream, plus one extra `SRT:RcvQ` and one extra `SRT:SndQ` per bound port.

## 4. What the rig could not do, stated plainly

**The load generator ran out of machine before the service did.** Everything runs inside one WSL2
virtual machine with 14 vCPU, and `ffmpeg` replaying a pre-encoded file over SRT costs far more per
stream than receiving it does:

| Streams | Senders container | Service container | Host total |
| --- | --- | --- | --- |
| 80, one port | 487 % | 570 % | ~10.6 cores |
| 100, four ports | 516 % | 395 % | ~9.1 cores |
| 125, four ports | 571 % | 349 % | ~9.2 cores |
| 150, four ports | 822 to 917 % | 308 to 402 % | ~12 cores |
| 200, four ports | 1062 % | 331 % | ~14 cores |
| 260, four ports | 1260 % | 149 % | ~14 cores |

Past 125 concurrent senders the rig stops being a measurement. At 150 the four receive threads
still read 32 to 34 % - well inside their headroom - while senders died and reconnected fast enough
that only a handful of streams were listed when the sample was taken. At 200 the service held 106
of 200 and delivered a fifth of the source. At 260 only **9 of 260 senders ever connected in eight
minutes**, the receive threads sat at 0 to 6 %, and the senders container was using 12.6 of the
machine's 14 cores. Those three rows measure the load generator and nothing else, and they are in
the table only so nobody re-runs them expecting a knee.

This is why the four-port knee is quoted as "about 125, and the threads still had headroom". A
clean four-port knee needs the senders on a different machine, and at camera rate it needs roughly
twenty cores of them.

**Two further reasons to read the absolutes loosely.** `docker stats` and the `/proc` integral
disagree by up to a factor of two on the same run (570 % against 298 % at the one-port knee), which
is what a contended host does to a sampled figure; the `/proc` integral is the quoted number.
And per-stream receive-thread cost is itself contention-sensitive - the same one-port listener cost
0.61 % of a core per stream with the host at 2.8 cores of load and 0.78 % with it at 10.6.

**The trustworthy parts are the thread count, the even split, and the same-load A/B at 100
streams.** Those three do not depend on absolute CPU at all.

## 5. Does a reconnect to another port resume the same stream?

**Yes, and it is now a test rather than an assumption.**
`LiveStreamTests.A_reconnect_on_another_ingest_port_resumes_the_same_stream` pushes a name at port
N, kills the sender, waits for the stream to go interrupted, and pushes the same name at port N+1.
The stream comes back live with a new `ConnectionId` and **the same `StartedAt`**, and `/api/live`
lists it once.

Nothing downstream ever sees a port. `AdmitPublisher` and the `_local` dictionary are keyed by name,
`ClaimAsync` writes `live-claim:{name}`, and `SrtListener` passes on an `AcceptedSocket` carrying a
name and a stream id and nothing else. The whole suite now runs against a replica bound to two
ingest ports, so every other live test exercises the range incidentally.

One thing did need fixing. `LiveListeners` recorded a bool per direction, so with a range bound, one
port failing to bind left readiness saying "listening" while a quarter of the encoders the Service
sent that pod failed. It now counts bound listeners against an expected count.

## 6. How to repeat it

```bash
docker build -f docker/Dockerfile -t storagedemo-api .
docker network create loadnet

docker run -d --name srt-api --network loadnet -e Live__Enabled=true -e Live__Token=t \
  -e Live__IngestPortCount=4 \
  -e Storage__FileSystem__RootPath=/data/files -e Database__LiteDb__Path=/data/database/app.db \
  -p 8085:8080 storagedemo-api

docker run -d --name senders --network loadnet --entrypoint sleep storagedemo-api infinity
docker exec senders bash -c '/app/ffmpeg/linux-x64/ffmpeg -y -f lavfi \
  -i "testsrc2=size=640x360:rate=25" -t 900 -c:v libx264 -preset ultrafast -tune zerolatency \
  -g 25 -keyint_min 25 -sc_threshold 0 -b:v 4000k -f mpegts /tmp/pattern.ts'
```

Senders are spread round-robin over the range - `scripts/load-senders.sh` with a `port` that is
`base + (i-1) % ports` and a distinct name per sender, since one name per port would be the name
lock refusing three quarters of them.

Per run, following `preview-cost.md`: the service container is **recreated** and the senders
container **restarted**, `GET /api/live` is asserted to report **zero streams** before the ramp (it
did before all ten runs), the ramp is watched to N, then **120 s of settle**, then 30 s of
per-thread CPU and a 10 s delivered-bitrate window. No `--cpus` or `--memory` limits, same as the
baseline. Delivered bitrate is the `"bytes"` deltas from `/api/live`; per-thread CPU is
`(utime+stime)` deltas from `/proc/1/task/*/stat`.

Four traps beyond the three `preview-cost.md` records, all of which cost time here:

- **The container DNS on this daemon is still broken** - anything but `--dns 8.8.8.8` fails to
  resolve, and `docker build` cannot restore NuGet packages. `--network=host` does not help. The
  image was assembled the way `preview-cost.md` describes: both stages run as containers with an
  explicit `--dns`, committed, and joined by a `COPY --from`.
- **`curl` is not in the runtime image**, so the API has to be polled from the host through a
  published port rather than from the senders container.
- **A sampler must verify it got a whole JSON body.** A truncated `/api/live` read silently
  produced a *negative* bitrate, which is how this was noticed; the sampler now retries and checks
  the closing brace.
- **Above about 125 concurrent senders this rig measures itself.** See section 4.

## 7. Recommendation

**Keep the option. Default it to one, which is what it does. Set it to two to four on a real node,
and re-measure there before sizing a fleet.**

- The mechanism is real: four ports give four receive threads, they share load evenly, and it is
  the only lever found so far that raises the *number of streams a pod can ingest*. Phase 8 does
  not - `preview-cost.md` showed removing the preview left the receive thread's share unchanged.
- **Do not plan on four times.** Plan on about two. Seventeen pods for a thousand camera-rate
  streams becomes eight or nine, not four or five. If the contention-corrected 2.2 holds up on real
  hardware it is still eight.
- **Size the pod for it.** Four ports at their knee wanted 6.2 cores and 6.4 GiB here. `baseline.md`
  defect 2 already says `k8s/live/deployment.yaml` is an order of magnitude short for one port;
  four ports moves that target again. A CPU limit sized for a one-port pod will bind before the
  receive threads do and the extra ports will buy nothing.
- **The deployment has to publish the whole range and the senders have to be spread across it.** No
  single port is any bigger than it was. An encoder fleet that all dials port 9000 gets the old
  ceiling with three idle threads beside it. That is a real operational cost: something has to
  assign ports, and a Service must expose a contiguous range.
- **The failure shape gets worse, and this is the honest argument against it.** A pod holding twice
  as many streams takes twice as many into a reconnect when it dies. Phase 1b holds a dead pod's
  names for about three beats, so those encoders are refused before they are admitted elsewhere,
  and the storm is larger and lasts longer. Fewer, larger pods also means each one is a larger
  share of the cluster, so a rolling update moves more streams per step. Two to four ports is a
  reasonable place to stop; sixteen would be trading a known failure mode for a worse one to chase
  a gain the measurement does not support.
- **What would make this measurement trustworthy**: a real node, the load generator on a different
  machine, and `net.core.rmem_max` raised as `baseline.md` suggests. The ratio between the two
  configurations is the part to trust here, and even that should be read as "about two", not as
  1.56 versus 2.2.
