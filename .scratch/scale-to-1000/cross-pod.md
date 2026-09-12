# Cross-pod behaviour, on real pods

Measured 2026-09-12 on the k3s rig in `docs/replica-failover.md`, two replicas at the sizing
`k8s/live/deployment.yaml` was committed with yesterday: **2 cores and 3Gi requested per replica,
unchanged**. The node is a 14-vCPU, 15.5 GiB WSL2 VM, so 4 cores and 6Gi of requests schedule with
room to spare and nothing had to be lowered.

Everything Phases 1b and 3 rest on had only ever run as two `WebApplicationFactory` hosts sharing a
dictionary in one process. This is the first time any of it has run on Kubernetes, through a
Service, with a pod actually dying.

**Two defects were found and fixed, and a third was found and deliberately not fixed.** The
headline one: a viewer on a healthy replica was dropped the instant the owning replica shut down
*cleanly*, while a force-killed owner left the viewer's connection intact. A graceful shutdown was
worse for a viewer than a crash, which is exactly backwards, and graceful is the normal-operations
path - rolling updates, node drains, scale-down.

## What was run

```bash
docker run -d --name k3s-server --privileged --tmpfs /run --tmpfs /var/run \
  -p 6443:6443 rancher/k3s:v1.32.1-k3s1 server --disable traefik --disable metrics-server
docker build -f docker/Dockerfile -t storage-demo:latest .
docker save storage-demo:latest -o storage-demo.tar
docker cp storage-demo.tar k3s-server:/tmp/ && docker exec k3s-server ctr images import /tmp/storage-demo.tar
docker cp k8s k3s-server:/manifests && docker exec k3s-server kubectl apply -f /manifests/k8s/ \
  && docker exec k3s-server kubectl apply -f /manifests/k8s/live/deployment.yaml
```

Senders and viewers run as pods from `storage-demo:latest` itself, because that image already
carries an FFmpeg with SRT (BtbN n8.1, `--enable-libsrt`) and needs no pull and no `apk add`. The
ffmpeg command lines are the ones in `k8s/live/failover-test.yaml`, unchanged.

**Pushing at a pod IP, not at the Service.** A Service spreads connections, so which replica a
publisher lands on is a coin toss. Every name-lock test below dials `srt://<podIP>:9000` directly,
which makes "A holds it, B refuses it" a fact rather than a lucky run. Only the failover producer
goes through `storage-demo-ingest`, because it has to be able to land somewhere else.

Three things about the setup worth knowing before repeating it:

- **The doc's two traps are real and the doc is right about both.** `imagePullPolicy` must be
  `Never` (the rig sets it with a `sed` on a copy of the manifests), and Redis has to stay on.
- **`kubectl apply -f k8s/` fails on its first pass against a fresh cluster.** kubectl applies in
  filename order, so `configmap.yaml` and `deployment.yaml` are submitted before `namespace.yaml`
  exists and three objects are refused with `namespaces "storage-demo" not found`. Running it twice
  works. Worth a line in `docs/replica-failover.md`, or an `apply -f k8s/namespace.yaml` first.
- **Container DNS was healthy this time.** `preview-cost.md` records it broken; `docker build`
  restored NuGet and pulled the FFmpeg tarball with no workaround needed. The daemon was restarted
  between the two sessions.

### What was changed for the local run, and where

**Nothing under `k8s/` was touched.** Both rig-only changes were made to a *copy* of the manifests
in a scratch directory and applied from there:

| Change | Why | Commit it? |
| --- | --- | --- |
| `imagePullPolicy: Never` added under each `image: storage-demo:latest` | The image is imported with `ctr`; Kubernetes would otherwise try to pull a placeholder tag | **No.** Rig only - the tag is a placeholder for a real registry |
| `k8s/configmap.yaml` replaced with FileSystem storage + LiteDb, Redis kept | The committed one selects S3 and PostgreSQL, which a failover test does not need. This is the "local storage with the Redis registry still on" the doc prescribes | **No.** Rig only |
| `k8s/postgres.yaml` not applied | Nothing uses it with LiteDb | **No** |

