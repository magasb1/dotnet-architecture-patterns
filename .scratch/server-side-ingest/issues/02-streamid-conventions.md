# How encoders set streamid

Type: research
Status: resolved

## Question

What does a streamid actually look like coming off real encoders, and what should this service
parse?

There is a published convention from the SRT project for access control, where the streamid carries
structured key and value pairs rather than a bare name. If that convention is what encoders emit,
this service should read it rather than invent its own format, because an operator configuring a
hardware encoder types into a field whose syntax that box already decides.

Establish:

- The SRT access control streamid convention: its syntax, its defined keys, and which of them carry
  a resource name and a publish or subscribe intent.
- What common senders put there in practice, including the ffmpeg command line, OBS, and at least
  one hardware contribution encoder.
- Whether a bare unstructured string is common enough that the parser has to accept it as a plain
  name.
- Where a token would conventionally sit if authentication is added later, so the format chosen now
  does not have to change then.

## Answer

Parse the SRT Access Control convention, and fall back to the whole string as a bare name. That is
what the SRT project's own reference listener does, and it is the only rule that survives contact
with both a Makito and a hand-typed OBS URL.

### The convention

Authoritative document: `docs/features/access-control.md` in the SRT repository —
<https://github.com/Haivision/srt/blob/master/docs/features/access-control.md>

Prefix `#!` (the POSIX shebang), then one character naming the content format. Only `:` is defined,
and it means "comma-separated key-value pairs with no nesting". So the whole prefix is `#!::` and
the content is `key1=value1,key2=value2,...`. Encoding is UTF-8. A second format is declared, `{`
for a nested brace block, which nothing in the wild emits.

Standard keys, quoted from that document:

| Key | Meaning | Relevance here |
| --- | --- | --- |
| `r` | **Resource Name** — "identifies the name of the resource and facilitates selection should the listener party be able to serve multiple resources" | **this is the stream name** |
| `m` | **Mode** — `request` (default, caller wants to receive), `publish` (caller wants to send), `bidirectional` | **publish vs subscribe intent** |
| `u` | **User Name** — "authorization name, that is expected to control which password should be used" | future auth identity |
| `s` | **Session ID** — "a temporary resource identifier negotiated with the server, used just for verification. This is a one-shot identifier, invalidated after the first use" | **the token slot** |
| `h` | **Host Name** of the resource | ignore, single-tenant |
| `t` | **Type** — `stream` (default), `file`, `auth` | ignore |

All other single-letter keys are reserved for future use. Custom keys must carry a `user_*` or
`companyname_*` prefix.

Two details in that document decide the parser:

1. **`m` is optional and frequently absent.** Quoted: "Note that `m` is not required in the case
   where you don't use `streamid` to distinguish authorization or resources, and your caller is
   expected to send the data." Haivision's own documented example is
   `#!::u=admin,r=ietf_107_srt_overview` with no `m` at all. A push-only listener must therefore
   treat *absent* `m` as publish, and reject only an explicit `m=request`.
2. **No escaping is defined.** The grammar splits on `,` and `=` and the document specifies no
   escape mechanism at all. A resource name containing either character is unparseable. `/` is safe
   and universally used (`r=live/camera1`).

### What real senders emit

**ffmpeg.** `streamid` is a plain string option on the `srt` protocol, passed verbatim to
`SRTO_STREAMID` — see the option table and the `srt_setsockopt` call in
<https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/libsrt.c>. FFmpeg's own doc says "SRT does
not enforce any special interpretation of the contents of this string". Two practical traps:

