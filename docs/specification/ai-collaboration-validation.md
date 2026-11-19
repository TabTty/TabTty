# AI collaboration validation plan

Confirmed testing approach, 2026-11-14, for accepted Q1-Q35 in the
[AI collaboration interview](../drafts/ai-terminal-collaboration-issue.md).
The user explicitly confirmed the shared UI/AI collaboration-operation boundary
and complementary Windows, MCP, and real-host integration tests before publishing
[GitHub issue #2](https://github.com/TabTty/TabTty/issues/2) with `ready-for-agent`.
This document defines acceptance criteria. Consult
[implementation status](implementation-status.md#ai-collaboration-validation-2026-11-14)
for the results and remaining cases. Source review and contract consistency
review provide design evidence.
Read with the [application/API contract](ai-collaboration.md),
[transport contract](ai-collaboration-transport.md), and existing
[session contract](session-and-recording.md).

## Layers and independent observations

Use the application/session interface used by the UI and AI adapter as the
primary test seam. Observe permission state, immutable terminal snapshots,
bounded result pages, and the session's ordered admitted/submitted input events.
Do not test the feature solely through private methods or a replacement session
implementation. Extend the existing [terminal seam](tdd-seams.md) with the
collaboration operations when implementation is authorized.
Cover every exported tool: `tabtty_list_shared_sessions`, `tabtty_read_state`,
`tabtty_read_history`, `tabtty_search_history`, `tabtty_wait`,
`tabtty_send_input`, and `tabtty_return_control`. GUI-only sharing and control
grant operations must reach the same authorization owner.

Use a substitute monotonic clock and controllable transport completion boundary
for deadlines and races. Feed real terminal-model bytes and inspect emitted
bytes with independently specified expectations; a production encoder decoding
its own output is insufficient. Script admission, partial submission, completion,
and failure independently. Later layers use real stdio, Windows IPC, WPF input,
and remote peers to cover behavior these substitutes cannot establish.

Maintain a test-only trace of session/connection, counterpart attachment, sharing,
grant, operation identity, reducer ordering, and byte counts. Synthetic fixtures
may record their known payloads; production diagnostics must not acquire a
terminal transcript or application-managed secrets for test convenience. An
independent remote recorder establishes received bytes where possible; submission
alone never proves remote execution or exactly-once command effects.

## Layer 1: application/session contract

| Case / decisions | Stimulus and required observable result |
| --- | --- |
| A01: empty start, Q3/Q9/Q31 | Attach two counterparts with identical display names. Neither lists or reads any tab. Share one tab with one selected attachment: only that counterpart sees it, initially read-only. Sharing several tabs never grants control to all of them. |
| A02: separate permissions, Q3/Q8/Q10 | Grant, return, grant again, take over, and end sharing. Return/takeover preserve permitted reads; end sharing denies future reads and sends while retaining the usable connection. Old grants cannot become valid again; an old return request cannot revoke a newer grant. |
| A03: operation surface, Q5/Q13/Q18 | Enumerate the exported operations and validate each initial-request/continuation `oneOf`, including legal cursor-only search and rejection of cursor plus a new query. Reject unknown fields; expose no connect/profile/macro/file-transfer/desktop/clipboard/vault operation. Secret canaries stay out of results/errors; adversarial terminal data cannot grant permissions or bypass send checks. |
| A04: screen observation, Q2/Q12/Q22 | Parse positioned text, wide/combining clusters, cursor, dimensions, supported attributes and modes. `tabtty_read_state` follows the live session independently of visibility or human views. A requested region cutting a wide cluster expands per row to whole clusters and reports actual coverage, without half-clusters or an invented rectangular crop. |
| A05: retained scope, Q1/Q11/Q12 | Share after output already exists; retrieve its retained normal buffer, including while `vim` owns the alternate screen. Sources are explicit, no disk recording is read, no alternate redraw archive is created, and no discarded rows reappear. |
| A06: bounded search, Q11/Q30 | Search literal punctuation, Japanese text, case variants, wrap/window boundaries, and partial prompts. Use bounded immutable windows and query-length overlap, validate positive/negative regions, context and progress revisions before publishing, and expose coverage/continuation. Reads never move human panes, selections, or search results. |
| A07: consistent ranges, Q30 | Capture page one, then append, edit, erase, reflow, and evict requested content before page two. Stable span anchors/revisions preserve an unchanged prefix even when the same long line receives a suffix. Changed/missing requested spans report their condition without positional substitution or a fabricated combined snapshot. |
| A08: input semantics, Q13/Q14 | Send text, mapped keys, multiline paste, and supported cell mouse operations. Verify the session's chosen codec/modes, one input owner, strict whole-unit preflight, bracket framing, ordering, local echo, and payload bounds against the existing input contract. |
| A09: stale observations, Q25 | Change connection/control generation, normal/alternate selection, dimensions, or reset layout between observation and a new send: no payload is sent, including change-and-change-back (ABA). Ordinary cell refresh alone remains admissible. Session-generation admission/completion ordinals require the next send's capture after prior completion, even without changed text. |
| A10: bounded wait, Q10/Q26 | Advance virtual time with and without qualifying changes. Return changed/no_change/elapsed or an explicit interruption according to the contract, never remote success/failure. A long model think interval neither expires an otherwise live grant nor counts as counterpart loss. |
| A11: clean grant, Q29 | Attempt Grant control during queued human sends, paced paste, held remote mouse buttons, and unresolved IME composition, including a frozen broadcast operation sourced from this tab to other recipients. Reject immediately with a reason; completion does not silently install a queued grant. A later explicit grant succeeds. |
| A12: human takeover, Q4/Q23 | Revoke during text/paste/mouse input. No later AI payload passes the cutoff. Stop the unsent tail, perform only valid required cleanup, expose Taking control while pending, and admit human input afterward. No triggering key, Ctrl+C, Esc, or cancelled payload is injected. |
| A13: rejection versus uncertainty, Q24 | Reject before submission for invalid bounds/encoding/stale state: report the cause and retain control. Fail after possible submission: report partial/unknown delivery and end control. Ordinary terminal error text does not revoke control by itself. |
| A14: disconnect/restart, Q15/Q34 | Disconnect, reconnect the retained tab, lose/recover a bridge, and restart the app. A disconnected shared tab may remain readable; a new connection or attachment needs fresh permissions and cannot accept the old input, grants, or history identifiers. |
| A15: broadcast transitions, Q16/Q19/Q20/Q28 | Active checked targets, including unready ones, reject sharing. Starting sharing clears inactive remembered checks. Start, Select All, scope reset, manual checks, and cross-window selection leave shared tabs OFF/unavailable with a reason; ending sharing does not restore a former check. |
| A16: broadcast source, Q16/Q19/Q28 | Use an unchecked read-shared tab as a human broadcast source and separately as a would-be target. Resolve input to checked eligible recipients, never implicitly to the source; sharing is not permission to join the target set. An AI-controlled source cannot emit human input before takeover. |
| A17: cross-feature races, Q16/Q22/Q28 | Race sharing/grant with broadcast Start, frozen paste/gesture selection, and scope-crossing tab moves. One ordered transition wins; no frozen recipient is silently removed/added and unrelated modes remain usable. Moving/splitting never duplicates a session delivery. |
| A18: future macro owner, Q16/Q29 | At the existing input-owner boundary represent an executing or paused macro owner. AI control is rejected in both states; permitted read-only sharing still works. Do not implement a macro runtime to perform this ownership check. |
| A19: help and secrets, Q18/Q27 | Return control with an inert reason for a synthetic password/OTP prompt; later work requires a fresh grant. No TabTty question dialog or foreground activation occurs. A remote echo can contain the synthetic secret: do not misreport complete redaction or universal prompt detection. |

A07 also covers 100, 5000, and 100000 retained rows, a single long wrapped logical
line, edits spanning page boundaries, no-match search exhaustion, and continuations
after eviction. Test exact limit, one below, and one above for every contract
bound. More pages must not pin the entire buffer or extend the captured quota.
Cancelled/expired/revoked reads release their reserved snapshot memory.
Screen continuation pages must retain one immutable capture: feed redraws between
pages, then test fixed 60-second expiry, two-snapshot counterpart admission, the
16 MiB snapshot and 64 MiB application caps, and explicit crop fallback when a
maximum grid cannot fit. An observation must never claim a cropped grid is full.
For history, test the 200-row default/1000-row maximum, 64 KiB default/256 KiB
maximum UTF-8 text, and cluster-boundary continuation without a fabricated LF.
Assert the 1 MiB serialized tool-result bound including the human-readable mirror.
Wait cases cover the 1000 ms default, 10000 ms maximum, and invalid deadlines;
the monotonic clock controls these limits independently of inference duration.
For A06, insert a match into a previously negative scanned region and edit/remove
matched text or surrounding context before result commit. The published page and
progress must reflect validated coverage or report change; a stale negative result
cannot suppress later work. Put a multi-cluster query across each scan boundary
and retain enough overlap to find it exactly once. End-of-scan identifies covered
ranges, not proof that all current retained history contains no match.
For A09, pause the first send after admission, capture a screen while it remains
in flight, then complete it and try the second send using that capture. Reject
until a post-completion capture exists. Also try a capture from an earlier grant
whose action sequence happens to match; it cannot satisfy the new control interval.

## Layer 2: wire protocol, bridge, and Windows access

Drive the packaged bridge with an independent protocol client before introducing
an LLM. Exercise every declared protocol revision and tool schema in the technical
contract; reject unsupported revisions explicitly. Test ordinary and adversarial
requests through the same IPC authorization path used by a real host.
Cover legacy `2025-11-25` initialization and modern `2026-07-28` per-request
metadata independently, including mixed-era requests, missing/invalid metadata,
discovery, error envelopes, and cancellation's response rules. An offered
legacy fallback revision is distinct from accepting an unsupported wire mode.
Check modern cache-result fields, including required `ttlMs`/`cacheScope` where
applicable, without leaking those semantics into legacy envelopes or caching
authorized terminal data across counterpart/sharing boundaries.

| Case / decisions | Required coverage and evidence |
| --- | --- |
| W01: stdio framing, Q6/Q32/Q35 | Split/concatenate UTF-8 JSON messages at every relevant boundary; malformed/unknown input, the 2 MiB frame bound, EOF mid-message, cancellation, parallel reads, and legal notifications. Stdout contains protocol messages only; bounded stderr errors contain no terminal/secret payload. |
| W02: counterpart binding, Q7/Q9/Q31 | Spoof self-reported names and use another attachment's handles. Unshared/unknown sessions return `not_shared` without leaking contents; an invalid cursor within an authorized session returns `cursor_expired`. Same-counterpart conversations can share grants; names and ordinary MCP chats are not authenticated isolation boundaries. |
| W03: local IPC, Q7/Q31/Q32 | Use real processes under different users, same-user different logons, different elevations, and non-elevated low/medium integrity levels. Verify both bridge attachment and file activation reject an integrity mismatch before data exchange. Test remote pipe rejection, endpoint squatting, stale endpoints, PID reuse, executable mismatch, and duplicate attachments. Record actual access decisions; a string comparison or mocked ACL is insufficient. |
| W04: duplicate sends, Q23/Q24/Q35 | Exercise strict per-grant sequence, one in-flight operation, 256 outcomes and high-water mark. Fix the argument digest at admission; after basic authority checks, consult the ledger before new-sequence stale/encoding preflight. Repeat in-flight, lost-response and expired-observation requests, cache-evicted sequences, and changed arguments: no replay or false `not_sent`. |
| W05: counterpart loss, Q15/Q26/Q32 | Close stdin/stdout, kill host/bridge with streams inherited by a surviving process, break IPC, suspend/resume, and trigger heartbeat/write deadlines. Confirm loss revokes sharing/control at its cutoff. Slow inference and completed individual requests do not revoke a healthy attachment. |
| W06: read revocation, Q8/Q15 | End sharing while reads/waits are queued, being captured, serialized, or awaiting transport. No result is newly committed to the bridge channel after GUI revocation. The bridge drops responses it can still cancel; already committed delivery may arrive later, and OS/host-buffered bytes cannot be recalled. |
| W07: unavailable app, Q33/Q34 | Start bridge with no TabTty, during exit, and after an app crash. Observe a bounded unavailable outcome and no GUI, hidden app, new connection, or app keepalive. A host retry does not recreate permissions. |
| W08: setup/deployment, Q32/Q33 | Manually register the supplied configuration from paths containing spaces and non-ASCII text on a clean supported Windows PC. No automatic third-party config edit, dependency/model download, installer, or external AI app launch is performed by TabTty. |

For W04-W06, place barriers before authorization, after admission, before first
transport write, between partial writes, before result admission, and after the
response is lost. Permute takeover/end sharing, resize, reconnect, duplicate
request, and late completion across those points. The core acceptance predicate
is **zero unapproved payload submissions and zero automatic payload replays**.
Required framing/gesture cleanup is trace-labelled separately and cannot become
new requested input. A lost completion remains unknown where it cannot be proved.
Exercise the 5-second attach deadline, 5-second heartbeat and 20-second health/
blocked-write limits with a controlled clock, then confirm real process/pipe
detection. Fuzz bounded JSON depth/duplicate properties/UTF-8 and both frame
length formats. Saturate ordinary requests, attachments, and owned buffers while
verifying that revocation, cancellation, and health capacity remains available.
Use a controlled clock to exhaust and refill ordinary-call limits of 20/s with
burst 40 per counterpart and 100/s with burst 200 application-wide, plus new-send
limits of 5/s with burst 10 per tab. Verify exact-boundary admission, bounded
over-limit rejection, and fair recovery across peers/tabs. Return control, cancel,
human revocation and private health retain independent reserved capacity under
ordinary rate and concurrency saturation. With a controlled clock, check that
repeat acknowledgements and unknown sessions consume the counterpart's return
allowance. Check rejection after eight requests and recovery after 250 ms.
Other counterparts and local takeover remain available throughout.

## Layer 3: native Windows UI and input

Use actual WPF event routing, Windows IME, clipboard, pointer capture, UIA, and
supported device/transport drivers. Session-interface substitutes do not satisfy
this layer. Record the Windows build, IME, DPI, keyboard layout, display setup,
and input route for each run. Synthetic UI events alone are not IME/capture proof.

| Case / decisions | Required observation |
| --- | --- |
| N01: indication, Q17/Q21 | In English and Japanese, test distinct shape-based sharing/control tab icons, tooltip/UIA names, unchanged profile label, and selected-tab details. A disconnected retained shared tab displays both connection and AI state. Light/Dark/system preserve distinguishability. |
| N02: accessible takeover, Q4/Q17/Q23 | Keyboard-only menus, status-bar takeover, and Narrator reach the same action, including an inactive/minimized window restored by the person. It revokes locally without an AI reply; no ordinary terminal key is consumed as an implicit grant or forwarded takeover key. |
| N03: IME, Q4/Q29 | Compose Japanese text, attempt a grant, cancel/commit, then grant explicitly. Cross-pane/tab focus changes cancel uncommitted text; commitment is once-only with no extra Enter. During AI control, local selection/copy/scroll work while terminal text/input remains blocked. |
| N04: paste, Q14/Q23/Q29, whole-codebase Q26-Q27 | Otherwise valid human text within its captured configured source maximum (default 512 MiB) shows one confirmation for CR/LF or UTF-16 source size >1,024 bytes. Without CR/LF at <=1,024 bytes, neither size nor equality with the maximum forces confirmation. Refuse invalid/over-limit text without prefix transmission. AI-supplied paste retains its 256 KiB UTF-8 cap and exemption from the clipboard, human source setting and confirmation. Takeover preserves the existing revocation cutoff; active paste cancellation uses complete scalar/required indivisible codec-control units, even within short/long graphemes, without splitting CRLF. Close only a started valid frame and report remaining cleanup. |
| N05: mouse, Q13/Q23/Q29 | Verify supported tracking/coordinate formats, multiple buttons, mode changes, capture failure/loss, and takeover during a gesture. No desktop pointer movement comes from an AI action; matching releases do not target a new connection/mode. |
| N06: view movement, Q22/Q25/Q28 | Move shared tabs across windows, split panes, resize, switch tabs, minimize, and detach the final tab. Identity/grants survive allowed moves; grid changes invalidate old observations, inner splits alone do not resize the grid, and output never steals focus. |
| N07: real transports, Q13/Q15/Q24 | Cover SSH/Telnet plus a controlled serial loopback/peer with the actual Windows driver. Disconnect/unplug/stall during sends and cleanup; inspect peer bytes and retained state. Driver completion is not automatically remote command execution. |

## Layer 4: Codex and full-screen peers

On 2026-11-19 the user selected Codex as the host for real-assistant acceptance.
The product exposes its existing host-independent MCP interface. Start the packaged
GUI and configure Codex to launch the sibling `TabTty.McpBridge.exe --stdio` in
the same Windows logon with matching elevation. Use a disposable terminal session
connected to a peer that records its input and emits unique test markers.
[Bridge setup](ai-collaboration-transport.md#deployment-and-setup)

Use a Codex thread that can request approval for the test MCP calls. In the
November 19 App Server trial, approval policy `never` blocked even the list
call. With `on-request`, the trial client approved only the named read tools
on the test connection, and the model listed and read the shared screen.

1. In a real Codex conversation, call `tabtty_list_shared_sessions`. Match the
   returned pairing code in TabTty and share only the test tab with that counterpart.
   Verify that the test tab first appears read-only and an unshared tab stays hidden.
2. Have the assistant call `tabtty_read_state` and `tabtty_search_history`
   or `tabtty_read_history`. Compare the screen and pre-sharing history markers
   it reports with the peer's output. The peer should have received zero input.
3. Grant control in TabTty, read a fresh observation and use `tabtty_send_input`
   to send a harmless marker. Compare the peer's recorded bytes and count with
   one expected delivery. Read the resulting terminal state in the conversation.
4. Call `tabtty_return_control`. Check that reading continues, input using the
   old grant is rejected and the peer receives no more bytes. End sharing in
   TabTty and verify that the tab disappears and its former read handle fails.

Capture the tool calls that Codex itself made and the independent peer observations.
A bridge subprocess regression records protocol evidence separately. Use the same
Codex connection for the following application tasks.

| Scenario | Independent result to inspect |
| --- | --- |
| Read-only investigation | Explain an earlier synthetic error from retained history, page/search into older output, and observe current output. Unshared tabs remain inaccessible and zero terminal payload is sent. |
| Ordinary editing task | After explicit control, modify one disposable remote fixture, save, verify its expected digest through the test peer, and return control. No per-change TabTty confirmation is introduced; there are no sends outside the current grant. |
| `top` | Use a pinned peer/version with deterministic workload. Observe refreshed rows, cursor/modes and dimensions, make a supported key operation, resize and re-observe, then return control without needing an image-capable model. Refresh alone must not make all input stale. |
| `vim` | Open a disposable UTF-8/Japanese fixture, navigate, insert multiline text, save, and verify exact file contents independently. Exercise supported mouse input separately, read parked normal history without exiting `vim`, and take over during a paced insert. |
| Prompt and help | Use a synthetic prompt. Returning control preserves reading. End sharing and confirm that reads fail. Display an ordinary marker while unshared, confirm the history disclosure on re-sharing, and verify that a retained marker is readable. A new connection requires fresh authorization. The host may retain content it already received. |
| Interrupted/uncertain action | Lose the bridge/transport or response during a non-idempotent fixture action. Count peer effects and inspect grant state; no retry automatically reproduces the action. An unknown result must not be called verified success. |

Pin the remote OS, shell, terminfo, `TERM`, `top`/`vim` versions and settings.
Use peer-side results to evaluate tool-contract correctness and record Codex task
outcomes, including failed attempts. Report terminal compatibility limits against
the [supported terminal contract](terminal-and-encodings.md). Exercise offline
startup of the bundled bridge in the independent protocol tests. The Codex
conversation uses its configured provider and the connectivity it requires.

## Resource, responsiveness, and delivery gates

Reuse the recorded Windows reference machine and 20-session workload in
[resources and validation](resources-and-validation.md), with sharing on all
20 tabs, two simultaneous counterpart attachments, bounded reads/search/waits,
and one paced AI send. Include 100000-row single-session paging, maximum grid,
slow readers, full budgets, and a stalled peer. Show that AI reads do not extend
retention, duplicate receive/log state, block terminal updates, or starve UI work.

Schedule at least 600 deterministic local interactions in each 30-minute loaded
run. Initial targets are p95 <=100 ms for local UI/input acceptance and p95
<=250 ms for takeover acceptance under overload, measured from original input
timestamp including dispatcher delay. Record max/tails and cleanup duration
separately: accepted takeover immediately revokes further input but does not
promise remote effects or stalled transport cleanup within 250 ms. These are
engineering targets, not benchmark results. Model inference latency is reported
separately and cannot conceal slow local revocation. Confirm every API/queue/
snapshot limit from the technical contract at and beyond its boundary.

Record case IDs, pass/fail/not-run status, TabTty package hash and source revision,
bridge/IPC/API/protocol revisions, Windows and hardware, Codex version or build,
model identifier when available, test prompt, remote fixture digest/configuration
and peer results. Record latency, resource peaks and network conditions where
relevant. Use synthetic data for screenshots and tool traces.

Design readiness requires traceability for Q1–Q35, fixed initial bounds and resolved
product decisions. Implementation requires separate authorization.
Delivery requires passing layers 1–3 and the resource gates, plus assistant-issued
tool calls and independently observed results through Codex in layer 4. Record
the two-counterpart authorization and workload cases using independent bridge
attachments. Report outcomes of the full-screen tasks individually.

## Retained-history architecture follow-up

Architecture Q12-Q19 selects the
[complete observation/publication interface](ai-history-observation.md) as the
test surface. These are future refactor checks, not executed results. Feed the
real terminal model and control publication, time, and scheduling through adapters.
Preserve A05-A07, W06, the resource workload, and independent private-channel,
Windows, and host checks. Replace superseded history-algorithm tests with these
interface checks rather than treating preparation-only assertions as publication
coverage. Screen/wait/input observation behavior remains a regression requirement.

| Case | Required observable result |
| --- | --- |
| H01: ordinary history and search | Both validate content and authority through actual publication, including an edit between preparation and admission. |
| H02: intervening edit | Editing and restoring the same text still rejects the old range; unchanged-prefix append and pure reflow remain valid. |
| H03: complete proof coverage | Changes in negative scan regions, overlap, context, or the required remaining range cannot be hidden by unchanged matches. |
| H04: unpublished successors | Validation failure, cancellation, expiry, or queue rejection leaves no live successor from that attempt and causes no eviction solely for its staged IDs. Source expiry, revocation, and independent quota retirement still apply. |
| H05: checkpoint reuse | Source handles remain immutable with unchanged fixed expiry; successors receive their own fixed lifetime only at successful admission; each reuse revalidates range and authority. |
| H06: result sizing | At and beyond the 1 MiB combined result limit, including escaping and the mirror, repaginate before admission with matching coverage and continuation; do not skip withheld matches. |
| H07: minimum retained content | Maximum retained spans of 256 scalars/1024 UTF-8 bytes fit implementation capacity; an explicit text limit smaller than the next stored span yields resource_limit without progress or an empty-success loop. Long incoming graphemes retain their prefix without session failure; exports preserve authoritative spans rather than re-segmenting concatenated prefixes. |
| H08: bounded verification | Large history and long lines do not cause full-row traversal/index rebuild under the final publication gate; contention has bounded attempts and may return busy. |
| H09: ownership and compatibility | Account staged, copied, queued, and cancelled-but-still-owned data until actual release; preserve retained read access after ordinary disconnect and invalidate it under reconnect/sharing-loss rules. Screen captures, waits, and input watermarks retain their contracts. |
| H10: common rules, distinct operations | AI paste preserves admitted original text, including a grapheme beyond 256 scalars, while local display/history use stored prefixes. Preserve the 256 KiB UTF-8 AI request cap and no-human-confirmation path. Count the shared original once in the 4 GiB application envelope; target work shares the existing 2 MiB/256-unit allowance with ordinary input. Competing admission cannot consume reserved sending/Stop/closure capacity; request-buffer limits and authority checks remain unchanged. |
