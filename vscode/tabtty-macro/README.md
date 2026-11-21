# TabTty Macro

Version 0.2.0 edits TabTty version 1 macros and shared constants. Pair the
VSIX with the TabTty ZIP from the same build. Both support named wait
alternatives, named recording options and two-digit hexadecimal escapes.

When updating older macros, give each `WaitAnyLine` or `WaitAnyPrompt`
candidate a name and branch on `AlternativeName`. After a `Connect` profile
path, name each recording option. Use `logCollision: "append"` to keep adding
to a fixed-name log, or choose `"error"` or `"overwrite"`. An ordinary `\x` escape consumes two
hexadecimal digits. Use `\u` for a four-digit UTF-16 code unit.

Install or update it in VS Code using **Extensions**, **...**, **Install from
VSIX**, then choose the downloaded VSIX. The VSIX contains its analysis
runtime and can be installed offline on Windows 11 x64.

Save the macro and run it from TabTty. A `.tttym` file can also be opened
using its Windows file association.
