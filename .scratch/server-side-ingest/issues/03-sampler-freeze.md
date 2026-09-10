# Why the preview sampler stops after the first frames

Type: research
Status: resolved

## Question

The running service produces one or two preview frames for a live stream and then serves the same
JPEG forever, while the recording keeps growing and the session stays active.

This matters beyond the component being replaced. If the cause sits in the analyzer, in how libav is
driven, or in how a file being written is read, the harvester inherits it. If it sits in the sampler
loop or in what the endpoint serves, it dies with the old design.

What is already known:

- The recording grows steadily, confirmed by watching its size inside the container.
- The analyzer is not at fault. Two tails of the same growing recording, taken eight seconds apart
  and sampled exactly as the sampler does, produce different pictures. This is pinned by
  `tests/StorageDemo.Tests/Infrastructure/LivePreviewSamplingTests.cs`.
- Decoding activity continues in the container after the preview has frozen.
- The served bytes stay byte-identical for long stretches and then change once, so it is not fully
  stopped.
- The failure is invisible in logs because the only message on the no-frame path is at debug level.

Find the cause. The relevant code is `SampleThumbnailAsync` and `StartThumbnailSampling` in
`src/StorageDemo.Infrastructure/Streaming/LiveStreamManager.cs`, the `Thumbnail` endpoint in
`src/StorageDemo.Api/Controllers/LiveStreamsController.cs`, and the analyzer in
`src/StorageDemo.Infrastructure/Media/LibavMediaAnalyzer.cs`. A stack running under Docker Compose
with a live SRT test pattern reproduces it.

Answer with the mechanism, and with whether the new harvester would inherit it.

## Answer

### Mechanism

The sampler asks for a poster frame and calls it a preview.

`SampleThumbnailAsync` hands the analyzer a byte window: the last `ThumbnailTailBytes` (4 MB) of the
recording. `LibavMediaAnalyzer.TryRenderThumbnail` then answers the only question that interface can
ask - "the frame `VideoFrameSeconds` (3 s) into this media" - and `Seek` computes that as an
**absolute** timestamp, `VideoFrameSeconds / time_base`, with no reference to the window's own
`start_time`. Two regimes follow, and between them they produce the reported symptom exactly.

**While the recording is smaller than the tail window**, the window is always `[0, N]`. Its start is
pinned at the head of the stream no matter how much `N` grows, so the absolute 3 s target resolves
to the *same frame* on every pass. The preview is byte-identical, pass after pass, for as long as it
takes the recording to reach 4 MB. That is the freeze. Its length is `ThumbnailTailBytes / bitrate`:
47 s on the test pattern here, ~67 s at 500 kbit/s, ~2.8 minutes at 200 kbit/s - "minutes", on any
ordinary feed.

The "one or two updates before it sticks" are the run-up: the first pass at 16 KB is below the
minimum and returns nothing, the next window is shorter than 3 s so the `seekable` guard skips the
seek and decoding starts at frame zero, and only once the window exceeds 3 s does the seek land on
the 3 s frame - where it then stays.

**Once the recording outgrows the window**, the window becomes `[N-4MB, N]` and its timestamps start
at a large PTS. Now the absolute 3 s target is far *below* everything in the window, so
`av_seek_frame(..., AVSEEK_FLAG_BACKWARD)` clamps to the window's first frame. Raising the target
does not help either: above the window's span the `seekable` guard (`format->duration` is a span,
compared against an absolute target) skips the seek entirely and decoding again starts at the
window's first frame. Both roads end on the **oldest** frame in the window. The preview does start
changing at this point - which is why it is "frozen, then it changes once" rather than dead - but it
is permanently one window-duration (~47 s) behind the live edge. The end of the window, the part
that is actually live, is never decoded at all.

Nothing is wrong with the loop, the cancellation token, `SetThumbnail`, the `Thumbnail` accessor or
the proxy path. The loop runs, the analyzer succeeds, and it faithfully stores a picture that is by
construction the same one it stored last time.

### Evidence

**Live, end to end.** Fresh ingest on the compose stack, polling `GET /api/live/{id}/thumbnail`
every 3 s against the recording size read inside the container:

```
20:40:15 size=262636   sha=c88e2bdf4d
20:40:19 size=565316   sha=c88e2bdf4d
20:40:23 size=880028   sha=0b6c2ee72d
   ... 12 consecutive polls, byte-identical, recording 0.88 MB -> 4.51 MB ...
20:41:07 size=4508804  sha=0b6c2ee72d
20:41:11 size=4813364  sha=4573f801f6   <- recording passes ThumbnailTailBytes (4 MB)
20:41:18 size=5435268  sha=b253758249
20:41:22 size=5737760  sha=4e10b88ec6   ... changes on nearly every pass from here
```

Two updates, then 44 s byte-identical, then it starts advancing at precisely the byte the window
starts sliding. Session `Active`, recording growing ~85 KB/s throughout. The response carries no
`Cache-Control`, so these are the server's own bytes, not a cache.

