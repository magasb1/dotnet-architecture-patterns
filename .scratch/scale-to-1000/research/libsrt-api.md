# libsrt (Haivision SRT) facts for a .NET P/Invoke listener

Researched 2026-09-11 against libsrt `master` (CMakeLists `SRT_VERSION 1.5.7`), FFmpeg master, Debian/Ubuntu trackers, vcpkg master, BtbN/FFmpeg-Builds master.
Abbreviations: `API-fn` = https://github.com/Haivision/srt/blob/master/docs/API/API-functions.md, `API-opt` = https://github.com/Haivision/srt/blob/master/docs/API/API-socket-options.md, `srt.h` = https://github.com/Haivision/srt/blob/master/srtcore/srt.h.

## 1. `srt_listen_callback`

- Registration: `int srt_listen_callback(SRTSOCKET lsn, srt_listen_callback_fn* hook_fn, void* hook_opaque);` — returns 0 ok / -1 error (`SRT_ECONNSOCK`). `hook_fn = NULL` removes it. Must be installed **before `srt_listen`**. (srt.h; API-fn#srt_listen_callback)
- Callback: `typedef int srt_listen_callback_fn(void* opaq, SRTSOCKET ns, int hsversion, const struct sockaddr* peeraddr, const char* streamid);` (srt.h)
  - `opaq` = the `hook_opaque` you registered; `ns` = the freshly created, not-yet-accepted socket; `hsversion` = handshake version (5 normally, 4 for pre-1.3 peers, which cannot send streamid); `peeraddr` = caller address; `streamid` = the peer's `SRTO_STREAMID` value, passed **directly as a C string**. (API-fn#srt_listen_callback)
- Return **0 to accept; -1 (or a thrown C++ exception) to reject**. Rejected `ns` is silently deleted; `srt_accept` never sees it; the listener's read-ready epoll bit is not set. (API-fn#srt_listen_callback)
- Timing: runs after the caller's conclusion handshake is received but **before it is interpreted / before the connection is accepted**; `ns` already has all options derived from the listener. (API-fn#srt_listen_callback)
- Thread: "called in the receiver worker thread" — must be fast; any delay stalls packet processing for the listener and every socket accepted from it. (API-fn#srt_listen_callback)
- Setting `SRTO_PASSPHRASE` on `ns` inside the callback is the documented pattern: `srt_setsockflag(ns, SRTO_PASSPHRASE, exp_pw.c_str(), exp_pw.size()); return 0;` — "the remaining part of the SRT handshaking process will check the passphrase of the client and accept or reject the connection"; the callback itself cannot inspect the passphrase. (https://github.com/Haivision/srt/blob/master/docs/features/access-control.md, sections "Purpose" and the example callback; API-fn: "Key Material processing will happen against this passphrase, after the callback function is finished")

## 2. `srt_setrejectreason` and `SRT_REJX_*`

- `int srt_setrejectreason(SRTSOCKET sock, int value);` / `int srt_getrejectreason(SRTSOCKET sock);` / `const char* srt_rejectreason_str(int id);` (srt.h)
- `srt_setrejectreason` refuses `value < SRT_REJC_PREDEFINED` (1000) with `SRT_EINVPARAM` (MJ_NOTSUP/MN_INVAL). (https://github.com/Haivision/srt/blob/master/srtcore/core.cpp, `CUDT::rejectReason(SRTSOCKET,int)`)
- Ranges (srt.h): `SRT_REJC_INTERNAL 0` (the `SRT_REJECT_REASON` enum: `SRT_REJ_UNKNOWN`=0, `SYSTEM`, `PEER`, `RESOURCE`, `ROGUE`, `BACKLOG`, `IPE`, `CLOSE`, `VERSION`, `RDVCOOKIE`, `BADSECRET`, `UNSECURE`, `MESSAGEAPI`, `CONGESTION`, `FILTER`, `GROUP`, `TIMEOUT`, `CRYPTO`), `SRT_REJC_PREDEFINED 1000`, `SRT_REJC_USERDEFINED 2000`. `SRT_REJC_VALUE(code) = 1000*(code/1000)`.
- Full `SRT_REJX_*` list from https://github.com/Haivision/srt/blob/master/srtcore/access_control.h:

| Name | Value | Name | Value |
|---|---|---|---|
| `SRT_REJX_FALLBACK` | 1000 | `SRT_REJX_CONFLICT` | **1409** (resource already locked for modification, e.g. m=publish on a read-only resource) |
| `SRT_REJX_KEY_NOTSUP` | 1001 | `SRT_REJX_NOTSUP_MEDIA` | 1415 |
| `SRT_REJX_FILEPATH` | 1002 | `SRT_REJX_LOCKED` | 1423 (locked for any access) |
| `SRT_REJX_HOSTNOTFOUND` | 1003 | `SRT_REJX_FAILED_DEPEND` | 1424 |
| `SRT_REJX_BAD_REQUEST` | 1400 | `SRT_REJX_ISE` | 1500 |
| `SRT_REJX_UNAUTHORIZED` | 1401 | `SRT_REJX_UNIMPLEMENTED` | 1501 |
| `SRT_REJX_OVERLOAD` | 1402 | `SRT_REJX_GW` | 1502 |
| `SRT_REJX_FORBIDDEN` | 1403 | `SRT_REJX_DOWN` | 1503 |
| `SRT_REJX_NOTFOUND` | 1404 | `SRT_REJX_VERSION` | 1505 |
| `SRT_REJX_BAD_MODE` | 1405 | `SRT_REJX_NOROOM` | 1507 |
| `SRT_REJX_UNACCEPTABLE` | 1406 | | |

- Wire encoding: the handshake request type carries `1000 + reason`; the caller recovers it with `RejectReasonForURQ`, so a listener's 1409 arrives at the caller as exactly 1409 from `srt_getrejectreason`. (https://github.com/Haivision/srt/blob/master/srtcore/handshake.h `URQ_FAILURE_TYPES = 1000`, `URQFailure`, `RejectReasonForURQ`)

## 3. How FFmpeg (caller) surfaces a rejection

- FFmpeg master `libavformat/libsrt.c` **does not call `srt_getrejectreason` or `srt_rejectreason_str` at all** (grep of https://raw.githubusercontent.com/FFmpeg/FFmpeg/master/libavformat/libsrt.c). The string "Connection setup failure" also does not appear in libsrt.c; it is libsrt's own error text.
- Caller flow in libsrt.c: socket set non-blocking (`libsrt_socket_nonblock(fd, 1)`), `srt_connect`, then `libsrt_network_wait_fd_timeout` polling an epoll with `SRT_EPOLL_ERR|SRT_EPOLL_OUT`. On rejection libsrt reports the socket in both read and write arrays (API-fn#srt_epoll_wait: "an error occurred on a socket then that socket is reported in both read-ready and write-ready arrays"), so `libsrt_network_wait_fd` returns `AVERROR(EIO)` and `libsrt_listen_connect` logs:
  `Connection to %s failed: %s` with `av_err2str(AVERROR(EIO))` → **`Connection to srt://host:port failed: Input/output error`** (AV_LOG_ERROR). If more addresses remain: `Connection to %s failed (%s), trying next address` (warning). (libsrt.c `libsrt_network_wait_fd`, `libsrt_listen_connect`)
- libsrt's own `srt_getlasterror_str()` for `SRT_ECONNREJ` is `"Connection setup failure: connection rejected"` (https://github.com/Haivision/srt/blob/master/srtcore/strerror_defs.cpp). ffmpeg only prints `srt_getlasterror_str()` via `libsrt_neterrno` when an SRT call itself returns -1 (e.g. blocking connect). In master's non-blocking path this does not happen for a handshake rejection.
- Additionally the caller-side libsrt logs, at `Warn` level, `processConnectResponse: rejecting per reception of a rejection HS response: ...` (core.cpp `processConnectResponse`). libsrt's default log level is `LogLevel::warning` to `std::cerr` (https://github.com/Haivision/srt/blob/master/srtcore/logging.h `LogConfig` ctor; https://github.com/Haivision/srt/blob/master/srtcore/logger_defs.cpp), and libsrt.c never calls `srt_setloglevel`, so this line should also appear on ffmpeg's stderr. Exact formatting of the trailing `RequestTypeStr(...)` text was not verified.
- Test assertion suggestion: match `Connection to srt://.* failed: Input/output error` and/or `rejecting per reception of a rejection HS response`. Not confirmed from a primary source: whether a connection *timeout* would print something different (it should: `srt_getlasterror()==SRT_ETIMEOUT` maps to `EAGAIN` and eventually `AVERROR(ETIMEDOUT)`, per libsrt.c `libsrt_network_wait_fd_timeout`), so "Input/output error" distinguishes rejection from timeout.

## 4. Socket options (all from API-opt; `Restrict` column semantics from the same doc)

Restrict meanings: `pre-bind` = cannot change after bind (incl. auto-bind on connect, and accepted sockets); `pre` = cannot change in LISTENING/CONNECTING/CONNECTED; `post` = any time. **"If an option was set on a listener socket, it will be inherited by a socket returned by `srt_accept()` (except for `SRTO_STREAMID`)"** (stated for `pre`; for `post`: "usually derived by the accepted socket, but this isn't a rule for all options"). Entity `GSD` = settable on socket or group, derived by group members; `GSI` = managed by group; `+` = also settable per member.

| Option | Type | Default | Restrict | Notes |
|---|---|---|---|---|
| `SRTO_LATENCY` | int32 ms | 120* | pre | Sets **both** `SRTO_RCVLATENCY` and `SRTO_PEERLATENCY` to the same value. |
| `SRTO_RCVLATENCY` | int32 ms | 120 (live), 0 (file) | pre | Minimum receiver buffering delay. Negotiated `Ln = max(own SRTO_RCVLATENCY, peer's SRTO_PEERLATENCY)`; reading it on a connected socket returns the negotiated value. |
| `SRTO_PEERLATENCY` | int32 ms | 0 | pre | Minimum latency you demand the *peer's receiver* uses. Reading on a connected socket gives the peer's effective latency. |
| `SRTO_MAXBW` | int64 B/s | -1 | post | `-1` = infinite (live-mode cap 1 Gbps); `0` = relative to input rate (`INPUTBW`/`OHEADBW`); `>0` = absolute B/s. Default -1 in all modes. |
| `SRTO_INPUTBW` | int64 B/s | 0 | post | Only when MAXBW=0. `MAXBW = INPUTBW*(100+OHEADBW)/100`; 0 = estimate from send rate (floor `SRTO_MININPUTBW`). |
| `SRTO_OHEADBW` | int32 % | 25 | post | Range 5..100. Only when MAXBW=0. Doc marks it set-only. |
| `SRTO_TLPKTDROP` | bool | true (live), false (file) | pre | Receiver skips too-late packets; sender drops packets with no chance to arrive in time if the receiving peer supports it. |
| `SRTO_PAYLOADSIZE` | int32 bytes | 1316 (live), 0 (file) | pre, W only | Live max **1456** (`SRT_LIVE_MAX_PLSIZE` = MTU 1500 - 28 UDP - 16 SRT; `SRT_LIVE_DEF_PLSIZE` = 1316 = 188*7 in srt.h). Not negotiated; receiver's value is a heuristic; receiver must accept up to MSS. `srt_recvmsg` buffer smaller than PAYLOADSIZE → `SRT_EINVALMSGAPI`. |
| `SRTO_RCVTIMEO` | int32 ms | -1 | post, GSI | Blocking recv time limit; -1 = none; on expiry behaves as non-blocking (`SRT_ETIMEOUT`). |
| `SRTO_SNDTIMEO` | int32 ms | -1 | post, GSI | Same for send. |
| `SRTO_PEERIDLETIMEO` | int32 ms | 5000 | pre, GSD+ | Connection considered broken if no packet from peer within this time. |
| `SRTO_REUSEADDR` | bool | true | pre-bind | Allows sharing a binding with another SRT socket in the same app via the Multiplexer (one UDP socket, packets dispatched to the right SRT socket). |
| `SRTO_STREAMID` | string | "" | pre | Max **512** bytes (UTF-8). Set on the caller before connect; **read via `srt_getsockflag` on the accepted socket** ("GET on the socket retrieved from `srt_accept`"). Explicitly **not** inherited from the listener. ffmpeg does exactly this with a 513-byte buffer (libsrt.c `libsrt_listen`). |
| `SRTO_ENFORCEDENCRYPTION` | bool | true | pre, W only | true = both sides must have the same passphrase or both none, else reject. Setting false on the listener only (caller true) yields a spurious short-lived accepted connection; keep true on the listener. |
| `SRTO_PASSPHRASE` | string | "" | pre, W only | Length 10..80. |

Whether `SRTO_RCVTIMEO`/`SRTO_SNDTIMEO` (post) are inherited by accepted sockets is not stated per-option; only the general "usually derived" rule above applies. Set them on the accepted socket to be safe.

## 5. Core function signatures and blocking semantics (srt.h; API-fn)

```c
int        srt_startup(void);            // 0 ok / already started, 1 first start (GC thread), -1 fail
int        srt_cleanup(void);            // refcounted: call once per srt_startup
uint32_t   srt_getversion(void);         // 0xXXYYZZ for x.y.z
SRTSOCKET  srt_accept(SRTSOCKET u, struct sockaddr* addr, int* addrlen); // SRT_INVALID_SOCK (-1) on failure
int        srt_recvmsg(SRTSOCKET u, char* buf, int len);
int        srt_sendmsg(SRTSOCKET u, const char* buf, int len, int ttl, int inorder);
int        srt_sendmsg2(SRTSOCKET u, const char* buf, int len, SRT_MSGCTRL* mctrl);
int        srt_close(SRTSOCKET u);       // -1 error else 0
int        srt_getlasterror(int* errno_loc);   // errno_loc may be NULL; receives errno / GetLastError()
const char* srt_getlasterror_str(void);
int        srt_bstats(SRTSOCKET u, SRT_TRACEBSTATS* perf, int clear);
int        srt_setsockflag(SRTSOCKET u, SRT_SOCKOPT opt, const void* optval, int optlen);
int        srt_getsockflag(SRTSOCKET u, SRT_SOCKOPT opt, void* optval, int* optlen);
```
`SRT_MSGCTRL` = `{ int flags; int msgttl; int inorder; int boundary; int64_t srctime; int32_t pktseq; int32_t msgno; SRT_SOCKGROUPDATA* grpdata; size_t grpdata_size; }` (srt.h).

Error codes (srt.h: `1000*major + minor`): `SRT_ECONNREJ`=1002, `SRT_ECONNLOST`=2001, `SRT_ENOCONN`=2002, `SRT_EINVSOCK`=5004, `SRT_EASYNCSND`=6001, `SRT_EASYNCRCV`=6002, `SRT_ETIMEOUT`=6003.

`srt_recvmsg` (API-fn#srt_recvmsg): returns size >0; **0 if the connection has been closed** (by the peer, orderly); -1 with `SRT_ECONNLOST` "only if the connection was unexpectedly broken, not when it was closed by the foreign host"; `SRT_ENOCONN` if not connected; `SRT_EASYNCRCV` only in non-blocking mode; `SRT_ETIMEOUT` in blocking mode when `SRTO_RCVTIMEO != -1` expires; `SRT_EINVSOCK` for a bad socket id. Live mode delivers at most one packet's payload per call, only once its time-to-play has come.

`srt_accept` (API-fn#srt_accept): blocks while `SRTO_RCVSYN=true` (default); `SRT_EASYNCRCV` in non-blocking mode; **`SRT_ESCLOSED` "the `lsn` socket has been closed while the function was blocking the call"** — i.e. `srt_close(lsn)` from another thread unblocks `srt_accept`. `SRT_ESCLOSED` is documented for `srt_connect*`/`srt_accept`. For `srt_recvmsg` the docs do not list `SRT_ESCLOSED`; `srt_close` doc only says it frees resources. Not confirmed from docs which error a blocked `srt_recvmsg` gets when its own socket is closed from another thread (core.cpp throws `MJ_CONNECTION/MN_CONNLOST` in several receive paths, but that mapping was not traced end-to-end here).

## 6. One receive worker thread per multiplexer

- `CMultiplexer` (https://github.com/Haivision/srt/blob/master/srtcore/queue.h) owns one `CChannel` (UDP socket), one `CSndQueue`, one `CRcvQueue`. `CRcvQueue` has exactly one `sync::CThread m_WorkerThread` running `CRcvQueue::worker` (started in `CRcvQueue::init`, thread name `SRT:RcvQ:w<N>`; https://github.com/Haivision/srt/blob/master/srtcore/queue.cpp). Same for `CSndQueue` (`SRT:SndQ:w`).
- `CUDTUnited::updateMux` (https://github.com/Haivision/srt/blob/master/srtcore/api.cpp) iterates `m_mMultiplexer` for the same port; if the bind address is a wildcard or the same IP and `channelSettingsMatch`, it does `++m_iRefCount; installMuxer(s, existing)` ("bind: reusing multiplexer for port"); otherwise it creates a new `CMultiplexer` with `new CChannel`, `new CSndQueue`, `new CRcvQueue`. Mismatched channel settings on the same address → `SRT_EBINDCONFLICT` (MJ_NOTSUP/MN_BUSYPORT).
- Accepted sockets always share the listener's multiplexer: `updateListenerMux` sets `s->core().m_pRcvQueue = mux->m_pRcvQueue` and bumps the refcount (api.cpp). API-opt `SRTO_REUSEADDR` text confirms "multiple SRT sockets may share one UDP socket".
- Throughput per multiplexer: **no figure found** in API-functions.md, API-socket-options.md, or docs/dev/developers-guide.md. Only the `SRTO_MAXBW` note "the limit in Live Mode is 1 Gbps" (per socket send cap), not a multiplexer figure.

## 7. Packaging

- Debian source package `srt` (https://tracker.debian.org/pkg/srt): bookworm **1.5.1-1+deb12u1**, trixie 1.5.4-1 (+deb13u1 security), sid/testing 1.5.6-1; upstream 1.5.7 not yet packaged. Binaries: `libsrt1.5-openssl`, `libsrt-openssl-dev`, `libsrt1.5-gnutls`, `libsrt-gnutls-dev`, `srt-tools`, `libsrt-doc`. Files in bookworm amd64 `libsrt1.5-openssl`: `/usr/lib/x86_64-linux-gnu/libsrt.so.1.5` (soname) and `libsrt.so.1.5.1` (https://packages.debian.org/bookworm/amd64/libsrt1.5-openssl/filelist). Tracker notes CVE-2026-55868/55869 open for bookworm.
- Ubuntu 24.04 noble: `libsrt1.5-openssl` **1.5.3-1build2**; files `/usr/lib/x86_64-linux-gnu/libsrt.so.1.5`, `libsrt.so.1.5.3` (https://packages.ubuntu.com/noble/libsrt1.5-openssl, https://packages.ubuntu.com/noble/amd64/libsrt1.5-openssl/filelist). SOVERSION = `MAJOR.MINOR` per CMakeLists (`SOVERSION ${SRT_VERSION_MAJOR}.${SRT_VERSION_MINOR}`), so `[DllImport("srt")]` needs a resolver mapping to `libsrt.so.1.5`.
- vcpkg port **`libsrt`** version **1.5.6**, depends on `openssl` (https://github.com/microsoft/vcpkg/blob/master/ports/libsrt/vcpkg.json). Features: `tool`, `bonding`. `x64-windows` triplet is `VCPKG_LIBRARY_LINKAGE dynamic` (https://github.com/microsoft/vcpkg/blob/master/triplets/x64-windows.cmake); port builds with `ENABLE_SHARED=ON` and patches `srt.h` to force `SRT_DYNAMIC` exports. The shared target's `OUTPUT_NAME` is `srt` (CMakeLists line ~1151) → **`srt.dll`** (+ `srt.lib` import lib). The exact bin/ file list was not verified from a vcpkg build log. OpenSSL DLL names on MSVC are `libcrypto-<SHLIB_VERSION><multilib>.dll` / `libssl-...` with `SHLIB_VERSION=3` (openssl-3.6 VERSION.dat) and `multilib="-x64"` for VC-WIN64A → **`libcrypto-3-x64.dll`, `libssl-3-x64.dll`** (https://github.com/openssl/openssl/blob/master/Configurations/platform/Windows/MSVC.pm, https://github.com/openssl/openssl/blob/master/Configurations/10-main.conf; vcpkg openssl port is 3.6.4 per its vcpkg.json).
- BtbN FFmpeg-Builds `scripts.d/50-srt.sh` (https://github.com/BtbN/FFmpeg-Builds/blob/master/scripts.d/50-srt.sh): builds libsrt from commit `fcae57145c000a9e7b72aa777adb8f85c2463242` with `-DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DENABLE_ENCRYPTION=ON -DENABLE_APPS=OFF`, `ffbuild_enabled` always true, configure `--enable-libsrt`. README: `gpl-shared` = "Same as gpl, but comes with the libav* family of shared libs" (https://github.com/BtbN/FFmpeg-Builds/blob/master/README.md). So libsrt is **statically linked into the shared avformat DLL**; no `srt.dll` ships, and a separately loaded `srt.dll` will not clash with symbol names at the loader level (both copies do run separate `srt_startup` state, though — that was not investigated).
- Current libsrt release: **v1.5.7** (GitHub release dated 28 Aug; https://github.com/Haivision/srt/releases/latest). `srt_getversion()` returns `SRT_MAKE_VERSION(major,minor,patch) = patch + minor*0x100 + major*0x10000` (https://github.com/Haivision/srt/blob/master/srtcore/version.h.in).

## 8. .NET side

- `[UnmanagedCallersOnly]` methods: must be `static`, only blittable parameters, not generic, never called from managed code; take the address with `&Method` and pass as `delegate* unmanaged[Cdecl]<...>` to a `[DllImport]` whose parameter is a function pointer. Official example: `[DllImport("NativeLibrary")] static extern unsafe void NativeFunctionWithCallback(delegate* unmanaged[Cdecl]<int, int> callback); [UnmanagedCallersOnly(CallConvs = new[]{ typeof(CallConvCdecl) })] static int DoubleInt(int i) => i*2; ... NativeFunctionWithCallback(&DoubleInt);` (https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.unmanagedcallersonlyattribute). For libsrt: `delegate* unmanaged[Cdecl]<void*, int, int, sockaddr*, byte*, int>` (SRTSOCKET is `int` in 1.5.x; callback returns `int`).
- `NativeLibrary.SetDllImportResolver(Assembly assembly, DllImportResolver resolver)` — one resolver per assembly (second registration throws); it is the first attempt for every `DllImport` from that assembly; register only for your own assembly. `public delegate IntPtr DllImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath);` return the handle (e.g. from `NativeLibrary.Load(fullPath)`) or `IntPtr.Zero` to fall back to default probing; called once per P/Invoke entry point, so cache the handle. (https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativelibrary.setdllimportresolver, https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.dllimportresolver)
