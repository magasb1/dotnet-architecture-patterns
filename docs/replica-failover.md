# What losing a replica costs a producer and a viewer

Measured, not estimated. One encoder pushes a test pattern into the ingest port and one player
pulls the same stream back off the consumption port, both reporting a timestamped frame counter
once a second; then the replica that owns the stream is taken away. The rig is
[`k8s/live/failover-test.yaml`](../k8s/live/failover-test.yaml); reproducing it is described at the
bottom.

This matters because the service is for cameras: the stream is meant to be live all the time, and
the only interesting question about a replica disappearing is how long somebody stops seeing
pictures.

## The rig these numbers came from

Two replicas of `k8s/live/deployment.yaml` at its committed sizing, two cores and 3 GiB each, on a
single-node k3s v1.32.1 running as a privileged container. That container sits on a 14-vCPU,
15.5 GiB WSL2 virtual machine under Docker Desktop, and the senders, the viewers, both replicas and
Redis all compete for the same cores. **Timings here are good to a few hundred milliseconds and no
better**, and every "cross-pod" hop crossed a veth pair inside one machine rather than a network.

Worth saying because an earlier version of this page carried numbers from the same *shape* of
cluster but not the same code: the listener was libav's, the in-cluster hop was a second SRT dial,
and two defects that only two real pods can expose were still in. Everything below was re-measured
after those changed. The full run, including what it found, is in
`.scratch/scale-to-1000/cross-pod.md`.

## The numbers

Gaps are between consecutive frame lines on each side. **Before** is the build as it stood when the
run started; **after** is the same rig with the two defects in "What running it found" fixed.

| What happened | | Producer gap | Viewer gap | Viewer had to reconnect |
| --- | --- | --- | --- | --- |
| Owner **deleted gracefully**, viewer on the other replica | before | 2.04 s | 5.84 s | **Yes — counter reset to zero** |
| | after | 3.07 s | 5.00 s | No |
| Owner **force-killed**, viewer on the other replica | before | 3.08 s | 7.00 s | No |
| | after | 2.04 s | 8.00 s | No |
| A viewer **joining** a stream that is on air, on the owner | | — | 1.93 s | — |
| A viewer **joining** a stream that is on air, relayed | | — | 1.81 s | — |

Wall clock from the kill to back on air is longer than the producer gap in both cases and for
different reasons. Graceful is 8.6 s, of which **5 s is the deliberate `preStop` sleep, during
which the stream is still live**; the outage is the 3.07 s. Force-kill is 7.85 s, of which 5.8 s is
the encoder waiting out its own SRT peer idle timeout before it notices anything is wrong.

Nothing was lost in any of them. The stream came back under the same name, in the same registry
entry, with the same start time — a reconnect resumes a stream rather than creating a second one —
with one exception that is now known rather than assumed: a *graceful* shutdown deletes its
entries, so after a rolling update there is no entry to resume and the start time does restart. See
"What is not covered".

The graceful row is the one that describes normal operations, because rolling updates, node drains
and scaling down are all graceful. A camera stops for about three seconds and a viewer keeps
watching.

## Where the time actually goes

### The producer

Two costs, and only one of them is ours.

**Noticing.** On a graceful shutdown the service closes its ingest sockets on the way out, so the
encoder learns immediately. On a force-kill nothing gets the chance to close anything, and the
encoder waits out its own SRT peer idle timeout: measured here at 5.8 s from the kill to the
producer's last frame. That is the encoder's setting rather than the service's — one configured
with a shorter `peeridletimeout` recovers proportionally faster from a node that vanishes — and it
is why the force-kill wall clock is longer than the force-kill *gap*.

**Reconnecting.** About two to three seconds, and the same either way. The encoder reconnects to the
same address it has always used, a replica accepts it, claims the name, and the stream is on air
again. Nothing had to be reconfigured and nothing had to be told where the stream went.

