# SRT latency, FFmpeg srt:// caller behaviour, and related facts

Sources pinned 2026-09-11: FFmpeg master `5b614efc`, Haivision/srt master `cae8f624`, dotnet/aspnetcore main `7768648d`, dotnet/runtime main `c5cc4512`.
Abbreviations: `libsrt.c` = https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/libsrt.c; `protocols.texi` = https://github.com/FFmpeg/FFmpeg/blob/master/doc/protocols.texi; `SRTO` = https://github.com/Haivision/srt/blob/master/docs/API/API-socket-options.md.

## 1. FFmpeg `srt` protocol options (libavformat/libsrt.c)

All FFmpeg defaults are `-1` = "not set; libsrt default applies" unless noted. FFmpeg only calls `srt_setsockopt` when the value is `>= 0` (`libsrt_set_options_pre`, libsrt.c L309-370). Time options are **microseconds** in FFmpeg and divided by 1000 before being passed to libsrt (`latency / 1000`, L314-316), so values < 1000 us become 0 ms.

| FFmpeg option | Unit (FFmpeg) | FFmpeg default | Maps to | libsrt default (SRTO doc) |
|---|---|---|---|---|
| `latency` (alias `tsbpddelay`) | microseconds | -1 (unset) | `SRTO_LATENCY` (ms) = sets both RCV+PEER latency | 120 ms live |
| `rcvlatency` | microseconds | -1 | `SRTO_RCVLATENCY` (ms) | 120 ms live, 0 file |
| `peerlatency` | microseconds | -1 | `SRTO_PEERLATENCY` (ms) | 0 |
| `maxbw` | bytes/s (int64) | -1 (unset) | `SRTO_MAXBW` (B/s), pre-connect | -1 = infinite (cap 1 Gbps) |
| `inputbw` | bytes/s | -1 | `SRTO_INPUTBW`, set **post**-connect (L297) | 0 = estimate |
| `oheadbw` | percent (max 100) | -1 | `SRTO_OHEADBW`, post-connect | 25 % |
| `tlpktdrop` | bool | -1 | `SRTO_TLPKTDROP` | true live |
| `payload_size` / `pkt_size` | bytes (max 1456; consts `ts_size`=1316, `max_size`=1456) | -1 | `SRTO_PAYLOADSIZE` | 1316 live |
| `connect_timeout` | **milliseconds** (help text L135) | -1 | `SRTO_CONNTIMEO` (ms) | 3000 ms (x10 rendezvous) |
| `timeout` | microseconds | -1 | FFmpeg-only: `h->rw_timeout` and cap on connect wait (L410-412, L236) | n/a |
| `listen_timeout` | microseconds | -1 | FFmpeg-only, listener accept wait | n/a |
| `streamid` (alias `srt_streamid`) | string <=512 | NULL | `SRTO_STREAMID` | "" |
| `passphrase` | string 10..79 | NULL | `SRTO_PASSPHRASE` | "" |
| `pbkeylen` | bytes {16,24,32} | -1 | `SRTO_PBKEYLEN` | 0 (effective 16 if neither side sets) |
| `enforced_encryption` | bool | -1 | `SRTO_ENFORCEDENCRYPTION` | true |
| `mode` | caller/listener/rendezvous | caller | FFmpeg-only (rendezvous -> `SRTO_RENDEZVOUS`) | n/a |
| `transtype` | live/file | SRTT_INVALID (unset) | `SRTO_TRANSTYPE` | SRTT_LIVE |
| `linger` | seconds | -1 | `SRTO_LINGER` (struct linger) | off in live, 180 s file |
| `peeridletimeout` | **not an FFmpeg option** (no match in libsrt.c) | – | – | `SRTO_PEERIDLETIMEO` 5000 ms |

