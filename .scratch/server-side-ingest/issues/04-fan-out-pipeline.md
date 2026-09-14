# The fan-out pipeline

Type: grilling
Status: resolved
Blocked by: 01, 03

## Question

What is the shape of one stream's pipeline in process, from packets arriving to consumers that come
and go?

Decide:

- What a consumer is, and how one attaches and detaches while packets are flowing.
- What each consumer receives. The harvester needs demultiplexed streams rather than only decoded
  pictures, because KLV rides as its own stream and detection later wants frames.
- What happens when a consumer cannot keep up. A viewer on a slow connection must not stall ingest
  or the recorder.
- Where decoding happens, and whether one decode is shared by every consumer that wants pictures or
  each does its own.
- Where the seam sits that detection, tracking and KLV extraction attach to later, and how much of
  it has to exist now.
- What the equivalent is for a manually created stream, which arrives the same way once its listener
  or caller is up.

This is the core of the design. Buffering, recording, snapshots and playback are all downstream of
it.

## Answer

One stream is one demultiplexer feeding one hub. The hub owns a rolling buffer and a set of
subscribers, and packets flow one way through it. There are two tiers.

**The packet tier is the fan-out.** Subscribers receive demultiplexed packets filtered by stream
index, with no decoding anywhere near them. The recorder, the viewer and a future KLV extractor all
live here, which is what keeps a stream cheap when nobody is watching. KLV needs no special path: it
is a subscriber on a different stream index.

**The frame tier hangs off it.** A single decoder is one packet subscriber, and it publishes frames
to its own subscribers. Exactly one decode exists however many things want pictures, and consumers
that only move bytes never pay for it. It decodes at the highest rate its subscribers ask for, which
today is keyframes only, because the preview wants one picture every couple of seconds. Detection
later asks for more and pays for it explicitly rather than getting it by default.

Those two tiers are the seam that detection, tracking and KLV extraction attach to. Nothing further
needs to exist now.

**The rolling buffer belongs to the hub, not to a consumer.** It holds ten to thirty seconds and does
three jobs at once: it gives a joining viewer a position a decoder can actually start from, it is the
rollback window, and it is what a recording reaches back into when someone presses record. One
mechanism, three needs, which is why it sits in the hub rather than beside it.

**A viewer is always live unless the user deliberately moved back inside the window.** Those are
different states and the distinction is the whole rule. Intentional rollback is a chosen position.
Unintentional lag is a fault, and the answer is to skip forward to the newest startable position
rather than to keep falling behind. A viewer never accumulates delay.

**A recorder overflowing is not the same event.** Its queue is larger, and exhausting it is a hard
failure that ends the recording and marks the document truncated. Dropping packets to keep going
would write a hole into a document that claims to be a recording, and silence is worse than
stopping.

**Each consumer that writes bytes owns its own muxer**, created when it attaches and closed when it
detaches, with its own timestamp base. A recording started at ten past and a viewer who joined at
twelve past have different timelines and different first packets, so a shared muxer would force both
to inherit whichever attached first.

**Rollback is never served from the recording.** The recording is a durable capture on its way to
becoming a document, not a scrub timeline. Rollback reads the buffer whether or not anything is
recording, which bounds it to the buffer window and keeps random access out of the packet path.

**A manually created stream is identical once a demultiplexer exists.** The only difference is how
the input was opened, and that difference ends before the hub begins.
