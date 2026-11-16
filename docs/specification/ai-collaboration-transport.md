# AI collaboration transport and local counterpart binding

Technical contract, 2026-11-14, under accepted AI interview Q1-Q35. The user
confirmed the testing boundary and requested publication of the synthesized
specification as [GitHub issue #2](https://github.com/TabTty/TabTty/issues/2).
The user subsequently authorized implementation. Product rules remain in the
[interview](../drafts/ai-terminal-collaboration-issue.md) and
[ADR 0005](../adr/0005-expose-existing-terminal-sessions-over-local-mcp.md).
The [collaboration contract](ai-collaboration.md) owns permissions, lifecycle,
terminal operations, and results; see the [validation plan](ai-collaboration-validation.md).

## Deployment and setup

The external AI host launches the supplied `TabTty.McpBridge.exe --stdio`
directly, with redirected standard streams and without an intermediate shell,
download runner, daemon, or console window. The bridge attaches to an already
running `TabTty.exe` through a private Windows named pipe. It creates no GUI,
terminal connection, HTTP listener, inbound TCP port, or inference connection.
The host/model may use an allowed network; TabTty does not configure that policy.

Deliver the bridge beside the GUI in the same versioned Windows deployment,
including the required Microsoft .NET runtime and bridge resources for offline
startup. Use the GUI's supported architecture/deployment variants. Installation
must not need `npx`, `uvx`, a package registry, or a first-run runtime download.
This is a packaging requirement, not a claim that an offline package exists.
Bridge/GUI private-interface versions must match; mismatch reports an actionable
installation error without falling back to another executable or transport.

Offer copyable host instructions and an absolute executable path from the actual
installation. The logical host configuration is executable =
`<TabTty installation directory>\TabTty.McpBridge.exe`, arguments = `["--stdio"]`,
and no secret environment variables. Example JSON for hosts using `mcpServers`:

```json
{"mcpServers":{"tabtty":{"command":"C:\\TabTty\\TabTty.McpBridge.exe","args":["--stdio"]}}}
```

`C:\TabTty` is an illustrative path, never an assumed install location.
The host owns its configuration file; TabTty does not silently edit it, install
an AI host/model, or store third-party credentials. No separate TabTty bridge
configuration, discovery file, persistent pairing token, or permission database
is required. A saved host launch configuration confers no output/control access.

## Private attachment and identity

Use `\\.\pipe\TabTty.Ai.v1.<logon-SID>` for the current Windows logon, derived
from the process token rather than a supplied username. The GUI creates the
first pipe instance with exclusive first-instance checking, then bounded server
instances under its ownership. A conflicting owner disables AI attachment with
a visible error; never connect to it as a substitute GUI. No endpoint exists
after the application run ends. The pipe name is discovery, not authentication.

Restrict its DACL to the current logon SID, with no broad user-account,
Everyone, Anonymous, or network grant; reject remote clients explicitly. Check same user,
logon session, and elevation on both ends. Run normally without elevation;
an elevation mismatch fails without prompting for administrator access.
Clients request individual read/write rights, not instance creation or generic
write; the server retains the rights needed to create its instances. Microsoft
documents the permissive default DACL and logon-SID restriction, while
`CurrentUserOnly` checks user and elevation but is not a complete logon policy.
[Pipe access](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights),
[pipe options](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0),
[remote rejection and first instance](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea).

Obtain peer PIDs from the connected pipe using Windows, open process handles
with query/synchronization rights, and retain those handles for attachment
lifetime. Validate creation time, token, live state, and canonical executable
image against the expected GUI/bridge sibling in this installation. Recheck
peer PID and live state after opening; fail closed on races or inaccessible
identity. A numeric PID alone is reusable and is never an authorization key.
[Client PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid),
[server PID](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid).

After validation, the GUI allocates a cryptographically random 128-bit
`counterpart_id` and an eight-hex-digit `pairing_code` unique among current
attachments. Bind the ID to this
application run, pipe instance, and live bridge process handle. The private
handshake exchanges independent 128-bit nonces and echoes both with interface
version 1 before admitting requests. Nonces distinguish attachments; they are
not an authentication substitute or transferable permission. Reattachment is a
new counterpart, even for the same executable, PID value, name, or launch config.

The GUI's counterpart picker shows the display code, validated bridge location,
and bounded claimed AI name marked as unverified. Its details explain that
access belongs to this integration and may cover multiple chats in that host.
`tabtty_list_shared_sessions` returns the same code so the person can match them.
Selecting it to share a tab is the pairing action; it grants read access only
to that tab. Separate bridges can represent separate integrations where the
host supports that setup, without promising per-chat isolation.

`clientInfo`, titles, icons, URLs, descriptions, request IDs, and chat identifiers
never establish the authorization principal. Ignore supplied icon/website
resources without fetching them; display strings as inert bounded text.
Identity checks bind a local process/channel, not the publisher, model, person,
or semantic task. This design does not sandbox hostile code with sufficient
same-user process access, protect a modified installation, or prevent an
authorized host from relaying received output elsewhere.

## Authoritative authorization

The GUI session module owns all sharing records, control grants, generations,
input ordering, and revocation. The bridge is a protocol adapter, not a second
permission owner. Every operation crosses the same authorization interface as
the GUI transition logic. Resolve its counterpart from the private attachment;
never accept a client-supplied counterpart ID as evidence of another attachment.
Require explicit application handles on tool requests as the tool contract
defines, and verify their binding and current grant at each operation.

Before sharing, expose only protocol information, static tool schemas, and this
attachment's readiness/pairing status. Shared-tab enumeration returns an empty
list. Do not expose unshared tab counts, names, destinations, profile IDs, window
layout, screen content, or other counterpart details. Unknown, expired, and
other-counterpart session handles all return `not_shared`; invalid control IDs
return `control_required` only within an authorized session. Within that session,
unknown/expired/foreign observation handles uniformly return `stale_observation`
and range/continuation handles return `cursor_expired`. The tool contract permits
more specific range-change/loss information only for a verified authorized range.
Permissions are denied by default; protocol initialization does not grant them.
[OWASP authorization guidance](https://cheatsheetseries.owasp.org/cheatsheets/Authorization_Cheat_Sheet.html).

Recheck authorization before committing a result to the bridge delivery channel
and before input admission. Sharing-end cancels uncommitted observations/waits
and invalidates queued work; already committed delivery may arrive later and
cannot be recalled. The bridge also drops not-yet-emitted results when notified
of revocation, without promising recall of already committed data. The
collaboration contract defines that cutoff and cleanup.
Moving tabs/windows never changes the owner. Bridge failure cannot prevent the
person's local takeover. No bridge request can approve itself, start sharing,
grant control, access vault values, or restore an obsolete grant.

## MCP revisions and messages

Implement two explicitly tested wire modes in the planned adapter: legacy
`2025-11-25` and modern `2026-07-28`. This is a compatibility target, not evidence
that any existing AI host has been tested. The public tool names/argument
semantics are the same in both; era-specific envelopes remain separate.

Legacy clients start with `initialize`; reply with `2025-11-25` and wait for
`notifications/initialized` before tool use. An unsupported legacy proposal
receives the supported legacy version; the client must disconnect if incompatible.
Do not offer modern semantics as a legacy negotiated version. `ping` remains
available as prescribed by the legacy lifecycle.
[Legacy lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle).

Modern requests independently carry `params._meta` entries
`io.modelcontextprotocol/protocolVersion` and
`io.modelcontextprotocol/clientCapabilities`; neither is inherited from an
earlier request. Missing required metadata returns `-32602`; an unsupported
version returns `-32022` with supported revisions. Implement `server/discover`;
return server identity, capabilities, and both supported revisions. Modern
successes use `resultType: "complete"`. Requests containing any modern
version/capability field remain modern even after legacy initialization;
legacy tool calls require a completed legacy handshake. Never interpret invalid
or incomplete modern metadata as a request to switch eras.
[Versioning](https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning),
[discovery](https://modelcontextprotocol.io/specification/2026-07-28/server/discover),
[base fields](https://modelcontextprotocol.io/specification/2026-07-28/basic/index).

Modern `server/discover` and `tools/list` return `ttlMs: 300000` and
`cacheScope: "public"`, covering only static protocol identity/capabilities and
tool schemas, never counterpart or terminal data. Discovery includes
`_meta["io.modelcontextprotocol/serverInfo"]` with name `TabTty` and the packaged
version. Advertise `tools: {}` with no list-change capability; all seven schemas
are fixed regardless of grants. Never cache `tabtty_list_shared_sessions` or
terminal tool results as public discovery data. Legacy replies omit modern-only
`resultType`/cache fields. Modern `ping` is not supported; the private heartbeat
is a separate internal mechanism.
[Tool messages, schemas, and rate limiting](https://modelcontextprotocol.io/specification/2026-07-28/server/tools),
[revision changes](https://modelcontextprotocol.io/specification/2026-07-28/changelog).

Modern statelessness does not make a stdio process a chat identity. Explicit
application handles name TabTty state; process binding is TabTty's deliberate
local authorization restriction under Q31/Q32, not MCP conversation continuity.
Advertise tools only: no resources, prompts, sampling, elicitation, subscriptions,
background-task extension, or model calls. Implement each era's required base
behavior and cancellation, with unsupported optional methods rejected normally.
Use tool errors for operational failures and standard protocol errors for invalid
messages; allocate no application codes in MCP-reserved error ranges.

Use UTF-8, one JSON-RPC object per line on stdin/stdout; escape newlines inside
strings. No JSON batch, BOM, banners, progress text, terminal data outside JSON,
or shell output goes to stdout. Observe era-specific cancellation rules; a
cancelled modern request receives no later response. Cancellation cannot undo
terminal bytes already admitted, and never implies safe automatic retry.
[stdio framing and cancellation](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio).

## Bounds, liveness, and failure

These are initial engineering guards, not measured performance claims. They
supplement the [tool contract](ai-collaboration.md) and existing resource
budgets; no budget permits evicting terminal retention early.

| Item | Planned bound and behavior |
| --- | --- |
| Public frame | 2 MiB total UTF-8 JSON bytes excluding LF; enforce incrementally before parsing or allocation growth |
| Private frame | 2 MiB length-prefixed UTF-8 JSON body, checked before allocation; fixed 4-byte little-endian unsigned length |
| JSON | Depth 32; reject duplicate property names, invalid UTF-8, non-finite numbers, malformed envelopes, and invalid argument types |
| Metadata | 16 KiB total client metadata; display name/version each <=128 Unicode scalars; ignore unknown optional metadata within bounds |
| Attachments | 8 bridge attachments per application, including unshared; excess fails without replacing existing counterparts |
| Outstanding work | 16 ordinary requests per counterpart, at most one admitted input operation per tab; reject excess with busy, never an unbounded queue |
| Invocation rate | Ordinary calls 20/s, burst 40 per counterpart; 100/s, burst 200 app-wide; new input also 5/s, burst 10 per tab, as specified in the tool contract |
| Serialization | Include text fallback, structured result, metadata, escaping, and envelopes in frame accounting; page before oversize, never split JSON arbitrarily |
| Tool result | 1 MiB serialized result including text fallback and structured data together, leaving space for the 2 MiB frame's envelope |
| Buffering | 8 MiB adapter-owned live payload per counterpart, charged within the tool contract's 64 MiB application transient-work total, including in-flight/failed-owned frames |
| Attachment | 5-second pipe connect/handshake deadline; absent GUI reports unavailable and exits, without launching or waiting for a future GUI |
| Progress health | Private heartbeat every 5 seconds, independent of tool/model activity; no valid heartbeat for 20 seconds ends attachment |
| Blocked stream | A write blocked for 20 seconds ends attachment; no unbounded response backlog or terminal receive backpressure |
| Incomplete frame | 20 seconds from first byte to complete frame; idle streams with no pending frame are allowed |

Reserve capacity for revocation/cancellation/health independently of ordinary
work and rate buckets. Excess tool invocations return tool `busy` with
`retry_after_ms`; protocol discovery overflow uses bounded JSON-RPC server error
`-31001` with the same delay metadata, outside the JSON-RPC/MCP reserved ranges.
Use this bounded application error consistently in both wire modes. No overload
response schedules a retry or consumes an input sequence. Heartbeats neither
contain terminal content nor require the AI to answer
while thinking. Quiet inference and a completed tool request do not end control.
Check deadlines on resume from suspension before accepting further work.
Process-handle termination, stdin EOF, broken stdout/pipe, failed identity,
or unrecoverable framing failure ends attachment promptly. Protocol errors with
a valid bounded frame receive the appropriate error; oversize/unframed input
closes the transport without echoing its content.

The bridge resolves its direct launcher from a Toolhelp process snapshot,
opens a query/synchronization handle, and rejects an exited process or a creation
time later than its own. It monitors that handle for exit; launcher information
is diagnostic, not a trusted host identity.
[Parent PID](https://learn.microsoft.com/en-us/windows/win32/api/tlhelp32/ns-tlhelp32-processentry32w),
[process creation time](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-getprocesstimes).
Hosts must retain the directly launched bridge and its streams for the
integration lifetime. Orphaned/inherited streams cannot preserve grants after
observed launcher exit. Failure to establish this lifetime binding rejects setup.
Process loss revokes all that counterpart's sharing/control through the GUI
contract. GUI exit closes every attachment and bridge; bridges never keep the
application run alive. Restart/retry creates a new empty permission state.
Do not replay commands or transparently reattach after failure. Partial or
uncertain input retains the tool contract's takeover/recovery behavior.

## Dependencies, diagnostics, and verification

Choose a small TabTty-owned MCP adapter using Microsoft .NET BCL facilities
such as `System.Text.Json`, `System.IO.Pipes`, safe process handles, cryptographic
randomness, and narrowly scoped documented Win32 interop. The GUI authorization
interface hides pipe transport and wire-era differences from session logic.
The official C# MCP SDK is jointly maintained by Microsoft, Anthropic, and MCP;
that description alone does not establish compliance with the repository's
direct-dependency policy. This draft does not adopt it or request an exception.
[Microsoft SDK provenance](https://learn.microsoft.com/en-us/dotnet/ai/get-started-mcp).

Stderr permits bounded event codes, version, time, and nonsecret failure category
only; the host may capture it. Exclude request/result bodies, terminal content,
input strings, pairing nonces, grants, credential references/values, client
metadata, process command lines, and exception dumps. Keep diagnostic logging
distinct from accepted session recordings. Terminal output may contain manually
entered secrets or remote/local echo; no universal prompt detection or redaction
is promised. End sharing before sensitive terminal work when required.
[Authentication policy](../drafts/authentication-issue.md),
[OWASP logging exclusions](https://cheatsheetseries.owasp.org/cheatsheets/Logging_Cheat_Sheet.html#data-to-exclude).

Primary-source review occurred on 2026-11-14. No implementation, installation,
runtime, offline, host/model interoperability, or security test was performed.
Later validation must cover both MCP revisions; absent/closing GUI; different
users/logons/elevations; remote pipe rejection; endpoint squatting/PID reuse;
same-name counterparts; multiple chats sharing one counterpart; cross-counterpart
handle rejection; cancellation and revocation races; malformed/oversize/slow
frames; quiet inference versus process loss; stale grants after restart; and
offline startup without network/package access. Recheck affected primary
guidance and dependency versions at implementation start and before release.
