# Confirmed SSH transport policy

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: Q137-Q139 accepted as recommended on 2026-11-08, after
Q133-Q136 acceptance and removal of built-in Telnet automatic login.
Specification only; no implementation, cryptographic execution, interoperability
test, package installation, or publication is authorized.

Read with `ssh-transport-issue.md`, `ssh-authentication-proposals.md`, and the
accepted own-SSH/OpenSSL ADR. Authentication keys and imported key-file
encryption are already selected; the choices here concern live SSH transport.

## Q137: Fixed initial algorithm and compatibility policy

Use a fixed reviewed algorithm policy with no initial user-editable lists,
reordering, weak-compatibility switch, or automatic retry with excluded algorithms.
Use the following preference order within each row, subject to SSH negotiation:

| Area | Accepted preference order |
| --- | --- |
| Key exchange | `mlkem768x25519-sha256`, `curve25519-sha256`, `curve25519-sha256@libssh.org`, `ecdh-sha2-nistp256`, `ecdh-sha2-nistp384`, `ecdh-sha2-nistp521`, `diffie-hellman-group16-sha512`, `diffie-hellman-group14-sha256` |
| Encryption, independently in each direction | `aes128-gcm@openssh.com`, `aes256-gcm@openssh.com`, `aes128-ctr`, `aes256-ctr` |
| MAC for CTR | `hmac-sha2-256-etm@openssh.com`, `hmac-sha2-512-etm@openssh.com` |

GCM uses its authenticated protection without a separate MAC. Never combine its
name with a generic AES-GCM framing assumption: implement the SSH packet format
and key/nonce rules. CTR uses the specified encrypt-then-MAC construction and
must authenticate before releasing plaintext to downstream consumers. Algorithm
names are ASCII protocol identifiers, independent of terminal text encoding.

The ML-KEM/X25519 hybrid is preferred, not required of every server. Negotiate a
listed traditional KEX when no common hybrid is available. This does not make
the entire SSH authentication system post-quantum: ordinary host signatures
retain their accepted scope. Use OpenSSL's ML-KEM-768 and X25519 primitives, with
TabTty implementing the RFC's SSH composition, length checks, validation, and
shared-secret processing. TLS's `X25519MLKEM768` name/construction is not the
SSH algorithm implementation. The libssh.org alias is a protocol identifier,
not adoption of that library.

Support and advertise strict key exchange using the current standard-track draft
name and established OpenSSH name. If correctly negotiated, enforce its ordering
and sequence-reset rules; it cannot be turned off by a profile. Permit a server
without that extension under this AES-only policy. Do not reset sequence numbers
as though strict mode had been negotiated with a non-supporting peer. This is
a compatibility choice, not a claim that absence of strict KEX is equivalent
to its additional protocol guarantees. Do not infer support from version text.
The current draft uses `kex-strict-c` and `kex-strict-c-v00@openssh.com` for the
client; recheck names/status before implementation and release.

Initially exclude SHA-1 KEX and MACs, CBC, 3DES, RC4, UMAC, non-EtM MACs,
ChaCha20-Poly1305, finite-field group exchange, and SNTRUP hybrids. Existing
SHA-1/DSA signature exclusions remain. ChaCha20's exclusion limits the number
of SSH-specific packet-protection constructions to implement and validate; it
is not a claim that OpenSSL lacks ChaCha20 or that SSH's construction equals
generic AEAD. Keep the accepted host/user key types and explicit trust behavior.

A server offering only excluded choices fails with an actionable negotiation
error naming the incompatible category. Do not silently fall back to Telnet,
load another crypto provider, or reconnect using weaker choices. A non-strict
server may still fail because its KEX/signature/cipher/MAC set is unsupported.
Show negotiated algorithm names and strict-KEX state in connection details,
without injecting them into terminal output or session logs.

The initial set deliberately differs from OpenSSH's wider default set. Include
group14-SHA256 (RFC 9142 MUST) and its recommended Curve25519, three NIST ECDH
curves, and group16-SHA512 methods. Authentication ECDSA key support and ECDH
KEX support remain separate protocol capabilities despite shared curve names.
Future security maintenance can change the policy in a reviewed app release;
this is not a permanent promise to retain deprecated algorithms.

## Q138: No SSH compression initially

Use `none` for both directions, with no profile compression setting.
Do not adopt immediate zlib or delayed `zlib@openssh.com` compression initially.
Terminal bytes after SSH packet extraction remain unchanged by this choice,
and Raw retains its accepted received-channel-payload boundary. Compression
is not required to provide terminal functionality; `none` is the protocol's
mandatory baseline. High-volume compressible output may use more bandwidth.
This does not prohibit compressing unrelated application packaging artifacts.

