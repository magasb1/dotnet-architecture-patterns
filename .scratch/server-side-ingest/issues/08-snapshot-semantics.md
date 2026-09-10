# Snapshot semantics

Type: grilling
Status: resolved
Blocked by: 04

## Question

What exactly is a snapshot, given the harvester is already decoding pictures continuously?

Decide:

- Whether a snapshot is the harvester's most recent frame handed over as it is, or a fresh decode at
  the moment of the request.
- How stale the most recent frame is allowed to be before that answer stops being honest.
- What resolution and format it is stored at, and whether that differs from the preview the tile
  shows.
- How it is named, given recordings are named by stream and start time and a snapshot from the same
  stream should be recognisable beside them.
- Whether a detector firing takes a snapshot as readily as it starts a recording, since triggers are
  already one caller for recordings and there is no obvious reason for snapshots to differ.
- What metadata rides with it: which stream, what moment, and how that moment is expressed for a
  live feed with no beginning.
- Whether a snapshot of a stream that has just ended is served or refused.

The analyzer this service already has offers only a poster frame, "the picture N seconds in", which
is a different question from "the picture right now" and is exactly what froze the old preview. If a
snapshot is the latest frame, something has to be able to answer that.

The result is a document, so it goes through the same upload path as any other file.

## Answer

**A snapshot is a fresh decode, not the harvester's frame.** The newest segment in the buffer is
decoded forward and its last picture is kept. The harvester decodes keyframes only, at the rate the
preview needs, so what it holds is stale by up to a keyframe interval plus the preview cadence: about
three seconds for a fine sender and about twelve for a coarse one. A snapshot is a deliberate act
performed once, and one decode is nothing next to being twelve seconds wrong about the moment
somebody meant to capture.

This is the latest-frame capability the analyzer does not have today, and it is the same gap the
sampler diagnosis identified. The existing analyzer answers only "the picture N seconds in", which is
a poster frame. Both the snapshot path and the preview path need the other verb.

**Full source resolution, JPEG at high quality.** The preview stays small because it is a tile in a
grid. A snapshot is a document someone opens, and sometimes the only surviving evidence of an event,
so it should not be a thumbnail. A lossless format is worth exposing as a setting if snapshots ever
need to be evidence-grade, but the size is not worth paying by default.

**Named by stream and wall-clock time of capture**, matching how recordings are named, so a
snapshot and a recording of the same moment sit together and read as related. A live feed has no
beginning, so an offset into it means nothing to a person, and a presentation timestamp is meaningful
only inside that one feed. The presentation timestamp still rides along in metadata for anyone
correlating a snapshot against a recording, but it is not what the file is called.

**Taken by the same callers as a recording.** A person pressing the button and a detector firing are
one operation, exactly as they are for recordings. There is no reason for the two to differ, and
keeping them the same means detection needs no new path for either when it arrives.

**Available while a stream is interrupted, refused once it is gone.** An interrupted stream is still
claimed and its buffer still holds the seconds before the feed stopped, so a snapshot returns the
last picture that arrived. Once the grace period has expired there is no stream and no buffer, and
the request fails. That reads correctly from the outside: while the tile is still there and marked
interrupted, the button still works.
