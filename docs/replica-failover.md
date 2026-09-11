# What losing a replica costs a producer and a viewer

Measured, not estimated. Two replicas on a single-node k3s cluster, one encoder pushing a test
pattern into the ingest port and one player pulling the same stream back off the consumption port,
both reporting a timestamped frame counter once a second. The rig is
[`k8s/live/failover-test.yaml`](../k8s/live/failover-test.yaml); reproducing it is described at the
bottom.

This matters because the service is for cameras: the stream is meant to be live all the time, and
the only interesting question about a replica disappearing is how long somebody stops seeing
pictures.

## The numbers

| What happened | Producer gap | Viewer gap | Viewer had to reconnect |
| --- | --- | --- | --- |
| Owner **deleted gracefully**, viewer on the other replica | ~1–2 s | ~1.5 s | No |
| Owner **force-killed**, viewer on the other replica | ~7 s | ~9 s | No |
| A viewer **joining or rejoining** a stream that is on air | — | ~2 s | — |

Nothing was lost in any of them. The stream came back under the same name, the same registry entry
and the same start time: a reconnect resumes a stream rather than creating a second one.

The graceful row is the one that describes normal operations, because rolling updates, node drains
and scaling down are all graceful. A camera stops for a second or two and a viewer barely notices.

## Where the time actually goes

### The producer

Two costs, and only one of them is ours.

**Noticing.** On a graceful shutdown the service closes its ingest sockets on the way out, so the
encoder learns immediately and the gap is about a second. On a force-kill nothing gets the chance
to close anything, and the encoder waits out its own SRT peer idle timeout, five seconds by
default. That is the whole difference between the seven-second row and the one-second one, and it
is the encoder's setting rather than the service's: an encoder configured with a shorter
`peeridletimeout` recovers proportionally faster from a node that vanishes.

**Reconnecting.** About a second. The encoder reconnects to the same address it has always used, a
replica accepts it and claims the name, and the stream is on air again. Nothing had to be
reconfigured and nothing had to be told where the stream went, which is the payoff of the newest
connection winning a contested name.

Two things keep a replacement available to reconnect to. `maxUnavailable: 0` on the rolling update
means a replica is never taken away before its replacement is serving, and a PodDisruptionBudget of
`minAvailable: 1` says the same to a node drain. A `preStop` sleep of five seconds covers the last
gap: Kubernetes removes a pod from its Services and sends SIGTERM at the same moment, so without it
the process can close its sockets before kube-proxy has stopped routing, and an encoder reconnecting
inside that window lands back on the pod that is going away.

### The viewer

**The viewer's connection survives its stream moving.** In both failure rows above there is no
reconnect: the frame counter carries on from where it was rather than restarting at zero. When the
replica owning a stream disappears, the encoder reconnects somewhere else and the name moves; the
replica holding the viewer notices, re-resolves the owner and re-attaches inside the viewer's
existing connection. The muxer's timeline is carried across, so the player is never asked to accept
timestamps jumping backwards mid-connection.

What is left is the feed's own gap plus about three seconds to follow it. Two changes took that
last part down from about eight seconds:

- **The relay dial is bounded.** A pod that is force-killed leaves its registry entry behind until
  its heartbeat goes stale, so for a while every viewer arriving elsewhere dials an address nobody
  is answering. libav's own connect timeout is several seconds; a peer inside a cluster answers in
  milliseconds or not at all, so the dial is capped at one second and the loop re-reads the registry
  every pass.
- **The probe is bounded.** libav reads five seconds of a transport stream before deciding what is
  in it. That is the single largest cost in a viewer joining, and capping it at one second and one
  megabyte took a join from **about eight seconds to two**.

That probe limit is worth understanding before copying it. A camera that presents its audio late
would have the probe end before the audio track appears, and the symptom is a stream that plays
without sound. `Live__ProbeSeconds` and `Live__ProbeBytes` raise it on the server; players set
`-analyzeduration` and `-probesize` for themselves, and the desktop client already does.

**A viewer on the replica that died is the one case that reconnects**, because nothing can save a
socket whose pod has gone. It then pays the join cost, which is the two-second row, on top of
whatever gap the stream itself has. Nothing spreads viewers away from owners deliberately, and it
would be the wrong thing to build: every viewer of a stream funnels through its owner anyway,
because only the owner has the bytes.

### Readiness

Readiness now includes whether the two media ports are accepting, not only whether storage and a
database are reachable. For a service whose purpose is being live, a replica that is reachable but
not accepting is worse than one that is plainly absent: the Service keeps sending encoders to it and
they keep failing. A replica whose stream identifier self-test failed, or whose FFmpeg has no SRT,
takes itself out of the Service rather than swallowing encoders it could only ever serve unnamed.

A `startupProbe` keeps the several seconds of boot self-test from being mistaken for a failure, and
readiness fails after two checks rather than three so a replica that stops accepting leaves the
Service in about ten seconds.

One consequence is worth stating: readiness gates the pod, and the pod backs all three Services. A
storage or database outage therefore also takes a replica out of the ingest Service, even though it
could still carry a stream. That is a deliberate simplification, not an oversight — Kubernetes has
no per-port readiness — and if it ever matters the answer is to move the storage checks out of the
`ready` tag and alert on them instead.

## What is not covered

**A recording dies with its pod.** This is an accepted limit, not an oversight: a recording is
written to the pod's own disk and uploaded when it ends. A pod that is force-killed mid-recording
loses it. A pod shut down gracefully finishes uploading first, which is what
`terminationGracePeriodSeconds: 60` is for. Smoothing this over is on the map as deliberately
deferred, paired with uploading a recording in completed pieces as it runs, since both are the same
problem: a recording outliving one pod.

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
docker exec k3s-server kubectl apply -f /manifests/k8s/
docker exec k3s-server kubectl apply -f /manifests/k8s/live/
```

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
the service is reachable at all. Neither is visible from the manifests alone.

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
