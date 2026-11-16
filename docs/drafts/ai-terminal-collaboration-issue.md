# Specify human and AI terminal collaboration

Status: design interview opened on 2026-11-14 using `grill-with-docs`,
`grilling`, and `domain-modeling`; Q1-Q35 accepted on 2026-11-14, with the
user's broadcast-exclusion amendment to Q16 and tab-icon amendment to Q17.
All four rounds are answered. The user confirmed the shared UI/AI testing boundary
and complementary integration tests, and requested publication through `to-spec`.
The synthesized specification is published as
[GitHub issue #2](https://github.com/TabTty/TabTty/issues/2) with
`ready-for-agent`; this document retains the interview record. Specification
and publication only: no implementation, prototype, dependency adoption, or initial-release
scope change is authorized. See the [research note](ai-terminal-collaboration-research.md).

## Confirmed direction

- Let the person and an AI work with the same terminal session and its output.
- Start with conversation in an external AI application. Preserve support for
  closed environments and local models through an open, interoperable mechanism.
  MCP is accepted as the integration boundary in Q6. Require the controlling
  AI application to run on the same PC in Q7; local and closed-network inference
  remain supported. These choices do not select an SDK or protocol revision.
- Aim to support full-screen applications such as `top` and `vim` where practical.
  Actual model/terminal interoperability and task reliability remain unverified.
- Permit autonomous work within the requested task, including configuration
  editing, saving, and service restarts when needed. Do not assume per-change
  confirmation is required.
- On interruption, return write control to the person and continue AI output
  sharing. This separation is accepted on 2026-11-14.
- Allow currently retained text from before sharing began, without a separate
  unlimited AI archive or reading disk recordings (Q1).
- Use positioned text, cursor, dimensions, and relevant visual attributes for
  current-screen observation, independently of the person's scroll position;
  image-capable models are optional (Q2).
- Start with no shared tabs. The person explicitly starts sharing a tab with
  a selected AI application in read-only mode and grants control separately (Q3).
- Keep human viewing, local scrolling, selection, and copy available during AI
  control. Human terminal input requires explicit takeover, without automatically
  forwarding a key that triggered it (Q4).
- Expose observation and terminal operations for explicitly shared existing
  sessions. Defer AI APIs for connection creation, profile editing, macro launch,
  dedicated file-transfer UI, and local desktop control (Q5).
- Provide a separate End AI sharing action, revoking future output access and
  AI control while leaving the terminal connection usable. It cannot retract
  information already delivered to the external application (Q8).
- Share each tab with one selected AI counterpart. One counterpart may receive
  multiple separately shared tabs and separately granted controls (Q9).
- AI returns control on task completion, retaining output sharing; subsequent
  work after return requires a new human grant. Thinking/waiting alone does not
  expire the grant (Q10).
- Provide bounded history/range retrieval and literal search without moving the
  person's views. Expose still-retained normal-screen history separately from
  the active alternate screen, even while the latter is in use (Q11-Q12).
- Support terminal text/keys, multiline paste, and supported remote mouse
  operations. AI-supplied multiline input does not require a per-send clipboard
  confirmation; human clipboard rules remain unchanged (Q13-Q14).
- Disconnect ends control but can preserve reading of a retained tab. New
  connections, recovered/restarted AI counterparts, and application restarts
  require fresh permissions; confirmed counterpart loss ends its sharing (Q15).
- Broadcast-target tabs cannot use AI collaboration, and tabs in AI collaboration
  are excluded when broadcast starts (Q16, user amendment). Collaboration includes
  read-only sharing (Q19). Checked targets in an active mode cannot start AI
  sharing; inactive remembered checks do not prevent it (Q20). The earlier
  blanket allowance for read-only AI/broadcast coexistence is superseded.
- Macro and AI input remain exclusive for one session. The broadcast-specific
  amendment does not withdraw read-only AI observation of a macro session.
  Accepted macro Q29 retains input ownership while paused as well.
- Show AI states with icons in the terminal tab, analogous to the disconnected
  indicator, rather than status text appended to the tab label. Keep selected-tab
  status-bar information and accessible sharing/control actions (Q17, amended).
- Hand password/OTP input to the person, with a fresh control grant afterward.
  Do not expose vault retrieval or saved-secret submission tools to AI (Q18).
- Use distinct shape-based read-sharing/control icons, with tooltip/accessibility
  text, alongside rather than instead of connection state. Background/minimized
  operation, tab movement, and splitting preserve sharing/control (Q21-Q22).
- Revoke further AI input immediately on takeover, stop unsent payload, finish
  required input cleanup, and only then admit human input. Show Taking control
  while necessary; do not inject Ctrl+C/Esc or replay input (Q23).
- Pre-send rejections can be corrected by the AI. Partial/uncertain send failures
  end control and return to the person without automatic replay (Q24).
- Reject actions based on an obsolete connection, normal/alternate selection,
  or terminal dimensions; ordinary text refreshes alone do not reject them (Q25).
- Provide bounded change/timed waits; no change is neither command success nor
  failure. The person can interrupt waits (Q26).
- Use external AI conversation for help requests, with a short inert reason in
  TabTty status/details. Do not add AI question dialogs or foreground activation
  (Q27).
- Keep shared tabs' broadcast checks OFF and disabled across all selection and
  move paths; do not restore an earlier check when sharing ends (Q28).
- Admit a human grant only at a clean input boundary, including IME, queued
  human input, paste, and pressed-button/broadcast-source gestures (Q29).
- Return coherent bounded history pages with explicit changed/evicted ranges;
  do not pin an unlimited transcript across calls (Q30).
- Authorize an identified integration counterpart, which may serve several
  conversations, without claiming that MCP provides per-chat isolation (Q31).
- Use a bundled stdio bridge launched by the host and attached to the running
  GUI, with manual host configuration and no automatic download, application
  launch, or permission restoration (Q32-Q34).
- Delegate routine technical choices within these decisions and the repository
  policies, then review the complete contract and future validation plan (Q35).

The accepted architectural boundary is recorded in
[ADR 0005](../adr/0005-expose-existing-terminal-sessions-over-local-mcp.md).

## Existing boundaries

- Use the glossary's terminal session, terminal tab, terminal pane, retained
  terminal buffer, AI output sharing, and AI control terminology.
- A tab represents one session; same-tab panes share its output. Their browsing
  positions are independent. [ADR 0002](../adr/0002-two-level-splits-with-one-session-per-tab.md).
- The normal-screen retained terminal buffer contains active rows and scrollback,
  subject to its existing captured quota, edits, erasure, and retirement rules.
  It is not a recording of every earlier screen or received byte. Alternate-screen
  active content is distinct from normal-screen scrollback.
  [Terminal contract](../specification/terminal-and-encodings.md),
  [interaction requirements](terminal-interaction-issue.md).
- Existing connection generations, input ordering, encoding, and paste framing
  remain applicable. Human clipboard confirmation is not silently redefined.
  [Session contract](../specification/session-and-recording.md).
- This feature does not expand the separate macro interview's full-screen scope
  or introduce a macro runtime. [Future macro constraints](future-macro-design.md).

## Design tree and current frontier

Question numbering is local to this interview, starting at Q1 independently
of other concurrent feature interviews. Q35 delegates the remaining technical
choices; it does not authorize implementation. No product question remains open.
The published specification consolidates the following concrete documents.

| Branch | Accepted product decisions | Delegated contract / review evidence |
| --- | --- | --- |
| Retained history | Q1/Q11/Q12/Q30/Q35 | Main contract: range anchors, bounded pages/search, revisions and resource budgets |
| Screen observation | Q2/Q12/Q25/Q26/Q35 | Main contract: positioned clusters, modes, layout epochs, region snapshots and waits |
| Sharing and delegation | Q3/Q9/Q10/Q22/Q29/Q31 | Main/transport contracts: counterpart binding, grant admission and lifetime |
| Human interaction | Q4/Q13/Q14/Q23/Q24/Q29 | Main contract: input actions, cleanup, duplicate suppression and cutoffs |
| Cross-feature exclusion | Q16/Q19/Q20/Q28 | Main contract: broadcast transitions, frozen operations and macro ownership |
| Indication and assistance | Q17/Q21/Q27/Q35 | Main contract: icon order, localized/accessibility labels and inert reasons |
| Exposed operation scope | Q5/Q18/Q27 | Main contract: seven bounded tools and external-host responsibility |
| Interoperability | Q6/Q32/Q33/Q35 | Transport/validation: two MCP revisions, BCL adapter, independent hosts and offline evidence |
| Deployment boundary | Q7/Q31/Q32/Q33/Q34 | Transport: same-logon pipe, live process binding, manual setup and absent-GUI behavior |
| Sharing lifetime | Q8/Q10/Q15/Q23/Q34 | Main/transport: revocation cutoffs, counterpart loss and fresh authorization |

- [Terminal/application technical contract](../specification/ai-collaboration.md).
- [MCP transport and local counterpart binding](../specification/ai-collaboration-transport.md).
- [Validation plan and Q1-Q35 traceability](../specification/ai-collaboration-validation.md).

The specification and testing boundary are confirmed for publication. All runtime, full-screen,
host/model, Windows, and offline checks remain planned, not performed.

## Round 1: accepted decisions

The user accepted all eight recommendations on 2026-11-14. The rows below are
binding product decisions; they do not imply acceptance of downstream proposals.

| Question | Decision | Accepted outcome |
| --- | --- | --- |
| Q1 | Past-text scope | Allow access to the shared tab's currently retained text, including content received before sharing began. Do not add a separate unlimited AI archive or read disk recordings. Present discarded/unavailable history explicitly rather than inventing completeness. |
| Q2 | Full-screen observation | Use positioned text, cursor, dimensions, and relevant visual attributes as the primary current-screen representation, keeping image-capable models optional. Read the live session screen independently of the person's scroll position. |
| Q3 | Starting sharing and control | Begin with no shared tabs. The person explicitly starts sharing a tab with a selected external AI application in read-only mode, then separately grants AI control. Do not share every open tab merely because an AI application connects. |
| Q4 | Human input during AI control | Keep viewing, local scrolling, selection, and copy available. Require explicit takeover before human terminal text, keys, paste, or remote mouse input; do not implicitly stop AI and forward the triggering key. |
| Q5 | TabTty-provided operation surface | Limit this feature to observing and operating explicitly shared existing terminal sessions. Defer AI APIs for creating connections, editing profiles, invoking macros, dedicated file-transfer UI, or controlling the local desktop. Requested remote changes through terminal commands remain supported. |
| Q6 | Open integration protocol | Adopt MCP as the public external-AI integration boundary. Keep the AI host/model selectable; do not adopt a particular SDK, protocol version, or inference runtime through this decision. |
| Q7 | Location of the controlling AI application | Initially require the external AI application/control client to run on the same PC as TabTty. Its model may run locally or on an allowed closed-network server; direct terminal control from a client on another PC is deferred. |
| Q8 | Ending output sharing | Provide a separate End AI sharing operation that revokes both future output access and any remaining AI control, leaving the terminal connection usable. Distinguish this from interruption with continued sharing. It cannot retract information already delivered to the external AI. |

## Round 2: accepted decisions

The user accepted Q9-Q18 on 2026-11-14, with explicit amendments to Q16 and Q17.
Wire formats and caller identification still require a technical contract;
a client's display name does not establish its identity.

| Question | Decision | Accepted outcome |
| --- | --- | --- |
| Q9 | Sharing participants | Share each tab with one selected AI counterpart at a time, for both reading and control. The same counterpart may receive multiple separately shared tabs; each tab needs its own control grant. Switching the counterpart ends the old sharing first. Defer simultaneous multi-AI sharing of one tab. |
| Q10 | Control lifetime and normal completion | Do not expire control merely because the AI is thinking or waiting. End it on human takeover, sharing end, explicit AI return, or the failure boundaries in Q15. Have the AI return control when its requested task is finished; returning control keeps output sharing. A new task after return requires a fresh human grant. TabTty cannot infer external conversation completion; a non-returning agent can still be interrupted by the person. |
| Q11 | History retrieval and search | Offer bounded range/recent-text retrieval, literal search, and surrounding context. Results may be paged and must identify unavailable/discarded portions. These operations do not move human panes or selections. Defer regex search in the AI interface initially. |
| Q12 | History while an alternate-screen application is active | Let the AI explicitly read the still-retained normal-screen buffer while separately observing the active alternate screen. Do not exit or disturb vim/top to retrieve it; label the two sources distinctly. Do not create history of alternate-screen redraws or resurrect discarded text. This extends access to the parked buffer, not its retention or the existing human search UI. |
| Q13 | Terminal action kinds | Expose text, terminal keys, multiline paste, and supported remote mouse actions within the granted session. Resolve keys/mouse modes through the session. Do not move the Windows pointer, click application UI, or expose the system clipboard. |
| Q14 | Autonomous multiline submission | Accept AI-supplied multiline text under the current control grant without displaying a per-send clipboard confirmation. Preserve encoding validation, payload bounds, paste framing, ordering, and interruption. The accepted human clipboard confirmation remains unchanged. |
| Q15 | Failures, reconnect, and restart | Remote disconnection ends AI control; read sharing may continue for output in a retained disconnected tab. A new connection in that tab requires fresh sharing and control. Confirmed loss of the authorized AI counterpart ends its sharing/control; a recovered/restarted counterpart needs fresh authorization. Application exit drops all grants. Exact loss detection must distinguish real counterpart failure from an ordinary completed HTTP request or a slow inference. |
| Q16 | Other input owners | Accepted with amendment: broadcast-target tabs cannot use AI collaboration; AI-collaborating tabs are excluded when broadcast starts. Q19 includes read-only sharing; Q20 excludes inactive remembered checks from the AI-start prohibition. This supersedes the earlier general read-only AI/broadcast coexistence allowance. AI and macro input remain exclusive; unrelated sessions and read-only observation of macro sessions remain available. |
| Q17 | UI indication and access | Accepted with amendment: use tab icons rather than tab status strings, analogous to the disconnected icon. Distinguish no sharing, reading only, and AI control; retain selected-tab status information and counterpart details. Keep AI-control indication while it thinks/waits. Provide tab-menu sharing/control actions with keyboard access and a status-bar takeover action. |
| Q18 | Passwords and interactive secrets | Initially hand control back to the person for terminal password/OTP prompts, then require an explicit grant to continue. Do not expose vault retrieval or a dedicated saved-secret submission tool to AI in this scope, and never return application-managed secret values through MCP. Do not promise to detect every prompt or redact arbitrary remote/local echo; ending sharing remains available. Existing connection authentication and vault behavior are unchanged. |

## Evidence and cross-feature constraints for round 2

- MCP 2026-07-28 removes protocol-level sessions and carries client information
  on requests. The specification explicitly describes client information as
  self-reported and unverified. A display name cannot establish the identity to
  which TabTty grants access. Choose and validate a separate counterpart binding
  in the technical design. [Changelog](https://modelcontextprotocol.io/specification/2026-07-28/changelog),
  [base protocol](https://modelcontextprotocol.io/specification/2026-07-28/basic/index).
- stdio subprocess lifetime and HTTP request lifetime are different. Localhost
  binding alone does not specify which AI counterpart may access a shared tab.
  Do not promise authenticated peer identity from a product name or an open
  transport. [stdio](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio),
  [HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http).
- Broadcast selection is distinct from eligibility. First Start, accepted scope
  resets, and committed cross-window moves normally select tabs. Accepted Q28
  now overrides these selection paths for AI-shared tabs and specifies stored
  check behavior. Preserve unrelated modes.
  [Broadcast contract](../specification/broadcast-and-split-views.md).
- Macro Q14 excludes manual/broadcast input during execution; macro Q19 accepts
  resumable pause. Subsequent accepted macro Q29-Q32 retain input ownership while
  paused and finish an already-started send before entering Paused. AI sharing
  must not bypass this ownership or add macro attachment to an AI session.
  [Macro interview](macro-issue.md).
- Existing authentication policy keeps application-managed secrets out of
  ordinary output, diagnostics, and exports, while terminal output may itself
  contain secrets. Q18 scopes future AI operations; it does not modify vault
  storage, unlocking, SSH authentication, or session-log redaction rules.
  [Authentication requirements](authentication-issue.md).

## Round 3: accepted decisions

The user accepted every Q19-Q27 recommendation on 2026-11-14. Q28 now addresses
the target-selection transitions enabled by Q19-Q20.

| Question | Decision | Accepted outcome |
| --- | --- | --- |
| Q19 | Meaning of AI collaboration for broadcast exclusion | Include the entire output-sharing lifetime, both read-only sharing and AI control. Human takeover that preserves sharing therefore does not make a tab eligible for broadcast; End AI sharing does. |
| Q20 | Meaning of a broadcast target for starting AI collaboration | Apply the prohibition to checked targets of an active broadcast mode, including checked unready tabs. Remembered checks while broadcasting is stopped do not prevent starting AI sharing. Q16 still governs exclusion when broadcast next starts; this answer does not select how stored checks are then updated or displayed. |
| Q21 | Icon semantics and coexistence | Use distinct tab icons for read-only sharing and AI control, with no AI indicator when unshared. Preserve the profile-name label. Distinguish states by shape, not color alone; provide localized tooltip/accessibility text. Show AI state alongside connection/disconnection state rather than replacing it. Keep detailed status text and the counterpart in the selected-tab status bar. |
| Q22 | Background operation and view movement | Keep sharing and control when the tab is inactive, its window is minimized, or the tab moves/splits. Keep the same session target and do not steal focus. Application exit still revokes permissions under Q15. Resize/relevant screen changes require fresh observations under accepted Q25, not implicit reconnection. |
| Q23 | Interruption during an in-flight operation | Revoke permission for further AI operations locally without waiting for the AI. Stop unsent payload; complete only required safe character/codec/paste or mouse-release cleanup under the existing session contract. If cleanup is pending, show Taking control and admit human terminal input only after it finishes or the connection fails. Do not send Ctrl+C/Esc, exit the remote application, disconnect solely for takeover, undo changes, or replay cancelled input. |
| Q24 | Failed input and uncertain delivery | A rejection known to occur before any input was sent is reported to the AI so it can correct the request while retaining control. A send failure with possible partial/uncertain delivery ends AI control, reports the uncertainty, and returns to the person without automatic replay. Remote command errors displayed as terminal text are not transport-send failures. Sharing continues where the existing Q15 lifetime allows. |
| Q25 | Observation-to-action boundary | Support short action-and-observation cycles. If the connection, normal/alternate screen selection, or terminal dimensions no longer match the observation used by an action, reject that action before sending and require a fresh observation. Do not reject merely because top refreshed text. This cannot guarantee knowledge of an application's internal mode or atomicity against subsequent remote output. |
| Q26 | Waiting for screen changes | Offer bounded waits for a screen change and bounded timed waits. A wait that reaches its limit reports no qualifying change rather than command failure or success. The AI may observe again; waits do not keep unbounded history or prevent takeover. Numeric limits and change-filter semantics remain technical-contract work. |
| Q27 | Asking the person for help | Use the external AI conversation for explanations/questions, and have the AI return control when terminal input by the person is needed. TabTty can show a short inert handoff reason in status/details, but does not add an AI chat/form, modal question, or automatic foreground activation. Human help does not grant access to the clipboard or secret storage. |

### Round 3 cross-checks

- Stopped broadcast modes keep hidden remembered checks. First Start and scope
  resets normally check all existing tabs; committed cross-window moves check the
  moved tab. Paste and pressed-button mouse operations freeze recipients. These
  facts constrain the Q28 selection policy following accepted Q19-Q20.
- The disconnected indicator is a broken-link icon before the connection name,
  with tooltip and accessibility text. AI read sharing can coexist with a retained
  disconnected tab under Q15, so its indication cannot replace connection state.
  See [persistence/windows](../specification/persistence-and-windows.md) and the
  [broadcast contract](../specification/broadcast-and-split-views.md).
- Macro pause ownership is now accepted, not an unsettled prerequisite. The AI
  interface cannot acquire input while the macro owns it, including while paused;
  this does not make AI interruption synonymous with macro pause or stop.

## Round 4: accepted decisions

The user accepted every Q28-Q35 recommendation on 2026-11-14 and explicitly
requested the remaining technical details and validation plan as documents,
without implementation. Q35 delegates the technical choices within these bounds.

| Question | Decision | Accepted outcome |
| --- | --- | --- |
| Q28 | Uniform broadcast exclusion and remembered checks | While AI output sharing exists, keep the tab's broadcast check OFF and unavailable with a reason. Clear any inactive remembered check when sharing begins; active checked targets must still be removed/stopped by the person before sharing under Q20. Apply the exception to Start, Select All, scope-reset selection, manual checking, and cross-window auto-selection. After sharing ends, leave the tab OFF until a subsequent ordinary explicit/bulk/start selection action selects it; do not restore the pre-sharing check automatically. Preserve frozen recipients and existing scope-move boundaries. |
| Q29 | Granting AI control during human input | Do not grant control while that session has unfinished human input/queued sends, a paste or pressed-button gesture, or unresolved IME composition. Tell the person to finish or cancel the operation and invoke Grant control again; do not silently queue a future grant. Read-only sharing remains separately available where broadcast/macro rules permit. |
| Q30 | Consistency of history reads | Make each returned page internally consistent at its capture point. Do not pin an entire unlimited history for subsequent requests. Track requested ranges so later reads explicitly report edits/erasure/eviction instead of silently substituting another range or joining old/new text as if unchanged. Appended output alone need not invalidate surviving unchanged ranges; the AI can request a fresh range when necessary. |
| Q31 | Permission recipient versus individual conversation | Grant access to the selected AI integration counterpart, not to an unverified product-name string or a promised individual chat. If multiple conversations share that counterpart, they can potentially exercise its grants. Document this boundary and support separately identifiable integrations where the host provides them; do not claim universal per-chat isolation through ordinary MCP. Keep the human's requested task as the agent's behavioral instruction, not a TabTty-enforced chat boundary. |
| Q32 | Initial MCP transport | Initially offer stdio through a TabTty-supplied local bridge launched by the AI host; the bridge attaches to the running TabTty application. Defer a local HTTP MCP endpoint. Include the required bridge components in the offline deployment; no runtime package download is needed at bridge startup. This does not adopt a particular SDK or implementation language/dependency exception. |
| Q33 | External-AI setup assistance | Provide host-specific instructions and copyable standard MCP configuration for one-time user registration. Do not automatically edit third-party AI configuration or install an AI application/model. Persist ordinary connection setup where appropriate, but never persist sharing/control grants. This policy applies independently of the selected transport. |
| Q34 | TabTty not running or exiting | Require the person to start TabTty and open a terminal tab before sharing. An external AI connection attempt while TabTty is absent reports that condition and does not launch a GUI, hidden TabTty instance, or terminal connection. Complete application exit ends bridge attachment and all grants; the bridge cannot keep TabTty alive or silently recreate it. Host restart/retry must not restore permissions. |
| Q35 | Delegation of routine technical details | Let the agent research and specify API fields, paging/search bounds, wait limits, observation IDs, icon details, and related routine mechanics within the accepted behavior and existing dependency/security policies. Prepare a reviewable technical contract and validation plan before the final shared-understanding review. Escalate material behavior changes, dependency-policy exceptions, or newly discovered trade-offs for a product decision. This authorizes design only, not implementation, prototype, installation, or runtime testing. |

### Round 4 primary-source evidence

- MCP stdio uses a subprocess launched by the host, with standard input/output
  for protocol messages. The EOF/lifecycle boundary is separate from the time
  the LLM spends thinking. The proposed existing-GUI bridge and no-download
  packaging are TabTty design choices, not features supplied automatically by
  MCP. [stdio](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio).
- MCP 2026-07-28 explicitly forbids treating a stdio connection/process as a
  conversation/session proxy. An application identity or bridge process therefore
  does not by itself identify a particular chat; Q31 selects the accepted product
  permission boundary. [Statelessness](https://modelcontextprotocol.io/specification/2026-07-28/basic/index#statelessness).
- Goose documents Standard IO custom extensions and executable/argument
  configuration. LM Studio documents local MCP program launch and `mcp.json`
  editing. These support the proposed manual setup path, not a claim of tested
  TabTty interoperability or identical protocol revisions across hosts.
  [Goose setup](https://goose-docs.ai/docs/getting-started/using-extensions/#mcp-servers),
  [LM Studio setup](https://lmstudio.ai/docs/app/mcp),
  [LM Studio technical details](https://lmstudio.ai/blog/lmstudio-v0.3.17#MCP-support-more-technical-details).
- Goose documents offline problems from runtime-download shims. A directly
  provisioned bridge avoids making those shims a TabTty prerequisite. Model and
  inference runtime provisioning remains the external AI setup's responsibility.
  [Goose offline issues](https://goose-docs.ai/docs/troubleshooting/known-issues/#airgappedoffline-environment-issues),
  [LM Studio offline](https://lmstudio.ai/docs/app/offline).
- Streamable HTTP is an available alternative, but an ordinary response ending
  does not signal counterpart loss. Q32 defers its additional local
  endpoint/access contract. [HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http).
- The base broadcast contract distinguishes source from target. Until separately
  changed, a read-only AI-shared tab may still originate human broadcast input to
  other checked tabs; it cannot receive that broadcast under Q19. AI control
  blocks human input under Q4, and AI tool sends never fan out through broadcast.
  Q28 changes target selection, not this inherited source-routing rule.

## Documentation and completion policy

Record answers as they arrive and recompute the dependent frontier. Add glossary
terms when resolved; create an ADR only for a settled architectural trade-off
whose rationale merits one. No ADR is selected merely to document a proposed
interaction. Complete the interview and obtain confirmation of shared
understanding before treating the feature design as settled. Development still
requires a separate explicit user request.

Completion: the user accepted Q1-Q35, delegated the remaining bounded technical
details, invoked `to-spec`, and confirmed the testing boundary and publication.
The specification is ready for implementation when separately requested; issue
publication and the ready label do not start that work.
