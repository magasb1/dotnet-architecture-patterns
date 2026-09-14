# Phase 9's health signal, under the overload it was built for

Measured 2026-09-12 against `docker/Dockerfile` built from the current tree (`4418d9f`, Phase 9
in), on the same rig as `baseline.md`: Docker Desktop through WSL2, 14 vCPU and 15.5 GiB, service
and load generator in two containers on one bridge. Absolutes are indicative; what this page rests
on is zero at one load and non-zero at another, on the same rig, in the same hour.

Until now every assertion about `packetsLost`, `packetsDropped` and `live.udp.receive.errors` was
an assertion of zero. This is the first time any of them has been seen with a value.

## The short version

| Load | Listed / live | Delivered per stream | Per-stream `packetsLost` / `packetsDropped` | Kernel `Udp: InErrors` (30 s) | Meter `live.udp.receive.errors` |
| --- | --- | --- | --- | --- | --- |
| 50 x 0.57 Mbit/s | 50 / 50 | **0.602 Mbit/s, clean** | **zero on every stream on all 53 beats read** | **0**, and 0 for the whole run | 0 in all 104 samples |
| 100 x 0.57 | 100 / 100 | 0.507 Mbit/s, clean by the baseline's measure | zero on 26 of 47 beats through the settle; non-zero on all 100 streams on the others, then mostly quiet | 0 in the sampled window; **227,135 over the run**, all in the first 90 s | **227,135**, the same number |
| 250 x 0.57 | 233-243 / 200-224 | **0.05 Mbit/s, collapsed** | **non-zero on 200-209 streams on every one of 58 beats**; every stream that held a socket for the beat | **+386,741** in 30 s (12,900/s); 2.4 million over the run | 2.34 million over its 210 s window, 11,000-33,000 per 2 s sample |

Delivered is the baseline's own measure: `"bytes"` deltas from `GET /api/live` over ten seconds,
divided by the stream count. The baseline had 0.51 at 100 and 0.10 at 250.

**The signal distinguishes the collapsed pod from the healthy one, at both levels, and the two
levels agree.** At 50 everything is zero. At 250 every stream with a socket reports loss on every
beat, the kernel counter climbs at 13,000 a second, and the service delivers a tenth of the media.
The plan predicted that only the pod-level counter would see this failure and that the per-socket
figures would be blind to it. **That prediction was wrong, in the useful direction**: see below.

## Method

`baseline.md` and `preview-cost.md` as written, with their traps honoured: `FFMPEG` set in the
senders container, the scripts run through `tr -d '\r'`, the pattern encoded at 900 s so no run
reached its loop point (every run ended 210 s after the ramp), no `--cpus` or `--memory` limits.
Container DNS was healthy, so the image was a plain `docker build -f docker/Dockerfile`, tagged
`storagedemo-api:p9`. The libsrt inside it is the apt package, `libsrt1.5-openssl 1.5.3-1build2`.

Per run: the service container **recreated** from the new image, the senders container
**restarted**, `GET /api/live` asserted to list **zero streams** before the ramp (it did, all three
runs and the two discarded ones), then the ramp, **120 s of settle**, and the samples. The run
script and the kernel sampler are `hs/run.ps1` and `hs/kernel.sh` in the session scratchpad;
`live.ps1` is the previous agent's delivered-bitrate probe, unchanged.

Three readings, taken as follows:

- **Per stream.** `GET /api/live` read once per heartbeat (2 s) from the ramp through the whole
  settle, and six consecutive beats after it. Each beat: how many streams had `packetsLost > 0`,
  how many `packetsDropped > 0`, the sum and the maximum across streams. Since the figure is an
  interval cleared per beat, a beat's reading is that beat's loss and nothing older.
- **Pod level, two ways.** The meter through `dotnet-counters collect --counters StorageDemo.Live`
  at a 2 s refresh, run from a sidecar sharing the service container's pid namespace and its
  `/tmp` (the runtime image has no SDK and runs as a non-root user; the diagnostic socket lives in
  `/tmp`, so a shared volume there is all the sidecar needs). And `/proc/net/snmp` read directly
  inside the service container over a 30 s window. The sidecar is attached **before the ramp** and
  left running for the whole run, for a reason recorded under Honesty.
- **Delivered.** `live.ps1 -Window 10`, the same ten-second `bytes` delta as the baseline.

`dotnet-counters` renders an `ObservableCounter` as a rate, "Count / 2 sec", so the cross-check
against the kernel is the sum of the meter's samples against the kernel's absolute `InErrors`.

## 50 streams: the quiet reference

On air in 3.3 s. Forty-seven beats through the settle and six after it: **every stream at
`packetsLost = 0`, `packetsDropped = 0` on all fifty-three.** `Udp: InErrors` was 0 before the
ramp, 0 across the 30 s window and 0 at the end of the run. The meter's 104 samples were all zero.
Delivered 0.602 Mbit/s, all fifty rising. Wire 30.5 Mbit/s at 4,482 packets/s, `SRT:RcvQ` at 49 %
of a core.

