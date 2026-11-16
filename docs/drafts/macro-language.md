# Macro language and API, version 1: accepted contract

Drafted under accepted Q69 and confirmed in the consolidated final review on
2026-11-14, including routine language and API details. Read the
[behavior contract](macro-specification.md) and [validation](macro-validation.md).
No parser, evaluator, API implementation or executable fixture is supplied.
Examples below are specification examples, not claims of supported current code.

## File shape and checking

The first non-whitespace/non-comment item is exactly `#tttym 1`, followed by a
line ending or EOF. There is exactly one version directive. Permit `//` line
comments and non-nested `/* ... */` comments before/after it. Other directives,
missing/unsupported versions and malformed comments are source errors.
BOM handling is outside this grammar. Preserve literal string contents while
counting CRLF/CR/LF source positions as specified in the main contract.

A file contains top-level statements and named function declarations. Functions
are declared at file scope, can be called before their declaration, and are not
executed merely by being declared. Top-level statements execute in source order.
Every function and branch is statically checked, whether reached or not. Reject
duplicate function names, overloads, direct/indirect recursion (including unused
cycles), unsupported members and type errors before effects. Name lookup never
falls through to CLR types, reflection, assemblies or operating-system APIs.

Names are case-sensitive ASCII identifiers `[A-Za-z_][A-Za-z0-9_]*`. Comments and
strings support Unicode. Keywords, builtin type names and builtin function names
are reserved. This initial identifier rule is deliberately smaller than C#'s
full Unicode identifier grammar. No escaped `@identifier`, namespaces, using,
attributes, preprocessing, generics defined by users, classes, lambdas, async,
await, tasks, goto, switch, do/while, exceptions defined by users or finally.

## Grammar envelope

The following EBNF defines the admitted structure; named expression rules and
literal/member tables below complete it. Whitespace/comments separate tokens.
Each simple statement ends with `;`; control-flow bodies require `{ ... }`.

```text
file           = version, { function | statement } ;
function       = returnType, identifier, "(", [ parameters ], ")", block ;
parameters     = type, identifier, { ",", type, identifier } ;
block          = "{", { statement }, "}" ;
statement      = block | declaration, ";" | assignment, ";" | call, ";"
               | if | while | for | foreach | try
               | "break", ";" | "continue", ";"
               | "return", [ expression ], ";" ;
declaration    = ("var" | type), identifier, "=", expression ;
assignment     = lvalue, ("=" | "+=" | "-=" | "*=" | "/=" | "%="), expression
               | lvalue, ("++" | "--") ;
lvalue         = identifier | identifier, "[", expression, "]" ;
call           = functionIdentifier, "(", [ expression, { ",", expression } ], ")" ;
if             = "if", "(", expression, ")", block,
                 [ "else", (block | if) ] ;
while          = "while", "(", expression, ")", block ;
for            = "for", "(", [ declaration | assignment | call ], ";",
                 [ expression ], ";", [ assignment | call ], ")", block ;
foreach        = "foreach", "(", ("var" | type), identifier, "in",
                 expression, ")", block ;
try            = "try", block, "catch", "(", "MacroError", identifier, ")", block ;
returnType     = type | "void" ;
type           = "bool" | "long" | "double" | "string" | "List", "<", type, ">"
               | builtinValueType ;
```

`functionIdentifier` names a statically resolved builtin or file-scope helper;
functions are not first-class values. `expression` consists of literals,
variable/enum/metadata names, calls, parentheses, list literals and permitted
postfix member/index access, combined by the precedence rules below. Only a
local/parameter identifier or a direct index of such a list variable is an
lvalue. To change `Macro.Args` or a nested list by index, first bind that list to
a local alias; members themselves are read-only. `foreach` accepts `List<T>`
only, not a string; an explicit iterator type must accept T by the usual
assignment conversion, while var infers T.

`break`/`continue` target the innermost lexical loop and cannot cross a function
boundary. A `for` continue runs its iteration clause; a while continue rechecks
its condition. Empty for condition means true. Top-level `return;` completes
the run; top-level return with a value is invalid. A void function can use
`return;`; a non-void function must return an assignable value on all paths.
For definite-return checking, a loop alone is not assumed to terminate/return;
an `if/else` returns on all paths only when both branches do, and a try/catch
requires both blocks. This finite check avoids inheriting all C# flow analysis.

