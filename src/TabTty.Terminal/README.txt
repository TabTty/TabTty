TabTty Rust terminal core (ABI 10)

The Rust terminal model owns source decoding, VT execution, screen buffers,
retained history, input-mode encoding, and Text and Terminal recording effects.
DATA and stderr have separate decoder and parser state. Local echo uses UTF-8.
The supported legacy mappings use Win32 or Windows ICU. JIS designations and
character conversion use Windows ICU.

The application orders model calls and owns transports, Raw recording,
file writers, presentation, and AI authorization and publication. The native
history seam supplies stable row anchors, bounded Unicode slices, and proofs
that detect changes to retained text, including edits that restore prior text.

Resource admission

- tc_create_required and tc_preflight estimate charged capacity before model
  creation and terminal operations. The adapter reserves the estimate and sets
  the limit. Execution checks the admitted operation before making changes.
- Resize builds a private plan. The adapter prepares both the displayed and
  receive-only plans before applying either geometry change when Text recording
  is configured.
- An over-limit recording effect reports refusal separately from terminal
  updates and replies. The recording owner ends the affected part.
- Snapshot estimates include the immutable copy and the allowance for expanded
  UI cells. History text is copied only for the admitted slice.
- After each operation, the adapter reconciles its charge to tc_quota_bytes.
  Whole-process memory is measured separately from the terminal quota.

C ABI

The library is tabtty_terminal.dll with the cdecl calling convention.
Entry points and repr(C) layouts are defined in src/lib.rs. The .NET adapter
checks ABI version and layout compatibility when opening a terminal.

Operation numbers for tc_preflight

- 0 receive
- 1 complete receive
- 2 flush Text recording
- 3 snapshot

Source numbers are 0 for DATA, 1 for stderr, and 2 for local echo. Feed flags
use bits 0 and 1 for Terminal and Text recording and bit 2 for the receive
observer. The reply queue count starts at bit 8.

A model's caller serializes operations. Effect, snapshot, and history row views
borrow native data. The adapter finishes reading or copies those views while
it owns the terminal gate. Asynchronous consumers own their copied results.
Close a model with tc_destroy and discard resize plans with tc_resize_drop.
