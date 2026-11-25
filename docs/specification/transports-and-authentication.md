# Transport and authentication contracts

Specification baseline, 2026-11-10, with the shared SSH channel contract accepted
in Q17-Q19 on 2026-11-18. The accepted SSH/OpenSSL ADR and Q131-Q143/Q161 remain
authoritative. Exact algorithm names/order remain in
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

When a known destination presents an additional key format at connection or
rekey, identify it as a known host in the approval dialog. Show its saved key
types and fingerprints alongside the presented type and fingerprint. Default to
Cancel. Explicit approval adds only that key and retains the other formats.
This lets the user recognize the existing trust relationship when verifying
the additional key independently. [RFC 4251 §9.3.4](https://www.rfc-editor.org/rfc/rfc4251.html#section-9.3.4)

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

Apply the profile trust decision before using the captured SSH destination,
account or credential reference. After the connection is permitted, verify
the host key before retrieving credentials from the selected vault. Explorer's
Edit action opens the profile for inspection. The connection-time trust
decision is specified for a future implementation. [Profile trust](profile-trust.md)

Show this notice in the profile editor's **Password authentication** field and
in the saved-password picker. Explain SSH host keys in the dialogs for first
approval and changed keys.

> When password authentication is used, TabTty automatically sends the saved
> password to the destination specified in the profile to authenticate as the
> configured user. Editing the profile outside TabTty can change the destination
> or username.

The captured connection profile supplies the SSH username for every user
authentication request, including `none`, partial success, and method fallback.
Selecting a vault entry changes only the credential reference.
[Profile fields](persistence-and-windows.md#profile-v1-fields)

After host key verification and before the first SSH user authentication request,
resolve enabled saved references by exact name and expected kind in the selected
vault. A kind mismatch ends the attempt. At secret handoff, enforce the same
reference and revision checks under the existing vault rules.
[RFC 4252 §5](https://www.rfc-editor.org/rfc/rfc4252.html#section-5)

If the selected vault is needed and its file is confirmed missing or zero
bytes, mark all methods that require it unavailable for the attempt. Show the
configured path and observed state. For a saved reference, explain how to
choose the intended vault or restore its backup. For connection-time key
selection, direct the user to **Manage vault** to import the intended key. If the
selected valid vault lacks the named saved key,
report the missing key and mark public-key authentication unavailable for the
attempt. Leave the configured path, file, and references as observed.

After reporting either condition, continue with the next available method
allowed by the profile and currently offered by the server. For example, a
password method already configured for manual entry can continue while a
saved password that requires the same missing vault remains unavailable. End
the attempt if no method can continue.

When an unavailable vault, a missing saved credential or a kind mismatch ends
the attempt, the terminal tab's status shows the failed connection.
Present the reason in an upper-right
floating notice with **OK**. For an inactive tab, mark it unread and announce
the failure once in a modeless window that identifies the affected tab and
leaves keyboard focus in place. **OK** on either surface marks the same notice
as read. If authentication is waiting for a decision or input, present its
dialog even when the relevant tab is inactive. Existing rules govern
cancellation, authentication fallback and other vault failures.
[Notification policy](notification-policy.md)

When the profile has no saved key reference and the user chooses a key during
SSH authentication, show the verified host, port, and fixed SSH username. Offer
every private key in the selected vault. If the vault is valid and has no private
keys, report that state. Treat public-key authentication as unavailable for that
stage and continue with the next method allowed by the profile and currently
offered by the server. End the attempt if there is no such method. When keys are
available, offer **Try another authentication method** in the picker only when
another eligible method can continue the current stage. That action skips
public-key authentication for the stage. **Cancel connection** ends the attempt.
Identify vault choices by unique name, and show the public-key type and SHA-256
fingerprint for keys. Render control and formatting characters visibly in entry
names and in the SSH username shown above the choices.
[RFC 4252 §5.1](https://www.rfc-editor.org/rfc/rfc4252.html#section-5.1),
[RFC 4252 §7](https://www.rfc-editor.org/rfc/rfc4252.html#section-7)

The first-use and changed-host-key dialogs ask the user to compare the fingerprint
with one obtained independently from the administrator before accepting. During
every keyboard-interactive round, keep a TabTty notice with the verified host
and port visible above the scrolling questions. It identifies the server as the
source of the questions and the recipient of the answers, and reserves the
TabTty vault master password for local vault operations. The submit button
explicitly sends the answers to the server. [RFC 4256, section 3.3](https://www.rfc-editor.org/rfc/rfc4256.html#section-3.3)

Enter ssh-userauth service only after encryption and host verification. Dispatch
method-specific message 60 against the outstanding method/request. At each
authentication stage, try the configured password and public-key methods in
profile order when the server offers them and existing attempt limits allow.
Use one selected key. Keyboard-interactive is enabled by default and has the
lowest priority among methods available at that stage. Use it immediately if it
is the sole eligible method offered, including at the first stage. If the
profile's **Allow keyboard-interactive authentication** checkbox is cleared,
exclude it at every stage.
[RFC 4252 §4](https://www.rfc-editor.org/rfc/rfc4252.html#section-4)

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

For password requests and every keyboard-interactive response, including an
empty response, pad the encrypted SSH packet body to the smallest multiple of
128 bytes that fits the payload, the padding-length byte and at least four
random padding bytes. For both GCM and CTR+EtM this uses 4–131 padding bytes.
The visible length thus reveals a 128-byte range for the combined payload.
Account for the full transmitted size, including length field and tag or MAC,
in packet limits and traffic-key usage. [RFC 4251 §9.3.9](https://www.rfc-editor.org/rfc/rfc4251.html#section-9.3.9)
and [RFC 4253 §6](https://www.rfc-editor.org/rfc/rfc4253.html#section-6)

Render banners/disconnect/debug/instruction bodies as inert native UI text
outside terminal logs, with controls escaped except ordinary layout whitespace.
Keep at most 64 messages/256 KiB per tab, 16 KiB displayed per non-interactive
body with an explicit truncation indication. Interactive prompt limits above
fail rather than silently shorten a question. Do not persist arbitrary server
bodies or secret replies in diagnostic output. Native error diagnostics use
bounded codes/categories and exclude secret material.

## Channel and termination

Open one session channel for the interactive terminal at startup, request PTY
then shell with want-reply=true. Become Connected only after both succeed,
preceding delivery settles successfully, and the attempt remains current and unstopped.
No exec/no-PTY fallback, automatic startup command, environment forwarding,
forwarding, or agent. The initial-release scope now includes on-demand
[SFTP file transfer](file-transfer.md) on a separate subsystem
channel within the tab's authenticated connection under
[ADR 0003](../adr/0003-reuse-the-tabs-ssh-connection-for-sftp.md). The contract
below defines terminal behavior; [shared channel ownership](#shared-ssh-channel-ownership)
completes the common SSH rules. The transfer contract defines SFTP lifecycle,
channel isolation, and shared-transport scheduling.
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
CLOSE may occur without EOF; finalize once and reply at most once. Both closes
resolve protocol closure, while delivery, actual release and identity retirement
must also satisfy the shared contract below. Local Disconnect cancels unsent
input and gives peer cleanup at most two seconds, without exit/Ctrl+D. Recording
drain has its own ownership and wait choice. Reconnect is explicit and fresh.

### Shared SSH channel ownership

Q17-Q19 of the [architecture interview](../drafts/whole-codebase-architecture-deepening.md#5-shared-ssh-channel-invariants)
accept a shared SSH channel module behind the existing
[terminal transport interface](session-and-recording.md#terminal-payload-transport-ownership).
It applies within one authenticated connection generation under ADR 0001 and
ADR 0003. Reconnect creates a fresh generation; old callbacks, effects and
charges remain with their original owners until actual settlement. This is a
design-ready contract, not implementation authorization or runtime evidence.

| Owner | Responsibility |
| --- | --- |
| Shared channel module | Local/peer ID pair and generation; open/state validation; independent windows and packet limits; ordered DATA/extended DATA; request-reply FIFO; directional EOF/CLOSE; operation outcomes and actual release. |
| Connection owner | Collision-free identity allocation, registration and routing coordinated with channel retirement; one packet reader and serialized writer; fair bounded scheduling; global-request replies, rekey, liveness and connection failure. |
| Terminal transport/session | PTY then shell, committed geometry, readiness and exit facts; candidate 2's input/echo policy, Rust processing and recording admission. |
| SFTP owner | Subsystem then INIT/version, frame parsing, SFTP request IDs and legal reply reordering, file-operation outcomes and recovery under the [transfer contract](file-transfer.md#ssh-and-sftp-channel). |

Use named complete operations with an awaited ordered consumer and a finite,
bounded synchronous peer-request policy installed before OPEN. Illustrative
names describe the interface, not finalized types:

```text
OpenSession(options, receiveBinding) -> channel + open observation, or rejection
channel.RequestReply(preparedRequest) -> request operation, or rejection
channel.Notify(preparedNotification) -> submission operation, or rejection
channel.Send(bytes, permittedStopOffsets) -> send operation, or rejection
channel.Close(cause) -> the channel's close operation
```

RequestReply always requests a reply; Notify reports local submission, not peer
acceptance or rendering. Normal requests, notifications and data require a
confirmed open; Close remains available while opening and repeated Close joins
the same shutdown. Reserve binding, receive/control/close capacity, identity
coordination and operation/result records before accepting OPEN. Reserve bounded
immutable copies of requests, bytes and stopping offsets at admission; the caller
may then reuse its input. Validate offset range/order; the terminal or SFTP owner
supplies character/frame meaning. Rejection issues nothing and retains no input.
No unbounded capacity-wait queue, caller credit-return/release recipe, arbitrary
packet interface or public local-EOF operation is introduced.

Expose bounded, immutable, monotonic/coalesced outcome and progress observations.
Keep admission, protocol/submission outcome, stop status, prior-delivery
settlement, protocol closure, channel-owned actual release and identity retirement
distinct. Successful protocol results survive later delivery failure. Actual
release includes pending writer/control work, callbacks, allocations and handoffs,
but not separately transferred parser/file I/O, recording or retained terminal
output. A timeout, completed close exchange or dropped view alone proves no such
release. Cancelled waits unregister observation only; the module continues to
observe owned work and failures. Explicit RequestStop targets its original work,
may prevent uncommitted issue or stop at a permitted point, and cannot recall
committed work or reopen a closing channel. Do not retain unbounded histories,
replay retired operations or evict unresolved ownership to reclaim capacity.

#### Packet commitment and sending

The serialized writer commits a packet before the first irreversible outgoing
cipher/nonce/sequence change, not merely before calling the socket. A per-operation
stop either prevents commitment or leaves the whole committed packet owned by
the writer through actual completion. Register reply correlation and reserve
credit in that ordering before a peer response is possible. Only work proved
uncommitted may relinquish those reservations. Do not pass individual-operation
cancellation into committed encryption/write work, skip its packet and continue
the encrypted stream, roll back cipher state or replay the packet. Connection
shutdown/failure follows its own rules while retaining pending I/O and uncertain
effects. Rekey restrictions still govern channel/control packet commitment.

Preserve per-channel send order and checked peer/local window and packet limits.
Publish a prompt monotonic definite local-submission byte prefix, distinguishing
uncommitted remainder from committed/pending/uncertain bytes. The terminal owner
maps newly confirmed complete units to echo/pacing once; partial characters are
not echoed and local submission does not prove remote receipt. Stop cannot move
behind committed bytes: on a healthy channel it reaches the next permitted
offset, retaining any required suffix while waiting for credit. Session-owned
codec/bracket cleanup remains unchanged.

Close rejects new work and may abandon uncommitted suffixes, including incomplete
application units, with truthful incomplete results. After the current committed
packet settles it can schedule CLOSE without waiting for a zero-credit Send to
finish a frame. It cannot force a stuck writer to finish or claim release at a
deadline. An incomplete SFTP frame must finish safely under its existing owner
or the channel must stop later DATA and close; never append another frame to an
abandoned prefix. An uncertain final packet may have delivered a complete mutation
request even if whole-frame send never succeeded. The SFTP owner applies
UnknownOutcome and recovery rules; shared SSH state does not acquire file semantics.

#### Ordered delivery, requests and credit

The reader validates/routes packets without awaiting application consumption.
One ordered nonrecursive consumer runs per channel, outside transport locks;
it cannot occupy the session reducer across I/O/credit/rekey waits or await work
requiring itself or a later callback. A blocked consumer or zero-credit channel
does not hold the common writer or stop other channels and allowed control/rekey
work. Schedule terminal input/control ahead of bulk data with bounded fair
transfer service. Mandatory control uses reserved bounded ownership.

Register want-reply requests in actual wire order. Work prevented from commitment
has no wire reply slot; committed or uncertain requests retain their original
slot until reply or safe termination despite stop/wait cancellation. Notify has
no slot. Unsolicited/duplicate replies follow protocol-error rules, never FIFO
remapping. Global requests and SFTP request IDs keep their separate owners.
Protocol results progress independently of consumption. Each reply's immutable
prior-delivery fence covers only the admitted receive prefix at that reply
position, not all future output. Terminal startup binds before opening, sends
PTY using current committed geometry, then shell after PTY success. Readiness
requires shell success, successful prior delivery and the current unstopped
attempt. Startup failure/stop preserves safe early output and original-generation
draining under candidate 2. SFTP callbacks cannot wait on subsystem success,
INIT/version completion, a whole future frame or another request reply.

The finite peer-request policy recognizes bounded terminal exit-status/signal
facts or rejects unsupported SFTP requests without I/O, UI, reentry or data waits.
The module owns required replies in receive order, at most once, independently of
the consumer; unknown requests are refused only when want-reply is set. Application
facts retain ordered delivery outside payload/logs. Exit facts neither imply EOF
nor successful exit by default, nor prevent later valid data. Unknown extended
data retains the existing bounded discard/credit policy.

Maintain separate ledgers for consumption eligibility, reserved/queued
WINDOW_ADJUST, committed possibly delivered grants and completed submission.
A queued grant is not peer entitlement. Reserve its receive capacity before
commitment; an in-flight grant can reach the peer before local completion, so
receive validation must account for that possibility without a second grant.
Only proved-uncommitted grants can be cancelled and their reservation reused.
Uncertain issue retains effects/ownership until the connection outcome settles;
never return credit twice or infer cancellation from a stopped observer.

Terminal and SFTP share one procedure to build and submit each `WINDOW_ADJUST`,
commit the grant through the writer, and collect the next eligible grant for
that channel. The existing per-channel eligibility and failure rules govern
the procedure. Verify that a four-byte queued grant adds no peer credit, its
commitment permits exactly four bytes before local completion, and consumption
while it is outstanding contributes once to the next grant. Closing SFTP with
a grant queued preserves terminal reception. A terminal-side send failure
follows the existing connection failure path.
See [#180](https://github.com/TabTty/TabTty/issues/180) and
[RFC 4254 §5.2](https://www.rfc-editor.org/rfc/rfc4254#section-5.2).

Terminal credit eligibility follows Rust processing, separately accounted
recording admission or its narrow failure, bounded generated-reply admission and
payload allocation release. It awaits neither reply/WINDOW_ADJUST transmission,
disk flush nor drawing. SFTP consumes only prefixes adopted into separately
bounded parser/frame storage; validate lengths before allocation, reserve reply
capacity with requests, and never await local file I/O or later delivery inside
the callback. Copy overlaps and upper-owner buffers remain charged after channel
credit returns; [SFTP framing and reopen](file-transfer.md#sftp-channel-lifetime-and-ownership)
define the remaining application rules.

Preserve payload-byte window accounting for DATA and extended DATA, including
stderr, checked uint32 arithmetic, 1 MiB receive windows and 32 KiB channel-data
limits. Transport packet limits independently include framing overhead. This is
an explicit interoperability choice differing from
[verified EID 3878](https://www.rfc-editor.org/errata/eid3878), whose correction
includes the SSH string length field. The 2026-11-18 review found payload-only
stream accounting in [OpenSSH channels.c](https://raw.githubusercontent.com/openssh/openssh-portable/master/channels.c)
(OpenBSD revision 1.478, 2026-10-30) and the pinned
[OpenSSH 10.2p1 source](https://raw.githubusercontent.com/openssh/openssh-portable/V_10_2_P1/channels.c);
the four-byte addition applies to its datagram form. The
[RFC Editor's informative errata rendering](https://www.rfc-editor.org/rfc/inline-errata/rfc4254.html)
was used to inspect the correction. Do not add four bytes or negotiate a heuristic
accounting mode. This choice adopts no OpenSSH code and expands no dependency
exception. Recheck guidance at implementation start and before release; independent
vectors and real-peer interoperability must verify the choice.

#### Closure, failure and bounded ownership

An OPEN prevented before commitment can retire its unexposed identity after local
settlement. A committed OPEN retains registration after stop/timeout; late
confirmation binds the peer ID only for that old instance's cleanup/CLOSE, never
PTY/shell/subsystem startup or replacement activation. Late refusal resolves only
that attempt. An unanswered committed OPEN remains unresolved until safe protocol
or connection termination. The registry follows channel retirement facts; it
cannot independently discard a closing identity or reuse it on timeout.

EOF is directional and retains permitted reverse sending. CLOSE may arrive
without EOF; finalize receive once and respond at most once. Protocol closure,
pending delivery, actual release and identity retirement remain separate, including
simultaneous CLOSE and rekey-deferred controls. Failure is observable independently
even when no final consumer callback can succeed. Release individual allocations
only when their owners finish; retain unresolved identity/effect records. Within
each current connection generation, keep one opening/live/closing SFTP slot per
tab until safe identity retirement and channel-owned release. Never completing
OPEN/CLOSE may block explicit reopen while a healthy terminal remains usable;
retain the existing bounded wait/admission policy, not a new slot or forced
disconnect. A fresh connection does not erase old-generation charges.

Terminal Disconnect retains its existing at-most-two-second peer grace and
connection termination. The local request is not the receive cutoff and this
grace does not bound actual I/O, consumer or recording release. Do not add a
universal SFTP close deadline, automatic reconnect/replay or new close confirmation.

Ordinary SFTP channel/subsystem refusal, malformed SFTP/version/replies and SFTP
consumer/file failures follow the affected-transfer policy with a healthy terminal
preserved. Invalid SSH framing/authentication, channel identity/state, excess
data/window overflow and connection/rekey/liveness failures retain connection-wide
failure semantics. Valid unknown requests/extended types follow refusal/discard
rules; resource exhaustion is not fabricated peer protocol corruption. Consumer
failure after partial application preserves safe effects, stops ordinary delivery
and reports failure without replay, false credit or a required final callback.
Terminal recording-only failure retains its narrow independent policy.

Keep existing SFTP guards of 2 MiB per live/closing channel, 32 MiB application-wide
and a 256 KiB frame body, plus separate terminal/log, request, listing/result and
local-provider limits. Account for peer entitlement, admitted reads/payload,
copy overlaps, prepared sends, parser/reply storage and actual pending I/O; count
shared allocation once only when ownership is truly shared. Also bound metadata,
peer requests/facts, mandatory replies, fences, FIFO records, deferred controls,
observers, snapshots, stop/close state, admission waiters and closing identities.
Tiny/zero-byte traffic and repeated stops cannot create unlimited records. Reserve
control/close capacity before opening without promising infinite request-flood
handling; reject new local work or apply the existing affected-resource failure
policy on exhaustion. Never stall the common reader on an application consumer.
Concrete new metadata limits, buffer form, registry strategy and instrumentation
are engineering choices to fix and validate at implementation start; they do not
justify synthetic release or a finite-completion promise for stuck external I/O.

#### Future shared-channel verification

The accepted [27-case plan](../drafts/whole-codebase-architecture-deepening.md#candidate-5-verification-plan)
uses these ordinary operations and observations with controlled internal peer,
I/O and time adapters, the real terminal/session/Rust and SFTP/file-operation
owners, independent protocol/cryptographic vectors and real peers. This is future
evidence, not tests executed by the specification work. Replace helper tests only
after equivalent behavior is covered at this seam; retain independent framing
and cryptographic coverage.

| Case | Required evidence |
| --- | --- |
| 1. Admission rejection, invalid offsets, original-input mutation | No issue on rejection; accepted bytes/requests/offsets remain immutable and charged; bounded waiters. |
| 2. Different local/peer IDs, windows and packet limits | Correct routing, recipients, segmentation and independent checked credit. |
| 3. Stop before commitment, during cipher/sequence changes and pending write | Only uncommitted work can be dropped; actual writer settlement, no early refund or replay. |
| 4. Packet/window splitting of terminal units | Prompt monotonic prefix, complete-unit echo/pacing once, no partial echo or uncertain replay. |
| 5. Partial SFTP frame, zero credit and Close | Close does not wait for the frame; after the current packet settles, no later DATA; terminal/control progress. |
| 6. Uncertain final mutation-frame packet | Possible complete delivery and applicable UnknownOutcome even without whole-write success; no blind retry. |
| 7. Stopped queued/committed requests, Notify and late replies | Original FIFO slot retained where required; no slot for prevented work/Notify or reassignment of late replies. |
| 8. Slow consumer, request reply and continuing output | Protocol result independent; fence covers only preceding delivery. |
| 9. Early DATA/stderr, shell success, delivery failure and stop | Safe output/source order retained; readiness requires all three accepted conditions. |
| 10. Exit facts/unknown want-reply requests with paused consumption | Bounded ordered once-only protocol replies progress; facts stay ordered outside payload/logs. |
| 11. Tiny/zero-byte DATA and interleaved extended data | Payload-byte credit, source order, finite metadata and no false credit. |
| 12. SFTP prefix/body splits, maximum/invalid length, multiple frames per chunk | Validation before allocation; incremental consumption without future callbacks; accurate ownership charges. |
| 13. Eight file requests plus browse/control and blocked local I/O | Existing request/buffer/provider bounds, retained charges and permitted terminal/control progress. |
| 14. Queued, committed, completed and uncertain WINDOW_ADJUST | Separate eligibility/peer entitlement; safe early peer response, no double grant or early reuse. |
| 15. Zero-credit channel or blocked consumer during rekey | Other channels and permitted control/rekey progress without lock/reducer cycles or unfair transfer starvation. |
| 16. Prevented/committed stopped OPEN and late confirmation/refusal | Unexposed identity can retire after local settlement; late outcomes clean up only the original attempt. |
| 17. Never-arriving OPEN/CLOSE and repeated reopen | One slot per current connection generation, bounded waiters/records and existing wait budget; no surprise open or forced disconnect. |
| 18. Old close retires before/after explicit reopen wait ends | Only a still-live explicit request admits a fresh channel. |
| 19. Directional EOF, reverse send, exit without EOF and CLOSE without EOF | Finalize once where needed, preserve permitted reverse/later traffic, no invented exit success. |
| 20. Simultaneous CLOSE, pending write/control/callback | At-most-once close issue; closure, delivery, release and retirement remain distinct. |
| 21. Terminal Disconnect with incoming output and slow recording | Existing peer grace/cutoff and original-generation ownership; no two-second save/release claim. |
| 22. SFTP/file failure versus malformed SSH/invalid ID/window overflow | Correct local or connection-wide failure; unknown types are not confused with corruption. |
| 23. Partial consumer failure and rejected final callback | Independent failure, safe effects preserved, no replay/false credit, actual cleanup observation. |
| 24. Parser/provider buffers, pending mutation and reopen after closure | Upper-owner charges/outcomes and existing mutation/refresh guards survive channel release. |
| 25. Old I/O/consumer/close completes after reconnect | Only old ownership settles; new matching, credit, status, readiness and slot remain untouched. |
| 26. All metadata/control/result/observer limits and repeated cancellation/Close | Finite ownership, no growing tasks/history, no evicted unresolved identity or synthetic release. |
| 27. Independent credit vectors and OpenSSH with tiny messages/stderr/exhaustion | Verify EID 3878 interoperability choice, byte totals and maximum payloads independently of local encoders. |

## Telnet

Select SSH initially when creating a new profile. Opening, editing or duplicating
an existing profile uses its saved transport. The Telnet settings explain that
passwords, commands and output travel in clear text. Show the unencrypted
transport in the connected tab's status even when the profile was opened directly.

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

Use the same COM request sizing and completion classification for ordinary
input and paste. Compare bytes completed with the physical request, which is
at most 1,024 bytes. A short physical write during paste confirms its prefix
and takes the paste-failure disconnect path. Echo only complete confirmed
units. Skip the remaining input, codec suffix and closing paste marker. A
full 1,024-byte write followed by a full 976-byte write completes a 2,000-byte
paste. TCP retains its ordinary progress on a positive short write.
See [#185](https://github.com/TabTty/TabTty/issues/185) and
[Win32 `WriteFile`](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-writefile).

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

Verify that the SSH username stays editable with saved references configured
and keeps its value when those references change. Connect with two profiles
that share one vault password and use different usernames, and verify that
each request uses its own profile's username. Verify that the connection-time
key picker lists all private keys and displays the captured SSH username.

Check the password notice in the profile editor and saved-password picker.
Verify that it describes automatic submission, the configured destination and
username, and the effect of external edits. Check that the first-approval and
changed-host-key dialogs explain host-key fingerprint verification.

Exercise missing and zero-byte targets for both a saved reference and
connection-time key selection, and an absent saved key entry in the intended
vault. Check the visible reason, continuation with an allowed server-offered
method using the same account, and failure when none remains. Verify that a
saved password in the same missing vault cannot be used and that a kind mismatch
stops authentication even when another method is available. Exercise a valid
vault with no private keys, with and without an alternative method offered by
the server. Verify that
**Try another authentication method** advances the current attempt, **Cancel
connection** ends it, and SSH leaves vault initialization to user management.
Verify that both method selectors stay visible and that a new profile shows
**Password authentication** first and **None** second, with the password
settings between the selectors. Set a second method and verify its settings
appear immediately below its selector. Swap the order, then change the second
selector to **None** and select a second method again. Verify that each
settings field follows its method with its draft reference and is hidden
while its method is unselected. Check that the standalone keyboard-interactive
checkbox follows both method sections. Verify that a saved profile opens with
its configured order. Check configured priority when both methods are offered,
keyboard-interactive at the first stage when it is the only offered eligible
method, keyboard-interactive after a partial success, and its exclusion when
the standalone checkbox is cleared.

Verify that both credential fields show **Select** followed by **Reset** to
their right. Open each picker with **Select**, including when unlock or setup is
required. Choose or replace a saved entry and verify its name appears in the
field. Cancel the picker and verify the draft does not change. With references
set and the vault locked, use **Reset** on each field and check that it clears
only that draft reference and leaves the vault locked and its entries intact.
Verify that the password field switches to **Ask at connection** and asks for
a password at connection after saving. Verify that the private-key field
switches to **No key selected**.

Verify that an SSH profile with public-key authentication first or second and
an empty private-key reference fails save validation. Check that the message
asks the user to select a private key, the draft stays editable, and no file
or registration changes. Select a key and verify that saving succeeds. Verify
that password-only profiles can save with an empty reference and that existing
profiles with public-key authentication and an empty reference can still open
for editing.

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