**Being refused once, sometimes.** A live name is locked to its owner, and a dead pod's names stay
locked until its heartbeat is three beats stale, about six seconds. An encoder whose retry falls
inside that window is refused at the handshake with a 409 and tries again a second later. It
happened in one of the two graceful runs and one of the two force-kill runs, so *about a second,
and otherwise nothing* is the honest figure; it is also the whole of the difference between the
2.04 s and 3.07 s producer rows above. That is the price of the lock, and it is cheaper than the
several seconds the design that introduced it expected to pay.

Two things keep a replacement available to reconnect to. `maxUnavailable: 0` on the rolling update
means a replica is never taken away before its replacement is serving, and a PodDisruptionBudget of
`minAvailable: 1` says the same to a node drain. A `preStop` sleep of five seconds covers the last
gap: Kubernetes removes a pod from its Services and sends SIGTERM at the same moment, so without it
the process can close its sockets before kube-proxy has stopped routing, and an encoder reconnecting
inside that window lands back on the pod that is going away.

### The viewer

**The viewer's connection survives its stream moving**, and that is now true of both rows rather
than only one. The frame counter freezes and carries on from where it was — 5978 to 6029 across a
force-kill — with no reconnect and no reset. When the replica owning a stream disappears, the
encoder reconnects somewhere else and the name moves; the replica holding the viewer notices,
re-resolves the owner and re-attaches inside the viewer's existing connection. The muxer's timeline
is carried across, so the player is never asked to accept timestamps jumping backwards
mid-connection.

It was not true of the graceful path until this run fixed it. A viewer that had been watching for
longer than the grace period was dropped the instant its owner shut down cleanly, so a rolling
update dropped every viewer it had, and this page claimed the behaviour the code did not have. The
detail is in "What running it found"; what matters here is that reading the code is not what caught
it.

**The in-cluster hop is HTTP, and that is where the relay's old penalty went.** A viewer on the
wrong replica used to be served by dialling the owner's SRT consumption port: a second handshake, a
second latency window, and a second libav probe of the relayed transport stream on the relaying
side. It cost about two seconds on top of a direct join, of which roughly half was the probe. The
owner now answers `GET /api/live/peer/view/{name}` with `video/mp2t` and the relaying replica copies
the body into the viewer's SRT socket, which decodes nothing and probes nothing. **A relayed join
is 1.81 s against 1.93 s direct** — the same number inside this rig's noise. SRT still reaches the
viewer, which was the fixed constraint; only the hop between two pods changed.

One bound survives the change, for the reason it was introduced. A pod that is force-killed leaves
its registry entry behind until its heartbeat goes stale, so for a while every viewer arriving
elsewhere is sent to an address nobody is answering. The relay's HTTP handler has a one-second
connect timeout and the loop re-reads the registry every pass, so a dead owner costs a second and a
retry rather than an operating system's idea of how long to wait for a TCP connection — which,
unbounded, would have been worse than the SRT dial it replaced.

What is left of a viewer's join is a probe, and it is the player's own. libav reads five seconds of
a transport stream before deciding what is in it, and bounding that — one second and one megabyte —
is what took a join from about eight seconds to two, before any of the work above. The same limit
applies on the server to a new feed arriving, where it is dead time between a camera connecting and
its stream being on air.

That limit is worth understanding before copying it. A camera that presents its audio late would
have the probe end before the audio track appears, and the symptom is a stream that plays without
sound. `Live__ProbeSeconds` and `Live__ProbeBytes` raise it on the server; players set
`-analyzeduration` and `-probesize` for themselves, and the desktop client already does.

**A viewer on the replica that died is the one case that reconnects**, because nothing can save a
socket whose pod has gone. It then pays the join cost, the last two rows of the table, on top of
whatever gap the stream itself has. Nothing spreads viewers away from owners deliberately, and it
would be the wrong thing to build: every viewer of a stream funnels through its owner anyway,
because only the owner has the bytes.

### Readiness

Readiness now includes whether the two media ports are accepting, not only whether storage and a
database are reachable. For a service whose purpose is being live, a replica that is reachable but
not accepting is worse than one that is plainly absent: the Service keeps sending encoders to it and
they keep failing. A replica that has no libsrt to open those ports with, or that failed to bind or
listen on one of them, takes itself out of the Service rather than swallowing encoders it could
never serve. Binding either succeeds or returns an error, so readiness is a fact rather than
something inferred, and a replica binding a range of ingest ports counts its listeners against the
number it expected.

