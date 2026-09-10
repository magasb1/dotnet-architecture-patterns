# Implementation brief: server-side stream ingest

Hand this to whoever picks the work up. It is written to be pasted into a fresh session.

---

You are implementing a redesign of live streaming in this repository. The design is already settled.
Your job is to build it, not to redesign it.

## Read these first, in this order

1. `docs/design/server-side-stream-ingest.md` — the design. This is your specification.
2. `CONTEXT.md` — the vocabulary. Use these words in code and in conversation; they are precise and
   they were argued over.
3. `.scratch/server-side-ingest/map.md` — an index of every decision, each linking to the ticket that
   holds its reasoning. Go here when the design says *what* and you need *why*.

If the design document and the map disagree, the map is right and the design document is stale.

## What this repository is

A demo service showing pluggable file storage and pluggable database persistence as two independent
infrastructure concerns, selectable by configuration, with no domain or application code aware of
either. Live streaming was built on top of it. `README.md` describes what exists today.

Nothing is committed yet. The working tree is the whole history.

## The state you are starting from

The code is the **old** design: a stream is requested before it exists, one remux writes a recording
file, a sampler decodes previews from that file's tail, and the recording becomes a document when the
stream ends. You are replacing that.

Recent fixes to the old design are already in the tree and worth keeping regardless: the desktop
client's two-tab sidebar, live playback starting at the live edge, finished sessions being removed
from the registry, a pinned Compose port for the API, and the benchmark cleaning up the documents it
uploads.

A Docker Compose stack with an SRT test-pattern sender exists and works. `README.md` has the commands.

## Before you write the first line

**One question is deliberately unanswered and you have to answer it.** The design flags it: what the
buffer does with a feed that runs a long way with no startable point, since a buffer holding one
open-ended segment can serve nobody. Decide it, write the decision down, then build.

## Suggested order

The sequencing matters, because the riskiest piece is also the foundation.

1. **Fix the three carry-over defects**, with tests. They are listed under "Fixes that carry over
   regardless" in the design. They live in code the new design still uses, and doing them first means
   the analyzer is trustworthy before anything is built on it. Note that
   `tests/StorageDemo.Tests/Infrastructure/LivePreviewFreezeTests.cs` already reproduces one of them
   deterministically.
2. **The accept loop.** Listener, re-open after accept, stream identifier captured from the log
   callback, boot-time self-test. Prove this in isolation with several concurrent named senders before
   anything depends on it. If it does not work, the whole design changes, so find out now.
3. **The hub and the packet tier**, including the buffer as segments. Verify by attaching a trivial
   consumer that writes to a file.
4. **The frame tier and the harvester**, replacing the old sampler.
5. **The registry**, rekeyed by name, with the claim, the interrupted state and the grace period.
6. **Recording**, then **snapshot**.
7. **The consumption port.**
8. **The desktop client.**
9. **The deployment manifests.**

## Constraints you must not quietly break

- **Storage provider independence.** Nothing may require a feature only one provider has. This is why
  recordings write locally and upload at the end rather than streaming into object storage, and it is
  the thesis the whole repository exists to demonstrate.
- **Standalone parity.** The same code must run as one process on Windows or Linux with the in-memory
  registry and lock, and nothing to forward to. The Kubernetes shape is configuration, not a different
  program.
- **No second SRT stack.** libsrt is statically linked into libavformat and not re-exported. Vendoring
  it separately was investigated and rejected; see the map.
- **FFmpeg version pinning.** The bindings target one ABI. Do not bump FFmpeg without reading the SRT
  research ticket first.
- **The accepted limits are deliberate.** They are listed in the design. Do not solve them silently;
  if one turns out to be intolerable, say so and reopen it.

## Out of scope

RTMP and RTSP ingest. A server-fetched source the service pulls. Building detection, tracking or KLV
extraction, though the seam they attach to is in scope and must exist. Authentication is deferred, but
do not design anything that would make a passphrase awkward to add.

## How to work

Follow the conventions already in the codebase. Comments explain *why*, not what; the existing code
has a distinct voice and reasons about trade-offs in prose. Match it.

The `ponytail` skill is active in this repository. Prefer the smallest thing that works, delete before
you add, and mark a deliberate shortcut with a `ponytail:` comment naming the ceiling and the upgrade
path.

When a decision the design did not anticipate comes up, do not invent an answer silently. Say what you
found, what you chose, and why, and add it to `.scratch/server-side-ingest/map.md` so the next person
inherits the reasoning rather than the result.

## How you will know it works

The end state is an encoder pointed at one address with a name in its stream identifier, appearing in
the client without anything having been requested, showing a preview that visibly updates, playable
live and rewindable by a few seconds, recordable and snapshottable from the client with both landing
in documents, with the recording continuing after the client is closed, and with the tile disappearing
when the feed stops and staying put through a brief interruption.

The Compose sender is the fastest way to exercise all of it, once you have changed it to stop
requesting a session first and just push.
