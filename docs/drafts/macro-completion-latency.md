# VS Code completion latency with shared macro constants

[Issue #197](https://github.com/TabTty/TabTty/issues/197), measured on 2026-11-25.

Keep the two-pass analysis for now. With shared constants already open, the
median increase over a same-length macro without constants was about 1 ms
for a 32-declaration macro and 4 ms for a 512-declaration macro. The long
fixture showed a consistent extra cost, and the median remained under 19 ms
in every case on this warm workload. Repeat the measurement with realistic
large workspaces or interactive latency reports before changing analysis.

## Setup and measurement

- TabTty commit `74646840a9e3a4e8073a4f38facb5529076194fc`, extension `0.2.0`,
  Windows x64 self-contained Release analyzer built with SDK `10.0.404` and
  bundled runtime `10.0.15`. Use the extension and analyzer built from the
  same commit. [Packaging script](../../eng/Publish-MacroExtension.ps1)
- VS Code `1.146.0` (`69047191fc427f2f92828279aae1d90d8eeda7ab`), Electron
  `43.8.6`, extension-host Node `24.22.1`. Windows build `26300`, Intel Core
  i7-8565U, eight logical processors and approximately 16 GiB RAM. Use an
  Extension Development Host with isolated user data and third-party extensions
  disabled. The run used `--disable-workspace-trust` for the isolated host so
  the temporary local files took the trusted read path.
- Use UTF-8 without a BOM and LF endings. Start the macro with `#tttym 1`.
  Add the two directive lines below. For the comparison with no dependencies,
  replace each directive line with `//` and enough spaces to preserve its
  length. Append `long local0000 = 0;` through `long local0031 = 31;`, or
  through `long local0511 = 511;`, incrementing both numbers. End with
  `WriteLine("");` and LF. Invoke completion at column 1 of the `WriteLine`
  line (one-based line 36 or 516). The sources are 748 or 11,240 UTF-16 units,
  respectively, in all three dependency states.

```text
#constants Common "open.tttyc"
#constants Other_ "open.tttyc"
```

- For the open-buffer case, save `string DiskLabel = "saved";` followed by LF
  to `open.tttyc`. Open it and replace its text with `string BufferLabel = "unsaved";`
  followed by LF. Leave it unsaved. For the closed-file case, use `disk.tttyc`
  in both directives and keep that file closed with the saved text above. In
  a separate correctness check, request member completion after `Common.` and
  `Other_.`. Both returned `BufferLabel` from the unsaved buffer.
- Wait for background diagnostics to settle. From an extension-host test,
  time one awaited `vscode.executeCompletionItemProvider` call from just
  before invocation to the returned candidate list with `performance.now()`.
  Use one persistent analyzer. Rotate the order of the six cases each round,
  discard five warm-up rounds and keep the next 50. The 300 measured requests
  ran serially. Require `local0000` to appear in every result.
- Count requests by observing JSON lines sent to the analyzer's stdin from
  the test host. Match the request path and completion offset for each analyzed
  macro. Count the `readLocal` request separately and exclude background
  diagnostics. Verify that the second analysis has one constants snapshot even
  though two aliases refer to it. [Extension completion provider](../../vscode/tabtty-macro/extension.js)

## Results

Elapsed times are milliseconds. The p95 is the 48th ordered observation of 50.
The paired change is the median difference from the no-dependency request in
the same round. Every short request returned 80 candidates and every long
request returned 560.

| Declarations | Dependency state | Analysis passes | File-read requests | Median | p95 | Max | Paired change |
| ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 32 | None | 1 | 0 | 5.46 | 9.97 | 49.78 | — |
| 32 | Open, unsaved | 2 | 0 | 6.45 | 34.38 | 47.56 | +0.97 |
| 32 | Closed, saved | 2 | 1 | 6.55 | 14.16 | 27.53 | +1.15 |
| 512 | None | 1 | 0 | 14.11 | 28.04 | 56.72 | — |
| 512 | Open, unsaved | 2 | 0 | 16.98 | 37.54 | 67.69 | +3.81 |
| 512 | Closed, saved | 2 | 1 | 18.40 | 41.05 | 46.71 | +4.35 |

The open-buffer case was slower than its paired control in 33 of 50 short
rounds and 46 of 50 long rounds. That comparison includes the second analyzer
round trip, constants processing and snapshot checks. It estimates their combined
cost. The spread in maxima and p95s calls for more runs before drawing a tail
latency conclusion. This is one machine with warm processes, one tiny local
constants file and two aliases. It excludes UI painting, cold startup,
concurrent diagnostics and heavily loaded or remote workspaces.

If the cost warrants revisiting, consider reusing the parse result for the
macro's exact document version and doing semantic analysis with one captured
snapshot per normalized dependency. Bind every alias for that dependency to
the same snapshot. Before publishing candidates, recheck the source version,
workspace trust and the open document identity and version for every dependency.
Discard stale candidates and repeat with the updated unsaved text if the buffer
changes during the request. Preserve distinct files on case-sensitive paths.
Keep the two-pass implementation until a separate optimization is accepted.
See [#195](https://github.com/TabTty/TabTty/issues/195).
