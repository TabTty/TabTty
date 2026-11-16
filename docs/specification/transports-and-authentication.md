# Transport and authentication contracts

Specification baseline, 2026-11-10. The accepted SSH/OpenSSL ADR and
Q131-Q143/Q161 are authoritative. Exact algorithm names/order remain in
`../drafts/ssh-transport-policy-proposals.md`; this document completes framing,
state, identity, and resource contracts without adding weak fallback modes.
See [implementation status](implementation-status.md) for runtime evidence.

## Destination and host records

Use one canonical destination for both DNS resolution and trust lookup. Accept
DNS names or explicit IPv4/IPv6, with port separate. IPv4 is four decimal octets
0-255, no octal/hex/single-integer shorthand. Parse bracketed IPv6 only as a
complete host literal, remove brackets for identity, canonicalize address bytes,
and preserve a numeric scope ID. IPv4-mapped IPv6 remains IPv6, not an IPv4 alias.
Reject URI schemes, credentials, path/query fragments, whitespace/control input,
and ambiguous host:port combinations in the host field.

For DNS use Win32 IdnToAscii directly with explicit UTF-16 input length and
only IDN_USE_STD3_ASCII_RULES. Do not set IDN_ALLOW_UNASSIGNED, IDN_RAW_PUNYCODE,
or IDN_EMAIL_ADDRESS. This fixes the processor to Windows NLS rather than
.NET IdnMapping's environment-dependent ICU/NLS selection. Map Unicode separators
as that processor specifies, enforce per-label 63-byte and total 253-byte limits
excluding one explicit trailing dot, and ASCII-lowercase the result. Preserve
the presence of that trailing dot. Do not apply DNS search/alias/canonical-name
results to the trust key. Pin this processor choice rather than hand-implement
another mapping. Record the resulting ASCII destination in the session and
verify label/normalization vectors on each supported Windows/runtime baseline;
an OS mapping change requires review of trust-identity compatibility, never
silent migration of old records to a different destination.

`host-keys.json` has common version fields and `destinations`: each contains
`kind` (`dns/ipv4/ipv6`), canonical `host`, nullable `scopeId`, `port`, and
`keys`. Each key has `format`, canonical Base64 `publicBlob`, SHA-256 fingerprint,
and UTC `approvedAt`. One key per format per destination; curve-specific ECDSA
formats differ, RSA SHA-2 signatures share the `ssh-rsa` key blob. Validate
blob type/size/fingerprint on load. Host records are independent of the vault.

Host signature preference: ssh-ed25519, ecdsa-sha2-nistp256,
ecdsa-sha2-nistp384, ecdsa-sha2-nistp521,
rsa-sha2-512, rsa-sha2-256. Stable-partition this list so already approved key
formats for that exact destination precede unknown formats; retain relative
order within each group. A different/new presented key still needs the accepted
first-use/change approval. Verify the exchange signature before offering trust
or releasing authentication credentials. RSA uses SHA-2 with PKCS#1 v1.5, not
SHA-1 or RSA-PSS. No host alias inheritance or opportunistic key learning.

## SSH transport framing and key generations

Accept SSH 2.0 or compatible 1.99 identification, never SSH1 packets. Identification
line limit is 255 bytes including CRLF; bound preceding server lines to 64 KiB
and 256 lines. These precede terminal data and are never Raw/Text/Terminal output.
Use the ordinary connection deadline and reject binary/NUL invalid identification.

Maximum accepted full binary packet is 256 KiB including length and MAC/tag.
Check integer additions, available bytes, cipher alignment, padding length >=4,
payload extent, and algorithm-specific framing before allocating or dispatching.
This meets RFC 4253's required 32768-byte payload/35000-byte packet capability;
the larger cap is TabTty's defensive choice. Never dispatch unauthenticated
plaintext. Packet parsing rejects impossible state/type/length combinations
without attempting resynchronization after a failed MAC/tag.

