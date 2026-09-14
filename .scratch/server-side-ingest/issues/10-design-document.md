# The design document

Type: task
Status: resolved
Blocked by: 04, 05, 06, 07, 08, 09, 11

## Question

Assemble the decisions on this map into the document a developer builds from.

It has to say what the pipeline is, what happens when a stream arrives, what the harvester does,
what a recording and a snapshot are, what the registry holds, what the Kubernetes shape is, and what
the standalone shape is. It has to name the limits deliberately accepted, in particular the
recording that ends when a reconnect lands on a different replica.

It has to state the accepted limits plainly, because each is a deliberate trade rather than an
oversight: a recording is bounded by pod disk and dies with its pod, a displaced owner closes its
recording rather than moving it, and a stream that is being recorded is visible on the stream rather
than in the document list until it finishes.

It has to name the conditions an operator must be able to see, in particular a buffer whose byte
ceiling is binding before its time window is reached, since that silently shortens every pre-roll
taken from it.

The analyzer needs a capability it does not have: answering "the picture right now" rather than only
"the picture N seconds in". Both the preview and the snapshot depend on it, and its absence is what
the sampler diagnosis found.

It has to carry the fixes that survive the redesign: `LibavMediaAnalyzer.Seek` targeting a timestamp
relative to the stream's start rather than an absolute one, and the live thumbnail response setting
the same no-store header on its local branch that its proxy branch already sets.

It has to be explicit about the surface changes that follow from keying by name: playback addresses
no longer carry an opaque identifier, and the session model gains an interrupted state it does not
have today.

It also has to say what happens to the code that exists now: what `LiveStreamManager` becomes, what
the REST and gRPC surfaces gain and lose, and what the desktop client has to change given a stream
tile no longer implies a file being written.

## Answer

Written to `docs/design/server-side-stream-ingest.md`, in the repository rather than in this scratch
directory, because a developer is meant to build from it.

It carries every decision on the map, the accepted limits as deliberate trades rather than
omissions, the three conditions an operator has to be able to see, a table mapping each existing
piece of code to what it becomes, and the three defects in code the new design still uses that have
to be fixed regardless of which sampler survives.

One thing is deliberately left open rather than decided, and is called out in the document as needing
an answer before build: what the buffer does with a feed that runs a long way with no startable point,
since a buffer holding one open-ended segment can serve nobody. It was surfaced while resolving the
buffer ticket and is an edge rather than a decision the map should have forced.