Every variable has an initializer. Variables have lexical block scope, cannot
refer to themselves in their initializer, and cannot be read before declaration.
Duplicate names or shadowing another visible local/parameter are source errors.
Parameters are local variables passed by value; a list value is a shared list
reference. Helpers do not implicitly capture top-level locals: pass them as
parameters. They can call builtins, which operate on the same run/session.
No `ref`, `out`, optional parameters, named arguments or user overloads initially.

## Types and expressions

`var` infers its type once from the initializer. The same assignment rules apply
to variable assignment, parameter passing, returns and list element writes.
There is no object/dynamic/null/nullable escape hatch. `void` is return-only.
Integers are signed 64-bit long; fractional/exponent literals are binary64 double.

| Rule | Result |
| --- | --- |
| `var n = 1; n = "done";` | Source type error |
| `var n = 1; n = 1.0;` | Source type error; double cannot implicitly narrow |
| `double n = 1;` | Implicit long-to-double conversion |
| `long n = (long)3.9;` | 3; explicit conversion checked at evaluation |
| `var xs = [1, 2];` | `List<long>` |
| `var xs = [1, 2.0];` | `List<double>` by numeric promotion |
| `var xs = [1, "x"];` | Source type error |
| `List<string> xs = [];` | Valid; target type supplies the missing element type |
| `var xs = [];` | Source type error |

Typed-list declarations can supply element context, but `var` uses only its
initializer. Context also comes from a known parameter/return type. There is no
conversion from an existing `List<long>` to `List<double>` or list covariance.
Homogeneous lists can contain any listed non-void value type; nesting is bounded.
List literals use `[a, b, c]` with an optional trailing comma and can be indexed.
Constructed generic types other than `List<T>` are not available.

Expression precedence, highest first:

1. Parentheses, calls, allowed property access, index access, list/literal values.
2. Unary `!`, `+`, `-`, and casts `(long)` / `(double)`.
3. `*`, `/`, `%`.
4. `+`, `-`.
5. `<`, `<=`, `>`, `>=`.
6. `==`, `!=`.
7. `&&`.
8. `||`.

Binary operators associate left-to-right. Evaluate operands and call arguments
left-to-right; `&&`/`||` short-circuit. Conditions and Boolean operators require
bool; no numeric/string truthiness or loose equality. Assignment, compound
assignment and postfix increment/decrement are statements/for clauses, not
values embedded in expressions. No prefix increment, bitwise/shift, ternary,
coalescing, pattern matching or assignment chains in version 1.

Numeric operators require numeric operands. `+` also supports string+string;
numeric/string mixing requires interpolation or explicit formatting. Equality
supports numeric promotion, bool/bool, ordinal string/string and the same enum
type. Lists and other builtin values have no implicit equality/to-string rule.
Numeric ordering is supported; use explicit string operations for string tests.
An assignment index is evaluated once; compound assignment checks both result
and assignability, without C#'s implicit narrowing. Thus `longValue += 0.5` is
a source error. All non-short-circuited operand side effects occur in order.

### Numeric details

Decimal integer literals use digits, optional digit separators between digits,
and optional `L`/`l`. Hex/binary literals and integer suffixes for other types are
not initially supported. A double literal has digits on both sides of a decimal
point or an exponent, optionally `D`/`d`; a digits-only `D` suffix also selects
double. Use `0.5`, not `.5`. Reject malformed separators, overflow and non-finite
literal values before execution. There are no NaN/Infinity literals.

Positive integer magnitude must fit long, except directly applying unary minus
to decimal magnitude 9223372036854775808 yields long.MinValue. Parenthesizing
that out-of-range positive literal does not make it valid. Unary negation of an
already evaluated long.MinValue is overflow. Arithmetic overflow is checked;
no unchecked context, saturation or wrapping.

Long/long division truncates toward zero. Remainder has the dividend's sign;
long.MinValue divided by -1 overflows, while its remainder by -1 is defined as
zero. Division/remainder by either signed zero double or zero long is an error.
Mixed arithmetic and comparison promote long to double, accepting rounding.
Long/long comparisons are exact; for example,
`9007199254740993 == 9007199254740992.0` is true after promotion, while comparing
those two integer literals as long is false. This follows accepted Q47's
precision tradeoff rather than introducing exact decimal arithmetic.