## Q139: Automatic rekey without reconnecting

Initiate rekey when either direction reaches 1 GiB (1,073,741,824
bytes) of protected SSH packet traffic under its current keys, or one hour has
elapsed since the last completed exchange, whichever comes first. Count
transport packet bytes, not terminal characters, decoded text, or recorded file
size; track directions separately. Final byte-accounting and threshold-reserve
rules must be explicit in the transport contract. Honor tighter packet-count,
nonce, block, and algorithm-specific key-use limits before any upper bound.
The ordinary 1 GiB/one-hour trigger is not permission to exceed another limit.

Support peer-initiated rekey and simultaneous initiation through the same SSH
state machine. Do not expose a disable switch or initial user threshold setting.
Rekey preserves the session, tab, terminal state, and log parts; it neither
reruns user authentication nor prompts for the vault master password. Reuse of
the approved host key needs no new trust prompt. If rekey presents a new or
changed host key, apply the accepted explicit host-key trust policy; never learn
it silently merely because an SSH session already exists.

Apply Q131's configured non-authentication protocol deadline to rekey, excluding
any required local trust interaction under that policy. User/application sends
may wait behind key exchange under bounded queue/backpressure rules; do not
interleave packets forbidden by the exchange state. Algorithm/direction-specific
NEWKEYS transitions must prevent premature use or reuse of key/nonce material.
Failure or timeout ends the connection, preserves the tab/output, finalizes
eligible logging, and requires explicit reconnect without input replay. Never
continue indefinitely with exhausted old keys or downgrade after rekey failure.

## Evidence reviewed on 2026-11-08

- RFC 10042 (Informational, August 2026) specifies the ML-KEM/traditional SSH
  hybrids, including `mlkem768x25519-sha256`. OpenSSH supports it from 9.9 and
  prefers it from 10.0; OpenSSL 3.5 supplies the required primitives.
- Strict KEX remains a work-in-progress Internet-Draft (`-02`, July 2026),
  alongside deployed OpenSSH extension names. It identifies AES-GCM as immune
  to the described Terrapin packet deletion and CTR+EtM as not practically
  exploitable by that attack. This supports the accepted non-strict compatibility
  choice for this restricted cipher set, not a universal transport-safety claim.
- OpenSSH supports a broader cipher/MAC/KEX set than selected here. Its initial
  strict extension is enabled when both peers support it; strict-only refusal
  would be an additional product restriction.
- RFC 4253 recommends rekey after one gigabyte or one hour; TabTty fixes
  the product byte unit as GiB. Current OpenSSH defaults vary data limits with
  cipher (1-4 GiB) and do not use a time limit by default. The one-hour policy
  therefore does not claim exact OpenSSH default equivalence. RFC 4344 adds
  packet-count/key-use considerations that must be completed in the contract.

Primary references:

- [RFC 10042 SSH hybrid KEX](https://www.rfc-editor.org/rfc/rfc10042.html)
- [OpenSSH post-quantum guidance](https://www.openssh.org/pq.html)
- [OpenSSL 3.5 ML-KEM](https://docs.openssl.org/3.5/man7/EVP_KEM-ML-KEM/)
- [OpenSSL 3.5 X25519](https://docs.openssl.org/3.5/man7/EVP_KEYEXCH-X25519/)
- [RFC 9142 KEX recommendations](https://www.rfc-editor.org/rfc/rfc9142.html)
- [OpenSSH algorithm and RekeyLimit configuration](https://man.openbsd.org/ssh_config)
- [OpenSSH protocol extensions](https://raw.githubusercontent.com/openssh/openssh-portable/master/PROTOCOL)
- [Strict KEX draft 02](https://datatracker.ietf.org/doc/html/draft-ietf-sshm-strict-kex-02)
- [OpenSSH 9.6 release](https://www.openssh.org/txt/release-9.6)
- [RFC 4253 compression and rekey](https://www.rfc-editor.org/rfc/rfc4253.html)
- [RFC 4344 packet and key-use limits](https://www.rfc-editor.org/rfc/rfc4344.html)

## Remaining technical work

Complete extension/signature negotiation, malformed-message handling, DH/EC/KEM
validation and failure timing, packet-size/count and per-key limits, exact rekey
byte accounting/reserves, non-strict sequence behavior, pause/backpressure and
cancellation races, and channel/PTY/EOF/CLOSE contracts. Validate through reference
vectors, negative cases, interoperability, and independent protocol/security review
after implementation is authorized. Selecting algorithms is not completed security
validation or implementation readiness.
