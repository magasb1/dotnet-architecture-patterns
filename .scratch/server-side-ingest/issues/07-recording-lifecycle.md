# Recording from request to document

Type: grilling
Status: resolved
Blocked by: 06

## Question

What happens between someone asking for a recording and a document existing?

Decide:

- What a recording promises about its pre-roll, given the buffer cuts at a segment boundary at or
  before the requested point and a coarse sender makes those boundaries far apart. A recording may
  begin considerably earlier than five seconds back, which is harmless, or the ceiling may have left
  less history than asked for, which is not.
- Who is allowed to trigger one. A person pressing record and a detector firing are the same
  operation, and if that is true from the start then detection needs no new path when it arrives.
- What starting and stopping look like as an operation, and what the caller gets back immediately.
- Where bytes go while the recording runs. A pod's disk is ephemeral and a long recording may
  outlive the space for it, so writing locally and uploading at the end is one answer and streaming
  straight into object storage is another.
- When the document appears. At the end is simplest, but a recording running for hours is invisible
  until then.
- What a recording does when the feed drops inside the grace period, and what it does when the
  stream is finally gone.
- What a recording contains across an interruption, since it keeps running through the grace period
  and records the silence, and what a player later makes of that gap.
- What happens when another replica takes the name mid-recording. The old owner closes its recording
  as a complete document, so one stream can produce several, and they need names that say so.
- What happens when the owning replica dies mid-recording, given a hand-off is explicitly not being
  built yet.
- Whether more than one recording of the same stream can run at once, and what names they get.

A recording is a durable capture on its way to becoming a document. It is not what a viewer scrolls
back through, since rollback always reads the buffer and is bounded to that window.

Every recording begins in the buffer. A trigger at time T yields a capture starting before T and
running past it, about five seconds either side, so an event already under way when it was noticed is
still caught. The driving scenario is an automatic one: something is detected, recording starts, and
the moments before the detection are the ones that matter.

That makes two shapes to decide between, or to support side by side:

- An open-ended recording, started with a pre-roll and ended when someone asks.
- A bounded clip around a trigger, started and ended by the system with no second call.

Decide which exist, and whether the second is simply the first with a duration attached.

The constraint that drives all of this: a recording continues whether or not any client is
connected. Closing the client must not stop it.

## Answer

**One shape, with an optional duration.** A clip is a recording whose stop time was set when it
started. Two operations for one thing would drift apart, and it keeps a detector firing and a person
pressing record as the same caller on the same path, which is what makes detection free to add later.

**Starting returns immediately** with the recording's identity and state. The recording itself runs
in the owning replica, attached to the hub as a packet subscriber, and has no further relationship
with whoever asked for it. Closing the client, losing the client, or never having had one changes
nothing.

**The pre-roll is a floor, not a figure.** It begins at the segment boundary at or before the
requested point, so a recording routinely starts earlier than asked, by as much as one keyframe
interval. That is harmless and should be documented rather than corrected. The case that is not
harmless is the buffer's byte ceiling having left less history than the window promised, which is why
that condition has to be observable.

**Bytes go to a local file and are uploaded through the ordinary storage path when the recording
ends.** Both storage providers stay equal, which is the point of the abstraction: streaming straight
into object storage would mean multipart upload, which S3 has and the filesystem provider does not.

The limits are accepted rather than hidden. A recording is bounded by the pod's disk, and losing the
pod loses the recording. A configured maximum duration makes it fail predictably instead of filling a
disk; on reaching it the recording closes as a complete document, and a still-firing trigger simply
starts the next one.

The upgrade path is uploading completed pieces as the recording runs, so a death costs only the last
piece. That is the same problem as the replica hand-off, since both are a recording outliving one
pod, and they should be picked up together.

**The document appears at the end.** A document is a stored file someone can download, and a
half-written one that cannot be downloaded is a lie the storage abstraction would have to carry
everywhere. The visibility gap is real and belongs to the stream: a stream reports that it is being
recorded and for how long, and a document appears when there is a file.

**One recording at a time per stream.** A trigger arriving while one runs extends its end rather than
starting a second. Continuous detection then produces one clip covering the whole event instead of a
drift of overlapping near-duplicates, which is what anyone actually wants out of it. Someone who
wants a separate file stops and starts.

**Recordings are named by stream and start time.** That also covers the case where one stream leaves
several documents behind, which happens whenever a name is taken over mid-recording or a maximum
duration is reached.

**An interruption does not end a recording.** It keeps running through the grace period and records
the silence, carrying the discontinuity as a gap in presentation time. If the encoder was
reconfigured while it was away and comes back with a different layout, the recording closes as a
complete document instead, because a file whose codec configuration changes halfway is not reliably
playable.

**When the stream is finally gone the recording closes as a complete document.** The same happens
when another replica takes the name: the displaced owner closes what it has rather than discarding
it. The only case that loses data is the owning replica dying, which is the accepted limit above.
