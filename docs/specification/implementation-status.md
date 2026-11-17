# Implementation and validation status

The [Rust terminal and closure plan](https://github.com/TabTty/TabTty/issues/16)
is in implementation on `codex/16-complete-rust-terminal`. Baseline commit
`61aa1865c1cc083d09cf91eb9747cc0a9340ed69` preserves the original source,
lockfile, and generated Unicode data. The local `artifacts/issue-16-baseline`
input record includes SDK versions and native dependency hashes. The working
toolchain is .NET SDK 10.0.404, Rust/Cargo 1.63.0, and MSVC on Windows x64.

- The [UTF-8 baseline](https://github.com/TabTty/TabTty/issues/17) passed with
  a saved Telnet profile, independent Japanese transmit bytes, Rust receive,
  WPF display, exact Raw and Text logs, and retained output after disconnect.
  The test checks native model creation and zero managed starts or handoffs
  in that case. The pinned baseline supplies a separate comparison reference.
- The [transfer-close slice](https://github.com/TabTty/TabTty/issues/33) passed
  WPF checks with a loopback SSH/SFTP fixture. Duplicate requests share their
  outcome. A vetoed dismissal or a report closed before rendering retains the
  results. Pending provider ownership survives visual dismissal until actual
  completion.
- The UTF-8 application path uses shared Rust admission and execution rules.
  The loopback fixture checks exact reply, Raw, Text, and Terminal output,
  IME commits, and retained WPF presentation. The core tests exercise
  paste cancellation, response ordering, and a failed transport write.
- Terminal tabs, window closure, bulk removal, and automatic removal share
  a per-tab operation. Loopback WPF tests exercise stale confirmation,
  an overlapping window review, the cleanup wait decision, SFTP dismissal
  rejection, and held output after the Rust model is released.
- The 47 supported encoding identities use strict Win32 or Windows ICU
  conversion from Rust. Windows ICU supplies the JIS designations and
  repertoires. The former IN/OUT choices remain readable profile fields.
  See the [codec decision](../drafts/terminal-codec-consultation.md).
- Production sessions use one Rust VT implementation for all 47 encodings.
  Saved-profile Telnet tests exercise each identity with independent expected
  output and terminal replies. DATA and stderr own independent decoder state.
  A JIS test also verifies that a rejected batch leaves the live ICU decoder
  and its pending character intact.
- The Rust terminal implements cursor, scrolling, tabs, key and mouse input,
  alternate screens, resize, and recording intervals. Display and receive-only
  Text resize plans are both prepared before either geometry changes. Core
  fixtures cover split units across recording intervals, reflowed text, and a
  refused second resize plan. A height-only resize keeps existing row identities
  when the cursor row is already materialized.
- A recording writer failure ends the owned interval while the terminal
  continues receiving. The session refuses to report a failed pause as
  successful. The controlled failure test checks actual logs for both parts
  and verifies that an old writer cannot stop the replacement part.
- AI screen and history read the active and retained Rust terminal state.
  Rust owns UTF-16 anchors, UTF-8/serialized slice bounds, Unicode cluster
  boundaries, and range content and revision proofs. The .NET literal
  comparison preserves .NET Ordinal and OrdinalIgnoreCase semantics.
  The WPF Telnet fixture checks parked history, clipped wide cells, and
  rejection of a staged search result after an edit-and-restore.
- Terminal creation and snapshot requests report an explicit resource
  failure when their ownership cannot be admitted. The tests check that
  refusal leaves quota accounting and existing native state intact.
- The production managed parser, state handoff, and engine selector were
  removed. Deployment and workload acceptance are tracked under #16.

A 30-second native-only probe with 20 SSH sessions sampled ten interactions
at 1,047,139 payload bytes/s. All Raw logs matched their input and no session
disconnected. Local p95 was 157.016 ms and Stop p95 was 5.767 ms. This probe
missed the local target and is distinct from the 30-minute acceptance runs.

Commit `c2291df` passed the Windows Release build, 34 Rust tests, 80 Core
tests, and 27 WPF checks. The WPF background receive workload used one
through six saved Telnet profiles, 5000 retained rows per profile, and all
three log formats. File digests matched the independent fixture payloads.
The maximum across the six runs was 678,215,680 bytes private memory and
758,874,112 bytes working set. Sampling covered visible seeding, unselected
and minimized reception, EOF and log drain, and restored output.

The 30-minute equal-distribution SSH run on `c2291df` passed both response
targets over 600 interactions. Local p95 was 19.741 ms, maximum 161.254 ms,
and Stop p95 was 1.104 ms. All 20 Raw logs matched the expected cycle. The
one-busy run on that commit completed its 600 samples and matched the
cycle, but every history request on the receiving session returned busy.
New archive rows could evict an entire lazy indexing slice between requests.
The Rust model now catches up with the index during reception after the first
history request. The regression test exceeds the original per-request slice.

Commit `40935e7` passed 35 Rust tests, 80 Core tests, 27 WPF checks, and both
packaged MCP checks. An isolated loader check rejected a missing native
library and a library with an older ABI. The packaged ZIP SHA-256 is
`A87A9F9D7093850869A9A96191E931314849B5DD1809BD6EFEB406E285572AC7`.
The repaired candidate accepted 12 history or search requests on the receiving
session during the 12-second concentrated-load probe.

The repaired 30-minute one-busy run passed with 600 interactions. Local p95
was 20.612 ms, maximum 143.356 ms, and Stop p95 was 1.093 ms. The receiving
session accepted 2,391 history or search requests. All 20 Raw files matched
the assigned input and there were no disconnects. Measured payload throughput
was 1,048,571 bytes/s.

The equal-distribution rerun on `40935e7` also passed 600 interactions.
Local p95 was 15.728 ms, maximum 142.737 ms, and Stop p95 was 0.326 ms.
All 20 Raw files matched the assigned input, with no disconnects and
1,048,446 measured payload bytes/s. Both reruns used the native DLL packaged
in the verified ZIP. The background-memory rerun on the same candidate passed
at every count from one through six tabs. Its maximum private memory was
701,530,112 bytes and its maximum working set was 780,509,184 bytes.
Fresh-machine deployment and interoperability with independent full-screen
terminal applications remain unverified.

At commit `801f96f`, the self-contained Windows x64 package passed both MCP
bridge integration checks. The package SHA-256 was
`6B2DC4928BCC613E3614609241A96222CC4AFF7832C83F2A8B5AFD39C26DF78D`.
The 20-session, 12-second SSH workload completed and all 20 Raw recordings matched
the input. The run recorded four interactions, and maximum tab switch latency
was 267.8503 ms. It missed the local responsiveness target. The run used the
partial implementation and does not satisfy the native-only acceptance gates.

Target-design update, 2026-11-16: [ADR 0007](../adr/0007-complete-terminal-processing-in-rust.md)
requires [complete Rust terminal processing](native-terminal-core.md). The
implementation and acceptance evidence above applies to the current migration.
The older measurements below describe their respective historical candidates.

Baseline implementation recorded on 2026-11-11. SFTP implementation and
validation were added on 2026-11-14.
The contracts in this directory define the initial release scope. The macro
implementation below follows the separate Issue #3 specification. Store
packaging remains future work.

| Area | Implementation |
| --- | --- |
| Windows and profiles | Native WPF shell, English and Japanese, system language/theme resolution, fixed terminal defaults, same-user activation, movable/detachable tabs, monitor-aware placement and retained disconnected output. File-backed SSH/Telnet/COM profiles use a typed editor, bounded versioned JSON, registration/search/favorites, duplicate and recycle operations, and explicit store recovery. |
| Terminal | TabTty-owned screen/parser/renderer, Unicode 17 clusters and widths, 47 encodings, independent SSH DATA/stderr decoding, VT input/display modes, reflow, selection/copy and UIA Text ranges. Literal search and scheduled regex highlighting run off the receive path. Multiline paste has separately paced character/line delays, cancellation and application-run memory. |
| Transports | TCP Telnet negotiation and NVT processing, Win32 COM with line settings and BREAK, and a TabTty-owned SSH client with app-local OpenSSL. SSH supports host trust/replacement, password, keyboard-interactive and public key, PTY/shell, channel credit and bounded close, rekey and liveness. Initial SSH PTY and Telnet NAWS use the available viewport. |
| SSH algorithms | ML-KEM-768/X25519, Curve25519 and its alias, three NIST ECDH groups and DH group 14/16. AES-128/256-GCM and AES-128/256-CTR with HMAC-SHA-256/512 EtM. Ed25519, NIST ECDSA and RSA SHA-2 host and user signatures. |
| Credentials | Portable encrypted .tttykey container with Argon2id and authenticated root/payload encryption. One shared first-use unlock. Entry updates/deletion, encrypted backup, guarded restore, master-password change, and supported OpenSSH, PKCS#8, RSA and EC private-key import envelopes. |
| Recording | Independent Raw, receive-only Text, and combined Terminal projections. Retained-line edits and reflow preserve Text boundaries, local wraps add no LF, and pause/retry start new numbered parts. The background writer batches up to approximately one second, bounds pending ownership, preserves in-file order, and offers bounded close choices. |
| SFTP | Initial-release two-pane transfer and file management over a separate SFTP v3 channel on the authenticated terminal connection. See the [transfer contract](file-transfer.md) for behavior and acceptance cases. |
| Broadcast and split views | Window-local and application-wide broadcast input, recipient checks, paste progress and cancellation, recursive tab groups, and same-session terminal panes. See the [feature contract](broadcast-and-split-views.md). |
| Macros | Versioned .tttym launch, bounded interpreter, independent terminal receive matching, run controls, credential references, dialogs and recording recovery. See [Issue #3](https://github.com/TabTty/TabTty/issues/3). |
| AI collaboration | Per-tab output sharing and input grants, keyboard-accessible takeover, bounded screen/history observations, and a bundled Windows stdio MCP bridge. The application owns the same terminal and input queue across manual, broadcast, macro, and AI operation. See [Issue #2](https://github.com/TabTty/TabTty/issues/2). |

## Rust terminal-core adoption and responsiveness, 2026-11-15

The Rust core is now the production default for supported UTF-8 terminal
reception, normal-screen edits, retained history and batched Text/Terminal
effects. C# still owns transports, session/input coordination, log files and
WPF. The native crate has no external crate dependencies. See
[ADR 0006](../adr/0006-native-terminal-core-with-lossless-managed-handoff.md).

Unsupported operations transfer the complete terminal state once into the
existing C# engine before processing the affected batch. This preserves output,
recording revisions, row identities, styles, cursor, modes and partial decoder
and parser state. Legacy encodings start in C#; after-data resize/reflow,
alternate screen, AI observations, macro hooks and some controls can cause
handoff. Common shell OSC and bracketed-paste changes remain native. A model
that hands off stays managed; native throughput is conditional on residency.
Performance tests assert complete native byte coverage and zero handoffs.

The latency investigation found two separate costs: full expanded snapshots and
WPF drawing allocations in the application, and synchronous diagnostic Raw-file
opens in the old prototype's UI-thread sampler. The latter introduced roughly
50–100 ms pauses, so older prototype latency figures below are withdrawn as
application responsiveness evidence. Frozen baselines were rerun with the same
corrected sampler. The separate formal issue #15 test is unaffected.

The application now captures immutable compact snapshots and expands cells only
for rows a consumer uses, reserves expansion quota before rendering, requests a
fresh activation frame promptly and caches bounded frozen drawing transforms.
Native admission caches exact validated batches while retaining pre-mutation
resource checks. Retention, all three logs and the existing idle/tab/scroll
highlight triggers are unchanged. Twenty-four rendered-frame comparisons at
three font sizes and two DPIs matched the repeated unchanged baseline.

All runs use 120x40 cells, 5000 retained rows, all three logs and Workstation GC.
The corrected six-tab, aggregate 2 MiB/s, 60-second/120-operation comparisons below use identical historical
readiness (new snapshot plus a Render-priority barrier); the current native-on
and native-off controls use the exact same published App/Core. Private peaks are
sampled every 100 ms from seeding through drain/restore and use decimal GB.

| Variant | Combined p95 (ms) | Tab p95 (ms) | Tab maximum (ms) | Private peak (GB) |
| --- | ---: | ---: | ---: | ---: |
| Frozen older C# | 149.4 | 221.6 | 437.7 | 1.304 |
| Frozen Rust prototype | 127.8 | 134.8 | 191.8 | 0.742 |
| Published app, native off | 135.0 | 351.4 | 394.7 | 1.306 |
| Adopted Rust, repeat a / b | 59.8 / 69.5 | 76.5 / 77.8 | 104.5 / 104.0 | 0.630 / 0.651 |

Final strict runs also require the selected new snapshot to complete OnRender.
They rotate tab switching, inline-menu open/close and input admission equally,
with latency measured from the originally scheduled due time. Rates are
aggregate loopback Telnet offers; completed receive rates include log drain.

| Offered MiB/s | Duration / operations | Combined p95 | Tab p95 / max | Menu / input p95 | Tab samples >100 ms |
| ---: | --- | ---: | ---: | ---: | ---: |
| 2 | 300 s / 600 | 58.6 ms | 66.0 / 96.3 ms | 20.8 / 14.1 ms | 0 / 200 |
| 5 | 60 s / 120 | 98.9 ms | 123.4 / 147.0 ms | 21.5 / 14.7 ms | 6 / 40 |
| 10 | 60 s / 120 | 110.3 ms | 128.8 / 190.5 ms | 31.3 / 22.1 ms | 7 / 40 |

Every operation kind met p95 100 ms at 2 MiB/s; the maximum across 600 samples was
96.3 ms. This is a **five-minute** test, not the earlier thirty-minute formal
acceptance. At 5 MiB/s, the combined p95 passes but tab switching fails. At
10 MiB/s, both fail; completed throughput was 6.421 MiB/s with 33.3 s drain,
whereas the 5 MiB/s trial completed at 4.975 MiB/s with 0.10 s drain. Private
memory stayed below 0.67 GB and working set below 0.77 GB in these strict trials;
the 600-operation trial used 0.641 GB private and 0.730 GB working set.

The remaining tab tail is predominantly the wait before a fresh snapshot is
published. From action completion to observation of the new snapshot, p95 was
113.7 ms at 10 MiB/s, compared with 67.0 ms in the 2 MiB/s repeat; this is not a
direct serialization or lock-wait measurement. Final render waiting was usually small. Cold retained-row
copying, per-row quota reservations, GC and shared receive/capture scheduling
remain plausible contributors; their individual shares are not isolated.
A separate 17-to-33 ms flowing-frame trial reduced allocation but raised tab
p95 to 141.4 ms and did not reduce GC pause. It was rejected. Immediate
activation and the shipping 17 ms ordinary interval remain in place.

Receive-only measurements were run separately, one trial per condition. The
headless Core uses 20 s warmup plus 20 s observation. The actual WPF application
offers 100 MiB over 20 s to one minimized tab, or 200 MiB over 20 s to six hidden
tabs; completion includes EOF/log drain. Actual current C#/Rust tests use the
exact published App. Intermediate pre-publish results are not used in this table.

| Receive case, MiB/s | Current C# control | Earlier Rust prototype | Adopted Rust |
| --- | ---: | ---: | ---: |
| Saturated Core, 1 session | 3.694 | 5.795 | 4.307 |
| Saturated Core, 6 sessions | 5.408 | 11.508 | 8.870 |
| Actual minimized, 1 tab, offer 5 MiB/s | 2.273 | 3.414 | 2.773 |
| Actual hidden, 6 tabs, offer 10 MiB/s | 4.711 | 9.743 | 8.719 |

Actual completion is 1.22x/1.85x the current C# control. Six-tab private memory
fell from 1.081 to 0.550 decimal GB, working set from 1.174 to 0.637 GB, and
GC pause including drain from 15.628 to 0.372 s for the same 200 MiB. All three
recordings match between actual-client variants. Native coverage guards passed.
The production version is slower than the limited earlier native prototype:
18.8% minimized/10.5% hidden, and 25.7%/22.9% in the one/six-session Core test.
The compatibility/admission implementation adds work; this comparison does not
isolate every source of its remaining overhead. C# speed and memory improvements
are retained, but the earlier prototype's peak receive speed is not fully retained.

These measurements establish the tested operating conditions, not a strict OS
memory cap or response guarantee under every load. They do not measure high-rate
SSH encryption, external network delay, physical display presentation or long-run
reliability. The final controls/repeats and failing overloads are all retained.

Validation on the final Core/native pair: 77 Core checks, 24 WPF checks,
29 Rust checks in each Debug and Release configuration, and 2 packaged MCP
checks passed. The C# diagnostic path passed 76 legacy checks; its native-only
group was skipped. Seven broad differential cases and 13 strictly native cases
matched all recording bytes and canonical terminal state against C#. The broad
suite exercised six expected handoffs; the strict suite finished with no
handoffs, fallbacks, native errors or leaked models/bytes. Optional SSH/key
fixtures were not rerun for this adoption.

The self-contained [Windows x64 package](../../artifacts/TabTty-win-x64-20261115-rust.zip)
includes the native DLL, static CRT, license notices and source/compiler/hash
metadata. ZIP SHA-256:
`2C5E8DDE53C931BFDCBCBD19709B174C04CBD12596D46F986B62AE7230CFED6C`.
The measurements select published App `14635815…`, Core `D27378B3…` and native
`8C6ABF95…`; superseded pilots remain labeled separately. See
[complete findings](../../artifacts/rust-terminal-adoption-20261115/findings.txt),
[audited evidence](../../artifacts/rust-terminal-adoption-20261115/evidence-summary.json)
and [selection manifest](../../artifacts/rust-terminal-adoption-20261115/evidence-manifest.json).

## Rust terminal-core experiment, 2026-11-15

Historical note: the interactive latency figures in this section include
synchronous diagnostic Raw-file observations on the UI thread. They are retained
as investigation history and must not be used as application latency evidence.
Use the corrected, frozen-binary comparisons in the adoption section above.
The separate issue #15 formal test did not use that intrusive sampler.

An isolated prototype moved supported UTF-8 decoding, normal-screen editing,
retained history and batched Text/Terminal serialization into Rust. C# continues
to own transports, session/input coordination, file writers and the unchanged
WPF client. Two independent native models preserve receive-only Text and combined
display/local echo. This prototype was not adopted at that stage; the production
integration and its compatibility boundary are recorded above.

Measurements completed on 2026-11-14 JST and were recorded on 2026-11-15.
All cases retain 5000 rows at 120x40, all three logs and Workstation GC. The core
test uses 20 seconds warmup plus 20 seconds measurement. Actual WPF rates include
EOF/log drain; one run per throughput condition is shown.

| Measurement | Existing C# | Rust core |
| --- | ---: | ---: |
| Saturated core, 1 session (MiB/s) | 3.520 | 5.976 |
| Saturated core, 6 sessions (MiB/s) | 5.070 | 11.281 |
| Actual minimized, 1 tab, offered 5 MiB/s for 20 s | 2.175 | 3.425 |
| Actual background, 6 tabs, offered 10 MiB/s for 20 s | 4.799 | 9.766 |

Actual completion improved approximately 1.57x and 2.04x. At the lower 3 MiB/s
minimized offer it improved from 2.055 to 2.980 MiB/s; at a 5 MiB/s background
offer both variants were near the sender cap (4.878 and 4.913 MiB/s). Every
background/minimized pair produced identical Raw, Text and Terminal bytes.

For the 6-tab higher-rate case, peak private memory / OS peak working set changed
from 1.065 / 1.159 to 0.636 / 0.720 decimal GB. The sampled receive-phase private
peak was 1.037 versus 0.526 GB, before EOF disposal of the receive-only model.
GC pause including drain fell from 15.250 to 0.464 seconds for the same 200 MiB
payload. Across the final actual-client trials, Rust stayed at or below 0.719 GB
private memory and 0.811 GB working set. These are observations, not enforced caps.

Responsiveness was measured separately at aggregate 2 MiB/s with 6 tabs,
60 seconds and 120 operations (40 tab, 40 menu, 40 input), twice per variant
in reversed order. The 100 ms p95 objective remains unmet.

| Latency (ms), run a / b | Existing C# | Rust core |
| --- | ---: | ---: |
| Combined local-operation p95 | 226.3 / 257.1 | 193.4 / 186.6 |
| Tab-switch p95 | 336.2 / 306.6 | 207.3 / 248.4 |
| Tab-switch maximum | 424.6 / 451.7 | 308.0 / 304.6 |
| Menu p95 | 129.4 / 131.3 | 106.7 / 108.9 |
| Input-admission p95 | 124.6 / 134.3 | 104.9 / 107.0 |

Interactive Raw/Text bytes matched; Terminal interleaving is timing dependent
because each run includes local echo. Deterministic replay verifies local-echo
ordering separately. Interactive GC pause fell from 12.89–13.62 to 4.64–4.72 s,
but cumulative managed allocation remained about 5.10 GB over 60 seconds.
This allocation traffic is distinct from retained process memory. Snapshot
materialization and WPF work remain investigation candidates; their precise
contribution to the remaining latency has not been profiled. Post-EOF restore
was not uniformly faster: one minimized high-rate sample was 61.7 versus 75.2 ms.

Validation: 13 supported differential cases matched all recording bytes and
canonical terminal state; 7 negative-scope checks, 4 ownership checks and 10 Rust
tests in each Debug/Release configuration passed. Native-off Core 76 and WPF 23
checks preserve the C# path; they do not establish full native compatibility.
All native performance runs verified actual native byte counts and zero
unsupported calls or managed fallbacks.

The native scope excludes alternate screen, after-data resize/reflow, recording
resume, AI history and macro hooks; legacy codecs select C# at creation only.
Full terminal compatibility and strict native allocation admission remain
production work. These results support the wider native boundary for receive
performance and memory, without proving a language-only speedup or satisfying
the response objective. They do not replace the earlier formal #15 acceptance,
and do not measure high-rate SSH encryption or physical display latency.

See [findings](../../artifacts/rust-terminal-core-prototype-20261115/findings.txt),
[exact results](../../artifacts/rust-terminal-core-prototype-20261115/results.json) and
[methodology](../../artifacts/rust-terminal-core-prototype-20261115/methodology.txt).

## Rust history-storage experiment, 2026-11-14

Historical note: this experiment's interactive latency harness also observed
Raw files synchronously on the UI dispatcher. Retain its figures as diagnostic
history, not application responsiveness comparisons. Receive throughput and
recording equality were verified separately.

An isolated prototype stored immutable scrollback cells in Rust-owned compact
arrays, retaining the C# parser, editing behavior and WPF application. A second
prototype used the same packed representation in managed C# to distinguish
storage layout from native ownership. That experiment left the optimized C# product
unchanged and was not adopted. Builds used Rust 1.63 and .NET SDK
10.0.404.

The table records one completed run per condition from series **b** only.
Core rates are aggregate saturated diagnostic throughput. The real background
case offered 100 MiB over 20 seconds to six tabs at 5 MiB/s aggregate; completion
rates include EOF and log drain.

| Diagnostic | Existing C# | Rust storage | Managed packed control |
| --- | ---: | ---: | ---: |
| Core, 1 tab (MiB/s) | 3.576 | 2.556 | 2.028 |
| Core, 6 tabs (MiB/s) | 5.347 | 4.084 | 2.903 |
| Real background, 6 tabs (MiB/s) | 4.880 | 3.859 | 2.911 |

In the background case, peak process private memory fell from 1.070 to
0.584 decimal GB and OS peak working set from 1.165 to 0.674 GB with Rust.
However, completion throughput fell approximately 21%. A separate six-tab,
2 MiB/s interactive diagnostic (60 seconds, 120 operations) had aggregate local
UI p95 of 264.8 ms for existing C#, 305.1 ms for Rust and 332.7 ms for the managed
packed control. Tab-switch p95 was 357.6 ms for existing C# and 344.3 ms for Rust.
The interactive private-memory peaks were 1.301 and 0.677 GB respectively.
Both versions missed the 100 ms response objective in this additional workload.

Repeating the interactive test in reverse order (series c, another 120 operations
per version) gave local UI p95 of 285.7 ms for C# and 323.7 ms for Rust. Rust's
tab-switch p95 was lower in both pairs, so the response change is not uniform.
Rust remained below 0.784 GB peak working set during both interactive runs.
In a separate minimized 1-tab test, completion fell from 2.270 to 1.522 MiB/s;
whole window-restore/selection readiness was 65.7 versus 79.3 ms (one sample each).

Each packed mode passed Core 76, WPF 23, seven differential replay cases and five
ownership checks; five native Rust checks also passed. Recorded bytes and terminal
state matched the C# reference. The memory improvement did not preserve receive
speed or establish adequate responsiveness, so this storage-only boundary is
not selected for production. This result does not rule out other Rust boundaries.
These short diagnostics are separate from the existing formal #15 acceptance.

See the [experiment report](../../artifacts/rust-receive-prototype-20261114/findings.txt)
and [exact results](../../artifacts/rust-receive-prototype-20261114/results.json)
for conditions, binary hashes, isolated sources and subsequent measurements.

## C# background receive optimization, 2026-11-14

The user authorized implementation for minimized windows and non-selected tabs,
with approximately 1 GB whole-process memory as an additional objective.
Text-row serialization now reuses completed content revisions, ordinary cells
write directly into an exact-sized string, and small log buffers encode through
bounded stack storage. Retention, all three logs and Workstation GC are preserved.

The saturated core diagnostic (20 s warmup + 20 s measurement, one run per case)
improved aggregate throughput from 3.223 to 3.725 MiB/s for 1 tab, 3.811 to
5.085 MiB/s for 3 tabs, and 4.320 to 5.064 MiB/s for 6 tabs. Managed allocation
per payload byte fell approximately 60%.

A separate real TerminalTab / loopback Telnet diagnostic populated 5000 rows
per tab before hiding them. Whole-stream completion, including EOF/log drain,
improved from 1.885 to 2.111 MiB/s for 1 minimized tab and from 4.112 to
4.757 MiB/s aggregate for 6 background tabs. All Raw bytes/hashes and final
Text markers passed; hidden snapshots stayed frozen and every tab presented
its latest state on restoration.

The 6-tab candidate's peak process private memory was 1,074,671,616 bytes
and OS peak working set was 1,157,992,448 bytes (1.075 / 1.158 decimal GB).
A strict 1 GB ceiling is not achieved or enforced. The baseline peaks were
1.051 / 1.126 GB; lower allocation did not establish a lower 6-tab process peak.
Memory covers visible-history seeding through receive, drain and restoration;
private samples are 100 ms apart and may miss shorter spikes. Hidden rich
snapshot ownership remains a candidate for further C# work.

Release solution build passed with zero warnings/errors; Core passed 76 checks
and WPF passed 23. Seven deterministic baseline/candidate replays matched all
log parts, normalized snapshots, replies and search mappings. These short,
single-run diagnostics do not replace 30-minute/600-operation or high-rate SSH
acceptance. Restore measurements are snapshot readiness, not physical-pixel
latency: the minimized window's complete restore/selection interval was 106.9 ms.

See the [measurement report](../../artifacts/csharp-receive-optimization-20261114/findings.txt),
[exact results](../../artifacts/csharp-receive-optimization-20261114/results.json),
and accompanying frozen harnesses, reports and source patch.

## Architecture follow-up validation, 2026-11-14

[Issue #4](https://github.com/TabTty/TabTty/issues/4) tracks the three
architecture refactors. Implemented changes are in the local TabTty working
tree. The Release build passed with .NET SDK 10.0.404 and zero warnings or
errors. Core and WPF suites passed with the independent loopback peer. The
MCP Bridge subprocess suite passed discovery, private transport and unavailable
GUI checks. The application publication tests exercise shared history with the
real terminal model and controlled queue admission.

- File operations covered refreshed overwrite approval, unknown commit and
  recycle outcomes, cancellation before a late approval, nested transfer,
  local and remote create/rename/delete, Windows Shell recycling, and reopen
  while the prior connection still owns provider work.
- AI history covered edit and restore, append and reflow, retirement, 100,000
  retained rows, Unicode cluster limits, paged continuation, bounded search and
  its coverage, and rejection at publication on queue, cancellation, expiry
  and sharing changes.
- Broadcast input covered cancelling inert previews, paced Stop Paste, target
  encoding preflight, overlapping gestures, capture cancellation, stale and
  duplicate events, cross-window placement and last-tab closure. Physical
  capture and drag-and-drop across different-DPI monitors remain open in
  [#14](https://github.com/TabTty/TabTty/issues/14).

[The workload runner](../../eng/Test-ArchitectureWorkload.ps1) uses the
repository's loopback peer, the test-only `ssh2` installation and synthetic
OpenSSH `fixture_host` key under `artifacts/interop-ssh`, the encoded reference
cycle, and the Release App test binary. Use `-Distribution equal` or
`-Distribution one-busy`. The default measurement interval is 1,800 seconds. Reports include fixture/application/cycle digests, elapsed
time, payload throughput, sample counts, latency tails, and quota/memory
measurements. AI operations in the combined workload call the application
publication seam. The independent bridge tests use the real private channel.
The combined workload does not yet exercise every pressure, revocation, and
movement case required by [#15](https://github.com/TabTty/TabTty/issues/15).

The fixture used Windows x64 build 26300, eight logical processors, two WPF
windows, twenty loopback SSH sessions, 120×40 grids, enabled recording and
the reference highlight patterns. The reference cycle SHA-256 is
`60DE481A2DEC1F4446B0773E64A5C18E43311DEFD4D86CD48702779A50581EC9`.

| Distribution | Seconds | Interactions | Payload bytes/s | UI p95 / max (ms) | Stop Paste p95 / max (ms) | Verified transfer cycles | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| equal | 1800.1 | 600 | 1,048,551 | 526.3 / 2093.9 | 37.6 / 529.2 | 465 | UI target missed |
| one-busy | 1800.0 | 600 | 1,048,589 | 47.2 / 368.5 | 0.6 / 67.6 | 586 | Targets met |
| equal | 60.0 | 20 | 1,047,513 | 343.2 / 344.7 | 14.6 / 14.6 | 18 | UI target missed |

The agreed 30-minute tests target 100 ms local p95 and 250 ms command
acceptance p95 under overload. The measured Stop Paste samples are local
acknowledgement. Other cancellation/disconnect/takeover and simultaneous
pressure cases remain open. The combined AI workload used the application
publication seam. MCP bridge private-channel behavior was tested separately.
The reports retain the separate build hashes. The 30-minute runs used a
newly constructed test menu per sample. The concentrated run and the
final diagnostic include the glyph-layout cache and Raw log comparison.
The final diagnostic uses the application's existing menu.

Reports in `artifacts/architecture-workload` identify these runs.

- `architecture-equal-1800-1cdd14550a554fd08a98e2b303f66cb7.json` used App SHA-256 `D722E3D42122BE007360EE527B72442473661778DBE8BF76C6F52D91F6FD64FA` and peer fixture SHA-256 `B0E97D8548C308CD4CBE13A23DFF71EFEDFB8F1DFF79963C802C425D02107217`.
  Peak private bytes 3,644,080,128, terminal charge 3,697,209,614, AI charge 20,154,368, log payload charge 1,279,260.
- `architecture-one-busy-1800-75e1270c48974608b8c0d6aed0273c30.json` used App SHA-256 `DE6E1FB6FF9E9CA04F7B075B9C7173EC945ED4E370461986FBE6AE1BB64D8F89` and peer fixture SHA-256 `B3C834DFCD8E6E5117272AC7695B2CE0CCD68F139DE278474D03BD74F89EC946`.
  The Raw output of 1 of 1 producers matched the generated sequence.
  Observation errors were busy 891, range_evicted 3,399, range_changed 190.
  Peak private bytes 395,182,080, terminal charge 304,064,912, AI charge 20,154,368, log payload charge 71,369.
- `architecture-equal-60-85bcb826968f4d05aa1218b3c9e7a80f.json` used App SHA-256 `09CBD24A4071577B0BED9E95EF6D4EACF6402C4E6F36ED2CC1617F5AD114E08D` and peer fixture SHA-256 `852C8A501E00191BF746B4B6CD3FB74D41D93CEE5A3306CE6E41202A5FBBC554`.
  The Raw output of 20 of 20 producers matched the generated sequence.
  Observation errors were busy 39, range_evicted 57.
  Peak private bytes 3,217,285,120, terminal charge 3,558,204,148, AI charge 20,154,368, log payload charge 1,199,632.

## AI collaboration validation, 2026-11-14

The Windows x64 Release solution build used .NET SDK 10.0.404 and passed with
zero warnings and errors. The core suite passed 78 checks and the WPF suite
passed 21 with the independent loopback SSH/SFTP peer. The AI WPF check used
the shared application interface and a real Telnet session. It covered
authorization, broadcast eligibility, alternate-screen reads and parked
normal history, Japanese search, range continuation, wait notification,
composition admission, duplicate delivery, observation generations, bracketed
paste takeover, and a paused macro's read-only sharing. A separate assertion
edits search context between capture and disclosure and verifies that the shared
commit path returns a range-change error.

An independent stdio client exercised both MCP revisions through the packaged
Windows pipe and verified empty discovery before sharing, foreign-session
denial, cache metadata, malformed/unsupported requests, ordinary-call
throttling, reserved return handling, and the missing-GUI deadline.
The package contains both executables and the .NET/WPF 10.0.15 runtimes.
The verified Windows x64 archive is
`TabTty-ai-collaboration-20261114-101227.zip` with SHA-256
`B051BBF840ED4878729125D3AD8B3BFD485386A6FEF4A94FAC844C430CEBB171`.

The remaining [validation plan](ai-collaboration-validation.md) includes
real Goose/Ollama and LM Studio model runs, blocked-egress operation, real
top/vim tasks, native IME/Narrator and physical COM interaction, cross-logon
and elevation cases, and the reference 20-session performance workload.
These checks are pending. The history implementation uses composable row
fingerprints and captures search slices under the session lock. If range proof
construction exceeds the work budget, the call reports busy and preserves the
existing continuation. Large-buffer search latency and memory use still require
measurement.

## Macro validation, 2026-11-14

The Release solution build passed with zero warnings and errors. The core
suite passed 78 checks with the independent SSH peer and its SFTP channel
enabled. Twelve core checks exercised macro behavior, including the SSH
startup receive path and the configured profile credential reference. The
WPF suite passed 20 checks, including launch arguments, modeless dialog
ownership, default Cancel and acceptance of empty text before connection.

The full macro acceptance matrix, reference-machine Pause and Stop latency,
and physical COM communication remain to be verified. The subsequent AI
collaboration integration includes a paused-macro ownership test.

## Broadcast and split view validation, 2026-11-14

The Release build completed with zero warnings and errors. The core and WPF
test suites passed. The WPF integration test used three loopback connections
and the application menus to verify split creation and removal, per-recipient
encoding, an unchecked input source, mode changes and selection resets, and
rejection before sending on an encoding failure. It also verified global paste,
Stop Paste across the frozen recipients, and moving a tab during that paste.
The move closed the source window and set the moved tab's check for future
operations. The disconnected-tab test verifies final-tab window closure.

The core tests cover skipped out-of-grid motion, re-entry, clamped release,
classic coordinate limits, stale generations, and press-only modes. The WPF
test verifies discarding a deferred composition result after pane focus moves.
Physical button capture and release across window moves and DPI changes, a live
Japanese IME composition across panes, Narrator workflows, and sustained rendering
with multiple visible panes remain desktop and performance acceptance checks.
Use the [acceptance matrix](broadcast-and-split-views.md#acceptance-cases) for
those checks.

## SFTP validation, 2026-11-14

The Release build completed with zero warnings and errors. The core and WPF
suites passed with the loopback SFTP fixture enabled. The core check covered
terminal traffic on the same authenticated connection, concurrent rekey, binary
payload fidelity, Raw log isolation, and continued terminal use after subsystem
refusal. The WPF-suite batch check covered recursive upload and download,
including an empty subfolder and an approved local overwrite in a merged tree.
Set `TABTTY_INTEROP_SFTP=1` with `TABTTY_INTEROP_SSH_PORT` to run both checks.

Independent OpenSSH server interoperability, hands-on desktop acceptance
(including drag-and-drop and deletion), and sustained transfer-load validation
remain release checks. Use the [SFTP acceptance cases](file-transfer.md#acceptance-cases)
and [resource and release gates](resources-and-validation.md) to track coverage.

## Build and package

Use the .NET 10 SDK and Windows x64. Building the terminal core also requires
the stable Rust MSVC x64 toolchain and Visual Studio C++ x64 tools. The normal
Core build invokes Cargo offline with the checked-in lockfile; the crate has
no external dependencies. `./eng/Build-TerminalCore.ps1` can build its DLL alone.
For the OpenSSL/helper build, use PowerShell 7 and a native Windows Perl in
addition to the C++ tools. From the repository root run
`./eng/Prepare-Native.ps1`, then `./eng/Publish.ps1`. The runtime
and development tools are separate. The generated self-contained ZIP contains
the application, .NET/WPF runtime, Japanese resources, OpenSSL, the scoped
OpenBSD bcrypt adapter, Rust terminal core, provenance metadata, and
redistribution notices. The terminal DLL links the C runtime statically;
`terminal-build.json` records its exact source, compiler and DLL hashes.

The original 2026-11-11 build used .NET/WPF 10.0.14 and the official OpenSSL 3.5.9 source archive
with SHA-256 `603f5602e2eef00d77fbd429d34dcd5822bb301757a1bc9cdb24c670f1eb859a`.
The x64 DLL was built with MSVC 14.44.35229. Its SHA-256 is
`4b4d9c75182a243b2caaf0a8167c37b79080bbbe26113f46c556c5dc52057de4`.
The helper's SHA-256 is
`2291898ba666b84618b75dd0e6789dbd6bd680201975c35e7fb9750e0358382d`.
The OpenSSL source build uses `VC-WIN64A-HYBRIDCRT` with the bundled default
provider, without external configuration or optional modules. Both native DLLs
are loaded from the application directory. The package records the exact
runtime-pack and native hashes used.

The baseline evaluation archive is
`artifacts/TermTab-win-x64-20261111-evaluation.zip`, built before SFTP was added.
Its SHA-256 is `9c834871e3e9d460aa67f22288d2004e10b2b06d2bbf76c7fa973ae7bf60f39a`.
The published executable passed startup, same-user forwarding of a second
invocation, and orderly exit after closing the initial window.

## Executed tests

```powershell
dotnet build TabTty.slnx -c Release
dotnet run --no-build -c Release --project tests/TabTty.Core.Tests
dotnet run --no-build -c Release --project tests/TabTty.App.Tests
```

The suites exercise Telnet over loopback, an unavailable COM port, profile
editing and store recovery, shared unlock cancellation/failure, independent
Argon2id/HKDF/GCM vault data and AES-CTR/GCM/HMAC vectors, encrypted recovery,
malformed packet tags, recording/reflow/encoding, and off-screen WPF rendering
and UIA. Loopback tests require local socket access. Crypto tests use the
app-local OpenSSL build copied by the shared targets.
The Release build completed with zero warnings and errors. The core and WPF
suites passed 68 baseline checks, plus five optional SSH/key fixture checks.

SSH interoperability used the ssh2 1.17.0 loopback fixture, including an
independently implemented ML-KEM/X25519 hybrid peer with Node's crypto API.
Across the runs, the peer verified all configured key exchange groups, both
GCM ciphers, CTR/EtM combinations, all host-signature names, all three user
authentication methods, rekey, split DATA/stderr and non-UTF-8 data. The
final native build also exercised PTY geometry, channel payload arriving
before the shell success response, and encrypted OpenSSH/PKCS#8 key imports.
The fixture corrects ssh2's missing comma in server-sig-algs and the RSA
PK_OK algorithm name. Those cases do not establish equivalence with an
unmodified upstream server.

To repeat the default peer test, copy `tests/fixtures/ssh-peer.cjs` and
`tests/fixtures/prepare-ssh-peer.cjs` to a new directory under `artifacts`.
In that directory install the test-only dependency
`npm install --ignore-scripts --save-exact ssh2@1.17.0` and run
`node prepare-ssh-peer.cjs` with Node 24. Generate a disposable Ed25519
OpenSSH host key named `fixture_host` and start `node ssh-peer.cjs port.txt`.
Set `TABTTY_INTEROP_SSH_PORT` to the resulting port before running the
core suite from the repository. Set `TABTTY_INTEROP_EARLY_DATA=1` to check
PTY size and pre-shell capture, and `TABTTY_OPENSSH_FIXTURE` to a disposable
OpenSSH private key encrypted with the fixture passphrase
`import-test-passphrase` to exercise bcrypt import. The optional test dependency
and keys are absent from the Windows package.

## Performance and remaining acceptance

The diagnostic runs used 20 headless synthetic sessions with all three
recordings, 120×40 grids, 5000 retained rows and an aggregate of approximately
1 MiB/s. The final batching run measured 30 seconds per distribution after
20 seconds of warmup. Input acceptance p95 was 13.1 ms (equal) and 14.6 ms
(one busy producer). Send completion p95 was 64.2 ms and 14.7 ms, respectively.
All 20 Raw files matched the independently generated received-byte digest in
each run. The measured peak working set was about 2.42 GB in both runs.
The cycle and report files are under `artifacts/core-stress-20261111-batched`.
These short headless runs are separate from the 30-minute SSH/WPF acceptance
workloads in [resources-and-validation.md](resources-and-validation.md).

The initial package still requires clean-PC verification, physical COM
transfer and modem-line tests, interactive IME/Narrator, tab dragging and
file association workflows, storage fault injection, the prescribed full
performance workloads, and independent review of the SSH/vault/native code.
Store certification and publication remain future work.

The embedded Unicode17.bin contains sorted property ranges generated from the
Unicode 17.0.0 UCD files EastAsianWidth, GraphemeBreakProperty,
DerivedCoreProperties (InCB), DerivedGeneralCategory (marks), and emoji-data.
Its SHA-256 is `e733cbce929cc14bea4191ef0c0982bb1296548fbbcbad88fc9417894ea3e0f6`.
Supplemental ISO-8859 tables come from the Unicode mapping files and CP932
positions come from Microsoft's published table.