A `startupProbe` keeps the first few seconds from being mistaken for a failure: the native libraries
load and the ports bind before anything can be served, and on this rig the first `/health/ready` on
each pod took about 5.2 s and answered 503. Readiness then fails after two checks rather than three,
so a replica that stops accepting leaves the Service in about ten seconds.

One consequence is worth stating: readiness gates the pod, and the pod backs all three Services. A
storage or database outage therefore also takes a replica out of the ingest Service, even though it
could still carry a stream. That is a deliberate simplification, not an oversight — Kubernetes has
no per-port readiness — and if it ever matters the answer is to move the storage checks out of the
`ready` tag and alert on them instead.

## What running it found

Both defects below were found by running the rig rather than by reading the code, and both had
passing single-host tests standing over them. That is the argument for this page existing: two
replicas sharing a dictionary in one process agree about things two pods do not.

**A viewer attached for longer than the grace period was dropped the moment its stream moved.** The
replica holding a viewer waits up to the grace period for a stream that has vanished from the
registry to reappear somewhere. That window was measured from the wrong instant — from before the
attach rather than from when the feed stopped — and an attach lasts as long as somebody is
watching, so the grace a viewer actually got was the grace period minus however long it had been
watching, which for anyone watching more than thirty seconds was none at all. It showed only on the
graceful path: a force-killed pod leaves its registry entry behind, so the give-up branch is never
reached, while a pod shut down cleanly removes its entries and the window fires immediately. **A
clean shutdown was worse for a viewer than a crash**, and clean is the normal-operations path — a
rolling update dropped every viewer it had. The fix is one line moved. The "before" row of the
table is what it looked like.

**A stream that moved replicas got a new start time.** The claim wrote the moment the new owner
built its entry and never read the start time out of the entry it was taking over. On one host that
entry is reused, so three tests asserted this and all passed. Across two pods the name survived the
move and the start time did not — which this page stated as a fact that had never been checked. The
claim now carries the existing start time forward whenever it takes over an entry that is still
listed.

## What is not covered

**A recording loses the part in progress with its pod.** A recording is muxed to the pod's own disk
a few minutes at a time and each part is stored as it completes, so a pod that is force-killed
mid-recording loses only the part it was writing, and the document keeps everything before it. A
pod shut down gracefully finishes the part and uploads it, which is what
`terminationGracePeriodSeconds: 60` is for. What is still not covered is a recording *moving*: a
displaced owner closes its recording rather than carrying it to the new one, so a flapping encoder
leaves several documents rather than one.

**A stream's "up for" clock resets on every rolling update.** A pod shut down cleanly deletes the
registry entries it owns, so for the second or two before the encoder reconnects the stream does not
exist — it is not interrupted, it is gone — and what comes back is a genuinely new stream. Marking
the entry interrupted instead would preserve the start time and cost a leaked entry on the common
path, because nothing in the service ever sweeps an entry whose owner is gone; that leak already
happens after every force-kill. Which of the two to keep is a design decision rather than a patch,
so it was left alone deliberately. The clock no longer resets when a pod is force-killed, which is
the case the fix above covers.

**A stream's buffer starts empty on the new owner.** Rollback and a recording's pre-roll reach back
into memory that died with the old pod. For the first thirty seconds after a move, a stream has less
history than it promises.

**One popular stream does not spread.** Every viewer of a stream funnels through its owner. Adding
replicas adds capacity for more streams, not for more viewers of one. Correct for contribution,
where cameras outnumber viewers; wrong for distributing one stream to an audience.

## Reproducing it

A k3s container is enough, and needs no cluster of its own:

```bash
docker run -d --name k3s-server --privileged --tmpfs /run --tmpfs /var/run \
  -p 6443:6443 rancher/k3s:v1.32.1-k3s1 server --disable traefik --disable metrics-server

docker build -f docker/Dockerfile -t storage-demo:latest .
docker save storage-demo:latest -o storage-demo.tar
docker cp storage-demo.tar k3s-server:/tmp/
docker exec k3s-server ctr images import /tmp/storage-demo.tar
```

