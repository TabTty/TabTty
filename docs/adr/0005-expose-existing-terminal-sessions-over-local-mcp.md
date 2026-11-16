---
status: accepted
---

# Expose selected existing terminal sessions to external AI through local MCP

Accepted on 2026-11-14 in Q1-Q8 and refined by Q31-Q34 of the
[AI collaboration interview](../drafts/ai-terminal-collaboration-issue.md).
TabTty will expose explicitly shared existing terminal sessions through MCP to
an external AI application running on the same PC. The external application owns
conversation, model selection, and agent orchestration; TabTty owns the terminal,
output sharing, and separately granted input control. This supports selectable
AI hosts and local/closed-network models without requiring a built-in chat panel
or inference runtime.

Reading includes the permitted current screen and retained text, including
pre-sharing history. It does not require an image-capable model or a separate
unlimited history store. Returning input control to the person preserves output
sharing; an explicit End AI sharing operation ends both. The initial AI interface
does not expose connection creation, profile editing, macro launch, dedicated
file-transfer UI, or local desktop control.

Built-in agent orchestration, an ACP chat panel, and visual desktop automation
were considered in the research note. They are not prerequisites for this
feature. MCP standardizes the integration boundary, not TabTty's terminal-tool
semantics or permission enforcement.

Initially use a TabTty-supplied stdio bridge launched directly by the external
host and attached to an already running GUI. Defer HTTP transport. Bundle its
runtime for offline startup, provide manual host configuration, and never launch
a hidden GUI/session or restore permissions after restart. Authorization belongs
to one identified integration counterpart, potentially shared by several chats;
MCP client names do not establish identity or universal conversation isolation.
This keeps one application authority and an open public protocol without an
inbound network endpoint or a built-in model dependency.

The delegated [transport contract](../specification/ai-collaboration-transport.md)
specifies process/channel binding, two MCP compatibility targets, and a small
TabTty-owned adapter using Microsoft .NET facilities. It adopts no third-party
SDK and requests no dependency-policy exception. The exact
[terminal/tool contract](../specification/ai-collaboration.md) and
[validation plan](../specification/ai-collaboration-validation.md) are consolidated
in [GitHub issue #2](https://github.com/TabTty/TabTty/issues/2), published after
the user confirmed the testing boundary and requested publication. Compatibility
has not been tested.
This decision authorizes no implementation or prototype and does not change
the initial-release scope.