Use separate protection adapters for GCM and CTR+EtM. For OpenSSH GCM, plaintext
length is AAD; the encrypted body includes padding length/payload/padding;
nonce is the SSH-derived 4-byte fixed prefix plus 8-byte invocation counter,
with 16-byte tag. Advance without reuse and fail before counter exhaustion.
Do not reject GCM merely because MAC lists have no common entry. For EtM,
authenticate sequence number, plaintext packet length, and encrypted body using
the selected HMAC before decrypting/exposing contents. Follow the exact OpenSSH
construction, not generic encrypt-and-MAC framing.

KEXINIT negotiates one allowed algorithm in each category/direction. Handle
first_kex_packet_follows by discarding exactly one wrong guessed packet when
required. Keep the first exchange hash as the session identifier forever.
NEWKEYS itself uses old protection; after sending switch only TX, after receiving
switch only RX. Retain old direction state only while still needed, then clear.

Advertise ext-info-c and both accepted strict-KEX client names. Strict initial
KEX checks that the first peer binary packet was KEXINIT and rejects forbidden
non-KEX/duplicate messages. During rekey obey RFC transport ordering while
handling legal previously in-flight traffic; do not apply initial-only strict
exclusions to every rekey. Under negotiated strict KEX reset each directional
sequence number after every NEWKEYS, including the initial exchange. Otherwise
sequence numbers continue modulo 2^32 as the protocol requires.

EXT_INFO is optional; parse its allowed post-NEWKEYS and pre-auth-success
positions. Accept well-formed messages through the packet cap, deriving count
bounds from remaining bytes. Do not impose a smaller arbitrary extension/value
limit. Unknown values are binary, including NUL, and can be skipped. The
server-sig-algs extension governs user signatures, not host-key negotiation.

Count protected bytes as length + encrypted body + MAC/tag per direction and
key generation; charge NEWKEYS to its old generation. Initiate rekey at 1 GiB
or one hour since completed exchange. Stop ordinary outgoing payload while KEX
requires it, retaining bounded queued input. Hard per-generation limits are
2 GiB protected bytes, 2^31 packets, 2^32 AES blocks, and no GCM invocation
counter wrap; the first reached controls. Reserve one maximum packet plus KEX
work before any hard boundary; if an exchange cannot finish in the remaining
budget, disconnect. The 1 GiB trigger is not permission to exceed hard caps.
Support simultaneous/peer rekey and existing deadline/trust-dialog exclusions.

Use SSH-specific shared-secret encodings. Hybrid mlkem768x25519-sha256 uses
HASH(K_PQ || K_CL) encoded as an SSH string, with the RFC's exact lengths/order;
do not substitute TLS hybrid encoding or an mpint. Reject X25519 all-zero
shared secrets and wrong lengths; validate ECDH named-curve points and finite-
field public values against each fixed group's requirements. OpenSSL performs
cryptographic primitives; the adapter must check every return value and clear
obsolete secrets. Compression is none in both directions.

## Authentication and user-facing messages

Enter ssh-userauth service only after encryption and host verification. Dispatch
method-specific message 60 against the outstanding method/request. Follow the
saved method allowlist/order, one selected key, and Password-only default.
Use server-sig-algs when present; absent that extension, RSA probes try SHA-512
then SHA-256 once each for the selected key, never SHA-1. PK_OK is not success.
Do not retry identical key/signature probes without a changed authentication
stage. Partial success advances the stage and marks the accepted static factor
used; do not replay it as though it were another identity.

Retain three password submissions/interactive conversation starts per stage.
Internal bounds: eight successful-factor stages, eight distinct public-key
probes total, 32 interactive rounds per conversation, 32 prompts per request,
8 KiB UTF-8 per prompt/instruction and 64 KiB combined per request or response.
Zero prompts produce zero responses; empty responses remain empty. At most one
unanswered interactive request. These limits do not count each prompt as a
failed attempt and do not add an authentication response timeout. Exhaustion
fails clearly; visible cancellation is always available.

