# Implementation audit for #41–#67

Source is the working tree based on b0a10cf after the architecture follow-up. Issue numbers refer to the
published child issues of [#40](https://github.com/TabTty/TabTty/issues/40).
Condition numbers below are one-based positions in each issue's Acceptance
criteria. A code path listed here still requires the corresponding runtime
check where the condition specifies one.

The last column records acceptance evidence and identifies work still open in this checkout.
External checks are recorded separately under Environment-limited checks.

| Issue | Conditions with production implementation | Acceptance evidence and open work |
| --- | --- | --- |
| [#41](https://github.com/TabTty/TabTty/issues/41) | 1–6. The session generation owns preparation, pause, retry and actual release. The controls receive immutable dispositions and pending release. Whole-group admission rejects partial activation. File close uses the same no-progress policy as write and flush. | 7 verified. Controlled tests cover partial writes, durable-flush failure, cancelled observation, late successful and failed group opens, file-close failure and timeout, and retry only after actual release. The retry checks exact Raw and Text bytes, including mixed-line exclusion, while terminal input remains usable. |
| [#42](https://github.com/TabTty/TabTty/issues/42) | 1–5. The recording owner distinguishes first and repeated EOF, Pause and final disconnect. The first cutoff flushes eligible content once, separately from transport release. Actual SSH retains reverse input and keeps the part open through EOF. | 6 verified. Split Data/stderr, repeated EOF with held Pause and disconnect, exact Raw/Text/Terminal bytes, and real directional SSH EOF with reverse input pass. A delayed durable-flush failure settles only the original recording while the replacement records and sends. |
| [#43](https://github.com/TabTty/TabTty/issues/43) | 1–8. Accepted tab and window close protect original recording ownership. The window keeps the later loss command accessible while other tab and menu controls are blocked. Loss rejection under full cleanup capacity preserves the same pending close. | 7–8. Real WPF tab and whole-window cases exercise Continue waiting, a later decision, and all 32 cleanup slots occupied. Core distinguishes completed recording from held transport release and retains issued flushes through actual return. |
| [#44](https://github.com/TabTty/TabTty/issues/44) | 1–8. Consumer binding, Telnet framing, echo facts, close and device release use the common transport. Geometry commits inside session ordering and issues outside it, with explicit success, supersession, no-notification and failure results. Ordinary TCP and COM sends preserve the confirmed byte prefix across a short or failed write. | Controlled TCP delivery now proves that callback failure after applying payload does not replay it or deliver later echo facts. Stopping a close observer retains both the callback and the held device. Ordinary direct, broadcast and AI input echo only complete confirmed units after a failed write, including escaped IAC and stateful JIS. |
| [#45](https://github.com/TabTty/TabTty/issues/45) | 1–5. The same transport provides ordered COM payload, partial send data, overrun reporting and a distinct actual-release task. Cancellation of observed startup keeps a late device and its preparation slot until actual close. | 6. Controlled late open/close, short writes and overrun preserve recording and terminal output. A delayed device close leaves the replacement adapter ready and its exact receive/send bytes intact. Independent native driver verification uses physical COM and remains environment-limited. |
| [#46](https://github.com/TabTty/TabTty/issues/46) | 1–5. The actual profile editor observes an admitted save and retains its operation identity for reinspection. Shared publication keeps immutable candidate bytes, original basis and target/backup/artifact evidence through actual release. Unissued cleanup retains both the failed artifact and the exact original basis. A separate 256 MiB artifact budget retains its charge with unresolved operations. | The profile editor covers stale original, admission exhaustion, replacement of a validated staging name, missing preservation, visible close and reinspection on reopening. Shared tests also cover hard-link aliases and unreadable preservation. A verified recovery transfers a surviving prior stage and its capacity into independent retention until explicit reinspection proves that the old file identity is gone. |
| [#47](https://github.com/TabTty/TabTty/issues/47) | 1–5. Every JSON store passes its actual staged format validator through common publication. Explicit recovery captures the original digest before confirmation. A failed recovery retains the earlier operation and namespace restrictions. Highlight save and host trust await actual publication without blocking the dispatcher. | The settings, highlight and host-key paths cover an external revision and full artifact capacity without changing effective state. Highlight and host-key cases retain prior state on missing preservation, then reconcile once. A cancelled login waits through an issued save and receives cancellation while the verified approved-key record remains available. |
| [#48](https://github.com/TabTty/TabTty/issues/48) | 1–4. Vault publication supplies encrypted snapshots and policy only. A partial operation blocks new secrets and writes, with root adoption restricted to verified preservation. Reinspection authenticates or revalidates the exact candidate. Retirement of a clean, unissued result releases the proposed root and result slot without adoption. | 4–6. Real locked/unlocked restores retain exact physical evidence and the applicable root/result charge after an issued write is cancelled. False original identity and missing preservation are refused. An unissued master change held a stage open, preserved the original and prior backup byte for byte, and released the result slot after cleanup proof. Both the open vault and a fresh unlock delivered the original credential. |
| [#49](https://github.com/TabTty/TabTty/issues/49) | 1–5. Bounded unlock consumers and observers are independent. A transferred live prompt remains bound to the original inspected file. Handoff rechecks kind, live host, selected owner, role epoch, revision and publication restrictions. Admitted reads retain their file owner through credential disposal or delivery. | A held worker plus 31 queued unlocks fill admission; the next derivation is refused and cancelled queued unlocks never run. WPF owner transfer and stale-context rejection remain covered through actual credential delivery. |
| [#50](https://github.com/TabTty/TabTty/issues/50) | 1–5. Management edits use revision-bound entry identity and one key source image. The vault reserves and prepares one shared metadata projection before publication, then adopts it with the encrypted generation and root. The management operation reserves notification capacity before user interaction and reads the committed projection without a second worker admission. | Private-key bytes are charged from acquisition through passphrase review and native import. Add and import use the reviewed vault revision. An independent DER key can be imported from the owned image after the source path changes; a stale revision refuses it before import. Eight 1 MiB acquired images exhaust the secret-input allowance and a ninth is refused. |
| [#51](https://github.com/TabTty/TabTty/issues/51) | 1–4. Master changes admit bounded input and expensive work before publication. The proposed generation, metadata and result/root ownership slot are reserved before issue. The old root remains effective until the exact encrypted candidate and preservation are both verified. The management form may close after master confirmation. Application exit waits for actual work and credential handoff checks, and keeps an unverified result visible after the last window closes. | Controlled input and RNG failures, worker cancellation and partial publication retain the original effective revision. The temporary root is cleared after its last use. Verified replacement followed by a reported I/O error adopts once and both the new vault and its previous backup resolve the same credential under their respective passwords. |
| [#52](https://github.com/TabTty/TabTty/issues/52) | 1–4. Locked and unlocked restore share one file-bound operation. The WPF command acquires exact target and backup evidence before confirmation. Restoration verifies the encrypted backup and preserves the exact original through common publication. | Locked recovery preserves the exact damaged original and authenticates the restored image. A future-version source remains byte-for-byte unchanged and creates no preservation artifact. Locked and unlocked restore, late observation, changed source identity and independent stale rescue remain covered. |
| [#53](https://github.com/TabTty/TabTty/issues/53) | 1–4. The explicit command is bound to missing/empty evidence. Empty initialization uses the shared namespace, registers the prepared identity before mutation and preserves unresolved parking and installation facts. Narrow original-execution compensation uses the confirmed image and requires an absent destination. | 3, 5. WPF shows the empty-file intent, active work and exact target/parked/candidate state. Competing publication, park/install/compensation errors, cancellation, cleanup-only failure and wrong-identity reinspection pass. Pre-issue failure with a held stage keeps the original empty file, evidence and namespace. Proven removal retires that record without adopting a root. Verified initialization keeps an undeleted parked original separately and shows its path. |
| [#54](https://github.com/TabTty/TabTty/issues/54) | 1–4. Archive binds consent to exact file evidence, checks both sides of a unique sibling move, and retains unresolved facts for read-only reinspection. The selected owner is cleared only after verified success. | Core verifies refusal to adopt an archive when the original name contains a replacement. The standalone WPF editor covers a held issued move, concurrent initialization refusal, a reported error after the move, exact archived bytes, the unchanged selected path and closure after acknowledgement. |
| [#55](https://github.com/TabTty/TabTty/issues/55) | 1–5. Settings and vault-role reservation share one adoption boundary, also used by recovery and reinspection. Pending settings and vault work retain their actual owners. A reserved destination editor can close while its eligible owner remains retained. | 6. Real settings replacement retains the prior effective epoch and path until verified. Destination-editor closure preserves its unlocked owner. Partial settings reinspection through the recovery form verifies the exact candidate once and promotes the matching unlocked editor. An actual extracted credential held over A→B→A retires without handoff. An external settings revision and full artifact capacity leave both effective path and epoch untouched. |
| [#56](https://github.com/TabTty/TabTty/issues/56) | 1–5. One FIFO packet writer admits terminal and SFTP payload with reserved control space. It publishes a definite local prefix, the committed range awaiting completion or its uncertain outcome, and the uncommitted suffix. Commitment precedes cipher changes, and observation and request cancellation leave the committed packet owned. | 6. Precommit cancellation, encrypted pending-write failure, terminal prefix and suffix, no replay, final SFTP mutation uncertainty, and the existing zero-credit/rekey and cipher checks pass. |
| [#57](https://github.com/TabTty/TabTty/issues/57) | 1–6. The channel prebinds an awaited incremental SFTP consumer before OPEN. The SSH reader advances independently, with ordered bounded callback delivery, shared credit and request correlation, and parser release distinct from protocol closure. | A real peer fragments SFTP frames and sends replies for eight file requests in reverse order. The ninth is held and cancelled without issuing. Control work progresses during rekey and zero terminal send credit. The oversized-reply, late OPEN/CLOSE and shared-window tests remain covered. |
| [#58](https://github.com/TabTty/TabTty/issues/58) | 1–9 and 11. Early DATA uses a prebound awaited callback. The transport pump can advance independently of terminal delivery. Startup geometry is applied under terminal ordering and the latest post-shell size is issued outside the reducer. Real directional EOF preserves reverse input. Broadcast and AI input release the reducer during pending network output. | Startup and steady SSH use the same awaited terminal payload operation and no session-facing raw read/acknowledge. Real shell refusal and cancellation retain exact Raw, Text and Terminal recordings. A burst of 1,536 alternating DATA/stderr messages retains byte order, treats exit status as metadata and permits reverse input. |
| [#59](https://github.com/TabTty/TabTty/issues/59) | 1–5. The SFTP slot retains its generation through actual parser and writer release. Reopen allows one live waiter, final uncertainty reaches the provider, and the FIFO packet writer shares capacity without overwriting earlier queued work. | An unanswered OPEN and then an unanswered CLOSE independently expire reopen waiters while terminal traffic continues. Later protocol release permits only a new explicit attempt. Eight pending and fragmented file reads, control work, zero terminal credit, rekey and a held recording flush progress together. A held real local download retains the transfer buffer budget through old-channel release and new-channel admission. The charge returns on actual provider completion and the partial download is never published. Independent OpenSSH remains an external check. |
| [#60](https://github.com/TabTty/TabTty/issues/60) | 1–4, 6. The pinned Rust tables are reproducible from exact input hashes. Production uses exact emoji membership, retained-width rules and independent grapheme state. | Eight emoji width vectors run through every byte split. The recording cases check retained spelling and exact recorded bytes with one-, two-, three-, four-, seven-byte and whole-input delivery. |
| [#61](https://github.com/TabTty/TabTty/issues/61) | 1–5. The native snapshot is the source of authoritative spans used by drawing, UIA and AI, including distinct active and parked projections. | The maximal span is atomic at the history page limit and the next span survives in parked history. WPF UIA checks the same stored span across reflow, alternate view and eviction. Owned history results remain readable after eviction. |
| [#62](https://github.com/TabTty/TabTty/issues/62) | 1–5, 7. Rust retains 256 scalars while advancing complete streaming boundary state. Geometry uses the retained spelling and original eligible input remains available to recording projections. | The two variation-selector thresholds, omitted Indic linker and omitted emoji continuation run in both display modes across six fragmentation schedules, with independent Raw, Text and Terminal expected bytes. |
| [#63](https://github.com/TabTty/TabTty/issues/63) | 1–5. The immutable paste plan retains counts and a bounded traversal of the original source. Whole-source preflight precedes collective commitment. | The full-source checks include empty input, RI, Indic, ZWJ, combining and CRLF boundaries and a final unpaired surrogate. An unrepresentable scalar on the final page prevents all target payload and bracket starts, and WPF exposes both preview endpoints. |
| [#64](https://github.com/TabTty/TabTty/issues/64) | 1–6, 8. The sender streams exact source input. Stop completes only the partly submitted encoded unit for TCP, COM and SSH. For ISO-2022-JP it reconstructs the close bytes for the submitted prefix. Local echo advances through definitely submitted units, and an uncertain failed write stops the affected generation. | Controlled direct and AI paste stop at the exact complete UTF-8, CRLF and ISO-2022-JP unit. Telnet escaping and local echo preserve only complete units. Real SSH verifies the definite prefix after a committed failure. |
| [#65](https://github.com/TabTty/TabTty/issues/65) | 1–8. The application envelope charges one retained source. A prepared target holds its reference and work reservation through actual send return. The 2 MiB/256-unit per-target allowance and 30-second collective deadline precede payload. Frozen targets recheck generation, input authority and terminal mode before commitment. Initial local echo consumes reserved growth and workspace without duplicate charging. | The WPF fixture saturates the 4 GiB accounting envelope during live clipboard review, retained terminal history and an outstanding SFTP request. A competing capture is refused while the original subsequently sends and stops at the exact peer-observed units. Competing local scopes and terminal-state echo pressure are covered separately. |
| [#66](https://github.com/TabTty/TabTty/issues/66) | 1–7. The positive saved clipboard limit defaults to 512 MiB. Capture uses original UTF-16 bytes, bounds the read and retains source ownership through review and sending. A later saved limit affects subsequent captures. | 8. Win32-block fixture covers ASCII/Japanese/supplementary equality, odd-byte rejection, CR/LF, settings changes during preview, failed Save, UIA preview and exact Stop. The isolated OS clipboard check was unavailable. See short workload results below. |
| [#67](https://github.com/TabTty/TabTty/issues/67) | 1–6. Actual SSH, recording, file publication and WPF regression paths run in controlled races. Zero-credit input/replies, rekey, held flush and SFTP progress are independently observed. Held master publication covers physical target/backup facts, closing the final window, actual application exit and refusal of the old credential result. | While a real SFTP request is pending and terminal history remains live, a full application envelope refuses a competing clipboard capture. The approved clipboard operation later sends and closes the exact framing. The eight-request SSH test proves independent zero-credit, rekey, provider and recording progress. Old-generation completion and final-window vault cases remain separately attributable. |

## Test reporting

Record a failure when the test ran and a check failed. Reserve “not run” for a
check whose external device, independent peer or required environment is
unavailable. A missing fixture or implementation item remains open in the
table above.

The Release candidate built with .NET SDK 10.0.404 on Windows x64 with
zero warnings and errors. The follow-up regression uses the actual Core and
WPF entry points. Its peer deliberately fragments SFTP frame lengths, version
and reply headers, and reports the exact clipboard bytes it receives. The
separate Win32 clipboard block fixture exercises UTF-16 capture, confirmation,
UIA page navigation, Stop and a concurrently outstanding SFTP request.
Core passed 88 checks, WPF passed 27 checks, and the standalone editor suite
passed six checks. The HGLOBAL/SFTP integration check passed.

A preceding Core run encountered a Win32 replacement failure during a vault
entry edit. The observed target remained original and the operation reported
Unchanged. The complete Core run was repeated with fresh files and passed.
The original mutation was not replayed. An earlier macro fixture also
could leave its open recording file behind if a test assertion failed before
cleanup. The fixture now awaits the actual owner before deleting its directory.

### WPF retest

| Check | Initial observation | Retest with the RDP client visible |
| --- | --- | --- |
| Terminal rendition | The 230-pixel viewport completed layout and the bitmap contained only zero bytes. | Passed the same pixel assertion in isolation and in the full suite. |
| Management menu | The submenu stayed closed. An independent WPF window could not acquire mouse capture. | The independent probe and the real submenu check passed. |
| Keyboard navigation | Focus stayed outside the menu. The independent window could not obtain foreground input. | The foreground probe and real keyboard focus checks passed. |

The earlier foreground and capture failures were independently reproducible
without a TabTty window. All three graphical checks succeeded during the
visible-session retest. The controlled retest recorded visibility and avoided
changes to the local or remote RDP configuration.

### Environment-limited checks

The native Rust suite passed 35 tests. All 766 Unicode 17 grapheme-break
cases matched the native boundary function. The Unicode and emoji generators
reproduced both embedded tables byte for byte.

| Check | Status and reason |
| --- | --- |
| Physical serial COM | Not run. Windows did not report a physical serial port. |
| Independent OpenSSH server | Not run. The Windows OpenSSH client was present and the independent server executable was unavailable. The repository's SSH peer ran the regression and workload fixtures. |
| Isolated Windows clipboard | Not run. Creating a private Windows window station returned Access Denied (5). The separate HGLOBAL fixture passed using the same native Unicode block readers, settings, WPF confirmation and SFTP channel as the normal path. |

The later [native cutover acceptance](implementation-status.md#native-cutover-acceptance-on-the-current-candidate-2026-11-19)
ran the independent Microsoft OpenSSH server successfully. It verified public-key
authentication, shell traffic and exact SFTP file bytes through rekey on one
connection, with separate Raw terminal data.

### Build and fixture identities

The controlled runs used the 8 MiB per-recording pending-byte allowance,
approximately one-second batching, the ten-second pending-I/O watch and
the five-second close question. SSH advertised a 1 MiB receive window and
32 KiB packet size. Its shared writer has 64 pending slots, including a
limit of 32 waiting payloads. Vault admission allows 256 ordinary operations,
32 waiting/running derivations or imports and 8 MiB of admitted secret input.
Paste used the 4 GiB application parent envelope,
2 MiB and 256 work units per target, and the 30-second broadcast preparation
deadline. Independent peers verified a 1,048,577-byte UTF-8 broadcast payload
at both targets, including brackets. The default clipboard maximum was
512 MiB with confirmation above 1 KiB of UTF-16 source or for CR/LF.

| Tested artifact | SHA-256 |
| --- | --- |
| TabTty.Core.dll | `ED2DCDCDD1F4E790A211C87EE88DCA4C58645D422DFCEBAE79D0A0163B8F98E7` |
| tabtty_terminal.dll | `C61B49145F728A77B05F061C8AF86F284F56146BCFBCE4D3BC2A4D5CDEEFCC4E` |
| TabTty.dll | `C89648A6D980ACC7ABB42AF64B014D5213EE7862DD36A12CEE53C5A9A2917BF0` |
| tests/fixtures/ssh-peer.cjs | `0673C92C8590EDA6CE621C0B151697DFC7A7780E59E74A9B8095C9CA82C67D12` |

The preceding regression logs are in
`artifacts/architecture-dependency-ac069a745e834d049ebce1913b85c534`.

### Send progress and final-window publication

The scripted SSH peer ran the ordinary terminal sender and SFTP writer over
real encrypted loopback connections. The fixture held a packet after the cipher
had advanced, cancelled both a progress observer and the send request, then
failed the socket write. The confirmed prefix remained monotonic, the pending
range became uncertain, and the unsent suffix was never replayed. Before
commitment, a separate test proved that cancellation left the next packet's
cipher and credit intact. An uncertain SFTP mutation surfaced as UnknownOutcome.
An SSH paste split across two-byte packets reported one complete encoded byte
when the following UTF-8 scalar was incomplete.

The last-main-window and standalone-vault-editor fixtures issued an actual
replacement before closing the window. Each inspected the installed candidate
and preserved original, held application exit until verification and credential
work settled, and refused delivery to the closed host. Independent unlocks
verified the exact candidate and backup with their respective passwords.
When the backup was removed during the held write, the application displayed the
unverified outcome and the affected backup path after the main window closed.
The candidate stayed in place. A separate Core fixture verifies read-only
reinspection and joint adoption of the root, metadata and revision after the
original is restored.

The profile editor fixture held a real profile replacement and refused closure
during physical work. After removing the backup, it verified the localized
outcome and preservation path on both save failure and the quiescent close
decision. Closing retained the installed candidate and the write restriction.
Long Indic cases cover an omitted virama and following consonant in the
recording projections on both screens, plus reflow and retirement in the
native terminal. The settings recovery fixture verifies that partial
preservation leaves the original effective path and epoch in force until
read-only reinspection adopts the exact candidate and its matching owner.

The Release build succeeded with zero warnings and errors. Core passed 89
checks, WPF passed 27, and the standalone file-editor suite passed eight.
All three final-window vault checks and the HGLOBAL clipboard/SFTP check passed.
One intermediate WPF run returned an all-zero bitmap during the terminal
rendition check. The same check passed 12 times in isolation, the desktop probe
passed, and the full-suite retests passed. The capture failure remains
intermittent. Two intermediate fixture setup errors used unsupported SSH local
echo and a retention count below 100. The corrected fixtures exercise exact SSH
submission and retirement at the supported minimum and passed. The final
environment-limited checks are listed above.

| Build or fixture | SHA-256 |
| --- | --- |
| TabTty.Core.dll | `7BC9201C9A91035AFDB37C08D66753C7CD558A5EDA48BE05FDBDFF0A78291E6A` |
| TabTty.dll | `B58D34C33ED9FAA0081A2FDC72B017FB9297208FEAAAF924E8A75CB73C0CF222` |
| tabtty_terminal.dll | `C61B49145F728A77B05F061C8AF86F284F56146BCFBCE4D3BC2A4D5CDEEFCC4E` |
| TabTty.Core.Tests.dll | `662BE8DD525C1E288A1865D2A3FB5E5219E44ED2BAEC09D3CAEF7C351DADBA49` |
| TabTty.App.Tests.dll | `27327AABFAE33459628F2C19C06D08662A5CCB36439A39881AA451CA83B37120` |
| tests/fixtures/ssh-peer.cjs | `AD12015EB6C86A1AFB1B54F1DACA948B2FE51C16EB368DA6B070D41AE83637E8` |

The Core, WPF, profile and vault logs are in
`artifacts/architecture-owner-release-3c28dff9e74d417a971ac31ac3d9cba4`.


### Tab switching diagnosis

The initial 20-session workload reproduced the limit miss. Its measured tab
switch p95 was 145.27 ms. On a subsequent instrumented run, the slow first
switch took 149.59 ms. Fixture startup occupied 71.41 ms ahead of input
submission, and the submitted input waited 75.57 ms in the dispatcher.
Selection and synchronous layout together took 2.55 ms, with no recorded GC
pause during that interaction. Scheduling input independently still recorded
149.96 ms of dispatcher wait on the next short run.

In the application, passive received-output notifications, broadcast paste
presentation and AI observations now yield to queued input. Starting an
idle terminal also schedules its first native viewport. In the workload,
fixture startup dispatches its provider, broadcast and observation operations
independently. The timing source posts input on its own schedule and verifies
that the selected terminal has rendered its viewport, including pending
snapshot work at selection. One intermediate polling-based run produced
104.60 ms with one busy producer. The final harness timestamps the actual
WPF viewport drawing callback. The drawing path also avoids a duplicate glyph
cache lookup for already cached glyphs.

### Concurrent workload

Each final run measured 30 seconds after warm-up with 20 SSH sessions
in two windows. Each sampled ten local interactions, including four tab
switches. All 20 Raw logs in each run matched the independent fixture
cycle. Both runs passed the short 100 ms local and 250 ms Stop targets.

| Receive distribution | Tab switch p95 | Stop p95 | SFTP round trips | Pastes | Stops | Peak private bytes | Peak terminal-state charge |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Equal | 53.85 ms | 1.10 ms | 9 | 12 | 5 | 1,435,107,328 | 2,916,494,686 |
| One busy | 86.90 ms | 0.98 ms | 10 | 12 | 5 | 226,738,176 | 405,070,612 |

These short runs cover the measured regression. Sustained release acceptance
uses its separate 30-minute workloads.

Detailed workload records are under:

- `artifacts/architecture-timing-paint-equal/architecture-equal-30-7cc6bb63116f46ec96c66d4415c4351f.json`
- `artifacts/architecture-timing-paint-one-busy/architecture-one-busy-30-db8a0474afa24ace9d9e703edc6461ea.json`

### Follow-up latency retest

The follow-up build exceeded 100 ms at 110.11 ms under equal receive and
116.43 ms under concentrated receive. The slow samples spent 4.67 ms and
1.70 ms changing the tab selection. Most elapsed time followed the selection
and synchronous layout. The cases occurred on the first timed visit to a tab
with a populated viewport. Measured GC pauses overlapped part of those
intervals.

Newly visible viewports can now prepare a snapshot in a single slot independent
of the four receive/terminal processing slots. An initial short pair reached
83.41 ms and 99.65 ms. Repetition still reached 120.98 ms and 125.60 ms, so
the slot change alone did not consistently meet the threshold. The renderer
now reuses horizontal glyph translations across rows and applies one vertical
transform per row. That change measured 70.05 ms and 101.42 ms. The final
candidate also schedules the initial visible frame at WPF's Loaded priority.
Repeated passive receive updates continue to yield to input.

The final Release candidate was measured for 30 seconds per receive
distribution after warm-up, with 20 SSH sessions and two WPF windows.
Each run sampled ten local interactions, including four tab switches, and
all 20 Raw logs matched the independent byte cycle.

| Receive distribution | Tab switch p95 | Stop p95 | Short-workload targets |
| --- | ---: | ---: | --- |
| Equal | 72.06 ms | 0.39 ms | Pass |
| One busy | 69.60 ms | 0.84 ms | Pass |

Both workload peers had SHA-256
`F51A89935217D99F0E2A8E2B794BCFFB6B211248C7FB89FE611BDEDE32DB2CD0`.
The reports are in
`artifacts/architecture-owner-release-3c28dff9e74d417a971ac31ac3d9cba4`:

- `architecture-equal-30-a0ea07f387054d858d48312eacfdfd6f.json`
- `architecture-one-busy-30-3a0d1fc078e64bce90bf91e373fa0317.json`

### Vault admission and delayed generation completion

The vault reserves 64 generation publication result slots, 64 management
delivery slots and 64 MiB for shared non-secret metadata projections. The
capacity fixtures independently fill all three allowances. Refusal preserves
the physical file and effective revision. An unresolved proposal on an unlocked
owner keeps its result slot until read-only adoption or owner disposal. The
management view reads already-committed metadata without acquiring another
vault worker slot.

Real file tests cover locked and unlocked restore after issued replacement and
observer cancellation, missing original preservation, different vault IDs,
substitution of a same-content backup file, and encrypted stale rescue.
Initialization tests exercise failure before parking, cancellation after
parking, install and compensation failures, identity substitution during
reinspection, and a deletion failure after verified installation. The WPF
fixtures close password input without publishing, retain the window during a
held initialization, refuse a competing writer, and display the exact paths
before and after reinspection. These paths determine completion from observed
file identities and bytes. Microsoft documents possible named-file effects
following a failed replacement and the no-overwrite move behavior.
[ReplaceFileW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-replacefilew)
[File.Move](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.move?view=net-10.0)

Controlled races retire a successful native unlock after its final consumer
leaves, reject a previously extracted credential after A→B→A, and fail an old
recording flush while the replacement records and sends. The scripted SSH peer
holds a cancelled SFTP OPEN and its corresponding CLOSE independently.
The original connection remains usable and permits one live explicit reopen
waiter. A replacement connection retains its own SFTP results. The clipboard
fixture holds a real captured Unicode block through Stop and reconnect,
then returns it while the replacement is sending. The previous source is
released exactly on return and the replacement Stop produces the exact bracket
closure. No system clipboard contents were changed by the HGLOBAL run.

The integrated Release build has zero warnings and errors. Core passed 90
checks. WPF passed 27 checks twice, file editors passed eight, the final-window
vault suite passed three and the vault faults suite passed four. The extended
HGLOBAL/SFTP integration and the isolated terminal-render check passed.
The initial recording fault fixture used a Raw-only writer with a profile
requesting three formats and expected the other streams to flush after Raw
failed. The corrected fixtures match the production format selection and
verify the recording group's actual failure policy. The three external checks
remain unavailable for the reasons already recorded.

| Build or fixture | SHA-256 |
| --- | --- |
| TabTty.Core.dll | `07D79C8BCA98FEAAE2B55CB2CBFC7559AC5596A74A637E70869747560C705A38` |
| TabTty.dll | `9F4294F44AC1424B0A1F93CC10AD4486F73BCC5D94A0BB490707944DBFED218E` |
| tabtty_terminal.dll | `C61B49145F728A77B05F061C8AF86F284F56146BCFBCE4D3BC2A4D5CDEEFCC4E` |
| TabTty.Core.Tests.dll, serial generation retest | `AF8688C640B619928E0981C616F06E47416E82543AA7873DC16B9E183660C2FB` |
| TabTty.App.Tests.dll, integrated run | `C729F957FB83BB6DB313014B787B42303B3546072B2D271E0D1205E66A0D2B50` |
| tests/fixtures/ssh-peer.cjs | `99D14E952FC46F6FE526878D4E2625CB931496E69F8AF48AB7DE101770C68A40` |

- Integrated logs are in `artifacts/architecture-vault-generation-verified-91ea9bf88d534df58ab6c005794b4424`.
- Core logs including the replacement serial adapter are in `artifacts/architecture-serial-generation-check-672580ccd78a4349a26d05bbe62777ec`.
- The WPF password cancellation retest is in `artifacts/architecture-vault-dialog-check-e1dac9be5f4b464f88c5925466b13b21`.

### Recording, publication and paste fault follow-up

The Release solution build succeeded with zero warnings and errors. Core
passed 90 checks. WPF passed 27 checks twice. The standalone file operations,
profile and vault editors, highlight/host-key faults, and vault-file faults
passed one, eight, two and four checks respectively. The three final-window
vault cases, separate terminal raster check and HGLOBAL/SSH/SFTP fixture also
passed. A fresh optimized Rust build passed 35 tests. The Unicode 17 corpus
matched all 766 cases through the integrated native DLL.

The recording fixture held a failed close and separately expired a close on the
test clock. Both cases retained the original file group after observer
cancellation and refused Retry until actual release. The first version of the
test expected Text recording to include a continuation of the previous
interval's line. The corrected test checks that the mixed line is excluded and
that the next line is written to the new part. An initial combined editor test
timed out. The highlight and host-key checks now run in their own WPF process,
and the original editor suite was rerun independently.

Actual Windows files verified hard-link exclusion, an unreadable backup, a
reported error after successful replacement, a reused stage name with identical
bytes, and cleanup failure before issue. With 64 unresolved operations, a new
publication was refused before staging, and all 64 could be reconciled without
issuing replacement again. A failed recovery retained the previous operation
and the newer cleanup record until exact file evidence permitted retirement.
File publication reserves 96 MiB for active candidate memory independently of
a 256 MiB artifact capacity. Admission charges the candidate's length and the
format's maximum possible original-preservation length before opening the
original. A full artifact budget refused replacement without changing the
target or creating a new file, and reinspection returned the reserved capacity.
Guarded vault initialization charges the encrypted stage and archive charges
the observed original length. The unissued vault fixture verified both the
old revision and exact credentials after cleanup-only retirement.

The competing-window case held one target's 2 MiB input allowance. The windows
shared one paste source, the affected window sent no bytes, and the independent
window stopped after the exact first byte. The held-send case verifies that
actual I/O retains its 64 KiB working reservation after observer disposal.
The local-echo case fills the terminal-state quota after preparation and again
after the first echo. The first echo uses its reserved capacity, the subsequent
echo is refused, and a separate terminal remains usable. The clipboard/SFTP peer
sends 80 history lines after review opens and holds its reply until the first
paste unit. On Stop the peer checks the exact UTF-8 unit and bracket framing.

The three environment-limited checks listed above remain unavailable. Items
explicitly marked open in the matrix remain engineering or verification work.
The integrated logs and binary and fixture hashes are in
`artifacts/architecture-acceptance-final-56f428f548e14aab9356e4c832d6ac9d`.

### Continued acceptance checks

Verified recovery now transfers surviving staging files into a separate
bounded record under their original file identity. It retains their names
and byte charge while releasing the recovered target for ordinary use.
Reinspection releases the record after the stage is gone and leaves a
foreign file at the old path untouched. A verified empty-vault initialization
likewise records a parked empty original that could not be removed. Profile,
JSON recovery and vault views can report the exact retained paths.

In the first integrated run after this change, the profile editor failed to
advance its saved revision. The new notice accessed the owner of an unhosted
test form even when the retained path list was empty. The notice now resolves
its window only when it has paths to display. Later full WPF runs passed the
original saved-revision check. A separate store test was also corrected to
schedule dismissal before triggering a synchronous capacity-error dialog.

The release tests cover real profile and settings revisions, host trust,
highlight publication, archive acknowledgement and actual SFTP provider
completion. SHA-256 files in the run directory identify the tested binaries
and loopback fixture. The checked RNG error path follows the existing
[OpenSSL return-value check](https://docs.openssl.org/3.5/man3/RAND_bytes/#return-values).
The SSH tests retain separate channel data, extended data, exit status and
close facts under the [SSH connection protocol](https://www.rfc-editor.org/rfc/rfc4254.html).

The short load retest used 20 SSH sessions in two WPF windows and a
30-second measured interval per distribution. Each interval included four
tab switches and ten local interactions. Each run preserved all 20 independent
Raw recording byte cycles.

| Receive distribution | Tab switch p95 | Stop p95 | SFTP round trips | Peak private bytes | Peak terminal-state charge |
| --- | ---: | ---: | ---: | ---: | ---: |
| Equal | 53.33 ms | 0.49 ms | 9 | 1,475,297,280 | 2,588,789,934 |
| One busy | 83.43 ms | 0.72 ms | 10 | 278,622,208 | 427,284,638 |

The measured candidate and workload reports are in
`artifacts/architecture-acceptance-complete-20a5050299de40d3b4dca24d7c233d94`.
The final parent-directory retirement check changes only file publication
reinspection and is covered by the subsequent integrated regression.

### Final integrated regression

The Release build completed with zero warnings and errors. Core passed 94
checks, including encrypted RSA key import and recovery reinspection after the
stage's parent directory was removed. Both full WPF runs passed all 27 checks.
The standalone runs passed three store, one file-operation, eight editor,
five vault-file fault and three final-window or editor checks. The terminal
raster and HGLOBAL clipboard/SFTP checks also passed. The loopback peer logged
connection aborts and a reset during the exercised shutdown paths. The isolated
clipboard check again returned Access Denied (5).

The final regression logs and SHA-256 manifest are in
`artifacts/architecture-acceptance-confirmed-f8769bde0e294fc78b92bc3fc9d39351`.
The remaining test exceptions for #41–#67 are the three checks under
Environment-limited checks. The separate sustained release qualification is
tracked by [#39](https://github.com/TabTty/TabTty/issues/39).
