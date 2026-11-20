# Share the macro parser with the VS Code extension

TabTty and its VS Code extension use the same lexer and parser source for
`.tttym` files. Shipping the parser in the standalone VSIX keeps editing
available on PCs without TabTty and gives both consumers the same grammar
when the macro language changes.

During editing, an unfinished call such as `WaitPrompt(` still has a function
name and an argument position that the editor can use for signature help.
At launch, TabTty requires a complete, valid program before execution, as
specified in [the macro language decision](0004-defined-macro-language-with-csharp-style-control-flow.md).