**Offline, deterministic, 3 s.** `tests/StorageDemo.Tests/Infrastructure/LivePreviewFreezeTests.cs`
replays the sampler over prefixes of a captured recording - a prefix of a file being appended to is
exactly what that file looked like earlier - so the whole thing reproduces with no clock and no
stream. Point it at a recording with `LIVE_RECORDING=<path to a .ts of 24 MB or more>`; without that
variable the file skips and the suite stays green.

- `Preview_advances_while_the_recording_is_smaller_than_the_tail_window` - **red**:
  `1MB=0B6C2EE72D12, 2MB=0B6C2EE72D12, 3MB=0B6C2EE72D12, 4MB=0B6C2EE72D12`. The same digest the live
  stack froze on, from the same recording. This is the regression test; it stays red until fixed.
- `Preview_frame_is_fixed_by_the_window_start_not_the_live_edge` - passes. Windows `[16MB,20MB]` and
  `[16MB,17MB]` yield the *same* JPEG; `[19MB,20MB]` yields a different one. The picture is decided
  by where the window starts. The live edge is not read.
- `An_absolute_seek_steers_only_in_a_window_that_starts_at_the_stream_start` - passes. Targets of
  1, 3, 10, 30, 60, 120, 180, 240, 300 s over the window `[0,4MB]` produce several distinct
  pictures; the identical scan over `[16MB,20MB]` collapses all nine onto one digest. The seek is
  absolute, and it is dead in any window that does not begin at the start of the stream.
- `Targeting_the_end_of_the_window_advances_the_preview` - passes. The same four growing windows
  that froze above produce four distinct pictures when the target is aimed near the window's end.

### The fix

Two defects, and they need separate fixes - the first alone is what unfreezes the preview.

**1. The preview must ask for the newest frame, not a poster frame.** `IMediaAnalyzer.AnalyzeAsync`
can only express "the frame N seconds in", which is the right question for a document and the wrong
one for a live tile. Give the analyzer a latest-frame mode - decode forward through the window
keeping the most recent frame and encode that (an MPEG-TS window has no index worth seeking, and
4 MB decodes in well under the sampling interval) - or, if a seek is preferred, target
`stream->start_time + stream duration - VideoFrameSeconds` with `AVSEEK_FLAG_BACKWARD`. Proven by
`Targeting_the_end_of_the_window_advances_the_preview`.

**2. `LibavMediaAnalyzer.Seek` must be start-relative.** Line 418, as shipped:

```csharp
var timestamp = (long)(_options.VideoFrameSeconds / ffmpeg.av_q2d(stream->time_base));
```

should be:

```csharp
var start = stream->start_time != ffmpeg.AV_NOPTS_VALUE ? stream->start_time : 0;
var timestamp = start + (long)(_options.VideoFrameSeconds / ffmpeg.av_q2d(stream->time_base));
```

The `seekable` guard on line 360 compares `format->duration`, a span, against that target; once the
target is start-relative that comparison is correct as written. This does *not* fix the freeze on
its own - while the recording is smaller than the tail the window starts at the stream start, so
start-relative and absolute are the same number - but it is a real, silent defect on its own account:
any media whose timestamps do not begin at zero currently gets its poster frame taken from frame zero
instead of 3 s in, and no caller can tell.

**3. Minor, in the endpoint.** `LiveStreamsController.Thumbnail` returns `File(local, "image/jpeg")`
on the local path with no cache headers, while the proxy path below it sets `Cache-Control:
no-store`. Verified: the response carries only `X-Content-Type-Options`. Not a cause - curl sees the
server's own bytes freeze - but a browser is free to keep showing a stale tile after the fix lands.
Set `no-store` on both branches.

**And make it audible.** The one clue on the failure path, `"Live preview found no frame in {Bytes}
bytes"`, is at `Debug` and never fired here anyway: the analyzer *succeeded* every pass. Nothing in
the system knows or reports how old the picture it is serving is. Whatever replaces this should
carry the age of the newest decoded frame as a first-class value, so a stalled preview is a number
someone can see rather than a picture someone has to stare at.

### Inheritance verdict

**No. The freeze dies with the old design.** Every load-bearing element of the mechanism is an
artefact of re-reading a byte window of a file: the window whose start pins the picture, the
per-pass `avformat_open_input` on a fragment with no context, the `format->duration` that is a span
rather than a position, and the seek that has to guess where in the stream it has landed. A
harvester that decodes from a live in-memory packet fan-out has none of them. It holds one
long-lived decoder, receives packets in order, and never seeks; the frame it holds is the most
recent one it decoded, by construction. There is no way to express this failure in that design.

**One part does transfer, and it is not the sampler.** Defect 2 lives in `LibavMediaAnalyzer`, which
the new pipeline will still use the moment a snapshot or a recording becomes a document and wants a
thumbnail. A recording harvested from a rolling buffer of a live stream begins at a large PTS, so
*every* such document would get its poster frame silently taken from its first frame rather than 3 s
in. Fix `Seek` in the analyzer regardless of which sampler survives - it is three lines and it is
inherited.

The wider lesson for the harvester's seam: the fault was not a bug in a computation, it was a
question asked of the wrong interface. "The frame 3 seconds in" and "the picture right now" are
different questions, and reusing the document analyzer for both is what let a live preview silently
serve a fixed frame while every component involved reported success. Give the harvester its own
verb.