Then apply the manifests, in that order, and add the rig:

```bash
docker cp k8s k3s-server:/manifests
docker exec k3s-server kubectl apply -f /manifests/k8s/namespace.yaml
docker exec k3s-server kubectl apply -f /manifests/k8s/
docker exec k3s-server kubectl apply -f /manifests/k8s/live/
```

The namespace goes first on purpose: kubectl applies a directory in filename order, so against a
fresh cluster `configmap.yaml` and `deployment.yaml` are submitted before `namespace.yaml` exists
and three objects are refused with `namespaces "storage-demo" not found`. Running the directory
twice works too. On Git Bash prefix these with `MSYS_NO_PATHCONV=1`, or `/manifests` is rewritten
into a Windows path before Docker sees it.

The manifests default to S3 and PostgreSQL. For a failover test neither matters, and a ConfigMap
selecting local storage with the Redis registry still on is enough. Redis has to stay: the registry
is what the replicas agree through, and with `InMemory` each pod would believe it owned everything.

`imagePullPolicy` also has to be `Never` or `IfNotPresent` for a locally imported image, since
`storage-demo:latest` is a placeholder for a real registry and Kubernetes would otherwise try to
pull it.

Watch both sides, find the owner, and take it away:

```bash
kubectl -n storage-demo logs -f deploy/srt-viewer
kubectl -n storage-demo logs -f deploy/srt-producer

kubectl -n storage-demo exec deploy/srt-producer -- \
  wget -qO- --header='X-Storage-Token: local-demo-token' http://storage-demo:80/api/live

kubectl -n storage-demo delete pod <owner>                          # graceful
kubectl -n storage-demo delete pod <owner> --grace-period=0 --force # it vanishes
```

The gap between two consecutive frame lines is the answer. A counter that carries on rather than
restarting at zero means the connection itself survived.

## What the load balancer has to do, and what breaks it

Two things were measured on a bare-metal Talos cluster with Cilium, and both of them decide whether
the service is reachable at all. Neither is visible from the manifests alone, and **neither is
visible from the k3s rig above either**: one node with klipper has no source-address masquerading
to see and no L2 election to go wrong, so nothing in the numbers above is evidence about any of
this.

**The sender's address must survive the trip.** With `externalTrafficPolicy: Cluster` the node
masquerades the sender, and the address an SRT connection is keyed on changes underneath it. A
sender connects, runs for ten to twenty seconds, drops with an I/O error, reconnects, and does it
again forever. It looks like packet loss and it is not. `Local` leaves the address alone and the
same sender runs indefinitely. `sessionAffinity: ClientIP` does not help, which is what rules out
the other explanation: the backend was never flapping, the source address was.

**Only one node answers for the address.** Cilium's L2 announcements elect a single node to answer
ARP for each load balancer address. Under `Local` that node only answers while it has a ready pod,
so a replica on every node is a requirement rather than a preference here. Worse, the election is
sticky: when the lease does move, the upstream router keeps the old node in its ARP cache, and the
whole address is unreachable until that entry expires. That was minutes, not seconds.

The consequence is that L2 announcement is the wrong mechanism for a service whose point is being
live. It is fine for bringing one up. For a service that must not go dark, the address wants to be
advertised from every node at once, which means BGP: Cilium's BGP control plane peering with the
upstream router, one address, equal-cost paths, and no single node whose health decides whether
anyone can connect.

## One address

Producers, consumers, the REST API and gRPC all share a single address and differ only by port. A
Service may carry more than one protocol, so the four live on one `LoadBalancer`:

| Port | Protocol | What connects |
| ---- | -------- | ------------- |
| 80 | TCP | REST, and the range requests a client seeks a recording with |
| 5080 | TCP | gRPC |
| 9000 | UDP | encoders pushing SRT |
| 9010 | UDP | players pulling SRT |

The alternative, one address per concern, means four things to configure in a camera that has room
for one. What a player is told to connect to is this address and this port, whichever replica owns
the stream: a viewer that lands on a replica which does not own it is relayed inside the cluster.
