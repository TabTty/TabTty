# Complete Rust terminal processing and unify terminal closure

## What to build

Implement the three architecture designs confirmed through Q14: terminal-owned recording operations, one set of Rust interpretation rules for admission and execution, and complete tab/window closure operations. Complete the native terminal coverage required by ADR 0007 so supported terminal behavior never switches to a managed terminal implementation.

The accepted Complete Rust terminal module design and Tab and window closure design are authoritative, together with the existing terminal/encoding, session/recording, retained-history observation, file-operation, window and resource contracts. Keep application coordination, transports, actual files, AI authority/publication and WPF presentation in their existing owners. This plan adds no macro runtime, authentication policy, public terminal selector or third-party dependency.

The native migration uses a shared integration branch. When implementation is authorized, record the exact current working tree, including untracked source, and toolchain as a reproducible baseline; the current workspace has no commit to use as an assumed source baseline. Each feature ticket connects its delivered scenario through the real application/session/terminal path and verifies that scenario. Incomplete native coverage is an integration defect, not permission to add a fallback. Whole-product green and the final integrated release candidate are promised only by the final cutover and validation gates. The former C# terminal may be isolated as a test reference, never a new production fallback.

Closure preparation and application coordination can proceed independently of most native feature migration. Only native retained-state integration and the final combined acceptance require the corresponding native capabilities. Existing feature issues and the earlier architecture map remain unchanged. The open broadcast-movement issue has a remaining physical cross-DPI capture check; it is not a reason to block work that uses the already implemented move ownership.

## Acceptance criteria

- [ ] Preserve the recorded codec decisions: existing ISO-8859-10/11/14/16 Unicode tables (approved 2026-11-16), and the Japanese compatibility layer over Microsoft CP932 plus specified shared recovery/exact-send policy (approved 2026-11-17). Use native Microsoft conversion for the remaining code pages, subject to exact acceptance checks. Consult before adopting newly discovered custom corrections/data, including any managed CP932 override port; do not reconfirm already approved scope.

- [ ] Every supported terminal encoding, receive/input operation, screen/reflow transition, recording path and bounded observation uses the authoritative Rust model after cutover; product-excluded controls preserve their specified behavior.
- [ ] Native admission and execution share interpretation definitions, maintain independent source state, and preserve pre-mutation rejection, bounded owned results and actual resource accounting.
- [ ] Recording callers use complete named operations; files, all-format outcomes and actual disk cleanup stay with their existing C# owners.
- [ ] Explicit, bulk and automatic tab closure close associated transfer windows under the accepted cleanup/reporting policy. Retained-tab disconnect preserves results.
- [ ] Repeated and overlapping close intents share target execution, ordinary progress does not restart confirmation, invalid approval starts no new effects, and all outstanding cleanup remains owned and observable.
- [ ] Complete the child-ticket dependency graph and combined functional, failure, ownership, packaging, presentation and measured workload validation. Historical partial-native results are not evidence of this replacement.
- [ ] Each implementation slice includes independent expected outcomes and relevant existing integration checks, not only agreement with the former managed implementation or private helper-call counts.

## Blocked by

None (coordination map; implementation tickets have their own blockers).