Password-change-required is a valid but initially unsupported server flow:
explain it and allow another configured method or cancellation; do not add a
password-change UI or silently resubmit an old password. Keyboard-interactive
never auto-fills a stored password based on prompt text or echo flags. SSH
passwords/responses use protocol UTF-8 without terminal-codec conversion.
Retain secret ownership only for the accepted authentication lifetime.

Render banners/disconnect/debug/instruction bodies as inert native UI text
outside terminal logs, with controls escaped except ordinary layout whitespace.
Keep at most 64 messages/256 KiB per tab, 16 KiB displayed per non-interactive
body with an explicit truncation indication. Interactive prompt limits above
fail rather than silently shorten a question. Do not persist arbitrary server
bodies or secret replies in diagnostic output. Native error diagnostics use
bounded codes/categories and exclude secret material.

## Channel and termination

Open one session channel for the interactive terminal at startup, request PTY
then shell with want-reply=true, and become Connected only after both succeed.
No exec/no-PTY fallback, automatic startup command, environment forwarding,
forwarding, or agent. The initial-release scope now includes on-demand
[SFTP file transfer](file-transfer.md) on a separate subsystem
channel within the tab's authenticated connection under
[ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md). The contract
below governs terminal-channel data. The transfer contract defines SFTP
lifecycle, channel isolation, and shared-transport scheduling.
PTY uses the captured advertised type, current character dimensions, and IUTF8
when transmit encoding is UTF-8. Send window-change for committed grid changes,
without synthetic terminal output or waiting for a reply.

Advertise a 1 MiB receive window and 32768 maximum channel data. DATA and stderr
type 1 consume the same byte window. Return credit only after downstream terminal
processing releases the admitted payload, in batches at 512 KiB consumed or
when a remaining low window would stall progress. Logging completion never
holds channel credit; log failure follows its independent policy. Unknown
extended-data types consume credit and are discarded without terminal/log
injection, then credited back. Window arithmetic is checked uint32; peer excess
or overflow is protocol failure. TX data obeys peer window and maximum packet.

Reject unsupported channel opens. Unknown channel/global requests receive
failure only when want-reply is set. Track request replies in send order and
never associate a response with a newer generation. SSH liveness uses an
authenticated global keepalive@openssh.com request with reply requested;
success/failure replies and other valid authenticated traffic establish life.
It never injects terminal characters; use the accepted optional interval/count.

EOF closes the whole channel receive direction, not only one source. Finalize
normal DATA decoder/parser then stderr decoder/parser at one reducer boundary,
preserving completed operation order and replacing incomplete characters once.
Discard incomplete controls. Exit status alone neither finalizes nor closes.
CLOSE may occur without EOF; finalize once, reply at most once, and release the
channel when both closes are accounted for. Local Disconnect cancels unsent
input and gives peer cleanup at most two seconds, without exit/Ctrl+D. Recording
drain has its own ownership and wait choice. Reconnect is explicit and fresh.

## Telnet

Implement RFC 1143's Q method independently per option/direction for BINARY,
ECHO, SGA, TERMINAL-TYPE, and NAWS. Initially offer TTYPE/NAWS, request remote
ECHO, and offer/request SGA; BINARY initiation follows its saved default-Off
switch, while peer BINARY requests remain acceptable. Refuse local ECHO and
unknown options without negotiation loops. Track effective echo only from the
negotiated remote direction; no login detection, stored password, or vault use.

Decode IAC IAC to one data byte. In nonbinary RX, CR NUL yields CR, CR LF yields
CRLF, and a CR before other data yields CR then reprocesses that data. Negotiation
bytes do not become terminal payload. Flush pending CR on EOF or before changing
that direction's binary interpretation. Preserve eight-bit data otherwise.
For nonbinary TX, encode CR not followed by LF as CR NUL, preserve CRLF, then
double IAC; binary TX still doubles IAC. Coordinate streaming CR state with
send-unit boundaries so it cannot wait indefinitely for unrelated user input.