Every double result must be finite. Finite underflow to zero is permitted;
NaN/infinity results fail. `+0.0` and `-0.0` compare equal. Explicit double-to-long
conversion accepts only finite values in `-2^63 <= d < +2^63`, then truncates
toward zero. Do not use rounded `(double)long.MaxValue` as an inclusive bound.
Casts never parse strings or convert booleans. Literal validity is a source
check; well-typed division by zero, casts and parsing are runtime value errors
even if their operands happen to be constant. Optimizations preserve that rule.

### Strings

Support ordinary `"text"`, verbatim `@"text"`, interpolated `$"text"`, and
interpolated-verbatim `$@"text"` / `@$"text"`. Raw triple-quoted strings and char
literals are not supported. Ordinary strings cannot contain a physical newline.
Verbatim strings preserve physical CR/LF and other literal contents; `""` inside
one represents a quote. Backslash is ordinary in verbatim strings.

Ordinary escapes are `\0`, `\a`, `\b`, `\f`, `\n`, `\r`, `\t`, `\v`, `\\`,
`\'`, `\"`, `\x` with 1-4 hex digits, `\u` with exactly 4 and `\U` with exactly
8. `\U` must name a Unicode scalar. `\u` can produce a UTF-16 code unit, including
a surrogate; send/path/UI boundaries strictly reject unpaired surrogates rather
than silently replacing them. `\x` consumes as many as four following hex digits;
prefer `\u001B` when an ESC is followed by hex characters. Unknown escapes fail
source validation. Source decoding itself never accepts malformed Unicode bytes.

Interpolation uses `{expression}` and `{{`/`}}` for literal braces. Embedded
expressions obey the same type/member rules and evaluate left-to-right. Permit
string, bool, long, double, Instant and listed enum values. Use lowercase bool,
decimal long, round-trip double, ISO Instant and stable enum identifiers, all
independent of UI culture. No generic object/credential/list/error interpolation.
Alignment and `:format` interpolation syntax are deferred; use `FormatNumber`
or `FormatTime` explicitly inside an ordinary interpolation expression.

Strings are immutable UTF-16 sequences. Length, index, substring offsets and
match indices count UTF-16 code units, never display cells or grapheme clusters.
`text[index]` returns a one-code-unit string; callers must not assume it is a
complete emoji. All index/range failures are recoverable errors. String equality
and default searches are ordinal and do not normalize Unicode or trim values.
Explicit IgnoreCase string search uses ordinal ignore-case, distinct from regex
invariant-culture case matching. Time/number parsing never inherits UI culture.

### Lists and read-only API values

List variables and parameters share references. `var b = a; Add(b, x);` changes
the same list observed by `a`. `Clone(list)` creates a shallow independent list.
Index writes, Add, Insert, RemoveAt and Clear change a list revision. `foreach`
captures the list identity/revision, yields a read-only loop variable, and raises
`CollectionModified` before its next step if that list changed, including through
an alias/helper or element replacement. Mutating an element's separate nested
list does not mutate the outer list's revision. Bounds and ownership are charged.

Builtin value types are `Instant`, `CredentialRef`, `WaitPattern`, `WaitMatch`,
`CaptureGroup`, `DialogResult`, `MacroError`, `ErrorCode`, `CredentialKind`,
`DialogButtons` and `DialogButton`. They expose only
the listed immutable properties. Lists returned by result APIs are fresh bounded
mutable copies; changing them never changes the original result or runtime state.
`Macro.Args` is one run-local mutable list, initially copied from launch arguments.
User code cannot construct builtin records except through their named factories.

## Control and result types