That is the half of the signal that has to be quiet or none of it means anything, and it is quiet.

## 100 streams: clean by the baseline's measure, and not quiet

On air in 5.2 s. Delivered in the ten-second window: **0.507 Mbit/s, all 100 rising**, which is
the baseline's 0.51 and by its definition a clean load. `SRT:RcvQ` at 60 % of a core, right at the
knee the baseline found.

The per-stream figure told a fuller story. For the first ninety seconds after the ramp, loss came
in bursts: twenty-one of the forty-seven beats through the settle had **`packetsLost > 0` on all
100 streams** (sums of 2,000 to 10,800 per beat, maxima of 30 to 130 per stream), and the other
twenty-six had every stream at zero. From +82 s to the end of the settle it was quiet. Of the six
beats after the settle, three were all-zero and three showed small figures (maximum 27 on one
beat, 7 to 10 on the others, on 31 to 100 streams). The 30 s kernel window that followed was
**+0**, and delivered was measured after that.

The kernel agrees, sample by sample. The meter's 2 s samples were non-zero in exactly the seconds
the beats reported loss: 8,000 to 14,000 per sample from 10:50:20 to 10:51:36, a lone 525 at
10:50:44 where the beats showed 178 lost on 67 streams, zero from 10:51:38 to 10:52:18, and 1,548,
1,144 and 160 at 10:52:20 to 10:52:26 where the six post-settle beats showed their blips. Every
lossy beat has a non-zero meter sample beside it and every quiet stretch a run of zeros.

**Meter and kernel are the same number.** The meter's samples summed to 227,135 over the run; the
kernel sampler read `InErrors` as 227,135 at both ends of its window, read from `/proc/net/snmp`
with no code of Phase 9's in between.

So 100 on today's host is marginal rather than clean: the host was at 85 % with the senders alone
at 4 to 5 cores (`multi-port.md`'s figure), and Docker Desktop's own Kubernetes control plane
turned out to be running in the same VM (below). The signal reported that honestly, in time, on
every stream, while the ten-second delivered figure taken after it had passed said clean. A
health figure that is an interval per beat sees a ninety-second episode that a bitrate sampled
afterward cannot.

## 250 streams: collapsed

**250 never fully connected.** The most listed at once was 243, the most live at once 224, and
through the settle it hovered at 212 to 242 listed with 180 to 215 live, streams dropping and
reconnecting as their senders died. The senders container was at **990 % of a core**, which is
`multi-port.md`'s "past 125 senders this rig measures itself" and `baseline.md`'s 28 s tail to 250
made worse by the control plane sharing the VM. So this is the honest ceiling: **the load that
actually connected was about 235 listed, 217 live**, and that is the load the readings describe.
It collapsed the service all the same.

Delivered: **0.05 Mbit/s per stream** (baseline: 0.10 at 250), 217 of 235 rising. Wire 130 Mbit/s
at 14,800 packets/s, `SRT:RcvQ` at 45 % of a core, which is the baseline's "past the knee the
thread's share falls".

Per stream: **`packetsLost > 0` and `packetsDropped > 0` on 200 to 209 streams on every beat**,
from +6.7 s after the ramp to the last beat read; 58 beats, two of them all-zero and both in the
first seconds before the sockets existed. Sums of 7,000 to 54,000 lost per beat across the pod,
maxima of 80 to 429 per stream per beat. Five streams picked at random after the settle:
`load/0001 lost=124 dropped=124`, `load/0002 124/124`, `load/0003 120/120`, `load/0004 117/117`,
`load/0005 127/127`. At 0.57 Mbit/s and 1,316-byte payloads a stream is sent about 54 packets a
second, about 110 per beat, so a stream reporting 124 lost in a beat lost about everything it was
sent, which is what 0.05 of 0.57 delivered says from the other side.

**Which streams: all of them that had a socket, not a subset.** The 25 to 35 listed streams
reporting zero on a given beat are the 15 to 20 that were not live at that moment (interrupted, no
socket, and `Health()` returns nothing rather than a stale figure) plus a handful of fresh
reconnects whose socket had existed for less than a beat. The kernel drops on the one shared UDP
socket and does not choose, so every SRT socket behind it sees the same gaps. Nothing in the
readings singles out an unlucky few.

**Lost and dropped are equal**, beat after beat, at 250 and nearly so at 100 (at 100, dropped ran a
few percent under lost). Lost is a gap libsrt detected; dropped is a gap it gave up on when the
latency window expired. Equal means nothing lost was recovered in time: the retransmit arrives on
the same overflowing socket and is dropped again. At 100 the small difference is the few
retransmits that got through.

Pod level: `Udp: InErrors` **1,669,965 to 2,056,706 in 30 s, +386,741**, or 12,900 a second,
and `RcvbufErrors` moved by **exactly the same +386,741**: every one of these errors is the
receive buffer being full, which is the mechanism Phase 9 named. The meter's 104 samples (210 s
window) summed to 2,335,926 with a maximum of 33,781 per 2 s and 102 of 104 non-zero; the kernel
read 2,408,848 a few seconds after the meter's window closed, the difference being those seconds
at 13,000 a second. Not the exact equality of the 100 run, because the sidecar's fixed duration
ended before the final read, but the same counter at the same rate.