Sources: option table libsrt.c L106-155; setsockopt mapping L309-370; SRTO doc sections `SRTO_LATENCY`, `SRTO_RCVLATENCY`, `SRTO_PEERLATENCY`, `SRTO_MAXBW`, `SRTO_INPUTBW`, `SRTO_OHEADBW`, `SRTO_TLPKTDROP`, `SRTO_PAYLOADSIZE`, `SRTO_CONNTIMEO`, `SRTO_STREAMID`, `SRTO_PASSPHRASE`, `SRTO_PBKEYLEN`, `SRTO_ENFORCEDENCRYPTION`, `SRTO_TRANSTYPE`, `SRTO_LINGER`, `SRTO_PEERIDLETIMEO`.

- **Default latency**: FFmpeg sets nothing when `latency` is absent (`s->latency >= 0` guard, L343) so libsrt's live default of **120 ms** `SRTO_RCVLATENCY` applies (SRTO `SRTO_RCVLATENCY`: "Default value: 120 ms in Live mode").
- **Doc vs code discrepancy**: protocols.texi says `maxbw` "Default value is 0 (relative)"; the code default is -1 and unset, so the effective default is libsrt's **-1 (infinite)** (SRTO `SRTO_MAXBW`: "default value of -1, regardless of the mode"). Same for `oheadbw` "Default 25%" and `inputbw` "Default 0": those are libsrt defaults, FFmpeg passes nothing.
- FFmpeg puts the socket in non-blocking mode before connecting (`libsrt_socket_nonblock`, L178-186, L452) and, for writers, sets `SRTO_SENDER=1` (L369); after connect it reads back `SRTO_PAYLOADSIZE` into `h->max_packet_size` (L503-510).

## 2. Latency negotiation and RTT guideline