| Type | Exposed properties/values |
| --- | --- |
| `Macro` metadata root | Reserved root, not a first-class/declarable type; read-only FullPath/FileName/BaseName/Extension/RunId strings and Args as `List<string>` |
| `Instant` | Opaque captured instant and offset; use time functions, no arbitrary members |
| `CredentialKind` | `Password`, `PrivateKey` |
| `CredentialRef` | Opaque identity; only credential-send/factory operations accept it |
| `WaitPattern` | Opaque validated literal/regex candidate; no exposed engine object |
| `WaitMatch` | `long AlternativeIndex`, `string Text`, `long Index`, `long Length`, `List<CaptureGroup> Groups` |
| `CaptureGroup` | `long Number`, `string Name`, `bool Success`, `string Value`, `long Index`, `long Length`, `List<string> Captures` |
| `DialogButtons` | `OK`, `OKCancel`, `YesNo`, `YesNoCancel` |
| `DialogButton` | `OK`, `Cancel`, `Yes`, `No` |
| `DialogResult` | `DialogButton Button`, `bool HasText`, `string Text` |
| `MacroError` | `ErrorCode Code`, `string Message`, `long Line`, `long Column` |

WaitMatch.Index/Length and successful group indices refer to the original
receive-line subject at match time under accepted Q72. AlternativeIndex is 0
for a single-pattern call. Regex Groups contains group 0 and numbered groups in
numeric order, with the .NET name (numeric string for unnamed groups). An
unmatched group has Success=false, Value="", Index=-1, Length=0 and no Captures;
a successful empty capture remains distinguishable. Value/Index/Length describe
the last capture; Captures contains that group's captured values in engine order.
A literal result supplies only group 0. These are snapshots, not live .NET objects.

DialogResult.Text access when HasText=false raises `NoTextValue`; accepted empty
text has HasText=true and Text="". Results from Cancel/X/Esc have HasText=false,
even if the input previously contained text. MacroError.Message is localized
non-secret explanatory text; scripts branch on Code, never Message. Source
positions refer to the failed operation, including a helper call's source file.

## Builtin signatures

Signatures use `T` only to describe builtin list operations and documented
overloads, not to grant user-defined generics. No unspecified members exist.
Arguments below are positional; names shown in signatures explain their roles.

### Session, paths, input, waits and window

| Signature | Behavior |
| --- | --- |
| `void Connect(string profilePath)` | One explicit attempt; contract readiness/history/logging rules |
| `void SetBaseDirectory(string path)` | Replace this run's relative-path base |
| `void ResetBaseDirectory()` | Restore captured source directory |
| `string BaseDirectory()` | Current absolute run-local base |
| `void Write(string text)` | Ordered text send, no implicit ending |
| `void WriteLine(string text)` | Append one captured Enter ending |
| `CredentialRef Credential(string vaultId, string entryId, CredentialKind kind)` | Validate canonical UUID identity/kind without unlocking or retrieving a secret |
| `void WritePassword()` / `void WritePassword(CredentialRef credential)` | Default profile reference or explicit password reference |
| `void WritePasswordLine()` / `void WritePasswordLine(CredentialRef credential)` | Same, with captured Enter ending |
| `void MarkReceiveBoundary()` | Advance match-start floor to admitted tail |
| `WaitPattern Literal(string text)` / `Literal(string text, bool ignoreCase)` | Nonempty literal candidate; default ordinal case-sensitive, optional ordinal ignore-case |
| `WaitPattern Regex(string pattern)` / `Regex(string pattern, bool ignoreCase)` | Compile/validate bounded pattern; default case-sensitive |
| `WaitMatch WaitLine(WaitPattern pattern)` / `WaitLine(WaitPattern pattern, long timeoutMs)` | Completed-line mode, indefinite or explicitly finite |
| `WaitMatch WaitPrompt(WaitPattern pattern)` / `WaitPrompt(WaitPattern pattern, long timeoutMs)` | Include unfinished line |
| `WaitMatch WaitAnyLine(List<WaitPattern> patterns)` / overload with `long timeoutMs` | Snapshot nonempty ordered candidates; completed-line mode |
| `WaitMatch WaitAnyPrompt(List<WaitPattern> patterns)` / overload with `long timeoutMs` | Snapshot candidates; prompt mode |
| `void Sleep(long milliseconds)` | Pause-aware local delay; 0 yields/checks cancellation |
| `void MinimizeWindow()` / `void RestoreWindow()` | Current run-tab host, including deferred-dialog rule |

