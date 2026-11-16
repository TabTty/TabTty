# Human and AI collaboration in one terminal session

Research checked 2026-11-13; discussion updated 2026-11-14. This is a future-feature research and discussion
note, not an issue specification, ADR, initial-release requirement, or
authorization to implement or prototype. Explicit user directions in the
follow-up section and accepted interview answers are confirmed; other
recommendations remain proposals.
No runtime or dependency is selected here.

The [design interview](ai-terminal-collaboration-issue.md) records subsequent
confirmed directions and the completed four-round decision tree.
Q1-Q35 are now accepted, including Q16's broadcast-exclusion amendment,
Q17's icon-based tab indication, and Q19's inclusion of read-only sharing in
AI collaboration. Later interview answers take precedence over the earlier
candidate interactions below. Q28-Q35 settle target selection, grants, history
consistency, counterpart scope, stdio deployment, manual setup, and delegation.
The [technical contract](../specification/ai-collaboration.md),
[transport contract](../specification/ai-collaboration-transport.md), and
[validation plan](../specification/ai-collaboration-validation.md) complete that
delegated design, consolidated after confirmation of the testing boundary in
[GitHub issue #2](https://github.com/TabTty/TabTty/issues/2) with `ready-for-agent`.
They are the current specification records;
the historical proposals below do not reopen accepted choices.

## Question and existing boundary

The requested experience is for a person and an AI agent to observe the same
terminal session, with the agent able to send input into that session. Starting
another shell or SSH connection would not preserve the same remote application
state. The following integration directions are design inferences, not claims
that a generic protocol already supplies this experience automatically.

TabTty already specifies one session-owned input queue and independent receive
observers, with no dependency on a visible pane or log file. These are useful
foundations without adding an AI adapter now. A reconnect creates a new
generation and must not replay old input. See the
[session contract](../specification/session-and-recording.md),
[connection requirements](connection-profiles-issue.md), and
[future macro constraints](future-macro-design.md).

## Protocol findings

| Mechanism | What the primary source establishes | Implication for TabTty, inferred |
| --- | --- | --- |
| Model Context Protocol (MCP) | An open protocol exposing tools, resources, and prompts to AI applications. [Specification](https://modelcontextprotocol.io/specification/2026-07-28) | Expose selected terminal sessions as tools and data to an external agent host. |
| Agent Client Protocol (ACP) | Connects an editor or other client to a coding agent, and can be combined with MCP. [Protocol comparison](https://agentclientprotocol.com/get-started/related-protocols) | Useful if TabTty itself provides an agent conversation UI; MCP can still carry terminal operations. |
| Application-specific API or local IPC | No interoperability claim is made here. | A narrow internal boundary can connect UI, an external bridge, or a built-in agent, but other hosts need an adapter. |
| Screen capture and keyboard automation | A possible fallback, not a selected protocol or dependency. | Closest to visual computer use; matching focus, scroll position, and terminal identity becomes part of the automation. |

MCP is an interoperability candidate with documented adoption, not a claimed
market-share winner. Microsoft documents support in Copilot Studio, VS Code,
and Agent Framework. VS Code documents adding servers by URL or launch command.
[Microsoft overview](https://learn.microsoft.com/en-us/dotnet/ai/get-started-mcp),
[VS Code configuration](https://code.visualstudio.com/docs/agent-customization/mcp-servers).
These sources do not establish identical support for every protocol revision,
notification, transport, or tool-result shape in every host.

ACP also has product adoption: JetBrains documents external agents in AI Chat,
including passing configured MCP servers to them. ACP's current documentation
labels v1 Latest and v2 Draft; its overview says full remote support remains
work in progress. Treat compatibility as a specific agent/client/version check.
[JetBrains ACP](https://www.jetbrains.com/help/ai-assistant/acp.html),
[ACP introduction](https://agentclientprotocol.com/get-started/introduction).

ACP v1's terminal methods are `terminal/create`, `terminal/output`,
`terminal/wait_for_exit`, `terminal/kill`, and `terminal/release`.
Creation starts a command in a new terminal; this method set does not define
attaching to an existing interactive TabTty session and sending its keystrokes.
Inference: merely implementing ACP terminals would not satisfy this request.
Custom MCP tools or an agreed extension would still be needed.
[ACP v1 terminals](https://agentclientprotocol.com/protocol/v1/terminals).

## MCP transport and attachment alternatives

The latest published MCP specification resolved to **2026-07-28** when checked.
It uses self-contained requests with per-request capability metadata; earlier
revisions use different lifecycle rules. Streamable HTTP in this revision
removes protocol-level sessions and the standalone GET stream. A TabTty terminal
session remains application state and must have its own identity regardless of
the selected MCP revision. [MCP specification](https://modelcontextprotocol.io/specification/2026-07-28),
[transport overview](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports),
[Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http).

| Transport | Standard behavior | Proposed connection to an already open TabTty |
| --- | --- | --- |
| stdio | The MCP client launches a subprocess and exchanges newline-delimited JSON-RPC over stdin/stdout. | Launch a small bridge that locates the running TabTty instance and uses application IPC. Launching the bridge must not implicitly create a new terminal connection. |
| Streamable HTTP | An independent server accepts POST requests on one endpoint, returning JSON or request-scoped SSE. | The running application, or a bridge, exposes a local endpoint attached to its existing session owner. |

The table's right column is an architectural inference. stdio does not itself
attach to an unrelated GUI process. HTTP does not require Internet deployment:
the specification explicitly discusses local servers. It requires Origin
validation, recommends localhost binding for local use, and recommends proper
authentication. Neither bridge discovery nor access policy is settled here.
[stdio](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio),
[Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http).

## Product arrangement alternatives, not selected

1. **External agent host plus a TabTty MCP server.** The person uses their
   preferred agent UI. TabTty owns the terminal and exposes bounded observations
   and input operations. The agent host owns conversation, model selection, and
   tool orchestration. This is the most direct candidate for interoperability.
2. **Agent panel inside TabTty, using ACP.** TabTty presents the conversation
   and starts a compatible external agent. Its terminal tools can still be
   supplied using MCP; ACP explicitly documents this composition. This adds
   conversation UX and agent-process lifecycle responsibilities.
3. **Built-in agent loop with a configurable model endpoint.** TabTty owns
   prompts, tool execution, history, cancellation, and model integration.
   This gives product control but also creates substantially more maintenance
   and evaluation work. MCP need not sit between in-process modules.

These tradeoffs are inferences from the protocol roles, not measured implementation
costs. [MCP architecture](https://modelcontextprotocol.io/docs/2026-07-28/learn/architecture),
[ACP composition](https://agentclientprotocol.com/get-started/related-protocols).

## Terminal-specific questions for a later specification

The following are proposed design concerns, not accepted requirements:

- Identify the terminal session and connection generation explicitly. Tab title,
  current focus, or profile name alone is insufficient to identify the live target.
- Distinguish ordered received text, current rendered terminal state, retained
  output, and a particular pane's viewport. For full-screen applications, a screen
  snapshot with cursor and mode state answers a different question from a transcript.
  This future AI candidate does not change the deferred full-screen macro scope.
- Offer bounded reads with sequence positions and explicit gaps/truncation.
  A slow agent must not stall reception, rendering, or recording.
- Serialize AI sends through the existing input owner. Define how human input,
  paced paste, agent input, and human takeover interact. Sending bytes does not
  prove command acceptance or completion; prompts may be partial or application-specific.
- Define cancellation and uncertain transmission results without automatically
  replaying input after bridge failure or reconnect. MCP retry behavior must not
  become implicit command replay.
- Keep AI observations separate from credential retrieval. Existing rules exclude
  authentication material from terminal-payload observations, but received output
  may itself contain sensitive text. Reading the terminal cannot promise complete
  secret redaction. See [authentication requirements](authentication-issue.md).
- Treat remote output as task data, including when it contains instructions aimed
  at the agent. Select session-sharing and write permissions deliberately; details
  belong to the later feature rather than changing trusted-profile activation now.

## Closed-environment operation

MCP standardizes context/tool exchange and does not dictate how an application
uses an LLM. Therefore a local MCP server does not, by itself, make its agent or
model local. [MCP scope](https://modelcontextprotocol.io/docs/2026-07-28/learn/architecture).

Inference: a fully closed deployment needs the entire path inside its permitted
boundary: agent host, model inference and weights, terminal bridge, tool execution,
and any required services. Validate offline startup, model/runtime provisioning,
authentication/licensing dependencies, telemetry, updates, and fallback behavior.
An on-premises inference server can satisfy a closed-network requirement while
remaining distinct from inference on the same PC. Actual tool-calling reliability
and terminal-task performance require evaluation of the selected model and host.

Concrete candidates include **Goose plus Ollama**: Goose documents local LLM
providers including Ollama and MCP-based extensions. The proposed chain is
`Goose <-> Ollama/model` plus `Goose <-> MCP bridge <-> TabTty session`.
[Goose providers](https://goose-docs.ai/docs/getting-started/providers/#local-llms),
[Goose extensions](https://goose-docs.ai/docs/getting-started/using-extensions/).
**LM Studio** is another candidate with a built-in MCP host and local model
operation after provisioning.
[LM Studio MCP](https://lmstudio.ai/docs/app/mcp),
[offline operation](https://lmstudio.ai/docs/app/offline).
These are documented component capabilities, not validated TabTty integrations;
no interoperability, offline, or model-quality test was performed for this note.

Open protocols and software/model licenses are separate choices:
[Goose is Apache-2.0](https://github.com/aaif-goose/goose),
[Ollama is MIT](https://github.com/ollama/ollama), while the LM Studio application
uses [proprietary terms](https://lmstudio.ai/app-terms). Model weights have their
own license; a permissive inference runtime does not determine that license.
An OpenAI-compatible inference API is also distinct from MCP: Ollama documents
compatibility with only parts of that API, for model access. An agent host still
orchestrates model calls and terminal-tool calls.
[Ollama API compatibility](https://docs.ollama.com/api/openai-compatibility),
[Ollama tool calling](https://docs.ollama.com/capabilities/tool-calling).

## Repository fit and remaining decision

External protocol compatibility does not automatically approve a bundled SDK,
agent, or inference runtime under the Microsoft/OpenSSL dependency policy.
Microsoft documents the official C# MCP SDK as jointly maintained by Microsoft,
Anthropic, and the MCP organization; that provenance is relevant evidence for a
future dependency review, not an adoption decision in this note.
[Microsoft C# SDK guidance](https://learn.microsoft.com/en-us/dotnet/ai/get-started-mcp).

The strongest initial research direction is **same-session operations exposed
through MCP, with the agent host and model kept selectable**. ACP is complementary
if an in-app conversation becomes desirable. The user subsequently accepted
MCP, an external same-PC host, and explicit existing-tab sharing in interview
Q1-Q8, recorded in [ADR 0005](../adr/0005-expose-existing-terminal-sessions-over-local-mcp.md).
This adds neither AI nor macros to the initial release and authorizes no implementation.

## Follow-up discussion: full-screen operation and human takeover

### Explicit user direction, 2026-11-13

- Aim to support full-screen applications such as `top` and `vim` where
  practical; this is desired coverage, not a verified compatibility claim.
- Start with conversation in an external AI application, rather than requiring
  an in-app chat panel.
- Support autonomous work within the requested task, including configuration
  editing, saving, and service restarts when the task calls for them. Do not
  assume that each change requires a separate confirmation.
- The user suggested showing AI control in the tab and status bar after
  delegation, with interruption from the tab context menu. Detailed control
  and interruption semantics below remain proposals.
- This continues specification exploration only. The user has not selected a
  particular AI host, model, SDK, or protocol revision or authorized development.

### Proposed interaction, not yet accepted

Expose the current terminal grid as positioned text, cursor location, dimensions,
and relevant attributes and terminal modes. Retrieve retained output separately.
The agent observes the screen, submits a small action, and observes again. This
keeps image input optional for local models. Do not promise semantic knowledge of
an application's internal mode or success merely from terminal state.

Use the shared session grid, not a focused pane's visible crop or scrollback
position. Under [ADR 0002](../adr/0002-two-level-splits-with-one-session-per-tab.md),
same-tab panes share one session and its whole-tab grid. A person can browse or
copy retained output while the agent observes the live application. Inner pane
splits and tab movement do not create another operation target.

Treat text, terminal keys, paste, and supported remote mouse actions as distinct
operations, using existing session encoding and key-mode behavior. AI text
submission is not inherently clipboard paste. Define its authorization path
explicitly without changing the accepted confirmation for human clipboard paste.
Avoid unbounded queued action sequences. Continuously updating programs need
bounded observation waits; neither silence nor an unchanged screen is a general
completion test. Do not reject every action merely because `top` refreshed.
Connection identity and relevant layout/mode changes still need validation.

Keep one AI writer for a granted session. Display its control state throughout
the grant, including time spent waiting or thinking. Human viewing, selection,
copying, and local scrolling remain available. Human terminal input would require
explicit takeover; do not silently send the key that happened to trigger it.

Offer a tab-menu action such as "Interrupt AI and take control", with the same
action accessible from the status bar and keyboard. TabTty revokes the grant
locally, rejects later requests from it, and stops unsent input without depending
on the agent acknowledging cancellation. Complete required in-flight input
framing according to the existing send contract before admitting human input.
Do not automatically send Ctrl+C or Esc, close the connection, exit `vim`, or
claim to undo submitted edits or stop already running remote work. These are
separate terminal operations after takeover.

Invalidate the grant on connection loss/reconnect; do not replay input or let
late agent work apply to a fresh connection. Detectable loss of the controlling
agent should also release control; liveness detection and timing remain open.
AI sends address only their granted session and do not use broadcast routing.
Human broadcast into an AI-controlled session needs an explicit conflict policy,
rather than silently mixing input or skipping a checked target. The policy is
not selected here.

On 2026-11-14 the user accepted returning write control to the person on
interruption while continuing AI output sharing. A separate action to end
sharing remains a proposal. The user also proposed letting the AI retrieve
past text that a person can browse in the tab; exact scope and retrieval
behavior are questions in the design interview.

The requested task defines the agent's operational intent. TabTty can enforce
which session and connection a grant addresses and whether it remains valid;
arbitrary interactive terminal input cannot establish a reliable semantic
boundary such as "edit only this one file" by itself. Remote account permissions
or isolation are separate constraints. This does not imply that TabTty controls
other tools independently available in the external AI application.

The subsequent design interview settled history access, input/takeover behavior,
broadcast conflicts, counterpart scope, and setup through Q35. The technical
documents linked above now define the remaining mechanisms and future evidence
gates. No implementation, installation, or interoperability test is implied.
