# TabTty initial implementation readiness

## Macro refactoring, 2026-11-26

The [eight decisions for macro-enabled builds](../drafts/macro-specification.md#macro-refactoring-decisions)
are ready for implementation. Start with duplicate compilation on profile
save. The [acceptance cases](../drafts/macro-validation.md#macro-refactoring-acceptance)
cover the seven editing and execution changes. M56, M62 and M73 in the
same document cover the single v1 trust record and its launch and reset behavior.
Runtime verification is pending.

## Refactoring follow-up, 2026-11-25

The three plans have 24 implementation tickets and one research ticket.
The implementation tickets specify entry points, preserved behavior and
acceptance checks. Start the terminal and persistence plan with #178. Implement
#186 before #187 because both use the encoder's input boundaries. The other
children of the three plans can start independently.

| Plan | Readiness evidence |
| --- | --- |
| [#177 Terminal, transport, recording and persistence](https://github.com/TabTty/TabTty/issues/177) | [Local stores and profile usage](persistence-and-windows.md#opening-locations-and-registration), [terminal observation](native-terminal-core.md#verification-and-replacement-readiness), [SSH credit and serial completion](transports-and-authentication.md), [transfer execution](file-transfer.md#file-management-deadlines-and-limits) and [session input and recording](session-and-recording.md). |
| [#188 Macro architecture follow-up](https://github.com/TabTty/TabTty/issues/188) | [Macro behavior](../drafts/macro-specification.md), [list mutations](../drafts/macro-language.md#lists-and-read-only-api-values) and [editor acceptance](../drafts/macro-validation.md#editor-acceptance). [#197 measured completion](../drafts/macro-completion-latency.md) and recommends deferring the optimization. |
| [#198 WPF refactoring](https://github.com/TabTty/TabTty/issues/198) | [Profiles, editors and tab ownership](persistence-and-windows.md), [group removal](broadcast-and-split-views.md), [file-transfer availability](file-transfer.md) and [highlighting and search](resources-and-validation.md). |

Runtime acceptance for the refactors follows implementation. In particular,
#186, #187 and #204 require comparable before/after measurements for paste peak
memory, short-send allocations and search CPU/latency, respectively. #194
requires actual screen-reader output in both languages.

## Earlier decisions and plans

Architecture review update, 2026-11-18: the
[whole-codebase interview](../drafts/whole-codebase-architecture-deepening.md)
has finalized all six candidates: recording finalization, terminal transport,
verified file commits, vault management operations, shared SSH channels and
shared grapheme ownership. The user accepted candidate 6's revised complete
contract and 39-case future verification plan in Q23, completing the design
review. Its accepted paste policy uses the original shared-source and
existing-input-budget accounting while retaining the configurable 512 MiB
default source cap and confirmation for CR/LF or source size above 1 KiB.
The later independent Q28-Q29 accounting branch is superseded.
All six accepted designs are consolidated in the English/Japanese specifications
and are ready for separately requested implementation. The verification plans
remain required future evidence. This review performed documentation checks only;
it provides no new runtime validation and does not authorize implementation or
prototyping.

On 2026-11-18, the user approved publication of this six-candidate breakdown.
The [whole-codebase implementation plan #40](https://github.com/TabTty/TabTty/issues/40)
and its 27 child issues are published: 26 implementation slices and one
cross-owner verification slice. Read-back verification confirmed the approved
bodies, labels, 27 native parent relationships and 34 native blocking relationships.
The 134 accepted verification cases are assigned to the slices. Publication
does not start implementation or prototyping; those require a separate explicit
request.

The [current codec decision](../drafts/terminal-codec-consultation.md)
records 47 supported identities and the use of strict Win32 or Windows ICU
conversion. The decision includes delegating Japanese JIS designations to ICU.

On 2026-11-17, the user approved the ticket breakdown and requested publication.
The [implementation plan #16](https://github.com/TabTty/TabTty/issues/16) and its
23 child implementation tickets are published. The user subsequently requested
implementation. See the [implementation status](implementation-status.md).

Architecture update, 2026-11-16: the user selected complete Rust terminal
processing without managed fallback in
[ADR 0007](../adr/0007-complete-terminal-processing-in-rust.md).
The [native terminal design](native-terminal-core.md) defines the target
coverage and verification. The recording-interface design is confirmed through
Q5, and shared interpretation with admission/execution walks and verification
is confirmed through Q8. The [closure design](tab-and-window-closure.md) records
Q9's ownership scope, amended Q10's transfer-window closure for both bulk and
automatic disconnected-tab removal, Q11's named complete operations, and Q13's
failure results and ownership after a caller stops waiting. Q12a accepts ordinary
same-target transfer completion without restarting an open close confirmation.
Q12b accepts a later window-close request joining an existing tab closure and
handling the remaining tabs with required confirmation, without duplicate work
or a user retry. Q14 in the
[completed interview](../drafts/terminal-architecture-deepening.md) accepts the
exceptional changed-target outcome and confirms candidate 3's final shared
understanding. All three designs—recording operations, shared native parser
rules, and tab/window closure—are ready for separately requested implementation.
This design review performed no implementation, prototyping, or runtime
validation. Current source still implements the earlier partial Rust design;
historical readiness and validation below do not establish this redesign's
implementation completion.

Scope update, 2026-11-14: the user added broadcast input and split terminal
views to the initial release. Their [feature interview](../drafts/broadcast-and-split-issue.md)
has resolved Q162-Q199, consolidated in the
[broadcast/split contract](broadcast-and-split-views.md). Q197 resets all
selection on scope-mode changes; Q198 selects the group-removal destination.
Final shared-understanding review (Q199), including reset timing, is accepted.
This extension's specification and implementation preparation are complete.
The user subsequently requested implementation. See the
[broadcast and split view validation status](implementation-status.md#broadcast-and-split-view-validation-2026-11-14)
for the implemented feature and acceptance evidence. The historical readiness
below covers the earlier baseline.

The feature is published as
[GitHub issue #1](https://github.com/TabTty/TabTty/issues/1) with
`ready-for-agent`, including the user-confirmed testing seams.

The initial-release SFTP specification was completed on 2026-11-13 under
[accepted Q1–Q36](../drafts/file-transfer-issue.md) and implemented on
2026-11-14. The [SFTP file transfer contract](file-transfer.md) defines the
transfer window, channel multiplexing, file management, and acceptance cases.
The [implementation status](implementation-status.md#sftp-validation-2026-11-14)
records the loopback results and outstanding release checks.

Architecture follow-up, 2026-11-14. Architecture Q1-Q27 are accepted in
the [file-operation](file-operations.md), [retained-history
observation](ai-history-observation.md), and [broadcast-input
operation](broadcast-input-operations.md) designs. The user subsequently
authorized implementation and issue publication. The
[GitHub plan](https://github.com/TabTty/TabTty/issues/4) links the eleven
implementation tickets. See the [architecture validation
record](implementation-status.md#architecture-follow-up-validation-2026-11-14)
for executed checks and outstanding acceptance cases.

Historical baseline status, 2026-11-10: specification and implementation
preparation were complete for the scope at that date. The
user's Q161 choice is UTF-8-only imported-key passphrases. **Implementation,
prototyping, installation, commit/push, issue publication, and Store publication
have not been authorized or performed by this specification work.** Wait for
the user's explicit implementation request.

This is the historical specification handoff. The user subsequently authorized
development. See [implementation-status.md](implementation-status.md) for
current build and acceptance evidence.

## Authoritative specification set

Accepted user decisions in the interview drafts remain the product authority.
The contracts below complete their internal details and replace the historical
"remaining technical work" lists in those drafts. They must not be used to
override an explicit user decision. Any newly discovered contradiction requires
correction before dependent implementation, rather than choosing a convenient
interpretation silently. Initial engineering limits are labeled separately from
accepted performance targets and measured results.

| Branch | Completed contract |
| --- | --- |
| Profile/settings schemas, storage/recovery, registration identity, activation, windows/DPI | [Persistence and windows](persistence-and-windows.md) |
| Session generations, receive-source isolation, input ordering, recording eligibility, Text revisions/retirement, file ownership | [Session and recording](session-and-recording.md) |
| VT/key/mouse baseline, Unicode width, IME/UIA, JIS/EUC mapping, malformed units | [Terminal and encodings](terminal-and-encodings.md) |
| Complete Rust terminal ownership without managed fallback; detailed design in progress | [Native terminal module](native-terminal-core.md) |
| SSH identity/framing/rekey/auth/channel, Telnet negotiation/NVT, serial controls/errors | [Transports and authentication](transports-and-authentication.md) |
| Initial-release SFTP extension | [File transfer](file-transfer.md) |
| Initial-release broadcast input and two-level split views | [Broadcast input and split views](broadcast-and-split-views.md) |
| Portable `.tttykey` byte format, AAD/KDF/key use, secret ownership, import and file-failure rules | [Vault format](vault-format.md) |
| Aggregate budgets, scheduling, deterministic acceptance workload, deployment/servicing, release gates | [Resources and validation](resources-and-validation.md) |
| Deferred macro seams | [Future macro constraints](../drafts/future-macro-design.md) |

The root [glossary](../../GLOSSARY.md), [SSH ADR](../adr/0001-own-ssh-client-with-openssl.md),
and repository instructions apply throughout. Exact C# type names, internal
class decomposition, drawing optimizations, and test fixture implementation are
development tasks within these contracts, not unresolved product choices.

## Initial release scope

- C#/WPF/.NET 10 LTS application with the Rust terminal module specified in
  ADR 0007, self-contained Windows 11 x64; ARM64 optional. Native
  TabTty terminal control; no xterm.js, WebView2, SSH.NET, ssh.exe, or ConPTY.
- Microsoft/OpenSSL dependency policy and scoped OpenSSH bcrypt_pbkdf exception.
  ADR 0007 retains the Rust/standard-library exception from ADR 0006.
  TabTty owns SSH protocol/session logic; OpenSSL supplies cryptography.
- English and Japanese UI follows the system language by default, with English
  as the fallback. The surrounding UI supports system, light, and dark themes.
  The terminal defaults to white on black and honors ANSI colors.
- Saved `.ttty` profiles are required for SSH/Telnet/COM. The revised
  [profile trust specification](profile-trust.md) defines when file activation
  may start a connection and when registration trusts the loaded revision.
  Implementation and validation of these rules are pending. Permitted file
  connections open in the last active terminal window. Multi-window drag/merge
  preserves live sessions. Telnet/serial login remains manual; no initial macro runtime.
- SFTP v3 provides two-pane file and folder transfer and file management on an
  on-demand channel within the invoking tab's authenticated SSH connection.
  Remote names use the effective terminal transmit encoding captured when the
  transfer window opens. See the [SFTP contract](file-transfer.md).
- Raw and Text receive-only, Terminal includes enabled local echo; exact mode
  semantics, filenames, pause/retry, resize/retirement, and failure policies stand.
- Portable shared `.tttykey`, shared unlock until manual lock or exit, transient selected
  secrets, explicit host-key trust/replacement, and UTF-8-only source-key import
  passphrases without normalization or automatic encoding retries.
- Regex match-only highlighting, retained-output search, native IME/accessibility,
  paste preview/pacing with app-run-only remembered values, and accepted defaults.

## Review completed in the documentation phase

Primary-source and repository fact reviews checked the terminal/codec, SSH/vault,
and profile/window branches. Consolidation resolved actual differences: JIS
Roman versus historical ASCII handling, unsupported JIS repertoires, CP932/C1
classification, erase versus history retention, UIA GetText semantics, IME cancel
on tab change, remote right-click ownership, Windows IDNA processor selection,
and vault external-change lifetime. These were documentation/source checks,
not executed implementation tests or an independent security audit.

The vault's retained random DEK is now explicitly a root for per-encryption
HKDF payload keys, preserving the accepted whole-payload AES-GCM and no-repeat-
prompt behavior. This closes the cloned/restored-file key-use contract without
claiming global counters, collision freedom, rollback prevention, or certification.
The fixed v1 framing/AAD and accepted master-password change remain explicit.

## What readiness does and does not mean

Readiness records the accepted product decisions, external file contracts,
event and failure behavior, initial resource policy, and verification plan.
The [implementation status](implementation-status.md) tracks code and executed
checks as each part of the scope is built.
Requirements are not proven by being written: protocol interoperability,
cryptographic vectors, accessibility, clean-PC deployment, performance, and
fault recovery must pass after implementation and before release.

Release gates include independent custom-SSH/vault/native-security review and
rechecking current primary guidance, supported Windows/.NET/OpenSSL versions,
dependency provenance/licenses, and servicing. If tests reveal an impossible
or unsafe contract, report it and revise the specification explicitly; do not
silently reduce compatibility, lose output, or weaken credential protection.

Initial ZIP delivery uses manual updates. Store identity/signing, final packaged
storage/transfer/uninstall policy, and certification are later Store gates.
Macro language/runtime/API, optional ARM64, a replay viewer, and custom log-name
UI are deferred features and do not block this initial specification readiness.

No GitHub Issues have been published as a result of consolidation. The drafts
and this index remain available for later authorized issue breakdown and work
sequencing. Specification completion itself grants no external-write authority.
