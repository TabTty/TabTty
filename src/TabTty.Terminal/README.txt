TabTty native terminal fast path (ABI 2)

Implementation note, 2026-11-16: this file documents the current partial
implementation and ABI 2. The target design in
../../docs/adr/0007-complete-terminal-processing-in-rust.md and
../../docs/specification/native-terminal-core.md replaces managed handoff with
complete Rust processing. That replacement is not implemented or tested yet.

This library owns UTF-8 decoding, normal-screen edits, retained rows and batched
Text/Terminal projection while the native path is active. The application owns
session ordering and admission budgets. The existing C# engine remains the
compatibility implementation; no C# shadow model runs during native reception.

A complete incoming batch is checked before any decoder, row, parser or log
mutation. On unsupported input the caller exports the existing native state,
imports equivalent managed rows and source states, then feeds the same original
unconsumed batch through C#. Handoff is one-way for that terminal model.
Legacy encodings start in C#. The managed adapter hands off before live reflow,
alternate-screen operation, recording-interval changes, AI proof generation,
macros, custom callbacks or other operations outside the native scope.

Native scope
- Independent UTF-8 DATA, stderr and local echo; split/malformed sequences,
  source-specific initial BOM handling and EOF replacement.
- Exact repository Unicode 17 grapheme/width tables, ambiguous-width policy,
  wide/combining/emoji cells, normal retained history and autowrap.
- CR/LF/VT/FF/BS, default TAB, normal full-screen scroll, SGR colors/styles.
- ESC D/E; CSI H/f/A/B/C/D/E/F/G/backtick/d/K/X/J 0..3; modes ?7, ?25,
  ?2004 (bracketed paste) and 20. OSC strings are ignored exactly as in the
  managed parser, including BEL/CAN/SUB/ESC termination and the 1 MiB limit.
- Edited-row Text and accepted Terminal operations returned as UTF-8 batches.
  Raw logging remains at the application's admitted-payload boundary.
- Native history is authoritative; immutable managed snapshots are built only
  when requested. Native negative row IDs are process-unique and disjoint from
  the managed engine's positive IDs.

Lossless export includes rows, styles, tabs, nullable submitted Text and its
revision, logical row/content/cluster anchors, cursor/modes/style, active grapheme,
all source decoder/parser/LastGraphic states, and Text recording position.
An initial empty screen row is included even if the public snapshot is empty.
There are no native AI per-cell proofs. The caller must switch before exposing
such proofs and assign fresh managed baseline cell revisions during import.
The known unchanged recording interval seeds decoder/escape/CR interval fields.

Resource admission
- tc_create_required quotes charged capacity before constructor allocation.
- tc_preflight quotes total peak charged capacity before Feed/Complete/Flush/
  Snapshot/Export. It uses bounded stack parser scratch and does not clone or
  mutate retained state.
- The caller reserves this amount, calls tc_set_limit, then invokes the operation.
  Each allocating operation repeats the admission check before mutation.
- Bounds cover row/container growth, UTF-16 clusters, parser state, output buffers,
  plain-text caches and export descriptors. They are deliberately conservative.
- A native effect batch with an upper bound exceeding 256 KiB requests managed handoff before mutation;
  the existing managed incremental writer preserves logging behavior.
- If admission cannot be reserved, the caller attempts managed handoff. It must
  not present a failed reservation or unsupported operation as successful capture.
- Physical ownership and quota charge differ. tc_owned_bytes and Stats describe
  requested native buffers/headers. tc_quota_bytes additionally charges every
  allocated cell slot at least 64 bytes, including spare rows. Quota/ownership
  are cached; ordinary receive does not rescan retained history.
- After each operation the caller shrinks its reservation to tc_quota_bytes.
  Allocator metadata and managed UI copies still require process-level memory
  measurement. Fatal process-wide allocator OOM is not promised recoverable.

C ABI
tabtty_terminal.dll; cdecl; tc_abi_version() == 2.
int tc_create_required(int cols,int rows,int retained,int ambiguous,out u64);
int tc_create(int cols,int rows,int retained,int ambiguous,u64 allowance,out handle);
int tc_preflight(handle,uint op,uint source,byte* data,uint length,uint flags,out u64);
int tc_set_limit(handle,u64 allowance);
int tc_feed(handle,uint source,byte* data,uint length,uint flags,out Effects);
int tc_complete(handle,uint flags,out Effects);
int tc_flush_text(handle,uint flags,out Effects);
int tc_resize(handle,int cols,int rows);
int tc_snapshot(handle,out SnapshotView);
int tc_export(handle,out ExportState);
int tc_stats(handle,out Stats);
u64 tc_owned_bytes(handle); u64 tc_quota_bytes(handle);
void tc_destroy(handle);
u64 tc_live_bytes(); u64 tc_peak_bytes(); u64 tc_live_models();

op: 0 Feed, 1 Complete, 2 FlushText, 3 Snapshot, 4 Export.
source: 0 DATA, 1 stderr, 2 local echo.
flags: bit 1 Terminal log, bit 2 Text log; stable while native is active.
status: 0 success, 1 invalid argument/state, 2 handoff before mutation,
3 internal/resource failure. Empty-model Resize changes geometry without
allocation; nonempty resize leaves the model exportable for managed reflow.

repr(C) layouts are in src/lib.rs and asserted on Windows x64:
CellView 20, RowView 72, SnapshotView 40, Effects 32, Stats 56 bytes.
Transfer: TabView 8, SourceView 48, ExportRow 120, ExportState 152 bytes.
SubmittedLength UINT_MAX means null; zero means a submitted empty string.
Source flags: started=1, previousCR=2, lastWasGraphic=4, OSC=8.
Source escape: Ground=0, ESC=1, CSI=2, ControlString=3, ControlStringEscape=4;
the caller maps to its managed enum. Source reserved stores StringBytes.
Model flags: autowrap=1, LNM=2, wrapPending=4, receiveCompleted=8, bracketedPaste=16.
Effects reserved0 publishes bracketedPaste=16 after every feed/complete/flush,
including when no log is active; the caller synchronizes its public mode getter.
Colors: 0 is default; 0x01000000|RGB is assigned.

Lifetime
One reducer serializes all calls for a model. Effects borrow memory until the
next feed/complete/flush; snapshot/export pointers borrow memory until the next
mutating/snapshot/export call or destruction. The caller copies them before that
boundary. No UI or asynchronous task may retain a native pointer. Native memory
is allocated and freed by this same DLL; managed code never frees its buffers.
Null/alignment checks do not validate arbitrary in-process addresses.

Build and validation
No external Rust crates. The repository build supplies the Rust/MSVC x64 linker,
Windows SDK paths and app-local packaging. Static CRT is supported because
allocator ownership never crosses the DLL boundary. Unicode17.bin is copied
unchanged from the repository, with its accompanying license.

Rust tests cover supported semantics, ABI layouts, partial source export, stale
Text revisions, initial empty rows, retired logical-cluster prefixes, complete-
batch unsupported preflight and admission rejection without mutation. Managed
differential tests must additionally compare imported state and all subsequent
compatibility operations against the original C# engine.

