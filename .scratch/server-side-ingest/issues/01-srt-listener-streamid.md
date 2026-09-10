# SRT listener: streamid and concurrent connections in libav

Type: research
Status: resolved

## Question

Can one always-on SRT listener, opened through the libav that ships with this repository, accept
many concurrent senders and tell us the streamid each one presented?

The whole automatic ingest path rests on this. If a listener opened as
`srt://0.0.0.0:9000?mode=listener` accepts exactly one connection and hands back no streamid, then
streams cannot name themselves and cannot share a port, and the design falls back to either a pool
of ports or calling libsrt directly rather than through libav's protocol layer.

Establish:

- Whether libav's SRT protocol exposes the caller's streamid to the application at all, and through
  which option or callback.
- Whether a single listener socket can serve multiple simultaneous callers, or whether libav's
  wrapper is one connection per open.
- What libsrt offers directly that libav's wrapper does not, in particular the listener callback
  that fires before a connection is accepted and carries the streamid.
- What it would cost to bypass libav for the accept path and keep it for demultiplexing, given this
  project already loads native libraries by explicit path.

The repository pins FFmpeg 8.1 shared builds with libsrt, fetched by `scripts/fetch-ffmpeg.sh` and
downloaded in `docker/Dockerfile`. Bindings are FFmpeg.AutoGen 8.0.0.1.

## Answer

**Yes. One port, many streams, each named by its streamid, works with exactly what is bundled — but
only in one shape: an accept carousel, with the name read out of libav's log stream.** Proven
empirically on the bundled Windows build; the Linux build in the container behaves identically.

The design does *not* fall back to a port pool, and it does *not* need libsrt via P/Invoke — which
is fortunate, because P/Invoke to libsrt is impossible here without shipping a new binary (see 4).

### 1. libav does read the caller's streamid, but only logs it

`libsrt_listen()` in FFmpeg 8.1 reads `SRTO_STREAMID` off the freshly accepted socket and does
nothing with it but log:

```c
ret = srt_accept(fd, NULL, NULL);
...
if (!libsrt_getsockopt(h, ret, SRTO_STREAMID, "SRTO_STREAMID", streamid, &streamid_len))
    av_log(h, AV_LOG_VERBOSE, "accept streamid [%s], length %d\n", streamid, streamid_len);
```