The service source *was* changed - see "The defects" - and those changes are in `src/` and
`tests/`, uncommitted on `srt-listener-libsrt`.

## The six checks

### 0. It comes up

**Pass.** Two replicas Ready in about 17 s from apply, both logging `Listening for SRT callers on
0.0.0.0:9000, the ingest port` and `...:9010, the consumption port`, `/health/ready` returning
`Healthy` on both. Node allocated 4650m of 14 cores and 6534Mi of 15.5Gi with both replicas plus
the rig, so **the committed 2-core/3Gi sizing was used as-is and nothing was lowered.**

The first `/health/ready` on each pod takes ~5.2 s and returns 503 - the startupProbe exists for
exactly that and it behaved.

### 1. The name lock across pods

**Pass, both halves.**

`demo` pushed at pod A (`10.42.0.10`): live, `owner=storage-demo-...-hn5gw`, and both replicas agree
about the owner when asked independently.

`demo` then pushed at pod B (`10.42.0.9`), 20 seconds later:

```
06:20:09.610  sender started
06:20:10.169  @325748675: processConnectResponse: rejecting per reception of a rejection HS
              response: ERROR:PREDEFINED:409
06:20:10.169  REJECT reported from HS processing: Application-defined rejection reason
              Connection to srt://10.42.0.9:9000?...&streamid=demo failed: Input/output error
```

**559 ms from launch to refusal, at the handshake**, with `SRT_REJX_CONFLICT` (1409) arriving intact
at the caller. B's own log shows the other end of it:
`newConnection: connection rejected due to: INTERNAL REJECTION - ERROR:PREDEFINED:409`.

The second half - that the refused publisher did not disturb the incumbent - holds:

| | packets | connectionId | owner |
| --- | --- | --- | --- |
| before | 492 | `5c787210` | hn5gw |
| +3 s | 792 | `5c787210` | hn5gw |
| +6 s | 893 | `5c787210` | hn5gw |
| +12 s | 1092 | `5c787210` | hn5gw |

Same connection, counter rising through the refusal. Nothing was interrupted.

One observability note, not a defect: the fast-path refusal logs nothing at application level, by
design - it runs on libsrt's receiver thread and must not block. What reaches the container log is
libsrt's own warning, which does carry the 409. An operator grepping for "name is held" finds
nothing; grepping for `PREDEFINED:409` finds it.

### 2. A free name is admitted anywhere

**Failed on the build as committed. Passes after the fix.**

Publisher killed on pod A, stream went `Interrupted` after the 5 s feed timeout, same name pushed at
pod B:

| | as committed | after the fix |
| --- | --- | --- |
| admitted on B | yes | yes |
| owner moved | yes | yes |
| new `connectionId` | yes | yes |
| entries in the registry | 1 | 1 |
| **same `startedAt`** | **no - `06:19:49.518` became `06:21:29.358`** | **yes - `06:45:08.781` on both sides** |

The start time was not carried across the move. Detail in "The defects" below.

### 3. A viewer on the wrong pod

**Pass.** Media arrives. The non-owning replica logs
`Relaying 'failover-demo' from its owner storage-demo-...` and the player decodes frames.

Time from launching a player to its first decoded frame, five samples each, the same stream:

| Pulled from | Samples (ms) | Median |
| --- | --- | --- |
| the **owner** | 1368, 1683, 1697, 1713, 1802, 1876, 2289, 2272 | **~1.8 s** |
| a **non-owner**, relayed | 3241, 3494, 3680, 3709, 3830, 4010, 4050, 4657 | **~3.8 s** |

**The relay roughly doubles the join, about +2 s.** That is what Phase 3 is for, and it is a larger
number than the plan's "under 10 ms difference" acceptance target assumes is reachable today: the
SRT-to-SRT hop pays a second handshake, a second 120 ms latency window and a second libav probe on
the relaying side. The probe is already capped at 1 s / 1 MB, so roughly half the extra is the
probe and the rest is handshake and latency window.

