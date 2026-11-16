# Specification interview frontier

## Completed feature interview: terminal automation macros

The [terminal automation macro interview](macro-issue.md) uses independent
question numbering. Q1-Q77 are accepted as amended, including all Q60-Q77 on
2026-11-14 and explicit authorization to create the specification draft.
The [behavior draft](macro-specification.md), [language/API](macro-language.md)
and [resource/acceptance draft](macro-validation.md) consolidate those decisions
and record routine details under Q69, accepted in final review. ADR 0004 records the selected
TabTty-owned interpreter. The default password is the connection profile's
configured saved-password reference, independent of authentication success;
each send obtains its current entry value without an authentication-value cache.

Q70-Q77 are accepted: pause during connection/authentication, independent holds
and repeated overflow, regex context after consumption/boundary, truncated-line
eligibility, match/deadline/pause races, closing active runs before connection,
uncertain send failure and unencrypted-password notices. All 50 acceptance
cases are required. The [published specification](macro-github-issue.md) follows
to-spec; the consolidated contract and primary run-entry testing seam were
confirmed on 2026-11-14 and published as
[issue #3](https://github.com/TabTty/TabTty/issues/3) with ready-for-agent.
The latest AI contract
supersedes older pending snapshots: sharing independently sets broadcast checks
OFF/disabled and leaves them OFF afterward; ordinary macro ownership preserves
checks while excluding input eligibility.

The initial-release macro exclusion remains until explicitly amended. This is
documentation only. The specification is ready for implementation handoff;
implementation still requires a separate explicit request.

## Completed feature interview: broadcast input and split views

As of 2026-11-14, [broadcast input and split views](broadcast-and-split-issue.md)
have completed their specification interview under accepted Q199. Both features
join initial scope and are ready for implementation after a separate request.
Cross-window broadcast is optional and disabled by default; mode/targets are
window-local when disabled and application-wide when enabled. Remote mouse
operations join keys/paste; local gestures remain independent. Q166/Q167/Q169
are accepted, and amended Q168 remembers target selection until app exit.
Amended Q170 requires tab groups with separate tab bars AND splitting inside a
tab. Final Q165 selects same-session inner views after the RLogin comparison;
Q172 applies recursive left/right AND top/bottom splits to both layout levels.
Q174-Q178 accept split creation/removal, settings-tab placement, no persisted
layouts, and target selection tied to the tab through disconnect/reconnect.
Q173 is accepted after rationale: size the shared terminal grid from the whole
tab area without inner splits. Amended Q179 starts with all terminal tabs in
scope checked, then restores remembered checks independently of the invoking
tab; Start/Stop operations are the same on first and later use. Amended Q180
initializes per window. Clarified Q181 applies a changed scope setting at the
next broadcast Start; an active mode keeps its scope until stopped. Q182/Q183/
Q185 accept same-cell mouse mapping, stopping on send failure, and retaining
empty groups with new tabs placed in the active group. Q184 is accepted after
rationale: independent view positions/selection/search navigation, with shared
query/case and zoom per tab. Amended Q186 resets cross-window moved tabs to
checked. Amended Q187 uses ordinary Stop to end all windows' broadcasts once
global scope is saved; no separate Stop All item. Q188/Q189/Q191 accept common
paste confirmation/Stop, frozen operation targets, and menu/status controls.
Q190 is accepted after source review: wait for actual button-up and clamp only
the release coordinate for matching press recipients, rather than ending a drag
early at the grid edge. Q192 closes windows with no remaining tabs. Q193 permits
busy tab moves within the active scope (global: across windows; local: within
the window), without changing a frozen operation's recipients. Q194-Q196 accept
pane-focus IME cancellation, independent scrolling, and preserved layouts with
overflow scrolling. The
[consolidated contract](../specification/broadcast-and-split-views.md) records
ordering, resource guards, and acceptance cases. Amended Q197 resets all
selection when changing between local/global scope, rejecting preservation of
previously used windows' selections across the change. Q198 accepts choosing
the destination when removing a group. Final shared-understanding review
(Q199), including applying the reset at each affected scope's next Start,
is accepted. This feature's design frontier is closed; the contract contains
the implementation acceptance cases. The historical completion statements
below describe Q1-Q161; this feature's separate handoff is complete as well.
Implementation/prototyping require a separate explicit request.

## New feature interview: SSH file transfer

[File transfer](file-transfer-issue.md) joins the initial release. Q1–Q4
establish SFTP upload and download between the local Windows PC and the SSH
endpoint of the invoking connected terminal tab. Legacy SCP is deferred.
Q5–Q8 establish an SFTP channel on the same SSH connection, a dedicated transfer
window with local files on the left and remote files on the right, recursive
folder transfers, and per-file conflict confirmation with name, size, and
last-modified date and time for both items. Q9–Q15 confirm folder merging,
link skips, temporary-file commit, independent initial pane locations, window
lifecycle, batch outcomes, and both button and mouse drag-and-drop controls.
Q16–Q21 are accepted, with file-row drops targeting the containing folder.
These decisions also cover retry, timestamps, concurrency, changes after
overwrite approval, and destination filename conflicts. Q22–Q26 are accepted,
including deletion and rename in Q23. Q25 follows RLogin: capture the effective
terminal transmit encoding at transfer-window opening and use it for remote
names in both directions. Q27 includes recursive deletion in both panes. Q28
accepts same-directory, single-item rename without replacing existing entries.
Q29–Q33 accept deletion confirmation, link handling, partial results, management
during transfer, and the SFTP v3 baseline. Q34–Q36 accept terminal close,
stalled remote operations, and source-change handling. The
[completed transfer contract](../specification/file-transfer.md) closes this
interview's specification work. The
[implementation status](../specification/implementation-status.md#sftp-validation-2026-11-14)
records the 2026-11-14 implementation and validation.

## Historical baseline

> Consolidated on 2026-11-10: [implementation readiness and completed contracts](../specification/implementation-readiness.md). Accepted product decisions below remain binding; historical lists of unfinished technical design are superseded by those contracts. Implementation and runtime verification have not begun.

Status: initial specification and implementation preparation complete, 2026-11-10.
See `../specification/implementation-readiness.md` for the consolidated contracts,
review findings, and later verification/release gates. Implementation remains
unauthorized. Q106-Q108 are accepted, with `.tttykey`
as the vault extension.
Q109-Q113 are accepted with amendments: external moves create new registrations;
profile activation is trusted after basic loading checks, without an approval
gate; detailed validation belongs to every editor open, editing, and saving.
Q114-Q116 window-lifecycle recommendations are accepted.
Q117-Q120 display/accessibility are accepted, with cursor choices excluded.
Q121-Q124 highlighting details and initial limits are accepted.
Q125-Q128 encoding/search-operation recommendations are accepted.
Q131-Q132 connection waits and SSH liveness are accepted. Q129-Q130's Telnet
automatic-login helper is superseded by manual login and connection-only readiness.
Q133-Q136 SSH authentication/key/host-trust recommendations are accepted.
Q137-Q139 SSH transport policy recommendations are accepted.
Q140-Q143 SSH startup/output/messages/closure recommendations are accepted,
including Q141 after clarification of remote standard error and safe composition.
Q144-Q145 legacy encoding coverage/Japanese compatibility recommendations are accepted.
Q146-Q148 retained-row/log-I/O recommendations are accepted.
Q149-Q151 normal-screen Text editing/finalization/displacement policies are
accepted, including imperfect transcripts during complex shell editing.
Q152-Q154 terminal external effects, links, and bell recommendations are accepted.
Q155-Q157 application-settings save, portability, and recovery are accepted.
Q155 clarifies that app settings normally apply immediately after save, while
profile changes and application defaults resolved through profiles apply on
the next connection. Q158 file-preparation wait/cancellation is accepted.
Q159-Q160 capacity targets and overload behavior are accepted.
Q161 is accepted with UTF-8-only source-key passphrases initially, without a selector.
Q99 and Q100 are accepted.
Q101 is accepted with ARM64 optional; Q102 is accepted. The user delegates
Q103 onward to reference-based decisions using Tera Term. This document indexes
the interview; the topic drafts retain the detailed accepted requirements. No implementation,
prototype, package installation, or publication is authorized by this round.

## Accepted foundation and compatibility decisions

### Q100: Application foundation (accepted)

Use C# with WPF on .NET 10 LTS, using self-contained deployment and a
TabTty-owned native terminal control. Bundle the required runtime and native
dependencies. A self-contained deployment need not be a single executable.
Do not adopt WebView2 or xterm.js.

This is a product-specific recommendation, not a claim that Microsoft generally
prefers WPF over WinUI 3 or that WPF has measured better terminal performance.
Microsoft recommends WinUI 3 for new general-purpose Windows desktop apps and
continues to support WPF. WPF is selected here for its .NET desktop integration
and custom-control model. Exact drawing APIs and measurable performance remain
subject to the terminal requirements and later authorized validation.

.NET 10 is currently an LTS release supported through 2028-11-14. Select a
supported servicing patch at implementation/release time, rather than freezing
today's patch in the specification. Bundled runtime security fixes require
updating the application distribution.

### Q101: CPU architectures and supported Windows versions (accepted, amended)

Require native x64 support, including matching native dependencies. ARM64 is
optional and does not block the initial release. Officially support Windows 11
releases/editions supported by both Microsoft and the selected .NET release at
the time of the TabTty release.
Exclude Windows 10 and 32-bit x86 from the initial support matrix.

Distinguish the support matrix from the manifest's minimum installable build.
Do not choose an arbitrary minimum build until required Windows APIs are known.
Include x64 in the build and acceptance matrix. If an ARM64 distribution is
provided, validate it with matching dependencies before claiming ARM64 support;
architecture support is not established merely by producing an executable.

### Q102: Distribution and updates (accepted)

Use a self-contained ZIP folder for initial evaluation and MSIX for
eventual Microsoft Store distribution. Use manual replacement for ZIP updates
and Store delivery for Store updates; omit an independent application updater
from the initial scope. Keep application data separate from replaceable program
files; exact storage and migration rules remain in the profile/settings branch.

Preserve profile-file activation in both forms. ZIP extraction alone does not
register `.ttty`: the user can select TabTty through Windows file association.
The MSIX package declares the association, subject to Windows default-app policy.
Do not silently take over an existing default. Packaging support does not imply
Store certification or authorize publication. Signing and final package identity
remain release-preparation decisions.

### Q103-Q105: Decisions delegated to the Tera Term reference

The user requested Tera Term-based decisions rather than accepting the original
proposals wholesale. `teraterm-behavior-baseline.md` records the verified
v5.7.1 reference, selected behaviors, explicit adaptations, and source links.

- Q103: default advertised type `xterm`, VT100 identification, and a defined
  initial control/mouse/color baseline. Actual 24-bit RGB rendering is an
  explicit TabTty extension. Unicode ambiguous width defaults to one cell,
  with a one/two-cell profile choice. The original `xterm-256color` default
  proposal is superseded. Detailed capability checks and Unicode/font
  behavior remain open; reference resizing is not silently adopted.
- Q104: 9600 bps, 8 data bits, no parity, 1 stop bit, no flow control. Data
  choices are 7/8, stop choices 1/2, and flow choices None/XON-XOFF/RTS-CTS/
  DSR-DTR. Preserve COM-name binding and manual reconnect. Select DTR/RTS On
  defaults outside handshake ownership, temporary manual line controls, and
  an explicit 1000 ms serial BREAK operation. The earlier 115200 and API-driven
  option-list proposals are superseded.
- Q105: proactive BINARY negotiation is configurable and defaults Off; peer
  BINARY requests are accepted. Preserve eight-bit terminal data even without
  BINARY, with appropriate NVT CR and IAC processing. Explicit Telnet profiles
  initiate ordinary negotiation on any configured port, with immediate input
  submission and the previously agreed local-echo behavior. These last choices
  adapt the reference to TabTty's existing requirements. Raw records decoded
  terminal payload, not the Telnet wire representation. Select AYT and BREAK
  commands; detailed protocol event ordering/conformance remains open.

## Confirmed resize and vault direction (Q106-Q108 accepted)

The user accepted the resize/logging and vault recommendations, changing the
vault extension to `.tttykey`. Implementation remains excluded.
`resize-and-vault-proposals.md` records the decisions and reviewed primary sources:

- Q106: normal-screen reflow, traditional alternate-screen resize, and no
  synthetic log events; preserve the 5,000 physical rows and flush eligible
  unrecorded Text only when content is actually retired at that limit.
- Q107: a documented TabTty container (`.tttykey`) with Argon2id,
  wrapped random data key, whole-payload AES-256-GCM, and password input rules.
  KDBX 4.1 was compared; this selection does not imply superior assurance or a
  completed byte-level format.
- Q108: master-password change, encrypted save/backup/restore, and
  single-writer/conflict behavior, preserving the accepted unlock lifetime.

The direction is accepted. Exact format/limit/failure specifications and the
remaining compatibility work are still required before implementation readiness.

## Confirmed profiles and credential references (Q109-Q113, amended)

`profile-management-proposals.md` records the answers. The user's reply labels
"119" and "120" refer to the preceding Q109 and Q110:

- Q109: a Documents-based suggested save directory, files opened in place,
  and a registered-file list with favorites/recent use.
- Q110: external file moves remove old registrations and create new profiles;
  no relocation continuity or duplicate-ID review gate. In-app duplication
  creates a new file/ID; list removal and file recycling remain separate.
- Q111: exact shared vault-entry references, deliberate shared updates, no
  cascade deletion, and migration using separate profile/vault files.
- Q112: trust explicitly opened profile files, including double-clicks and
  external edits. No PC-local destination/credential/logging approval gate.
  Validate detailed settings/consistency on every editor open, changes, and save.
  The later Telnet scope change removes its saved-password editor warning;
  SSH host-key and vault-unlock checks remain where required.
- Q113: explicit Save/Cancel, previous-version backup, external-edit conflicts,
  and fresh reads with basic bounded format/interpretation checks at connection.
  Do not run the full editor validator or require security-change review there.

These are accepted product choices. The rejected local-approval proposal is
superseded, including older import/new-PC setup-consent requirements. The trusted
configuration boundary and its distinction from broader OWASP semantic-input
validation guidance are recorded in `authentication-issue.md`. Implementation
remains excluded, and other specification branches remain open.

## Confirmed window behavior (Q114-Q116)

`window-lifecycle-proposals.md` records the accepted TabTty-specific choices:

- Q114: restore one window's last active geometry/normal-maximized state,
  recover from unavailable monitors, and place detached windows near the drop.
- Q115: restore minimized activation targets, defer file opens behind modal
  interactions, and share one in-progress vault-unlock interaction.
- Q116: keyboard-accessible tab-menu movement to new/existing windows,
  preserving sessions and leaving failed transfers at their source.

These decisions preserve Q109-Q113's
trusted activation policy and do not introduce another profile approval step.

## Confirmed terminal display and accessibility (Q117-Q120)

`terminal-display-proposals.md` records accepted font defaults/profile overrides,
temporary tab zoom, Unicode width/glyph fallback, and initial keyboard/Narrator
scope. Q119 is amended to fixed local cursor behavior without shape/blink choices
or app/profile fields; supported received cursor controls and OS blink preferences
remain. Detailed width/font/IME/UI Automation cases still require specification.

## Confirmed highlighting details (Q121-Q124)

`highlighting-details-proposals.md` confirms invariant-culture regex options and
cluster-to-cell styling, explicit shared-rule editing and disabled invalid rules,
text-change-based scheduling, and bounded overload behavior. These are accepted
TabTty product policies, not measured performance claims or implementation.

## Confirmed encoding and search operation (Q125-Q128)

`search-and-encoding-proposals.md` confirms next-connection-only encoding
changes, explicit UTF-8 initial-signature handling, wrapping literal-search
navigation with bounded work, and selection/search behavior across screen switches.
These accepted product choices preserve the established encoding
inventory, log modes, normal-only search, and profile activation trust.

## Confirmed connection and login operation (Q129-Q132)

`connection-attempt-proposals.md` confirms client connection deadlines distinct
from authentication/user waiting and optional authenticated SSH liveness checks.
The user's later answer accepting Q133-Q136 removes Telnet automatic login:
connection readiness follows transport establishment, without detecting remote
account login. Initial Telnet and serial login is manual; no Telnet credential
binding, prompt matcher, stage timer, or saved-password editor warning remains.
Future macros may own those workflows; no initial macro runtime is introduced.

## Confirmed SSH authentication, keys, and host records (Q133-Q136)

`ssh-authentication-proposals.md` records accepted method allowlist/order and
Password-only defaults, separate interactive responses, partial-success/retry
policy, Ed25519/ECDSA/RSA-SHA2 key scope, and the explicit key-import envelope
matrix. Imported keys use canonical PKCS#8 inside the vault; source passphrases
are transient import input only. Local host-key management supports multiple
approved formats and explicit deletion without known_hosts import/export or
automatic key learning. Detailed schema/resource/protocol contracts remain.

## Confirmed SSH transport policy (Q137-Q139)

`ssh-transport-policy-proposals.md` accepts the fixed algorithm/strict-KEX
compatibility policy, preferred ML-KEM/X25519 hybrid with traditional alternatives,
AES-GCM/AES-CTR with SHA-2 EtM, no compression, and automatic rekey at 1 GiB per
direction or one hour with tighter limits respected. No user weak-mode/algorithm
editor or rekey-disable switch is introduced. Remaining packet/state/crypto
contracts and validation are distinct from selecting this policy.

## Confirmed SSH sessions (Q140-Q143)

`ssh-session-proposals.md` accepts PTY/shell readiness, protocol-message display
outside terminal/logs, and remote/local closure. Q141 is accepted after its
explanation: include separate extended stderr with normal terminal output, keep
Raw payload bytes in receive-message order without source labels, and isolate
source decoder/partial-control state before completed operations reach the common
terminal. A headerless Raw stream is not promised to reproduce that display.
Apply initial UTF-8 signature recognition and interval eligibility per source.

## Confirmed legacy encoding policy (Q144-Q145)

`legacy-encoding-proposals.md` accepts completing the 48 identities with Microsoft
codecs plus small mappings based on published Unicode data, documented standard/
Windows-compatible behavior rather than reproducing source discrepancies, and
strict transmit rejection without best-fit/width substitutions. Japanese details
use JIS IN @/B and OUT B/J with B/B defaults, receive/transmit kana 8-bit defaults
and optional 7-bit SO/SI, standard EUC SS2, and no initial JIS X 0212/X 0213.
Exact extension/designation/error-consumption matrices remain technical work.

## Confirmed retention and log I/O (Q146-Q148)

`retention-and-log-io-proposals.md` accepts the 100-100,000 retained-row range,
8 MiB combined per-session owned pending-log budget, ten-second active-I/O stall
policy, normal batch-writing target, and durable flush requests at boundaries.
After five seconds of unresolved close finalization, offer waiting (default) or
closing with unsaved log data; the watchdog must not override chosen waiting.
These are product policies, not measured capacity or universal OS guarantees.

## Confirmed normal-screen Text editing (Q149-Q151)

`text-editing-proposals.md` records acceptance on 2026-11-09: apply destructive
edits without pre-edit snapshots, finalize on received LF/CRLF without treating
cursor movement as a boundary, and preserve eligible output actually displaced
out of retained state by scrolling. Q150's final answer accepts imperfect Text
layout/order during complex multiline editing, including backslash-escaped
newlines. Do not infer shell-command acceptance or add special repair heuristics.
Preserve ordinary edit/wrap behavior and the separate alternate-screen fallback.

## Confirmed terminal external effects (Q152-Q154)

`terminal-external-effects-proposals.md` confirms no remote clipboard access,
title replacement or outer-window manipulation, no initial clickable links, and
a quiet visual bell with an application-wide Off option. Keep profile tab names,
ordinary terminal control behavior, and the three logging-mode boundaries intact.

## Confirmed application settings (Q155-Q157)

`application-settings-proposals.md` records Save/Cancel and the user's general
timing rule: saved application settings normally apply immediately; profile
changes and application defaults resolved through profiles apply on the next
connection. Keep the accepted shared-highlight live revision and temporary
session actions. Separate portable JSON files support offline manual-copy
migration without an initial import/export UI. Retain one previous validated
backup and explicit recovery choices without silently overwriting unreadable files.

## Confirmed file-preparation waits (Q158)

`profile-file-preparation-proposals.md` accepts separate fixed 30-second budgets
for profile loading and the whole initial log-directory/file preparation stage.
Keep preparation visible/cancellable and prevent startup from late results after
cancellation/expiry. This is separate from transport timing and cannot guarantee
immediate OS/provider cancellation. Retain bounded cleanup ownership and explicit
retry. JSON/profile-trust and relative-path rules are unchanged.

## Confirmed capacity targets and overload behavior (Q159-Q160)

`capacity-and-overload-proposals.md` accepts twenty connected tabs at the default
5,000 retained rows each and 1 MiB/s aggregate received payload as a reference
workload, with local UI latency targets. Twenty is not a hard connection limit;
the reference PC is not a minimum installation requirement. Prioritize existing
sessions at resource admission, and fail an affected active session explicitly
when its hard terminal-processing budget prevents correct continuation. Preserve
the narrower logging, regex, and search failure policies. No benchmark has run.

## Confirmed imported-key passphrase encoding (Q161)

`key-import-passphrase-proposals.md` records the user's UTF-8-only choice for
source-key passphrases in the initial release. The proposed manual legacy
encoding selector is superseded. Use strict UTF-8 without normalization or
alternate-encoding retries; imported keys requiring different passphrase bytes
remain outside initial compatibility. Source passphrases remain transient, and
the terminal encoding inventory and vault master-password rules are unchanged.

## Closed initial branches and later verification gates

Q161 is settled; no product question is currently awaiting an answer.
The specification contracts in `../specification/implementation-readiness.md`
complete the previously open initial technical branches. Source/repository
reviews found and corrected consequential compatibility and lifecycle issues.
This is implementation readiness, not executed verification or release readiness.

Separate initial ZIP readiness from later Store release preparation. The initial
architecture must preserve a viable packaged-storage/activation boundary, but
final Store identity/signing, ZIP-to-MSIX transfer, and Store uninstall-retention
decisions belong to that later delivery gate under accepted Q102. Do not imply
that an initial ZIP implementation is Store-certified or that publication is
authorized. Future macro language/runtime choices remain outside initial scope.

| Branch | Completed initial contract and later obligations |
| --- | --- |
| Terminal behavior and encodings | `terminal-and-encodings.md` fixes controls, keys/mouse, width/IME/UIA, mapping/error rules, and explicit reference differences; runtime compatibility remains to verify. |
| Input, logging, and resize | `session-and-recording.md` fixes generation/operation order, provenance/revisions, boundaries, serialization, writer ownership, and paste cancellation; fault/concurrency tests remain to run. |
| Highlighting and search | Terminal styles and `resources-and-validation.md` complete worker/cache accounting and acceptance; existing Q121-Q128 semantics remain. |
| Profiles/settings/windows | `persistence-and-windows.md` fixes schema/defaults/limits, opening-location identity, recovery, modal/activation/transfer order, and DPI placement. |
| SSH/authentication/Telnet/serial | `transports-and-authentication.md` completes identity/framing/state/limits and transport-specific handling; protocol interoperability and security review remain release gates. |
| Vault | `vault-format.md` completes byte framing/AAD, root/derived-key use, fixed KDF profile, schema/limits, ownership, and file-failure transitions; crypto/fault tests and independent review remain release gates. |
| Distribution/maintenance/capacity | `resources-and-validation.md` completes initial API/dependency/servicing policy, budgets, deterministic workload, and acceptance matrix; no benchmark has run. Store-specific delivery remains later. |
| Future macros | Existing small session/receive/credential/naming seams remain; language/runtime selection and implementation are deferred. |

The contract filenames in this table are under `../specification/`.

Research findings do not require the user to choose factual answers. Distinguish
the remaining product choices from protocol/format facts that must be researched
and checked by the agent. Apply the user's delegation for reference-based
decisions and preserve earlier explicit requirements. Unreviewed reference
behavior is not an accepted default.
These initial branches are now consolidated and ready for implementation when
the user separately requests it. No implementation starts from this status change.

## Primary evidence reviewed for this round

- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [Microsoft Windows framework guidance](https://learn.microsoft.com/en-us/windows/apps/get-started/windows-developer-faq)
- [.NET deployment options](https://learn.microsoft.com/en-us/dotnet/core/deploying/)
- [Bundled runtime servicing](https://learn.microsoft.com/en-us/dotnet/core/deploying/runtime-patch-selection)
- [WPF architecture support](https://github.com/dotnet/wpf/blob/main/README.md)
- [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md)
- [WPF MSIX packaging](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/dotnet/package-app)
- [Windows file-association policy](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/default-apps-platform)
- [ncurses terminal-type compatibility](https://invisible-island.net/ncurses/ncurses.faq.html#xterm_generic)
- [Microsoft VT reference](https://learn.microsoft.com/en-us/windows/console/console-virtual-terminal-sequences)
- [Microsoft SerialPort](https://learn.microsoft.com/en-us/dotnet/api/system.io.ports.serialport?view=net-10.0)
- [Windows serial setting constraints](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-dcb)
- [Microsoft serial handshake choices](https://learn.microsoft.com/en-us/dotnet/api/system.io.ports.handshake?view=net-10.0)
- [COM enumeration limitations](https://learn.microsoft.com/en-us/dotnet/api/system.io.ports.serialport.getportnames?view=net-10.0)
- [Windows USB serial driver coverage](https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-driver-installation-based-on-compatible-ids)
- [Telnet host requirements and 8-bit NVT](https://www.rfc-editor.org/rfc/rfc1123.html#section-3.2.5)
- [Telnet BINARY](https://www.rfc-editor.org/rfc/rfc856.html)
- [Telnet TERMINAL-TYPE](https://www.rfc-editor.org/rfc/rfc1091.html)
- [Telnet NAWS](https://www.rfc-editor.org/rfc/rfc1073.html)
- [Telnet option state-machine design](https://www.rfc-editor.org/rfc/rfc1143.html)
