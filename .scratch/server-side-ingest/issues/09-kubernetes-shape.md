# One ingest endpoint in Kubernetes

Type: grilling
Status: resolved
Blocked by: 05

## Question

What does an operator configure, and what do the manifests become?

An encoder is pointed at one address that never changes. Behind it, any replica may receive any
connection, and whichever does owns that stream.

Decide:

- How SRT reaches the pods. It is UDP, so the Service and load balancer behaviour differ from the
  HTTP paths, and source address preservation matters for anything that later authenticates.
- Whether connections from one sender need to keep landing on the same replica, and whether that is
  even expressible for UDP through the load balancers in play, which are Cilium and k3s NodePort.
- What replaces the current per-pod ingest Services in `k8s/live/statefulset.yaml`, which exist
  precisely so a sender can choose its pod.
- Whether a StatefulSet is still the right object once ownership is claimed on connect rather than
  assigned by address.
- How two media ports are published rather than one, given ingest and consumption are separate and
  a deployment may expose them to different networks. That is the whole point of splitting them, so
  the manifests have to make it expressible.
- What a replica asks for in ephemeral disk, since a recording is written locally before it is
  uploaded and a configured maximum duration puts a ceiling on how much that can be.
- What a replica's memory limit has to be, given every stream it owns holds a buffer in memory whose
  ceiling is per stream. The number of streams a replica accepts and the pod's memory request are the
  same decision wearing two hats.
- What happens when many encoders reconnect at once, since the listener accepts only two or three
  connections a second. A pod restart makes every sender retry together.
- How the existing proxy to the owning replica behaves when ownership can move between connections.
- What the same design collapses to when it runs as a single process on Windows or Linux, where
  there is exactly one owner and none of this applies.

## Answer

### What an operator configures

Two addresses, each its own Service, each exposable to a different network. An encoder is pointed at
the ingest address and never changes. A viewer reaches the consumption address. Everything else is
the API. That separation is the point: a deployment can put ingest on an untrusted network and keep
consumption inside, or the reverse, and say in one sentence what each grants.

### The workload

A Deployment, not a StatefulSet. Stable pod identity existed only so a sender could be pointed at a
particular pod, and nothing points at a particular pod any more. When a replica claims a name it
records its own pod address, so a replica needing to forward a request looks the address up rather
than deriving it from a predictable name. That deletes the headless Service and the per-pod ingest
Services together.

### How a connection finds a pod

The load balancer picks. Two different things are often confused here and only one is needed:

- **Per-flow stickiness is required.** An SRT connection is a UDP flow, and its packets must all reach
  the same pod. This is ordinary connection tracking and every load balancer in play does it.
- **Stickiness across reconnects is not required.** A returning encoder may land anywhere, because the
  newest connection wins the name and the previous owner stands down. This is the payoff of that
  decision: no affinity configuration, no session tables, nothing to get wrong.

The external traffic policy stays local, so the sender's address survives. That is a diagnostic
argument rather than a security one, since the chosen authentication candidate is a passphrase, but
an encoder nobody can identify in a log is painful exactly when it matters. The cost is that only
nodes actually running a pod receive ingest traffic.

### How it scales

Streams spread across replicas because the load balancer spreads connections. Each replica owns some
streams and holds everything for them: the hub, the buffer, any recording. Adding replicas adds
capacity for more streams, and the useful autoscaling signal is streams owned per replica rather than
processor load, since an idle stream still costs a buffer.

A replica's ceiling comes from two resources that follow directly from earlier decisions. Memory is
the buffers, at a per-stream ceiling times the streams it owns. Ephemeral disk is recordings in
progress, bounded by the configured maximum duration times how many can run at once. Both are
calculable rather than empirical, which is what makes a request and limit honest.

Accept rate is the third limit. One listener accepts a couple of connections a second, so a mass
reconnect is absorbed by senders retrying, and more replicas mean proportionally more accept capacity.

### What does not scale this way

Viewers of one stream all funnel through that stream's owner, because only the owner has the bytes.
Spreading streams across replicas does nothing for a single popular stream, and the owner's egress is
the ceiling. This is fine for the contribution-style use this is built for, where streams outnumber
viewers, and it is the wrong shape for distribution to an audience. Recorded on the map as a known
boundary rather than solved.

### Standalone

The same code with nothing switched on. One process owns every stream, the registry and the lock are
the in-memory implementations, no request is ever forwarded because there is nowhere to forward it,
and the two ports are just two ports. None of the above applies, which is the property worth
protecting: the Kubernetes shape is configuration, not a different program.
