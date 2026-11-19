# Whole-codebase ownership ticket breakdown

Status: approved and published on 2026-11-18. Implementation and prototyping have not started as part of this publication.

[Implementation plan #40](https://github.com/TabTty/TabTty/issues/40) has 27 child issues: 26 implementation slices and one cross-owner verification slice. All children carry `ready-for-agent` and `wayfinder:task`; the parent carries `ready-for-agent` and `wayfinder:map`. Read-back verification confirmed all bodies, labels, 27 native parent relationships and 34 native blocking relationships.

## Published tickets

| Slice | Candidate | Issue | Blocked by |
| --- | --- | --- | --- |
| 01 | 1 | [#41: Own recording pause, resume and retry through complete operations](https://github.com/TabTty/TabTty/issues/41) | None |
| 02 | 1 | [#42: Finalize recordings once across receive EOF and disconnect](https://github.com/TabTty/TabTty/issues/42) | [#41](https://github.com/TabTty/TabTty/issues/41) |
| 03 | 1 | [#43: Preserve recording work throughout close waiting and later loss decisions](https://github.com/TabTty/TabTty/issues/43) | [#42](https://github.com/TabTty/TabTty/issues/42) |
| 04 | 2 | [#44: Run Telnet sessions through complete terminal-payload transport operations](https://github.com/TabTty/TabTty/issues/44) | None |
| 05 | 2 | [#45: Run serial sessions through the terminal-payload transport seam](https://github.com/TabTty/TabTty/issues/45) | [#44](https://github.com/TabTty/TabTty/issues/44) |
| 06 | 3 | [#46: Save connection profiles through owned verified file operations](https://github.com/TabTty/TabTty/issues/46) | None |
| 07 | 3 | [#47: Save and recover application stores through verified operations](https://github.com/TabTty/TabTty/issues/47) | [#46](https://github.com/TabTty/TabTty/issues/46) |
| 08 | 3 | [#48: Publish encrypted vault revisions with verified preservation](https://github.com/TabTty/TabTty/issues/48) | [#46](https://github.com/TabTty/TabTty/issues/46) |
| 09 | 4 | [#49: Own shared vault unlock and credential handoff operations](https://github.com/TabTty/TabTty/issues/49) | None |
| 10 | 4 | [#50: Manage vault entries and key imports through owned operations](https://github.com/TabTty/TabTty/issues/50) | [#48](https://github.com/TabTty/TabTty/issues/48), [#49](https://github.com/TabTty/TabTty/issues/49) |
| 11 | 4 | [#51: Change the vault master password through one owned operation](https://github.com/TabTty/TabTty/issues/51) | [#48](https://github.com/TabTty/TabTty/issues/48), [#49](https://github.com/TabTty/TabTty/issues/49) |
| 12 | 4 | [#52: Back up and restore vaults through complete authenticated operations](https://github.com/TabTty/TabTty/issues/52) | [#48](https://github.com/TabTty/TabTty/issues/48), [#49](https://github.com/TabTty/TabTty/issues/49) |
| 13 | 4 | [#53: Create vaults only through explicit guarded initialization](https://github.com/TabTty/TabTty/issues/53) | [#48](https://github.com/TabTty/TabTty/issues/48), [#49](https://github.com/TabTty/TabTty/issues/49) |
| 14 | 4 | [#54: Archive the confirmed vault image without requiring unlock](https://github.com/TabTty/TabTty/issues/54) | [#46](https://github.com/TabTty/TabTty/issues/46), [#49](https://github.com/TabTty/TabTty/issues/49) |
| 15 | 4 | [#55: Coordinate vault selection and closing with owned operations](https://github.com/TabTty/TabTty/issues/55) | [#47](https://github.com/TabTty/TabTty/issues/47), [#49](https://github.com/TabTty/TabTty/issues/49) |
| 16 | 5 | [#56: Preserve committed SSH packets and truthful send progress](https://github.com/TabTty/TabTty/issues/56) | None |
| 17 | 5 | [#57: Run SFTP through shared SSH channel operations](https://github.com/TabTty/TabTty/issues/57) | [#56](https://github.com/TabTty/TabTty/issues/56) |
| 18 | 2 | [#58: Deliver SSH terminal startup and interaction through the shared transport and channel owners](https://github.com/TabTty/TabTty/issues/58) | [#44](https://github.com/TabTty/TabTty/issues/44), [#57](https://github.com/TabTty/TabTty/issues/57) |
| 19 | 5 | [#59: Preserve SFTP outcomes and bounded reopen through channel shutdown](https://github.com/TabTty/TabTty/issues/59) | [#57](https://github.com/TabTty/TabTty/issues/57) |
| 20 | 6 | [#60: Apply pinned grapheme and emoji rules to received terminal output](https://github.com/TabTty/TabTty/issues/60) | None |
| 21 | 6 | [#61: Preserve authoritative stored spans in rendering, UIA and history](https://github.com/TabTty/TabTty/issues/61) | [#60](https://github.com/TabTty/TabTty/issues/60) |
| 22 | 6 | [#62: Continue long received graphemes with bounded stored spelling](https://github.com/TabTty/TabTty/issues/62) | [#61](https://github.com/TabTty/TabTty/issues/61) |
| 23 | 6 | [#63: Analyze paste previews through owned Rust input plans](https://github.com/TabTty/TabTty/issues/63) | [#60](https://github.com/TabTty/TabTty/issues/60) |
| 24 | 6 | [#64: Send exact long-grapheme input with scalar-safe Stop](https://github.com/TabTty/TabTty/issues/64) | [#62](https://github.com/TabTty/TabTty/issues/62), [#63](https://github.com/TabTty/TabTty/issues/63) |
| 25 | 6 | [#65: Admit shared large pastes within existing application and input budgets](https://github.com/TabTty/TabTty/issues/65) | [#64](https://github.com/TabTty/TabTty/issues/64) |
| 26 | 6 | [#66: Configure large clipboard pastes with bounded full-text confirmation](https://github.com/TabTty/TabTty/issues/66) | [#65](https://github.com/TabTty/TabTty/issues/65) |
| 27 | Integration | [#67: Verify cross-owner close and actual resource release](https://github.com/TabTty/TabTty/issues/67) | [#43](https://github.com/TabTty/TabTty/issues/43), [#51](https://github.com/TabTty/TabTty/issues/51), [#55](https://github.com/TabTty/TabTty/issues/55), [#58](https://github.com/TabTty/TabTty/issues/58), [#59](https://github.com/TabTty/TabTty/issues/59), [#66](https://github.com/TabTty/TabTty/issues/66) |

Initial implementation frontier, after separate implementation authorization: [#41](https://github.com/TabTty/TabTty/issues/41), [#44](https://github.com/TabTty/TabTty/issues/44), [#46](https://github.com/TabTty/TabTty/issues/46), [#49](https://github.com/TabTty/TabTty/issues/49), [#56](https://github.com/TabTty/TabTty/issues/56), [#60](https://github.com/TabTty/TabTty/issues/60).

## Dependency choices

- Recording lifecycle and transport migration meet at existing session-owned lifecycle entry points, allowing their refactors to proceed independently. Ticket 27 verifies the combined behavior.
- SFTP exercises the shared SSH channel owner first; terminal migration depends on that owner and the transport seam. Serial migration and SFTP reopen completion are separate branches.
- Vault unlock is independent of file publication. Each mutation depends on the shared operations it actually uses; entry, master-password, restore, initialization and archive work are not serialized behind one another.
- Owned observations and immutable input analysis proceed separately after the shared Unicode rules. Exact long-input sending requires both bounded local-echo storage and the input plan.
- Cross-owner verification depends on the paths its scenarios exercise. Other leaves still require their own acceptance checks before the whole map is complete.

## Evidence

The [134-case coverage assignment](coverage.md), [publication journal](publication/journal.json) and [read-back verification](publication/verification.json) preserve the approved breakdown and its GitHub identifiers. The `issues` directory retains the pre-publication drafts; `publication` holds the exact submitted bodies and snapshots. Existing parent issues were not edited or closed.
