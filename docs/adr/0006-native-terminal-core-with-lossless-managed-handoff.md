---
status: superseded by ADR-0007
---

# Use a native terminal core with lossless managed handoff

Superseded for the target architecture on 2026-11-16 by
[ADR 0007](0007-complete-terminal-processing-in-rust.md), at the user's request.
The decision below records the earlier adoption and still describes the current
partial implementation; the complete Rust replacement has not been implemented.

Accepted on 2026-11-15 after the user requested production adoption of the Rust
terminal-core experiment. UTF-8 decoding, normal-screen editing, retained rows,
and batched Text/Terminal recording use the app-local Rust core by default.
C# continues to own connections, session scheduling, recording files, AI and
macro authority, and WPF. The measured throughput and memory gains justify a
scoped exception to the default dependency policy for Rust and its standard
library; the terminal crate has no external crate dependencies.

Before each native operation, validate the complete payload and reserve its
bounded peak state charge. An unsupported operation transfers the current model
once into the existing managed reducer before processing that payload. Transfer
includes retained row identities, cells, tabs, recording submission state,
decoder/parser partials, cursor/rendition, and pending grapheme state. It must
neither replay previous input nor omit output or recording bytes. Native and
managed receive processing never run in parallel on duplicate model state.

Legacy encodings start in C#. Resize after reception, alternate-screen and other
unsupported controls, recording interval changes, AI observation, and macro
hooks can require the managed path. Each transferred projection remains managed. Native
residency is therefore part of performance evidence: adopting this boundary
does not promise Rust throughput after every terminal operation. Broad native
VT/reflow support can be added without changing the session/UI contract.

UI snapshots own immutable compact copies. They expose text and stable row
identities immediately and expand cells only when a consumer needs them.
Native memory cannot be borrowed across receive mutations or library disposal.
Hidden/minimized tabs retain all configured history and recordings while
periodic UI work remains suspended. The existing resource bounds and 100 ms
p95 response target continue to apply.

The native DLL is built and distributed with the application, licenses, and
source/build hashes. Missing or mismatched native binaries fail visibly;
compatibility handoff is not a way to hide packaging failures. The diagnostic
environment switch `TABTTY_TERMINAL_CORE=off` selects the existing C# reducer
for comparison and regression tests.