### 4. A request that needs bytes is forwarded

**Pass.** Asked of the replica that does **not** own the stream:

| Request | Answer |
| --- | --- |
| `GET /api/live/preview/demo` | 6226 bytes, `ff d8 ff` - a real JPEG. Owner returned 6581 bytes of the same stream |
| `POST /api/live/snapshot/demo` | `{"documentId":"4ee6753c-..."}` |

The snapshot is a real document: 28550 bytes, `image/jpeg`, `640 x 360`, metadata
`"Live stream":"demo"`. Full source resolution, decoded fresh, not the preview - which is what
`CONTEXT.md` says a snapshot is.

One rig artefact worth stating so it is not mistaken for a defect: with `Storage__Provider=FileSystem`
and LiteDb each pod has its own store, so the *document* is only readable from the pod that took the
snapshot. In the committed configuration (S3 + PostgreSQL) both are shared. The forwarding itself is
what was under test and it works.

### 5. The owner dies

Producer through the ingest Service so it can land elsewhere; viewer pinned to the pod that is
**not** the owner so "a viewer on the other replica" is deterministic. Gaps are between consecutive
frame lines, which is `docs/replica-failover.md`'s own method.

| | Producer gap | Viewer gap | Viewer reconnected |
| --- | --- | --- | --- |
| **Recorded**, graceful | ~1-2 s | ~1.5 s | No |
| **Recorded**, force-kill | ~7 s | ~9 s | No |
| Measured, graceful, **as committed** | 2.04 s | 5.84 s | **Yes - counter reset to zero** |
| Measured, force-kill, **as committed** | 3.08 s | 7.00 s | No |
| Measured, graceful, **after the fix** | 3.07 s | 5.00 s | **No** |
| Measured, force-kill, **after the fix** | 2.04 s | 8.00 s | **No** |

Wall-clock, kill to back on air: graceful 8.6 s of which **5 s is the deliberate `preStop` sleep
during which the stream is still live**, so the real outage is the 3.07 s; force-kill 7.85 s.

**Where the force-kill time goes, observed rather than reasoned.** The producer's last frame lands
5.8 s after the kill - that is the encoder's own SRT peer idle timeout, exactly as the doc says, and
it is the encoder's setting not ours. Everything after it is ours and is about 2 s.

**Phase 1b's cost, observed.** The dead pod's name is held for about three beats, and the refusal is
visible on a real pod:

```
06:33:01.008  newConnection: connection rejected due to: INTERNAL REJECTION - ERROR:PREDEFINED:409
06:33:02      Accepted 'failover-demo' on the ingest port at 120 ms of SRT latency
06:33:02      Taking 'failover-demo' over from storage-demo-...-slx6x, which will stand down
```

It cost **one refusal and one retry interval, about 1.0 s** - not the "few seconds" Phase 1b
predicted. It landed in one of the two graceful runs and one of the two force-kill runs: whether an
encoder's retry falls inside the ~6 s hold is jitter against a 1 s retry loop, so *sometimes 1 s and
otherwise nothing* is the honest figure. This is also the entire difference between the 2.04 s and
3.07 s producer rows above; the fixes did not change the producer path at all.

**The viewer's connection surviving is real, once the fix is in.** Across both the graceful delete
and the force-kill, the viewer's frame counter froze and then carried on from where it was -
5978 to 6029 across the force-kill - with no reconnect and no reset. The relaying replica's log
shows it re-resolving the owner every pass with the dial capped at 1 s, which is the bounded relay
dial working:

```
06:32:57  Relaying 'failover-demo' from its owner storage-demo-...-slx6x
          Connection to srt://10.42.0.15:9010?mode=caller&connect_timeout=1000 failed
06:33:02  Serving 'failover-demo' to a viewer from 0s back (asked for 0s)
```

