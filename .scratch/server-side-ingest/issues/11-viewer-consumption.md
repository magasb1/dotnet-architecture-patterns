# How a viewer consumes a stream

Type: grilling
Status: resolved
Blocked by: 04

## Question

Ingest and consumption sit on separate ports, so consuming is its own surface with its own firewall
posture. What does that surface speak, and what does opening or closing it actually expose?

Decide:

- What protocol the consumption port serves. An SRT listener that a viewer calls, naming the stream
  in its streamid exactly as a sender does, is symmetric with ingest and is playable directly by the
  desktop client, which already uses a player that opens `srt://`. Serving MPEG-TS over HTTP on a
  dedicated port is the other candidate and is closest to what the client does today through the
  API.
- How a viewer says where in the window it wants to start, and how it moves back to live, given
  rollback is bounded to the buffer and a viewer must never accumulate delay by accident.
- How a viewer expresses a position, given the buffer moves in segments rather than seconds and the
  granularity depends on the sender's keyframe interval. Asking for twenty seconds back and being
  given twenty-six is the normal case, not an error.
- How a viewer names the stream it wants, given the registry is now keyed by name and playback URLs
  no longer have an identifier to embed.
- What a viewer sees during an interruption, since the stream is still there and still claimed but no
  packets are arriving.
- What happens when a viewer reaches a replica that does not own the stream. The existing answer is
  an HTTP proxy to the owning replica. A media transport needs a media relay instead, which is a
  different and more expensive thing, and it has to be decided rather than assumed.
- Whether the desktop client keeps pulling through the API or moves to the consumption port, and
  what that means for the token it sends today.
- Whether finished recordings and snapshots are also served here, or stay documents fetched the
  ordinary way. They are files by then, so there is a case for keeping them out of the media path
  entirely.
- What a deployment closes off with each port. The reason for splitting them is that ingest can face
  outward while consumption stays inside, or the reverse, so the design has to state exactly which
  capabilities each port grants to whoever can reach it.

Related: `How encoders set streamid` matters here if consumption also names streams that way. The
published SRT convention already distinguishes publishing from subscribing within the same field,
which is worth knowing even though the ports are being split rather than shared.

## Answer

**The consumption port serves MPEG-TS over HTTP.** A stream listener that a viewer calls, symmetric
with ingest, was the more elegant answer and was rejected on three concrete costs. Rollback needs a
position, and the stream identifier field has nowhere sensible to carry one. Reaching a replica that
does not own the stream is already solved for HTTP and would be a new media relay otherwise. And the
accept rate that limits ingest to a couple of connections a second would then limit viewers too.
Elegance was not worth three mechanisms.

**A stream is addressed by name**, since the registry is keyed by name and there is no identifier left
to embed in a URL.

**Position is a parameter saying how far back to start**, resolved to the segment at or before that
point. The response says what was actually given, because the buffer moves in segment-sized steps and
asking for twenty seconds and receiving twenty-six is the normal case rather than an error. Omitting
the parameter means live. Returning to live is a new request, not a control message, which keeps the
connection one-way and stateless.

**A viewer reaching a replica that does not own the stream is forwarded to the owner**, using the pod
address the owner recorded when it claimed the name. This is the mechanism that already exists.

**During an interruption the connection is held open and nothing is sent.** The client already knows
the stream is interrupted from its state and can say so. Closing would push every viewer into
reconnecting at the exact moment a reconnect storm is already under way on the ingest side.

**Live only.** Finished recordings and snapshots are documents and stay on the API, where the
existing authorisation lives. This is what makes the firewall statement short enough to be useful:
reaching the consumption port lets you watch live streams and nothing else. Reaching the ingest port
lets you push one. Reaching the API is everything else.

**The desktop client moves live playback to the consumption port** and keeps using the API for
control, documents and the stream list. The token it sends today carries over unchanged, and is
deferred along with the rest of authentication.
