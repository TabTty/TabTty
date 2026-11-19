# Published terminal architecture implementation tickets

Status: published on 2026-11-17 after the user approved the breakdown and explicitly requested issuance. The three architecture designs and identified custom-codec approaches are confirmed. Newly discovered custom codec processing still requires consultation, including additional ports of current C# behavior. Ticket-breakdown approval is not approval of an unreviewed codec method, and publication does not authorize implementation.

## Publication plan

Published [coordination map #16](https://github.com/TabTty/TabTty/issues/16) plus 23 implementation tickets in TabTty/TabTty, with ready-for-agent and the appropriate wayfinder label. Native sub-issue and blocked-by relationships represent the approved graph. Existing issues and parent maps remain unchanged. T identifiers below preserve the planning sequence and are not GitHub issue numbers; each item links to its published issue. Actual publication IDs and readback evidence are recorded in the publication artifacts beside this plan.

## Execution and verification

Capture the exact current workspace baseline when implementation is authorized; no existing commit is available to assume as its source. Native work uses a common integration branch and actual application/session/terminal paths, with scenario-level verification for each slice and whole-product green required at T22-T23. Do not create an additional runtime engine selector or extend the obsolete handoff format for new native state. The existing managed model may be frozen as an isolated test reference; independent expected results are also required.

T01 and T17 are the initial implementation frontier. T17-T20 do not wait for the full Rust replacement. T21 needs only the complete native operation/recording ownership available through T11 (which includes T02), plus single-tab closure. Its initial native checks can use existing owned snapshots; complete cross-command and observation coverage is required by T22.

The open [broadcast-movement issue](https://github.com/TabTty/TabTty/issues/14) reports its implementation complete with physical cross-DPI capture validation still outstanding. Do not duplicate that feature or treat its environment-dependent remaining check as a prerequisite for already available code. Record outstanding external coverage accurately during the final gate; do not close or modify that issue as part of this plan.

## Published slices

T07-T10 have their known custom-codec decisions recorded. Exact native-API compatibility and supported-platform checks are acceptance work during authorized implementation, not claimed completed by this plan. Apply the standing consultation requirement if any slice discovers a need for additional custom corrections or data.

Consultation update: existing Unicode table reuse for ISO-8859-10/11/14/16 was approved on 2026-11-16. The Japanese compatibility layer over a planned Microsoft CP932 base and shared recovery/exact-send policy were approved on 2026-11-17. Static assessment found no additional identity-specific mapping table/patch for the other 37 single-byte identities or CP936/949/950; plan native Microsoft conversion with the approved shared policy. Any additional CP932 or other mapping overrides require a demonstrated mismatch and separate consultation. The user subsequently approved publication of this breakdown on 2026-11-17.

1. **Establish the native-only UTF-8 integration baseline** ([#17](https://github.com/TabTty/TabTty/issues/17))  
   **Blocked by:** None (can start immediately)  
   **Delivers:** Capture a reproducible baseline and run the existing basic UTF-8 profile/session/display path using the candidate native integration.

2. **Share interpretation and admit complete native operations** ([#18](https://github.com/TabTty/TabTty/issues/18))  
   **Blocked by:** T01  
   **Delivers:** Use one interpretation definition for Estimate and Execute, with bounded admission, owned results and rejection before native mutation.

3. **Submit native UTF-8 input with committed local echo** ([#19](https://github.com/TabTty/TabTty/issues/19))  
   **Blocked by:** T02  
   **Delivers:** Send input and pasted text through strict native preparation, with local echo committed only at the accepted send boundary.

4. **Complete native normal-screen editing and rendition** ([#20](https://github.com/TabTty/TabTty/issues/20))  
   **Blocked by:** T02  
   **Delivers:** Render and copy contracted normal-screen editing, scrolling, tabs, character sets, colors and Unicode clusters from Rust.

5. **Run alternate-screen applications with native input modes** ([#21](https://github.com/TabTty/TabTty/issues/21))  
   **Blocked by:** T03, T04  
   **Delivers:** Run full-screen terminal interactions with alternate screens, resets, replies, application keys and mouse modes in Rust.

6. **Resize native normal and parked terminal output** ([#22](https://github.com/TabTty/TabTty/issues/22))  
   **Blocked by:** T05  
   **Delivers:** Resize and restore output without leaving Rust, preserving reflow, anchors, clusters and recording provenance.

7. **Use all single-byte encodings in native sessions** ([#23](https://github.com/TabTty/TabTty/issues/23))  
   **Blocked by:** T03  
   **Delivers:** Receive and strictly transmit the 41 single-byte encoding identities through actual profile/session paths.

8. **Use native Chinese and Korean multibyte encodings** ([#24](https://github.com/TabTty/TabTty/issues/24))  
   **Blocked by:** T03  
   **Delivers:** Use cp936, cp949 and cp950 in native receive/transmit with exact mappings, split-input and malformed-unit behavior.

9. **Use native CP932 and EUC-JP terminal encoding** ([#25](https://github.com/TabTty/TabTty/issues/25))  
   **Blocked by:** T03  
   **Delivers:** Use CP932 and EUC-JP with the accepted Japanese mappings, kana and strict transmit behavior.

10. **Use native stateful JIS terminal encoding** ([#26](https://github.com/TabTty/TabTty/issues/26))  
   **Blocked by:** T09  
   **Delivers:** Keep JIS receive/transmit and paced-paste closure state inside Rust, preserving source independence and exact byte order.

11. **Own complete native recording interval operations** ([#27](https://github.com/TabTty/TabTty/issues/27))  
   **Blocked by:** T02  
   **Delivers:** Start, pause, resume and abort recording through complete terminal operations and real files without caller-owned repair.

12. **Record edited native normal-screen Text across intervals** ([#28](https://github.com/TabTty/TabTty/issues/28))  
   **Blocked by:** T06, T11  
   **Delivers:** Write receive-only normal-screen Text with the accepted editing, line, retirement, tab and pause/resume rules.

13. **Record native alternate Text and canonical Terminal operations** ([#29](https://github.com/TabTty/TabTty/issues/29))  
   **Blocked by:** T05, T11  
   **Delivers:** Write alternate-screen Text and combined canonical Terminal output across screen, source and recording-interval transitions.

14. **Keep recording files and native state consistent through failures** ([#30](https://github.com/TabTty/TabTty/issues/30))  
   **Blocked by:** T05, T11  
   **Delivers:** Handle opening, retry, cancellation and write/flush failure across all format selections while preserving healthy native terminal work.

15. **Read retained native output through presentation and history APIs** ([#31](https://github.com/TabTty/TabTty/issues/31))  
   **Blocked by:** T06  
   **Delivers:** Render, copy, expose UIA and capture screen/history using owned Rust results, including retained disconnected output.

16. **Search and publish native terminal history consistently** ([#32](https://github.com/TabTty/TabTty/issues/32))  
   **Blocked by:** T15  
   **Delivers:** Use native bounded search and revision/retention evidence through the existing ordered AI publication path.

17. **Preserve transfer cleanup outcomes when a terminal closes** ([#33](https://github.com/TabTty/TabTty/issues/33))  
   **Blocked by:** None (can start immediately)  
   **Delivers:** Report transfer-window cleanup outcomes through the existing visible close paths while retaining actual provider ownership.

18. **Close a terminal tab through one owned operation** ([#34](https://github.com/TabTty/TabTty/issues/34))  
   **Blocked by:** T17  
   **Delivers:** Own a single tab's confirmation, termination, waiting, cleanup outcome and visual removal through one operation.

19. **Close windows by joining existing tab closures** ([#35](https://github.com/TabTty/TabTty/issues/35))  
   **Blocked by:** T18  
   **Delivers:** Join already closing tabs, confirm remaining targets and close the window without duplicate work or a second click.

20. **Close disconnected tabs and their transfer windows consistently** ([#36](https://github.com/TabTty/TabTty/issues/36))  
   **Blocked by:** T18  
   **Delivers:** Use the complete closure workflow for bulk and automatic removal, including associated transfer windows and required reporting.

21. **Retain native terminal state until final tab removal** ([#37](https://github.com/TabTty/TabTty/issues/37))  
   **Blocked by:** T11, T18  
   **Delivers:** Retain Rust output after disconnect and release its owner on final removal while independent results and I/O keep their lifetimes.

22. **Integrate the complete native-only terminal candidate** ([#38](https://github.com/TabTty/TabTty/issues/38))  
   **Blocked by:** T07, T08, T10, T12, T13, T14, T16, T19, T20, T21  
   **Delivers:** Integrate all 48 encodings and terminal/recording/observation/closure paths, remove production managed selection and verify native-only behavior.

23. **Validate native terminal deployment and acceptance workloads** ([#39](https://github.com/TabTty/TabTty/issues/39))  
   **Blocked by:** T22  
   **Delivers:** Verify deployment, actual integration, presentation and measured response/memory workloads for the complete candidate.

## Granularity and approval

Each feature ticket includes its own Rust/application integration, relevant visible or file/transport outcomes, independent expected results, and ownership/failure checks. The 41 single-byte encodings share a data-driven one-byte path; T07 can be split into ISO-8859 and the other families if a smaller assignment is preferred. Normal Text and alternate/Terminal recording are separate and do not block each other. Window joining and disconnected-tab closure can also proceed independently after the single-tab operation.

The user approved the proposed size and blocking graph by explicitly requesting publication on 2026-11-17. Remote readback verified all 24 titles, bodies, labels, open/unassigned states, 23 native sub-issue links, and 35 native dependency edges. The invoked to-tickets review/publication workflow is complete. Implementation remains a separately requested activity.



