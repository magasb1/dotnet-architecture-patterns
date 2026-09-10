# The rolling buffer and live playback

Type: grilling
Status: resolved
Blocked by: 04

## Question

What does the service keep of a stream nobody is recording, and where does a viewer's bytes come
from?

Decide:

- How ten to thirty seconds is held when the measure that matters is time and the thing being stored
  is bytes at a bitrate nobody declared in advance. This window is settled; how to honour it is not.
- Whether it lives in memory or on disk, and what that costs per stream when several run at once on
  one replica.
- What a viewer joining receives first. A decoder needs a table and a keyframe before it can show
  anything, so the buffer has to hand back a startable point rather than the newest byte.
- How the buffer guarantees the pre-roll a recording is promised. The window has to be comfortably
  deeper than the largest pre-roll, or a trigger arriving during a lean patch silently gets less
  history than it asked for.
- Where a pre-roll actually begins. A cut at exactly five seconds back is not startable, so the real
  answer is the last startable position at or before that point, which makes the pre-roll a floor
  rather than an exact figure.
- How a viewer moves to a position inside the window and back to live, given the buffer must serve
  arbitrary startable positions within it rather than only its newest byte.
- What happens to the buffer when no consumer has wanted anything for a long time.
- What the buffer holds through a thirty second interruption, which it now has to survive, and
  whether a resumed feed can simply be appended to what was already there.

Settled in `The fan-out pipeline`: the buffer belongs to the hub rather than being a consumer, it is
ten to thirty seconds deep, and it does three jobs at once. It is the join point for a new viewer,
the rollback window, and what a recording reaches back into. It is never the scrub timeline for a
long recording, which is a durable capture and a different thing.

How those bytes travel and which port carries them is decided in `How a viewer consumes a stream`.
This ticket is about what is kept and what a joining viewer needs, not about the transport.

Related work already done: live playback currently starts at a computed live edge rather than at the
beginning, in `LiveStreamManager.LiveEdge`. The reasoning there about packet alignment and needing a
few seconds of backlog carries over even though the file it reads from does not.

## Answer

**The buffer lives in memory.** It is recent history for a live pipeline and it dies with the stream
regardless, so disk buys no durability: a restart loses the connection too. The cost is bounded
rather than growing, roughly six megabytes for a modest feed and seventy-five for a contribution one
at thirty seconds.

**It is bounded by time and by bytes, whichever binds first.** Seconds are what the product promises,
and a per-stream byte ceiling is what stops one careless encoder evicting the service. When the
ceiling binds, the window is shorter than promised, and that has to surface as an observable
condition rather than quietly shrinking. A pre-roll that silently gets less history than it asked for
is the one failure the recording feature cannot absorb, because nobody finds out until they watch the
document and the event is missing.

**It holds segments, not a flat ring of packets.** Each segment begins at a position a decoder can
start from and runs to the next one. Every job the buffer does asks the same question, and segments
answer it once: joining a viewer, rolling back, and cutting a pre-roll all become choosing a segment,
and eviction becomes dropping the oldest.

The honest cost is granularity. The window moves in segment-sized steps, so the promise is at least
the requested seconds rather than exactly them, and a recording's pre-roll starts at the segment
boundary at or before the requested point rather than at the point itself.

Segment length is not ours to choose. It is whatever the sender's keyframe interval is, so an encoder
with a ten second interval yields a window of two or three coarse segments while a one second
interval yields thirty fine ones. Two consequences follow. The byte ceiling has to be forgiving
enough to hold at least a couple of segments from a coarse sender, and a feed that goes a long way
with no startable point at all needs a fallback, because a buffer with one open-ended segment can
serve nothing.

**Buffering is unconditional.** A stream nobody is watching and nobody is recording still buffers,
because a trigger can arrive at any moment and the pre-roll is the entire reason the detection
scenario works. Retaining packets costs no decode, which is what makes always-on affordable.

**A resumed feed appends when its layout matches**, as a new segment boundary, and a recording in
progress carries the discontinuity as a gap. When the layout has changed, because the encoder was
reconfigured while it was away, the recording closes as a complete document and the buffer starts
fresh. A file whose codec configuration changes halfway is not something anything will reliably play.