## Layout check on Linux with libsrt 1.5.3

`docker/Dockerfile.test` built from the same tree (`storagedemo-tests:p9`) and run with
`--filter FullyQualifiedName~The_statistics_libsrt_writes_land_in_the_fields_this_service_reads`:

```
Passed StorageDemo.Tests.Infrastructure.SrtListenerTests.The_statistics_libsrt_writes_land_in_the_fields_this_service_reads [565 ms]
Test Run Successful. Total tests: 1  Passed: 1
```

That test asserts `stats.byteMSS == 1500` and `stats.msRcvTsbPdDelay == SRTO_RCVLATENCY` as read
back through the socket option (the listener is configured with 60 ms), on a socket that had
received packets, against `/usr/lib/x86_64-linux-gnu/libsrt.so.1.5.3` from
`libsrt1.5-openssl 1.5.3-1build2`. Those fields sit at 360 and 392 bytes into the struct. With
the Windows run against vcpkg's 1.5.6, the layout is now checked against both libsrt builds the
service is ever loaded with, and the figures above are read from the version the container runs.

## Honesty and hygiene

- **Two runs were discarded, and one of them is instructive.** The first attempt at 100 read zero
  on every stream for the two beats after the settle, then loss on 61 and then 100 streams on the
  next two, then zero again, with the kernel going 0 to 9,390 in exactly those seconds. Those
  seconds were when the run script had just started the `dotnet-counters` sidecar container and a
  PowerShell job. That is the signal working, on a hiccup the measurement caused. The run was
  redesigned so the sidecar attaches before the ramp and nothing is started mid-window; the runs
  above are that design. The other discard was a 50 run during which the Docker daemon stalled
  for four minutes (below) and `/api/live` stopped answering within 20 s.
- **Docker Desktop's Kubernetes control plane is in the VM.** `top` inside the `docker-desktop`
  distro shows `kube-apiserver`, `etcd`, `kubelet`, `kube-scheduler` and `k8s.io` shims running
  throughout, on top of the rig; the VM's load average reached 30 after the 250 run and the daemon
  answered `docker ps` only after minutes. The `k3s-server` container from `cross-pod.md` was at
  1.2 cores idle and was stopped for the measurements and restarted afterward. The built-in
  Kubernetes was not touched, since that is a user setting. It is the likeliest reason 100 is
  marginal here where the baseline found it clean.
- **The baseline's "RcvbufErrors is non-zero even in the clean rows" was a column mislabel.** The
  earlier probe printed fields 4 and 5 of the `Udp:` line as InErrors and RcvbufErrors; field 5
  is `OutDatagrams`. RcvbufErrors is field 6, and it is zero at 50 and at 100 in the sampled window
  and equal to InErrors at 250. The InErrors figures in the baseline are unaffected; the remark
  about clean rows should be read as "the pod sends ACKs".
- **The senders are the ceiling, as documented.** 250 listed 243 at most. If a real collapse at a
  connected 250 is wanted, the senders need another machine; here the honest statement is that
  the service collapsed at about 217 live and the signal saw it.
- **Interval semantics need more than one beat.** At 100 the post-settle beats went 0, 0, 27, 10,
  0, 7 (maximum per stream). A rule that flags a stream on a single non-zero beat would flap on a
  marginal pod; a rule over a window of beats would not. At 250 there was no such ambiguity: every
  beat, every socketed stream.
- Nothing in `src/` or `tests/` was changed by this work. Another agent was editing the same tree
  during the session (KLV fields appended to `LiveStream` after `PacketsDropped`, and files not on
  the health path); `git status` showed a clean `src/` when the build started at 10:37 UTC, and
  none of those edits touch how loss, drops or the kernel counter are read. The rig scripts were
  used unchanged apart from `tr -d '\r'`.

## Verdict: can Phase 4 trust this signal?

Yes, at both levels, and it should use both. The pod-level counter `live.udp.receive.errors` is
the kernel's `Udp: InErrors` read through a meter, shown here to equal it exactly, zero on a quiet
pod and 13,000 a second on a collapsed one; it is the one number an autoscaler wants and it is
what "collapse" physically is on this service. The per-stream `packetsLost` and `packetsDropped`
are not blind to that failure as the plan feared: because the drops happen on the one shared UDP
socket, every SRT socket behind it reports them, so `GET /api/live` at 250 names every degraded
stream in one call, with a figure that is the whole beat's worth of media. What the per-stream
figure adds is time resolution and attribution the counter lacks (the 100 run's ninety-second
episode, visible beat by beat while the delivered bitrate sampled afterward read clean), and what
it costs is that a single beat is noisy near the knee, so Phase 4 should decide over a window of
beats rather than one. Delivered bitrate remains the ground truth and both signals agreed with it
at every load measured.