On accepted TTYPE SEND reply with the captured terminal type; NAWS sends current
16-bit columns/rows and escapes IAC even inside subnegotiation. Consume complete
bounded unknown subnegotiations without text leakage. Recognize NOP/GA/AYT/
BRK/IP/AO/EC/EL/DM as protocol commands, never received text or implicit local
editing; the initial UI sends only accepted AYT and BREAK actions. Malformed
IAC/SB framing or subnegotiation beyond 64 KiB fails the affected connection.
TCP fragmentation does not reset option, subnegotiation, or pending-CR state.

## Serial

Use Microsoft Win32 communications APIs so DSR/DTR is available alongside the
other accepted handshakes. Open the selected COM name exclusively with overlapped
I/O, set/verify DCB and timeouts, then Connected. Reject driver failures or
unsupported settings instead of substituting values. Baud is a positive uint32
within the driver's accepted range; data/parity/stop choices remain fixed.

RTS/CTS uses CTS output gating and RTS handshake; DSR/DTR uses DSR output gating
and DTR handshake. XON/XOFF uses software input/output flow control with DC1/DC3.
Outside handshake ownership apply saved DTR/RTS, default On. Do not enable extra
DSR sensitivity or null/parity replacement that silently edits terminal bytes.
Temporary manual line controls and a timed 1000 ms BREAK do not rewrite profile
settings. Ensure BREAK is cleared on cancellation/closure where the handle is
still usable. Disconnect/unplug invalidates I/O ownership; never auto-rebind USB
identity or replay pending input.

Report CE_OVERRUN/CE_RXOVER as data-loss/resource errors and disconnect the
affected session, preserving available output and marking capture incomplete.
Report framing/parity errors explicitly; do not claim Raw includes bytes the
driver never delivered. Reads continue independently of slow log storage.
Cancellation requests do not release buffers until actual completion.

## Evidence and later verification

Normative SSH field encodings/message legality come from RFC 4251/4252/4253/4254,
4256, 5656, 8308, 8332, 8731, 9142, and 10042, plus OpenSSH's EtM/GCM protocol
description and the pinned strict-KEX draft. The exact selected names remain
subject to the already required implementation-start/release security recheck.
The accepted limits above are TabTty choices, not universal RFC maxima.

Verify later: independent primitive/wire vectors, all allowed algorithm pairs,
modern OpenSSH and a supported non-strict peer, malformed packets, EXT_INFO at
maximum size, initial/simultaneous rekey, host replacement, MFA rounds, stderr
interleaving/EOF, low-window behavior, Telnet option-loop/CR/IAC fragment cases,
and serial busy/unplug/handshake/overrun. Independent SSH/security review is a
release gate. No such testing or review is claimed by this document.

References: [OpenSSH PROTOCOL](https://github.com/openssh/openssh-portable/blob/master/PROTOCOL),
[strict KEX draft](https://datatracker.ietf.org/doc/html/draft-ietf-sshm-strict-kex-02),
[RFC 4253](https://www.rfc-editor.org/rfc/rfc4253.html),
[RFC 4254](https://www.rfc-editor.org/rfc/rfc4254.html),
[RFC 8308](https://www.rfc-editor.org/rfc/rfc8308.html),
[RFC 10042](https://www.rfc-editor.org/rfc/rfc10042.html),
[Telnet Q method](https://www.rfc-editor.org/rfc/rfc1143.html),
[Telnet host requirements](https://www.rfc-editor.org/rfc/rfc1123.html#section-3.2.5),
[Windows DCB](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-dcb),
[IdnToAscii](https://learn.microsoft.com/en-us/windows/win32/api/winnls/nf-winnls-idntoascii).