There is no implicit conversion from string to WaitPattern. WaitAny copies its
candidate list at operation start. A no-timeout overload is indefinite; a finite
timeout must be nonnegative and within bounds. A zero timeout performs one search
of already-admitted eligible history without waiting for future input; it still
has finite regex evaluation work and remains interruptible. Connect retains its
profile/app connection timeouts; response wait settings do not override them.
Sleep cannot use a negative sentinel for infinity. Calls requiring a session
before Connect/readiness report `NotConnected`; MarkReceiveBoundary also requires
the run's ready session. Initial history can simply be used by the first wait.

The copy action emits an expression such as:

```csharp
Credential("11111111-1111-4111-8111-111111111111",
           "22222222-2222-4222-8222-222222222222", CredentialKind.Password)
```

These UUIDs are example identifiers, not references to a real vault. Canonical
input uses hyphenated UUID spelling; compare identity values, not case-sensitive
display text. No name lookup, vault-path argument or secret-value constructor.

### Dialog and ordinary data operations

| Signature | Behavior |
| --- | --- |
| `DialogResult Dialog(string title, string message, DialogButtons buttons)` | Decision without input |
| `DialogResult Dialog(string title, string message, DialogButtons buttons, bool inputEnabled, string initialText)` | Optional single-line text; initialText must be empty when disabled |
| `long Length(string text)` / `long Count(List<T> items)` | UTF-16 units / elements |
| `string Substring(string text, long start, long length)` | Exact bounded range |
| `bool Contains/StartsWith/EndsWith(string text, string value)` | Ordinal case-sensitive; each has a final `bool ignoreCase` overload |
| `long IndexOf(string text, string value)` | First ordinal position or -1; final ignoreCase overload |
| `string Replace(string text, string oldValue, string newValue)` | Ordinal literal replacement; oldValue must be nonempty |
| `List<string> Split(string text, string separator)` | Nonempty literal separator; retain empty items |
| `string Join(string separator, List<string> items)` | Join in order; empty list yields empty string |
| `string Trim(string text)` | Trim ASCII space, TAB, CR and LF at both ends only |
| `string ToUpperInvariant(string text)` / `ToLowerInvariant(string text)` | .NET 10 invariant case mapping; no locale dependence |
| `void Add(List<T> items, T value)` / `Insert(List<T> items, long index, T value)` | Mutate with assignment-type/budget checks |
| `void RemoveAt(List<T> items, long index)` / `Clear(List<T> items)` | Mutate; range checks |
| `List<T> Clone(List<T> items)` | Shallow copy |
| `long ParseLong(string text)` / `double ParseDouble(string text)` | Invariant explicit parsing; no whitespace/thousands/NaN/infinity |
| `string FormatNumber(long value, string format)` / double overload | Invariant supported format set |
| `Instant Now()` / `Instant UtcNow()` | Capture current local offset / zero offset |
| `string FormatTime(Instant value)` / `FormatTime(Instant value, string format)` | Default ISO or explicit supported format |
| `long UnixMilliseconds(Instant value)` | Milliseconds since Unix epoch, truncated toward negative infinity |
| `long ElapsedMilliseconds()` | Monotonic run elapsed time from admission, including pauses |

Contains/IndexOf etc permit an empty search string with ordinary start-position
semantics; this does not make an empty terminal-wait literal valid. Replace/Split
do not execute regex. Long parsing admits optional sign then decimal digits;
double parsing admits optional sign, digits, optional fractional part and optional
signed exponent; require digits in each present part. No literal suffixes or
digit separators in parsed input. Finite overflow fails; finite underflow may
produce zero. These parsing rules intentionally do not trim automatically.

FormatNumber admits `G` (general), `D`/`D1`-`D19` and `X`/`X1`-`X16` for long;
`G`, `R`, `F0`-`F15`, `E0`-`E15` for double. Formats are case-sensitive. Use
.NET 10 invariant semantics for those formats, including signed hexadecimal
representation; no currency/grouping/custom format language is exposed.
Default numeric interpolation uses decimal long or double `R`.