- **The `#` fragment problem.** `#` starts a URL fragment per RFC 3986, so
  `srt://host:9000?streamid=#!::r=cam1` is malformed and a strict parser truncates it
  (<https://github.com/Haivision/srt/issues/1871>: "we can't use some standard library to parse the
  url, unless treat the fragment specifically as streamid value"). The correct form is
  `streamid=%23!::r=cam1,m=publish`. FFmpeg percent-decodes `streamid` from **n7.0 onward**
  (`ff_urldecode` on `s->streamid`; on master via `ff_parse_opts_from_query_string` →
  `ff_urldecode_len`). **FFmpeg 6.1 and older do not** — they hand the literal `%23!::r=cam1` to the
  socket. This repository pins FFmpeg 8.1, so our own senders are fine, but a third-party sender on
  an older build will present a literal `%23` prefix.
- **The option-name collision.** ffmpeg already has a generic muxer option `-streamid <index>:<pid>`
  for MPEG-TS PID remapping, so libsrt registers the alias `srt_streamid` "to avoid conflict with
  ffmpeg command line option" (<https://github.com/Haivision/srt/issues/1540>). Document the query
  form, not the `-streamid` flag.

**OBS Studio.** No Stream ID field exists. The operator picks Custom service and pastes the whole
`srt://ip:port?streamid=...&latency=...` URL into the Server box, leaving Stream Key empty
(<https://obsproject.com/kb/srt-protocol-streaming-guide>: "OBS Studio will accept options in the
syntax srt://IP:port?option1=value1&option2=value2 ... Leave the key field empty"). The Stream Key
box is discarded for SRT and has been an open request since 2021
(<https://github.com/obsproject/obs-studio/issues/4250>). Observed values in the wild are plain:
`?streamid=DX01`, `?streamid=livestream`. **Expect bare names from OBS far more often than
structured ones**, because the structured form has to be hand-typed with `%23` escaping into a
single-line box.

**Haivision Makito X4.** The field is "Stream Publishing ID" and has two UI modes: *Standard Keys*,
where the box is read-only and auto-filled from separate Resource Name and User Name fields into
`#!::u=admin,r=haivision1,m=publish`, and *Custom*, free text the operator types
(<https://doc.haivision.com/MakitoX4Enc/1.5/configuring-srt-access-control>,
<https://doc.haivision.com/MakitoX4Enc/1.7/stream-settings>). This is the only vendor found that
builds the syntax for the operator — unsurprising, since Haivision wrote the convention.

**Teradek Prism.** A plain labelled free-text "Stream ID" box beside Host/Port/Passphrase/Latency,
documented only as "a customizable identifier that allows routing and identification of individual
SRT streams" (<https://guide.teradek.com/a/1892744-return-video-configuration-via-encoder>). Nothing
constructs `#!::`. Whatever the operator types is what arrives.

Receiving servers converge on `#!::r=<path>,m=publish`:

- Wowza — `#!::m=publish,r=<application>/<instance>/<stream>`, plus `u=` in per-user passphrase mode
  (<https://www.wowza.com/docs/ingest-and-publish-an-srt-stream-with-wowza-streaming-engine>)
- Flussonic — `#!::r=stream_name,m=publish` (<https://flussonic.com/doc/srt-protocol/>)
- SRS — `#!::r=live/livestream,m=publish` (<https://ossrs.net/lts/en-us/docs/v5/doc/srt>)
- BytePlus — `#!::h=push.example.com,r=AppName/StreamName,m=publish,volcTime=...,volcSecret=...`
  (<https://docs.byteplus.com/en/docs/byteplus-media-live/PushWithSRT>)

### Is a bare string common enough to matter

Yes, and the SRT project itself blesses it. `SRTO_STREAMID` is documented as "This string can be
used completely free-form"
(<https://github.com/Haivision/srt/blob/master/docs/API/API-socket-options.md>), the access-control
document lists "identify the file name of a stream that is about to be sent" as the simple target
use case, and its own reference callback ends with:

```c++
else
{
    // By default the whole streamid is username
    username = streamid;
}
```

SRS does exactly this in production: `srs_srt_streamid_info` tests `streamid.find("#!::") != 0` and
falls through to using the entire value as the app/stream path, so `streamid=camera1` and
`streamid=live/mystream` both work
(<https://github.com/ossrs/srs/blob/develop/trunk/src/protocol/srs_protocol_utility.cpp>).

MediaMTX is the counterexample worth knowing: it parses `#!::` correctly but its non-prefixed form
demands `action:pathname[:query]`, so a truly bare `camera1` is *rejected*
(<https://github.com/bluenviron/mediamtx/blob/main/internal/servers/srt/streamid.go>). Given that
OBS and Teradek both present a bare box, refusing a bare name means telling operators to type shell
punctuation into an encoder GUI. Accept it.

### Where a token goes later

`s`, the Session ID. The spec defines it as a temporary, one-shot identifier verified by the server,
which is precisely a push token. MediaMTX already treats it that way — its `#!::` branch is literally
`case "s": s.pass = value`. Reading `r` now and `s` later is a pure addition; nothing about the
format changes. `u` stays available as the identity that selects which secret to check, which is how
both the SRT reference callback and Wowza's per-user mode work. Vendor-specific token keys do exist
(BytePlus `volcSecret`) but those are that vendor's server, not a convention to copy.

A bare name carries no token. That is an accepted consequence: when auth arrives, bare-name ingest
either stays on a trusted network or is gated by the SRT passphrase, which is orthogonal to the
streamid and is already the map's preferred answer (note 41).

### Length and character limits

- **512 bytes, not characters.** The handshake extension caps at 512 bytes of UTF-8
  (draft-sharabayko-srt §3.2.1.3, <https://datatracker.ietf.org/doc/html/draft-sharabayko-srt-01>:
  "The maximum allowed size of the StreamID extension is 512 bytes"), matched by
  `MAX_SID_LENGTH = 512` in `srtcore/socketconfig.h`. Content is zero-padded to 4-byte words, so a
  trailing NUL is padding, never data.
- `,` and `=` are structural in the convention with no escape defined, so a stream name must not
  contain them.
- `#` must be written `%23` whenever the streamid travels inside a URL query.
- `/` is safe and idiomatic for a path-shaped resource.

### Decision: the parsing rule to implement

Given the raw `SRTO_STREAMID` string read from the accepted socket:

1. Decode as UTF-8, trim whitespace, strip any trailing NUL. Empty → reject the connection.
2. If it begins with `%23!::`, rewrite that prefix to `#!::`. One line, and it turns an old-ffmpeg
   sender's silently-wrong stream name into a working connection.
3. **If it begins with `#!::`** — split the remainder on `,`, split each part on the *first* `=`.
   Keys are case-sensitive. Ignore unknown keys rather than failing; the convention explicitly
   permits `user_*` and `companyname_*` extensions and we must not break on a vendor's extras.
   - `m=request` → reject. This is a push-only ingest and the caller is asking to pull.
   - `m` absent, `m=publish`, or `m=bidirectional` → publish. **Do not require `m`.**
   - `r` → the stream name. `r` missing → reject with a message naming `r`; we serve one port and
     have no other way to know which stream this is.
   - `s` → ignored today, reserved for the token. Log its presence, never its value.
4. **Otherwise** — the entire string is the stream name. This is the OBS/Teradek case and the SRT
   reference behaviour.
5. **Validate the derived name identically in both branches**, because it becomes the stream
   identity, the registry key, and eventually part of a document name: `^[A-Za-z0-9._/-]{1,128}$`,
   no leading or trailing `/`, no empty segment, no `..`. Reject rather than sanitise — two encoders
   whose names normalise to the same string would silently collide onto one stream, and under
   decision 3 of the map that means one hijacks the other's identity.
6. The name is thereafter compared **byte-exactly and case-sensitively**. Reconnect matching (map
   decision 3) keys on this exact string.

Worked examples:

| streamid on the wire | stream name |
| --- | --- |
| `camera1` | `camera1` |
| `#!::r=camera1` | `camera1` |
| `#!::u=admin,r=live/camera1,m=publish` | `live/camera1` |
| `#!::u=admin,r=ietf_107_srt_overview` (Makito Standard Keys) | `ietf_107_srt_overview` |
| `#!::r=live/cam1,m=request` | rejected — pull request on a push listener |
| `#!::u=admin` | rejected — no `r` |
| `../../etc/passwd` | rejected — fails name validation |

Sender-side documentation to ship with the service:

```
ffmpeg ... -f mpegts 'srt://ingest.example:9000?streamid=%23!::r=live/camera1,m=publish'
```

and, for any box with a plain Stream ID field, "type `live/camera1`".

The line worth carrying into the design document: **the streamid is a name, optionally wrapped in a
standard envelope.** Read the envelope when it is there; take the name as given when it is not.