- Rule (SRTO `SRTO_RCVLATENCY`): "The actual value of the receiver buffering delay `Ln` (the negotiated latency) used on a connection is determined by the negotiation in the connection establishment (handshake exchange) phase as the **maximum of the `SRTO_RCVLATENCY` value and the value of `SRTO_PEERLATENCY` set by the peer**." `SRTO_PEERLATENCY` = "provided by the sender side as a minimum value for the receiver". `SRTO_LATENCY` "sets both SRTO_RCVLATENCY and SRTO_PEERLATENCY to the same value".
- Per direction: for data flowing caller->listener, effective latency = max(listener `SRTO_RCVLATENCY`, caller `SRTO_PEERLATENCY`). For listener->caller, = max(caller `SRTO_RCVLATENCY`, listener `SRTO_PEERLATENCY`). An ffmpeg caller with `latency=N` therefore raises the listener's delivery delay to at least N even if the listener configured a lower `SRTO_RCVLATENCY`. Reading `SRTO_RCVLATENCY` on a connected socket returns the negotiated `Ln`. RFC draft: latency "negotiated as the greater of those reported by each party" (https://www.ietf.org/archive/id/draft-sharabayko-srt-01.txt, s.4.4).
- Only significant when `SRTO_TSBPDMODE` is enabled (SRTO `SRTO_RCVLATENCY`, `SRTO_PEERLATENCY`).
- RTT guideline (Haivision, https://doc.haivision.com/SRT/1.5.3/Haivision/rtt-multiplier): "SRT Latency = RTT Multiplier * RTT"; rule of thumb for 0.1-0.2 % loss "approximately 4"; table: loss <=1 % -> multiplier 3, overhead 1 %, min latency 60 ms (RTT <= 20 ms); <=3 % -> 4 / 4 % / 80 ms; <=7 % -> 6 / 9 % / 120 ms; <=10 % -> 8 / 15 % / 160 ms. The GitHub docs (`docs/features/latency.md`, `docs/features/live-streaming.md`) contain no multiplier; it is only in the Haivision product docs.

## 3. `SRTO_MAXBW` semantics and burst behaviour

- Values (SRTO `SRTO_MAXBW`): "-1: infinite (the limit in Live Mode is 1 Gbps); 0: relative to input rate (see SRTO_INPUTBW); >0: absolute limit in B/s." Default -1 in every mode.
- Relative mode formula (SRTO `SRTO_INPUTBW`): `MAXBW = INPUTBW * (100 + OHEADBW) / 100`; with `INPUTBW=0` "the real INPUTBW value will be estimated from the rate of the input (cases when the application calls the srt_send* function)", floored by `SRTO_MININPUTBW` (default 0).
- Estimator (https://github.com/Haivision/srt/blob/master/srtcore/buffer_tools.cpp `CRateEstimator::updateInputRate`, `buffer_tools.h` L116-119): starts at `INPUTRATE_INITIAL_BYTESPS = BW_INFINITE`; first sample window `INPUTRATE_FAST_START_US = 500 ms` (or early after `INPUTRATE_MAX_PACKETS = 2000` packets), then `INPUTRATE_RUNNING_US = 1000 ms` windows; rate = bytes (payload + 44 B headers) / window. Applied in `CUDT::updateCC` on ACK / LOSSREPORT / CHECKTIMER / SYNC events: `updateBandwidth(0, withOverhead(max(llMinInputBW, inputbw)))` (https://github.com/Haivision/srt/blob/master/srtcore/core.cpp L8030-8048). If the estimate is 0 (blocked sender) the previous max is kept.
- Pacing (https://github.com/Haivision/srt/blob/master/srtcore/congctl.cpp `LiveCC`): `m_dPktSndPeriod = 1e6 * pktsize / m_llSndMaxBW`; `setMaxBW(maxbw)` uses `BW_INFINITE` when `maxbw <= 0`. With `-1` a 2-second backlog is sent at up to ~1 Gbps (no effective pacing). With `maxbw=0,inputbw=0` the first ~500 ms are also unlimited, then the burst itself inflates the next estimate, after which the cap collapses to steady-rate x 1.25 - any later burst then queues in the send buffer. With `maxbw=0,inputbw=X` the cap is fixed at 1.25 X from the start, so a 2 s backlog drains in ~1.6 s and everything behind it is delayed by that much. RFC draft s.5.1.1 warns exactly this: after a rate dip "the input rate rises sharply. SRT would not start up again fast enough ... Packets might be accumulated in the SRT's sender buffer and delayed".
- Recommendations: SRTO `SRTO_MAXBW`: "For live streams it is typically recommended to set the value 0 here and rely on SRTO_INPUTBW and SRTO_OHEADBW ... make sure that your stream has a fairly constant bitrate ... therefore the default -1 remains even in live mode." SRTO `SRTO_INPUTBW`: "Recommended: set this option to the anticipated bitrate of your live stream and keep the default 25% value for SRTO_OHEADBW." RFC draft s.5.1.1: "INPUTBW_ESTIMATED mode is recommended ... However ..." (caveat above). For a join-time backlog burst, leave `maxbw=-1` or set an absolute `maxbw` well above the burst rate; do not use relative mode with a fixed `inputbw`.

## 4. `SRTO_TLPKTDROP` and `SRTO_TSBPDMODE`

- `SRTO_TSBPDMODE`: "Default: true in Live mode, false in File mode"; receiver restores sender timing and holds packets until `PTS = ETS + LATENCY` (SRTO `SRTO_TSBPDMODE`; https://github.com/Haivision/srt/blob/master/docs/features/latency.md).
- `SRTO_TLPKTDROP`: "Default: true in Live mode, false in File mode"; receiver "skips missing packets that have not been delivered in time and delivers the subsequent packets ... when their time-to-play has come", sender drops packets that "have no chance to be delivered in time" (SRTO `SRTO_TLPKTDROP`). Sender drop trigger = `SRTO_PEERLATENCY + SRTO_SNDDROPDELAY + 2 * ACK interval (10 ms)`, minimum `1000 + 2*ACK interval` ms; `SRTO_SNDDROPDELAY` default 0 live, -1 file (SRTO `SRTO_SNDDROPDELAY`).
- Effect: together they give "constant latency"; "The receiving end will not 'fall behind' in time by waiting for missing packets ... overall latency is maintained and does not increase over time" (RFC draft s.7.1, which also states both "must be enabled" for live streaming). Disabling TLPKTDROP makes latency grow with loss.

## 5. FFmpeg caller when the listener rejects (`srt_listen_callback` -> -1 / `srt_setrejectreason`)

- libsrt.c never calls `srt_getrejectreason` / `srt_rejectreason_str` (grep: no "reject" in the file), so the reject reason **does not appear** in ffmpeg output.
- Path: socket is non-blocking, so `srt_connect` returns 0 immediately (L280). FFmpeg then waits on an epoll with `SRT_EPOLL_OUT|SRT_EPOLL_ERR` (L188-199). In libsrt the rejection is processed asynchronously: `CRendezvousQueue::updateConnStatus` marks the connector `SRT_ECONNREJ`, sends `UMSG_SHUTDOWN`, sets `m_bConnecting=false` and raises `SRT_EPOLL_IN|OUT|ERR` (https://github.com/Haivision/srt/blob/master/srtcore/queue.cpp L990-1035). `srt_epoll_wait` reports ERR sockets in both fd sets (https://github.com/Haivision/srt/blob/master/srtcore/epoll.cpp L601-611), so `libsrt_network_wait_fd` returns `AVERROR(EIO)` (`ret = errlen ? AVERROR(EIO) : 0`, L219).
- Log line (libsrt.c L286-293, `libsrt_listen_connect`):
  - single address: `av_log(h, AV_LOG_ERROR, "Connection to %s failed: %s\n", h->filename, av_err2str(ret));`
  - more addrinfo entries pending: `"Connection to %s failed (%s), trying next address\n"` (WARNING), then it retries the next resolved address only (L532-538).
  `%s` = the full URL incl. query string; `av_err2str(AVERROR(EIO))` = platform `strerror(EIO)` ("Input/output error" on glibc; "Input/output error" also on MSVCRT). Assert on the prefix `Connection to srt://` + ` failed`.
- `avio_open2`/`avformat_open_input` then fails with `AVERROR(EIO)`; no reconnect logic exists in libsrt.c (no `reconnect` option). ffmpeg CLI prints `"Error opening input: %s\n"` (fftools/ffmpeg_demux.c L2393-2395, https://github.com/FFmpeg/FFmpeg/blob/master/fftools/ffmpeg_demux.c), then `"Error opening %s file %s.\n"` with `input` (fftools/ffmpeg_opt.c L1414), then `"Error %s: %s\n"` -> `Error opening input files: Input/output error` (ffmpeg_opt.c L1500) and exits non-zero. It does not retry.
- Timing: rejection surfaces after one handshake round trip (listener sends a rejection HS, caller processes it), not after `connect_timeout`. Not verified: whether libsrt's own default log handler prints its `Warn`-level "REJECT reported" line to stderr (core.cpp L4183-4187) - do not assert on it.

## 6. Caller's local UDP port on reconnect

- FFmpeg caller mode never calls `srt_bind`; each open does `srt_create_socket()` (L433) and each close does `srt_close` + `srt_cleanup` (L613-617), so nothing is reused across opens in the same or a new process.
- libsrt: `CUDTUnited::connectIn` on an unbound (`SRTS_INIT`) socket does "the same thing as bind() does, just with empty address so that the binding parameters are autoselected" -> `updateMux(s, autoselect_sa)` (https://github.com/Haivision/srt/blob/master/srtcore/api.cpp L2005-2030). `updateMux` only reuses an existing multiplexer whose port equals the requested port (L3191-3260); port 0 never matches, so a new `CMultiplexer` is created and `CChannel::open(family)` binds with `getaddrinfo(NULL, "0")` + `::bind` (https://github.com/Haivision/srt/blob/master/srtcore/channel.cpp L235-281) - i.e. the OS assigns a fresh ephemeral port per connect.
- Consequence: every ffmpeg reconnect has a new 5-tuple; a UDP load balancer hashing on source port may route the retry to a different pod. Only explicit `srt_bind` to a fixed local port (not exposed by FFmpeg's caller mode) would keep it stable.

## 7. Kestrel / HttpClient HTTP/1.1 trailers

- **Kestrel does NOT send response trailers over HTTP/1.1.** `IHttpResponseTrailersFeature` is implemented only in `Http2Stream.FeatureCollection.cs` and `Http3Stream.FeatureCollection.cs` (https://github.com/dotnet/aspnetcore/blob/main/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.FeatureCollection.cs L16-24, .../Http3/Http3Stream.FeatureCollection.cs L15-19); `Http1Connection.FeatureCollection.cs` and `HttpProtocol.FeatureCollection.cs` only expose `IHttpRequestTrailersFeature` (request side). `Http1OutputProducer` ends a chunked body with the bare `"0\r\n\r\n"` (`EndChunkedResponseBytes`, https://github.com/dotnet/aspnetcore/blob/main/src/Servers/Kestrel/Core/src/Internal/Http/Http1OutputProducer.cs L22, L127). So on HTTP/1.1 `Response.SupportsTrailers()` is false and `Response.AppendTrailer` throws `InvalidOperationException("Trailers are not supported for this response.")` (https://github.com/dotnet/aspnetcore/blob/main/src/Http/Http.Abstractions/src/Extensions/ResponseTrailerExtensions.cs L31-52; API doc https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.http.responsetrailerextensions). `DeclareTrailer` merely appends to the `Trailer` header and works on any protocol.
- Kestrel does *read* HTTP/1.1 chunked request trailers (`Http1ChunkedEncodingMessageBody.ParseChunkedTrailer`, https://github.com/dotnet/aspnetcore/blob/main/src/Servers/Kestrel/Core/src/Internal/Http/Http1ChunkedEncodingMessageBody.cs L277-295).
- HttpClient (SocketsHttpHandler) does parse HTTP/1.1 chunked trailers into `HttpResponseMessage.TrailingHeaders` (`ChunkedEncodingReadStream` state `ConsumeTrailers` -> `ParseHeaders(..., isFromTrailer: true)`, https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Http/src/System/Net/Http/SocketsHttpHandler/ChunkedEncodingReadStream.cs L395-400; `HttpConnection.cs` L1295-1304). Docs: "the TrailingHeaders property returns an empty HttpResponseHeaders instance if it is accessed and the response content has not been read completely"; available since .NET Core 3.0 / netstandard 2.1 (https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpresponsemessage.trailingheaders).
- Implication: to emit trailers from ASP.NET Core you need HTTP/2 (or HTTP/3), or write the chunked framing yourself on an upgraded/raw connection.

## 8. MPEG-TS join latency and GOP

- Decoding of a passthrough H.264/H.265 TS can only begin at an IDR/key frame; FFmpeg's own stream-copy path drops leading non-key frames unless `-copyinkf` ("When doing stream copy, copy also non-key frames found at the beginning", https://github.com/FFmpeg/FFmpeg/blob/master/doc/ffmpeg.texi). x264 documents IDR frames as the random-access mechanism (`--intra-refresh  Use Periodic Intra Refresh instead of IDR frames`, https://code.videolan.org/videolan/x264/-/blob/master/x264.c L687). Worst-case join wait = one GOP (keyint) duration; average = half. (The H.264 spec's random-access rule itself was not fetched.)
- Encoder knobs: x264 `-I, --keyint <integer or "infinite">  Maximum GOP size`, `-i, --min-keyint  Minimum GOP size`, `--scenecut`/`--no-scenecut` (x264.c L683-686). FFmpeg libx264 mapping: `g` (keyint), `keyint_min` (min-keyint), `sc_threshold` (scenecut), plus `x264-params`/`x264opts` (https://github.com/FFmpeg/FFmpeg/blob/master/doc/encoders.texi "libx264"). Generic `-g` = "Set the group of picture (GOP) size. Default value is 12" (doc/codecs.texi). `-force_key_frames expr:gte(t,n_forced*5)` forces a key frame every 5 s; docs warn "forcing too many keyframes is very harmful for the lookahead algorithms ... using fixed-GOP options ... would be more efficient" (doc/ffmpeg.texi `-force_key_frames`).
- 1-second GOP at 25 fps, fixed:
  `ffmpeg -i IN -c:v libx264 -g 25 -keyint_min 25 -sc_threshold 0 -bf 0 -tune zerolatency -f mpegts OUT` (equivalently `-x264-params keyint=25:min-keyint=25:scenecut=0`). Time-based alternative: `-force_key_frames expr:gte(t,n_forced*1)`.