**Did the numbers move?** Producer: no, 2-3 s either way, the recorded ~7 s for a force-kill was
counting the encoder's peer timeout in with it and 5.8 s of the 7.85 s still is. Viewer: the
graceful row is *better* than recorded once fixed (5.00 s against a stated 1.5 s is worse on
paper, but the recorded 1.5 s cannot have been measured against a producer that took 3 s to return -
the viewer cannot beat its own feed). The force-kill row, 8.00 s against ~9 s, is the same number.

## The defects

### 1. A viewer attached for longer than the grace period was dropped the moment its stream moved

`LiveConsumptionService.ServeAsync` holds a viewer's socket open while the stream is missing from
the registry, for up to `GracePeriodSeconds`, and re-attaches wherever it reappears. The window was
measured from the wrong instant: `waitingSince` was set *before* the attach, and an attach lasts as
long as somebody is watching. So the grace a viewer got was the grace period **minus however long it
had been watching**, which for anyone watching more than 30 seconds is none at all.

```
06:30:24 INF  Giving up on 'failover-demo' for a viewer; it is not on air
```

Five seconds after the delete, on a replica that was perfectly healthy, to a viewer that had been
connected for 47 seconds.

Why it only showed on the graceful path: a force-killed pod leaves its registry entry behind, so
`GetAsync` keeps returning it and the give-up branch is never reached. A pod shut down cleanly
removes its entries, `GetAsync` returns null, and the stale window fires immediately. **A clean
shutdown was worse for a viewer than a crash.**

Fix: one line moved, so the wait is measured from when the feed actually stopped. Verified on the
rig - same delete, no reconnect, counter carried on.

### 2. A stream that moved to another replica got a new start time

`ClaimAsync` wrote `entry.StartedAt`, which is the moment the *new* owner built its entry, and never
read the start time out of the registry entry it was taking over. On one host the entry is reused so
this is invisible, which is why three single-host tests assert it and all pass. Across two pods the
name survived a move and the start time did not.

`docs/replica-failover.md` states the opposite - "the same registry entry and the same start time" -
so this was a documented claim that had never been checked on two pods.

Fix: the claim carries `existing.StartedAt` forward whenever it is taking over an entry that is
still listed. Verified on the rig: a force-kill moved `failover-demo` from `k2j29` to `k6djc` with
`startedAt` unchanged at `06:43:05.649`.

A new test pins it: `LiveNameLockTests.A_name_resumed_on_another_pod_keeps_its_start_time`.

**Writing that test found something about the fixture.** It has to run with a 30 s grace period
rather than the file's 5 s, because at 5 s the window it tests does not exist: a name frees three
beats (6 s) after its owner stops heartbeating, and a stream whose heartbeat is 6 s old is already
*gone* under a 5 s grace. A gone stream leaves no trace and the next publisher of that name starts a
genuinely new stream - which is correct, and is why the fix only carries a start time forward for an
entry that is still listed.

Regression: `LiveStreamTests` and `LiveRelayTests`, 13 tests, all pass. `LiveNameLockTests`, 4 tests
including the new one, all pass.

### 3. Not fixed: a graceful shutdown still deletes the stream rather than interrupting it

On `DisposeAsync` the coordinator calls `registry.RemoveAsync` for every stream it owns. So after a
rolling update the stream does not exist for the second or two before the encoder reconnects - it is
not `Interrupted`, it is gone - and what comes back is a genuinely new stream with a new start time.
Fix 2 above does not reach this path, because there is no entry left to inherit from. Measured: a
graceful delete moved `failover-demo` and `startedAt` went `06:41:07.100` to `06:43:05.649`.

**This was left alone deliberately.** The obvious change - mark it `Interrupted` instead of removing
it - trades a documented-behaviour discrepancy for a resource leak on the *common* path, because
**nothing in the service ever sweeps an abandoned entry.** A stale entry is filtered out of listings
by `IsGone` and is only ever overwritten if somebody republishes the name; a pod that dies holding
ten streams leaves ten entries in Redis forever. That is already true today after every force-kill.
Making graceful shutdown stop removing entries would add the same leak to every rolling update.
Doing it properly means an owner-less sweep, which is a design decision rather than a patch, and it
belongs on the plan next to Phase 6's leases.