An Instant captures time and offset; later OS timezone/clock changes do not
rewrite a value already obtained. Default time text uses
`yyyy-MM-dd'T'HH:mm:ss.fffffffzzz`, including `+00:00` for UTC. This does not claim
100 ns hardware clock accuracy. Explicit time formats admit the tokens `yyyy`,
`MM`, `dd`, `HH`, `mm`, `ss`, `f` through seven `f` characters, and `zzz`, plus
single-quoted literal text and unquoted ASCII punctuation/space. Reject other
alphabetic format tokens; use .NET 10 invariant token semantics. No arbitrary
timezone database, date parsing or calendar arithmetic is initially exposed.

Dialog title/message are inert Unicode text; message may contain line breaks.
Title and initialText must not contain CR/LF/NUL or other C0 controls. Input is
single-line with the same control-character restriction; Tab navigates controls,
not text insertion. Reject an invalid initialText as InvalidArgument. Invalid
user paste leaves the existing field value and presents a localized validation
message; never silently remove characters and submit a different value. Preserve
ordinary keyboard navigation and bounds. No string is evaluated as markup.

## Error model

Use one `catch (MacroError e)` per try; nest try blocks if needed. No catch-all
CLR type, exception filters, throw/rethrow or finally in version 1. A catch runs
after the failed operation and does not roll back completed sends or other
effects. Stable ErrorCode members are:

| Family | Codes |
| --- | --- |
| Value/type-boundary operations | `InvalidArgument`, `NumericOverflow`, `DivideByZero`, `NumberFormat`, `Format`, `IndexOutOfRange`, `CollectionModified`, `NoTextValue` |
| Profile/connect preparation | `ProfileLoad`, `ConnectionAlreadyAttempted`, `ConnectionFailed`, `UserCancelled`, `NotConnected`, `Path` |
| Credential send | `MissingProfilePassword`, `CredentialUnavailable`, `CredentialKindMismatch`, `VaultMismatch`, `VaultStale`, `VaultUnlockCancelled` |
| Wait/regex | `WaitTimeout`, `EndOfInput`, `InvalidPattern`, `RegexTimeout`, `ZeroLengthMatch`, `CaptureLimit`, `MatchWorkLimit` |
| Operation admission | `Encoding`, `PayloadLimit`, `PatternLimit`, `OperationUnavailable` |

Codes that a constant literal can determine before execution (invalid source
escapes/literals, unsupported identifiers) are source errors, not MacroError.
Regex/Literal factory arguments are values: literal and dynamic patterns alike
are checked when the factory executes and report catchable InvalidPattern or
InvalidArgument. Do not pre-evaluate one literal factory and change catchability.
Ordinary well-typed value failures such as `1 / 0` are likewise catchable when
evaluated. Under accepted Q76, a send failure proven to have submitted no payload
uses OperationUnavailable unless a more specific listed error applies.

User Stop, disconnect, alternate-screen entry and an exhausted interpreter
hard-state budget terminate outside catch. Recording/history holds are coordinated
states, not exceptions. Partial/uncertain send failure follows accepted Q76;
there is no generic retrying catch added by the runtime. Error UI/Message must
not include managed secrets, sent text, argument values or capture contents.

## Readable example

The wait strings below are examples for a particular shell, not universal login
or success detection. The script can finish after submission while the healthy
terminal stays open, as required by the main contract.

```csharp
#tttym 1

string BuildCommand(long number)
{
    return $"echo round-{number}";
}

var number = 1;
if (Count(Macro.Args) > 0)
{
    number = ParseLong(Macro.Args[0]);
}

Connect(@"profiles\server.ttty");
var ready = WaitPrompt(Literal("$ "));
MarkReceiveBoundary();
WriteLine("sudo -v");

try
{
    var result = WaitAnyPrompt([Literal("[sudo] password:"), Literal("$ ")], 10000);
    if (result.AlternativeIndex == 0)
    {
        WritePasswordLine();
        WaitPrompt(Literal("$ "));
    }
}
catch (MacroError error)
{
    var answer = Dialog("Macro", "Continue after the operation failed?", DialogButtons.YesNo);
    if (answer.Button != DialogButton.Yes)
    {
        return;
    }
}

WriteLine(BuildCommand(number));
Sleep(100);
```

The example converts its argument to a number before command composition.
General ordinary strings require the author's own protocol/shell quoting;
the language does not guess a remote shell or claim injection-safe composition.
