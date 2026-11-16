---
status: accepted
---

# Complete terminal processing in Rust without a managed fallback

On 2026-11-16, the user explicitly requested moving the remaining terminal
processing into Rust and completing it without switching to C#. This supersedes
[ADR 0006](0006-native-terminal-core-with-lossless-managed-handoff.md): routine
resize, recording changes, alternate-screen use, and observation must keep the
terminal model in Rust. The previous partial implementation made native
performance and memory benefits conditional on avoiding those operations.

Rust owns the authoritative terminal state and the terminal semantics required
by the existing product contracts, including all supported receive/transmit
encodings, VT processing, reflow, input-mode interpretation, recording
projections, and bounded screen/history observation. C# owns the application
and session orchestration, transports, actual file writes, authorization,
publication, and WPF presentation. A managed adapter exposes the Rust module;
it does not contain a second production terminal implementation.

## Consequences

- Supported features and encodings use Rust from creation through retained
  disconnected output and disposal. Neither unsupported native functionality
  nor resource pressure selects C#. Product-excluded controls retain the
  existing consume/ignore behavior inside Rust; resource failures retain their
  existing narrow or affected-session outcomes.
- The cost is completing and verifying the existing terminal contract in Rust
  before declaring the replacement ready. This is not a promise of every VT
  extension or of improved performance without measurement.
- Retain ADR 0006's Rust/standard-library dependency exception, pre-mutation
  resource admission, owned immutable results, and explicit packaging failures.
  New third-party dependencies are not adopted by this decision.
- The existing C# implementation may remain an isolated test reference during
  migration. Production sessions have no managed terminal selection, handoff,
  shadow model, or diagnostic switch to that reference implementation.
- SSH/OpenSSL ownership, file-backed profiles, the one-session-per-tab rule,
  and the initial-release exclusion of macros remain unchanged.

The [native terminal design](../specification/native-terminal-core.md) records
the allocation and acceptance criteria. The direction and recording-interface
design, including failure, cancellation, and repeated-operation rules, are
confirmed through Q5 of the
[architecture interview](../drafts/terminal-architecture-deepening.md).
Q6-Q8 confirm shared source interpretation with separate admission/execution
walks and their verification contract. Closure scope, automatic/bulk transfer-
window closure, named complete operations, and failure/abandoned-wait rules are
accepted in Q9-Q11 and Q13. Q12a accepts ordinary same-target transfer completion
during confirmation without restarting it; Q12b accepts a later window close
joining an existing tab closure and handling the remaining tabs with required
confirmation, without duplicate work or a user retry. Q14 accepts the exceptional
changed-target outcome and completes candidate 3's final shared-understanding
review. All three designs are ready for separately requested implementation.
These are design decisions; this review performed no implementation, prototyping,
or runtime validation, and current source still implements the earlier partial
Rust design.
The [codec consultation](../drafts/terminal-codec-consultation.md) separately
records approved ISO-8859-10/11/14/16 table reuse and Japanese proposals A+B,
including Microsoft CP932 as the candidate base with the specified Rust-owned
mapping/state/recovery and exact-send policies. This does not establish runtime
parity or approve `Cp932Duplicates` ports, new mapping corrections, or other
unreviewed custom processing; newly discovered work still requires consultation.
This decision authorizes documentation changes, not implementation or prototyping.

During subsequent implementation the user approved strict Win32 and Windows
ICU conversion from Rust. The supported set is 47 encodings, with ICU owning
JIS designations and repertoire selection. See the current
[codec decision](../drafts/terminal-codec-consultation.md).