The practical consequence, worth stating because a tile will show it: **a stream's "up for" clock
resets on every rolling update.** It no longer resets when a pod is force-killed.

## The single-node caveat

Single-node k3s with klipper, inside Docker Desktop on WSL2. **This reproduces none of the
load-balancer findings at the end of `docs/replica-failover.md`, and nothing here should be read as
evidence about them.** Those came from a bare-metal Talos cluster with Cilium:

- There is no source-address masquerading problem to see, because there is one node and traffic that
  reaches a pod IP was never translated. `externalTrafficPolicy: Local` is set on both media
  Services and was never exercised.
- There is no L2 announcement, no election, and therefore no sticky-ARP outage. klipper puts a
  hostPort on the single node.
- Every "cross-pod" hop in this document crossed a veth pair inside one Linux network namespace on
  one machine. The relay's +2 s join cost is protocol and probe, not network, so it should transfer;
  the absolute figures should not.

Also: the senders, the viewers, both replicas and Redis were all competing for the same 14 vCPUs,
and the host was otherwise busy. Timings are good to a few hundred milliseconds, not better.

## The rig is still up

The k3s container is left running with everything in place.

```bash
docker exec k3s-server kubectl -n storage-demo get pods
docker exec k3s-server kubectl -n storage-demo logs -l app=srt-viewer --tail=20
docker exec k3s-server kubectl -n storage-demo exec tools -- /app/ffmpeg/linux-x64/ffmpeg -version
```

| | |
| --- | --- |
| Container | `k3s-server`, published `6443` (API), `30900/udp`, `30910/udp`, `30080/tcp` for NodePorts if wanted |
| Manifests | `/manifests/k8s/`, `/manifests/failover.yaml`, `/manifests/tools.yaml` inside the container |
| Namespace | `storage-demo`; token `local-demo-token` |
| Replicas | `storage-demo`, 2, at the committed 2-core/3Gi sizing |
| Rig | `srt-producer` (through the ingest Service), `srt-viewer` (pinned by `VIEW_AT` to a pod IP), `tools` (a shell with the service's own ffmpeg and `/tmp/join.sh`) |
| Pod IPs | `kubectl -n storage-demo get pods -o wide` - they change on every restart |

From the host, `kubectl` needs only the kubeconfig out of the container - it already points at
`https://127.0.0.1:6443`, which is the published port:

```bash
docker exec k3s-server cat /etc/rancher/k3s/k3s.yaml > k3s.kubeconfig
KUBECONFIG=k3s.kubeconfig kubectl -n storage-demo get pods
```

Everything in this document was run through `docker exec k3s-server kubectl ...` instead, which
needs no kubeconfig at all. On Git Bash prefix it with `MSYS_NO_PATHCONV=1`, or paths like
`/manifests` are rewritten into `C:/...` before Docker sees them.

Rebuilding the image after a source change is the only slow step - `docker build`, `docker save`,
`docker cp`, `ctr images import`, then `kubectl rollout restart deploy/storage-demo`. About three
minutes.

## What this changes about the plan

- **Phase 1b is verified on real pods.** The refusal is a 409 at the handshake, the incumbent is
  undisturbed, a free name is admitted on either replica, and the dead-pod hold costs about one
  retry interval. `plan.md`'s "Never run on real pods" line can go.
- **Phase 3's acceptance target is a long way off.** "Under 10 ms difference" between a viewer on
  the owner and one on a non-owner; measured today it is about 2000 ms. Halving it is the probe on
  the relaying side, which an HTTP hop removes entirely.
- **`plan.md`'s "Nothing has run on more than one replica"** is now false for the name lock, the
  wrong-pod viewer, request forwarding and the owner dying. It remains true for everything at scale:
  this was two replicas and one stream, not a thousand.
- **The two fixes are in `src/` and uncommitted**, on `srt-listener-libsrt`, with one new test.
