# Define a macro language with C#-style control flow

TabTty macros use a defined language with familiar C#-style control flow and
an explicit set of supported operations. Macro-local Q10 accepts this boundary;
Q23 subsequently prefers types inferred from initialization expressions and
fixed for each variable. Dynamic reassignment is only a possible fallback if
concrete technical difficulties justify revisiting that preference, not the
selected default. Full C# compatibility and unrestricted .NET execution are
outside the language contract. Q62, accepted on 2026-11-14, selects a
TabTty-owned interpreter with whole-source checking before effects and explicit
pause/Stop checkpoints during computation and waits. Exact language and resource
rules are in the [specification draft](../drafts/macro-specification.md).

This keeps the automation scope explicit and lets pause/stop behavior form part
of the language contract. The trade-off is owning the supported language's
semantics, diagnostics and future extensions instead of inheriting the complete
C# language/library surface. The design targets trusted macros and does not
claim a security sandbox. Owning the interpreter also makes cancellation and
supported host operations explicit; simply hosting arbitrary C# with a
cancellation token would not establish those guarantees. This accepts the cost
of maintaining the limited evaluator and its tests. Roslyn scripting and
unrestricted CLR execution are not selected. The decision adds no implemented
dependency and does not authorize implementation or prototyping.