— `libavformat/libsrt.c` lines 239–268, tag `n8.1`
(<https://github.com/FFmpeg/FFmpeg/blob/n8.1/libavformat/libsrt.c>).

The value is **never written back into the context**. The `streamid` / `srt_streamid` AVOption is
set-only, used at line 361 to send the id when *we* are the caller; `av_opt_get` after a listener
open returns what we put in, not what the peer sent. There is no other accessor: the `SRTSOCKET`
lives in the protocol's private `SRTContext`, which is not public API.

So the only application-visible channel is that log line. That is usable and more robust than it
sounds:

- `av_vlog()` hands *every* message to a custom callback and does the level filtering **inside
  `av_log_default_callback`** (`libavutil/log.c`: `av_vlog` at line 460, the `level > av_log_level`
  test at line 396). A custom callback therefore sees the `AV_LOG_VERBOSE` streamid line **without
  raising the global log level**, so nothing else gets noisier.
- The line is emitted on **every** accept, including unnamed callers. *(empirical: a caller with no
  streamid produced `accept streamid [], length 0`.)* Absent line = capture broken; empty brackets =
  unnamed sender. Those two failures are distinguishable, which matters for policy.
- Correlation is easy: the protocol open runs on the thread calling `avformat_open_input`, so a
  `[ThreadStatic]` slot filled by the callback and read immediately after the open returns is
  unambiguous. (The callback's `avcl` is the `URLContext*`, a second correlation key if wanted.)
- `av_log_set_callback` and `av_log_format_line2` are both present in FFmpeg.AutoGen 8.0.0.1
  *(empirical: reflected over `FFmpeg.AutoGen.Abstractions.dll` 8.0.0.1)*.

It is still string-matching an internal log literal. Mitigation: a startup self-test that opens a
loopback listener, calls it with a known streamid, and fails fast if the name does not come back.
An FFmpeg upgrade then breaks at boot, not silently in production.

### 2. One `avformat_open_input` = exactly one connection

`libsrt_listen()` calls `srt_listen(fd, 1)` — **backlog of one** — then a single `srt_accept()`, and
the caller at line 458 does `srt_close(fd)` on the *listening* socket the moment accept returns.
The `// multi-client` comment above that call is aspirational; there is no loop.

*(empirical: listener on 9100, caller A with `streamid=camera-A` connected and streamed; caller B
arriving 4 s later got `Connection to srt://…streamid=camera-B failed: I/O error`. Two listener
processes on one port: the second died with WSAEADDRINUSE (−10048).)*

### 3. …but re-opening the listener after each accept gives concurrency on one port

Because libsrt multiplexes SRT sockets over a **shared UDP multiplexer per bound port within a
process**, and `libsrt_listen` sets `SRTO_REUSEADDR`, a fresh `avformat_open_input` on the same
listener URL rebinds the same port immediately while previously accepted connections keep running.

*(empirical, in-process harness P/Invoking the bundled `avformat-62.dll`: a loop of
`avformat_open_input("srt://0.0.0.0:9105?mode=listener")`, each accepted context handed to its own
reader thread, the loop re-opening at once. Three callers — `cam-alpha`, `cam-bravo`, `cam-charlie` —
were all accepted and ran concurrently on one port, peak 3 live, each with its own
`AVFormatContext`. Re-listen latency after each accept was under 1 ms.)*

That is the design the map wants: one always-on port, many streams, each named.

**Its two real limits, both measured:**

- **Burst loss.** With `backlog = 1`, connections that finish the handshake while another sits
  unaccepted are destroyed when libav closes the listening socket. *(empirical: five callers fired
  simultaneously at one port — four were accepted with their streamids; the fifth completed its
  handshake, then failed with `Error submitting a packet to the muxer: I/O error`.)* Accept
  throughput was ~350–460 ms per connection (SRT handshake bound), so ~2–3 accepts/second. Senders
  must reconnect — `docker/srt-sender.sh` already loops, and real encoders retry — but the design
  should say so, and a reconnect grace period (settled item 3) absorbs it.
- **No pre-accept rejection.** The name arrives *after* the connection exists. Refusing a stream by
  name means accepting it and then closing it. This bites the deferred auth decision (settled item
  10): a token inside the streamid can be validated only post-accept, whereas an SRT passphrase is
  enforced by libsrt during the handshake. Passphrase is the stronger of the two candidates for this
  reason.

### 4. What libsrt offers natively, and why reaching for it costs more than it looks

libsrt has exactly the API the wrapper lacks:

```c
int srt_listen_callback(SRTSOCKET lsn, srt_listen_callback_fn* hook_fn, void* hook_opaque);
typedef int srt_listen_callback_fn(void* opaque, SRTSOCKET ns, int hs_version,
             const struct sockaddr* peeraddr, const char* streamid);
```

It fires after the conclusion handshake and **before** `srt_accept` returns the socket, receives the
peer's `SRTO_STREAMID` as a typed argument, and returning −1 rejects the connection silently. One
listening socket serves unlimited sequential `srt_accept` calls, with a real backlog.
(<https://github.com/Haivision/srt/blob/master/docs/API/API-functions.md>)

**But it is not reachable from here.** libsrt is *statically linked into libavformat* in both
bundled builds and its symbols are not re-exported:

- Windows: `ffmpeg/win-x64/` contains only `av*.dll`/`sw*.dll` — no `srt.dll`. `scripts/fetch-ffmpeg.sh`
  copies every `bin/*.dll` from the BtbN `win64-gpl-shared` archive, so if one existed it would be
  there. *(empirical: `LoadLibraryEx` on `avformat-62.dll` succeeds and `GetProcAddress` resolves
  `avformat_open_input` and `avio_open2`, while `srt_startup`, `srt_socket`, `srt_accept`,
  `srt_listen`, `srt_setsockopt`, `srt_getsockopt`, `srt_create_socket` and `srt_listen_callback`
  all return NULL.)*
- Linux: no `libsrt*.so` anywhere in the image and no libsrt entry in `ldd libavformat.so.62`
  *(empirical, in the running `api` container)*.

So going direct costs, concretely:

1. **A new native binary per platform** — `srt.dll` + `libsrt.so`, built and vendored alongside the
   FFmpeg fetch, plus OpenSSL (`libcrypto`) unless encryption is compiled out. Compiling it out
   forecloses the SRT passphrase, which §3 just argued is the better auth answer.
2. **A custom `AVIOContext`.** libav has no "adopt this SRTSOCKET" entry point, so an accepted socket
   cannot be handed to `avformat_open_input`. Each connection needs `avio_alloc_context` with a read
   callback pumping `srt_recvmsg`, then `avformat_open_input` with `pb` pre-set and
   `AVFMT_FLAG_CUSTOM_IO`. Both `avio_alloc_context` and `avio_alloc_context_read_packet` exist in
   FFmpeg.AutoGen 8.0.0.1, so this is ordinary work — roughly a day — not exotic.
3. **A second SRT stack in the process.** libsrt's `srt_startup()` and its epoll/GC threads running
   beside the copy already inside libavformat. Two independently versioned SRT implementations on
   one UDP port is a real operational hazard, not a theoretical one.

Item 1 alone outweighs the benefit while the carousel holds. The pattern this project already uses
for native loading (`FfmpegLibrary.cs` → `DynamicallyLoadedBindings.LibrariesPath = Ffmpeg.Directory`)
would extend to a bundled libsrt cleanly, so the *mechanism* is not the objection — the extra
vendored binary and the duplicate SRT stack are.

### Recommendation

Build the accept carousel on the bundled libav. One listener URL, a loop that re-opens after every
accept, a global `av_log` callback capturing `accept streamid [...]` into a thread-local read
immediately after each open, and one demux pipeline per accepted `AVFormatContext`.

Write into the design as known limits: burst arrivals beyond ~2–3/second are refused and must be
retried by the sender; a stream cannot be rejected before it is accepted, so streamid-carried
tokens are post-hoc and an SRT passphrase is the stronger auth candidate. Guard the log-scrape with
a boot-time self-test.

Revisit direct libsrt only if pre-accept rejection becomes a hard requirement, or if measured burst
loss hurts. Both are one-way doors away from a single-binary deployment, and neither is a problem
yet.

### Evidence index

Primary sources:

- FFmpeg 8.1 `libavformat/libsrt.c` — <https://github.com/FFmpeg/FFmpeg/blob/n8.1/libavformat/libsrt.c>
- FFmpeg 8.1 `libavutil/log.c` — <https://github.com/FFmpeg/FFmpeg/blob/n8.1/libavutil/log.c>
- libsrt API functions (`srt_listen_callback`, `srt_accept`) — <https://github.com/Haivision/srt/blob/master/docs/API/API-functions.md>

Empirical, run against `ffmpeg/win-x64/` (FFmpeg 8.1, BtbN win64-gpl-shared) and the `api` container's
`/app/ffmpeg/linux-x64`:

- `ffmpeg -h protocol=srt` lists `streamid`/`srt_streamid` described as what "an Initiator can pass to
  a Responder" — a send-side option; no receive-side counterpart exists.
- Listener + caller, Windows and Linux: `accept streamid [camera-A], length 8` /
  `accept streamid [linux-cam-1], length 11` at verbose level.
- Second concurrent caller against a single listener open: I/O error. Second listener process on the
  same port: WSAEADDRINUSE.
- In-process carousel: 3 concurrent named streams on one port; 5-way burst: 4 accepted, 1 dropped.
- `GetProcAddress` for every `srt_*` symbol on `avformat-62.dll`: NULL.

No repository source file was modified; the harnesses live in the session scratchpad.
