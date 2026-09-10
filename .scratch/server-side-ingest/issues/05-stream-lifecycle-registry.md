# Stream lifecycle and the registry

Type: grilling
Status: resolved
Blocked by: 01, 02

## Question

What is the life of a stream that nobody announced, and what does the shared registry hold?

Decide:

- What happens the moment a connection arrives: how the name is taken from the streamid, what the
  service does with a name it has never seen, and what it does with one it has.
- What the accept loop looks like, given a libav listener serves one caller and must be re-opened
  after each accept, and given the name is only readable once the connection already exists. A
  connection therefore cannot be turned away by name, only dropped after the fact.
- How a replica claims a name, given the load balancer decides which replica receives the
  connection, and what happens if two replicas believe they hold the same name.
- What the grace period is between a feed dropping and the stream being considered gone, and what a
  client sees during it.
- Whether a stream that has ended leaves any trace at all, now that a stream no longer becomes a
  document by itself.
- Whether the registry is still keyed by an opaque identifier or by the name, and what that means
  for a reconnect that lands on a different replica.
- How manually created streams share this model, since they have a name before anything connects.

The current model is in `src/StorageDemo.Core/Streaming/ILiveSessionRegistry.cs` and
`LiveSessionStaleness.cs`, with a Redis-backed implementation and an in-memory one for standalone
use. Both have to keep working.

## Answer

**The registry is keyed by name.** Nothing looks a stream up by an opaque identifier any more. An
identifier survives as a per-connection detail, useful in logs and for telling one attempt from the
next, but it is not the key. This is the mechanical consequence of the name being the identity: a
reconnect can only resume a stream if what gets looked up is what the encoder presented.

**A connection is accepted before it can be judged.** The listener takes one caller and re-opens, and
the name is only readable once the connection exists. Nothing can be refused by name, only accepted
and then dropped. An unnamed caller is likewise accepted before it is known to be unnamed. Any rule
about who may push has to be enforced after the fact, which is a further reason the passphrase is the
stronger authentication candidate: it acts during the handshake, before any of this.

**The newest connection wins the name.** Having read the name, the replica claims it with the
distributed lock that already exists, renewed by the heartbeat. A replica finding the name already
held takes it anyway and records itself as owner. The previous owner discovers on its next heartbeat
that it no longer holds the claim, shuts its hub down and closes any recording as a complete
document.

The alternative, refusing the newcomer, was rejected because it makes recovery wait on a timeout the
service does not control. An encoder actively pushing bytes is more real than a socket that has not
yet noticed its peer is gone, and SRT takes seconds to work that out. During those seconds the
refusing design has a live encoder being turned away by a dead connection.

**The grace period is thirty seconds**, matching the staleness timeout already in the code.
Everything survives it. The hub stays up, the buffer keeps its contents, and a recording in progress
keeps running and records the silence, which is the point of a recording surviving a blink at all.

**The client keeps showing an interrupted stream** rather than removing it. A tile that vanishes and
reappears is worse than one showing a state, and since a reconnect resumes the same stream the tile
would be showing the same thing on both sides of the gap. This needs a state on the session model
that does not exist today.

**After the grace period the stream is gone and leaves nothing behind.** No entry, no history. The
documents it produced are its trace. The registry stays a picture of what is live now, which is the
only thing every replica needs to agree on.

**Manual and automatic streams share one namespace and one claim.** A manual stream is simply one
that claimed its name early and is waiting for bytes, owned by the replica that opened its socket. An
encoder presenting a name a manual stream holds is the same conflict as any other and resolves the
same way, with the newest connection taking it. Two namespaces would mean two lookup paths and a name
that means different things depending on how it arrived.
