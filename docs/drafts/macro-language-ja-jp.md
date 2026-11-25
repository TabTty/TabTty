# マクロ言語・API リファレンス（バージョン 1）

TabTty のマクロファイル（`.tttym`）で使える文法と関数をまとめました。初めてマクロを動かす方は、日本語 README にある、接続してプロンプトを待つ例から試せます。[マクロの起動方法と最小例](../../README-ja-jp.md#マクロ) · [英語原文](macro-language.md)

**目次**

- [ファイルの構成と静的検査](#file-shape-and-checking)・[文法](#grammar-envelope)・[マクロの共有定数](#shared-macro-constants)
- [型と式](#types-and-expressions)・[文字列](#strings)・[リスト](#lists-and-read-only-api-values)・[制御と結果の型](#control-and-result-types)
- [組み込み関数一覧](#builtin-signatures)・[接続・送信・受信待機](#session-paths-input-waits-and-window)・[ファイルパス](#file-path-parts)・[ダイアログとデータ操作](#dialog-and-ordinary-data-operations)
- [接続時の記録オプション](#connection-recording-options)・[送信後の応答を待つ](#starting-a-receive-window-at-submission)・[複数の応答を待つ](#named-wait-alternatives)
- [エラー処理](#error-model)・[応答に合わせて分岐する例](#readable-example)

本書はマクロ対応ビルド向けの仕様です。実装・検証済みの範囲は[実装状況](../specification/implementation-status-ja-jp.md)で確認できます。

言語と API の仕様は、決定 Q69 に基づいて作成され、2026 年 11 月 14 日の最終レビューで確定しました。Q78～Q88 でバージョン 1 に[共有定数](#shared-macro-constants)が加わりました。次のマクロ対応実装に求められる 6 件の構文上の要件は [#103](https://github.com/TabTty/TabTty/issues/103) と[構文の受け入れ条件](macro-validation.md#macro-syntax-acceptance)にあります。関連する仕様は[動作仕様](macro-specification.md)と[検証仕様](macro-validation.md)に記載しています。

<a id="file-shape-and-checking"></a>

## ファイルの構成と静的検査

空白とコメントを除き、ファイルの先頭には正確に `#tttym 1` と書き、直後に改行またはファイル末尾が続きます。このバージョン指示はファイルに一度だけ記述します。前後には `//` で始まる行コメントと、入れ子にできない `/* ... */` 形式のブロックコメントを書けます。バージョン指示の後、最初の文または関数の前には、任意の数の `#constants` 宣言を置けます。未知の指示や誤った位置にある指示、バージョンの欠落や未対応のバージョン、不正なコメントはソースエラーになります。

バイト順マーク（BOM）の扱いは、この文法とは別に定めています。文字列リテラルの内容はそのまま保持し、ソース上の位置を数える際は、動作仕様で定めたとおりに CRLF・CR・LF を扱います。

ファイルには、トップレベルの文と名前付き関数の宣言を記述します。関数はファイルスコープで宣言し、宣言より前の位置からでも呼び出せます。宣言しただけでは実行されません。トップレベルの文は記述順に実行されます。静的検査は、実行時に到達するかどうかにかかわらず、すべての関数と分岐を対象とします。関数名の重複、オーバーロード、直接・間接の再帰（呼び出されない循環も含む）、未対応のメンバー、型エラーがあれば、マクロが何らかの操作を行う前にエラーになります。名前解決の対象に、CLR の型、リフレクション、アセンブリ、OS の API は含まれません。

名前には、大文字と小文字を区別する ASCII 識別子 `[A-Za-z_][A-Za-z0-9_]*` を使います。コメントと文字列では Unicode を使えます。キーワード、組み込み型名、組み込み関数名は予約されています。バージョン 1 の識別子で許される文字は、C# の Unicode 識別子よりも限定されています。`@identifier` のようなエスケープ付き識別子、名前空間、using、属性、プリプロセッサ、ユーザー定義のジェネリック、クラス、ラムダ式、`async`、`await`、タスク、`goto`、`switch`、`do/while`、ユーザー定義の例外、`finally` は使えません。

<a id="grammar-envelope"></a>

## 文法

次の EBNF（拡張バッカス・ナウア記法）は、マクロに記述できる構造を示します。式の詳細な規則と、リテラルやメンバーの表は後の節にあります。トークンの区切りには空白やコメントを使えます。単純文は `;` で終え、制御構文の本体は `{ ... }` で囲みます。

```text
file           = version, { constantsFile }, { function | statement } ;
constantsFile  = "#constants", horizontalSpace, identifier, horizontalSpace,
                 pathLiteral, directiveEnd ;
function       = returnType, identifier, "(", [ parameters ], ")", block ;
parameters     = type, identifier, { ",", type, identifier } ;
block          = "{", { statement }, "}" ;
statement      = block | declaration, ";" | assignment, ";" | statementCall, ";"
               | if | while | for | foreach | try
               | "break", ";" | "continue", ";" | "throw", ";"
               | "return", [ expression ], ";" ;
declaration    = ("var" | type), identifier, "=", expression ;
assignment     = lvalue, ("=" | "+=" | "-=" | "*=" | "/=" | "%="), expression
               | lvalue, ("++" | "--") ;
lvalue         = identifier | indexedTarget | "(", lvalue, ")" ;
indexedTarget  = listReceiver, "[", expression, "]" ;
listReceiver   = identifier | "Macro", ".", "Args" | indexedTarget
               | "(", listReceiver, ")" ;
statementCall  = call | "(", statementCall, ")" ;
call           = functionIdentifier, "(", [ argument, { ",", argument } ], ")" ;
argument       = expression | identifier, ":", expression | alternatives ;
alternatives   = "[", identifier, ":", expression, { ",", identifier, ":", expression }, [ "," ], "]" ;
if             = "if", "(", expression, ")", block,
                 [ "else", (block | if) ] ;
while          = "while", "(", expression, ")", block ;
for            = "for", "(", [ declaration | assignment | statementCall ], ";",
                 [ expression ], ";", [ assignment | statementCall ], ")", block ;
foreach        = "foreach", "(", ("var" | type), identifier, "in",
                 expression, ")", block ;
try            = "try", block, "catch", "(", "MacroError", identifier, ")", block ;
returnType     = type | "void" ;
type           = "bool" | "long" | "double" | "string" | "List", "<", type, ">"
               | builtinValueType ;
```

`functionIdentifier` は、静的に解決できる組み込み関数か、ファイルスコープの補助関数を指します。関数そのものを値として扱うことはできません。`expression` は、リテラル、変数・列挙型・メタデータの名前、修飾された共有定数、関数呼び出し、括弧、リストリテラル、許可された後置のメンバー参照・添字アクセスを、後述の優先順位に従って組み合わせたものです。値を再代入できるのは書き込み可能な変数です。添字で指定した要素への書き込みには[リストに関する規則](#lists-and-read-only-api-values)が適用されます。

メンバー自体は読み取り専用です。`foreach` の対象にできるのは `List<T>` だけで、文字列は対象外です。ループ変数の型を明示する場合は、通常の代入規則で `T` の値を代入できる型を指定します。`var` を使うと `T` が推論されます。

`break` と `continue` は、それらを囲む最も内側のループに作用し、関数の境界を越えません。`for` の `continue` は更新部に進み、`while` の `continue` は条件を再評価します。`for` の条件を省略すると真として扱います。

トップレベルで `return;` を実行するとマクロが終了します。トップレベルでは値を返せません。void 関数では `return;` を使えます。戻り値のある関数では、到達可能なすべての終了経路で、戻り値の型に代入できる値を返すか、[捕捉したエラーを再スローする](#rethrowing-a-caught-error)必要があります。ある分岐を抜けると補助関数の末尾まで到達し得る場合、静的検査を通すには `if/else` の両方の分岐、または try/catch の両方のブロックを、return か有効な再スローで終えます。ループがあるだけでは、この条件を満たしません。

すべての変数には初期値が必要です。有効範囲は、宣言を囲むブロックによって決まります。初期化式でその変数自身を参照したり、宣言前に変数を読み取ったりすることはできません。名前の重複と、同じ名前を宣言して外側の参照可能なローカル変数や引数を隠すことはソースエラーです。関数の引数は値渡しのローカル変数として扱います。リストの場合は、同じリストへの参照が値として渡ります。補助関数からトップレベルのローカル変数を使うには、引数として渡してください。補助関数から組み込み関数を呼ぶと、同じマクロ実行・セッションに対して操作が行われます。補助関数の引数は位置で対応づけます。`ref`、`out`、省略可能な引数、ユーザー定義のオーバーロードは使えません。名前付き引数を使えるのは、明示的に列挙された組み込み関数のオプションだけです。候補を列挙するリテラルを渡せるのは、複数のパターンを待つ関数だけです。

<a id="parentheses-in-assignments-and-call-statements"></a>

### 代入文と関数呼び出し文の括弧

バージョン 1 では、代入先の変数、添字でアクセスするリスト変数、添字を含む代入先全体、または単独の文として使う関数呼び出し全体を括弧で囲めます。括弧を付けても、操作の内容、評価順序、型や書き込み可否の検査は変わりません。`for` ループの初期化部と更新部にも同じ規則が適用されます。

次の例は、TabTty の起動時検査と VS Code の両方に適用されます。`n` は書き込み可能な `long`、`items` は要素が 1 つ以上ある書き込み可能な `List<long>` とします。

| ソース | 結果 |
| --- | --- |
| `(n) = 2;`、`(items)[0]++;`、`(items[0]) += 1;` | いずれも代入または更新として有効です。添字は一度だけ評価されます。 |
| `(Sleep(0));` | 呼び出し全体が 1 つの文になります。`Sleep` は一度だけ呼ばれます。 |
| `for ((n) = 0; n < 2; (n)++) {}` | 初期化部と更新部のどちらの代入先も括弧で囲めます。ループの終了時、`n` は 2 です。 |
| `n` が 0 の状態から `for ((Sleep(0)); n < 2; (Sleep(0))) { n++; }` を実行 | `Sleep` は初期化時に 1 回、2 回の反復がそれぞれ終わった時点で 1 回ずつ呼ばれます。 |
| `(n + 1) = 2;`、`(1 + 2);` | 計算式を代入先にはできず、算術式だけを文にすることもできないため、どちらもエラーです。`n = (1 + 2) * 3;` のように右辺の算術式を括弧でまとめることはできます。 |
| `(Sleep)(0);`、`(n++);` | どちらもエラーです。前者は関数名を値として使おうとしています。後者は文としてのみ許される更新操作を式の中に置いています。 |

<a id="shared-macro-constants"></a>

## マクロの共有定数

定数ファイルを使うには、マクロ内でファイルごとに 1 行ずつ宣言します。この拡張に対応していない旧バージョン 1 の読み込み処理では、未知の指示として実行前にエラーになります。別名（エイリアス）とパスのリテラルを、スペースまたはタブで区切って同じ行に書きます。パスには通常の文字列リテラルか逐語的文字列リテラルを使います。たとえば、`#constants Site @"C:\Ops\site.tttyc"` では Windows パスのバックスラッシュがそのまま保持されます。リテラルの後にはスペースやタブ、任意の `//` コメントを書くことができ、改行またはファイル末尾で宣言を終えます。セミコロン、文字列補間、式で計算したパスはソースエラーです。指示は `#constants` という綴りなので、`constants` 自体は通常の識別子として使えます。

相対パスは、起動時にマクロのソースファイルがあるディレクトリを基準として解決されます。絶対パスは指定した場所を指します。各宣言の別名には、その起動時に読み込んだファイルの内容が対応づけられます。[起動と読み取り許可の規則](macro-specification.md#loading-launch-and-source-identity)を参照してください。

別名はファイル全体で有効です。別名と定数名には、マクロと同じ、大文字と小文字を区別する ASCII 識別子と予約名の規則が適用されます。ルート名 `Macro` も予約されています。別名の重複や、別名と同じ名前の補助関数、引数、ローカル変数の宣言はできません。トップレベルの文や任意の補助関数から `Alias.Name` と書くと、宣言されたスカラー値を参照できます。別名を使用できるのは、この修飾子としてだけです。異なる 2 つの別名がそれぞれ `TimeoutMs` というメンバーを持つことはできます。別名で修飾した定数に対する代入、複合代入、インクリメント、デクリメントはソースエラーです。定数の値で初期化したローカル変数には、通常の型の規則に従って再代入できます。

<a id="constants-file-format"></a>

### 定数ファイルの形式

各ファイルは、マクロのソースと同じ文字エンコーディング規則で個別にデコードします。BOM は長いものから照合します。UTF-8 は BOM の有無にかかわらず使用できます。UTF-16LE、UTF-16BE、UTF-32LE、UTF-32BE には BOM が必要です。どの方式も厳密にデコードし、BOM のないファイルには UTF-8 を適用します。例では拡張子 `.tttyc` を使いますが、定数ファイルかどうかはマクロ内の宣言で決まるため、別のファイル名も使えます。

定数ファイルには、`type Name = literal;` または `var Name = literal;` 形式の宣言を 0 個以上記述します。バージョン指示はありません。トークン間には空白や、マクロと同じ行コメント・ブロックコメントを置けます。定数名にはマクロと同じ、大文字と小文字を区別する規則が適用されます。同じ名前を重ねて宣言するとファイルエラーになります。

スカラー型は `string`、`bool`、`long`、`double` の 4 つです。初期化式には、通常の文字列リテラル、逐語的文字列リテラル、`true`、`false`、または先頭に符号を付けられる数値リテラルを書けます。リテラルの表記、値の範囲、エスケープの規則はマクロと同じです。文字列補間、数値の符号以外の演算子、名前による参照、関数呼び出し、実行可能な文は記述できません。`var` はリテラルから型を推論します。型を明示した場合は、`long` から `double` への拡大変換を含め、マクロの通常の代入規則が適用されます。たとえば、`var Timeout = 1000;` は `long` を、`double Rate = 1;` は `double` を定義します。符号付き 64 ビット整数の範囲を超える整数はファイルエラーです。定数ファイルには機密性のないデータを記述してください。

宣言された定数ファイルはすべて検査され、未使用のメンバーも対象になります。その後、マクロ内のすべての参照を解決して型を検査します。これには未使用の補助関数内の参照も含まれます。定数ファイルにエラーがあると、ファイル名と、特定できる場合は問題の位置が示されます。存在しないメンバーの参照や、その型の誤った使い方については、マクロ内の参照位置が示されます。こうしたエラーがあると起動処理が中断され、マクロは実行されません。読み込み時と検査時には[受け付けるサイズや個数などの上限](macro-validation.md#admission-and-accounting)が適用されます。

<a id="two-macros-using-one-file"></a>

### 2 つのマクロで 1 つの定数ファイルを使う例

2 つのマクロと同じディレクトリに `common.tttyc` を置きます。それぞれのマクロは別のプロファイルを使用し、タイムアウトの値を共有します。

```csharp
string Prompt = "Ready>";
string ReadyLine = "Service ready";
long TimeoutMs = 5000;
```

`inspect.tttym` は、補助関数から共有のプロンプト文字列を使います。

```csharp
#tttym 1
#constants Common "common.tttyc"

void AwaitPrompt()
{
    WaitPrompt(Literal(Common.Prompt), Common.TimeoutMs);
}

Connect("inspection.ttty");
AwaitPrompt();
```

`startup.tttym` は、確定した行を待つ際に共有のタイムアウト値を使います。

```csharp
#tttym 1
#constants Common "common.tttyc"

Connect("startup.ttty");
WaitLine(Literal(Common.ReadyLine), Common.TimeoutMs);
```

<a id="types-and-expressions"></a>

## 型と式

`var` は、宣言時に初期値から一度だけ型を推論します。変数への代入、引数の受け渡し、戻り値、リスト要素への書き込みには同じ代入規則が適用されます。object、dynamic、null、null 許容型を使ってこの規則を回避することはできません。`void` は戻り値の型にだけ指定できます。整数は符号付き 64 ビットの long 型、小数部または指数部を持つリテラルは binary64 の double 型です。

| 記述例 | 結果 |
| --- | --- |
| `var n = 1; n = "done";` | 実行前に型エラー |
| `var n = 1; n = 1.0;` | 実行前に型エラー。double から long への暗黙の縮小変換はできない |
| `double n = 1;` | long から double に暗黙に変換 |
| `long n = (long)3.9;` | 結果は 3。明示的な変換が可能かどうかは式の評価時に検査 |
| `var xs = [1, 2];` | `List<long>` |
| `var xs = [1, 2.0];` | 数値の型昇格により `List<double>` |
| `var xs = [1, "x"];` | 実行前に型エラー |
| `List<string> xs = [];` | 有効。代入先の型から空リストの要素型が決まる |
| `var xs = [];` | 実行前に型エラー |

リストの宣言で型を明示すると、その型からリストリテラルの要素型が決まります。`var` による推論で使えるのは初期値だけです。引数や戻り値にリストリテラルを使った場合も、対応するパラメーターや戻り値の型が要素型を決める手がかりになります。

既存の `List<long>` を `List<double>` に変換することはできず、リストの共変性もありません。要素型はリスト全体で共通です。このリファレンスに記載された void 以外の型を使えます。リストの入れ子の深さには上限があります。リストリテラルは `[a, b, c]` と書きます。末尾のカンマは任意で、リテラル自体に添字を付けることもできます。ジェネリック型として使えるのは `List<T>` のみです。

式の優先順位を高い順に示します。

1. 括弧、関数呼び出し、許可されたプロパティへのアクセス、添字アクセス、リストおよびリテラルの値。
2. 単項演算子 `!`、`+`、`-` とキャスト `(long)`、`(double)`。
3. `*`、`/`、`%`。
4. `+`、`-`。
5. `<`、`<=`、`>`、`>=`。
6. `==`、`!=`。
7. `&&`。
8. `||`。

二項演算子は左結合です。オペランドと関数の引数は左から右に評価します。`&&` と `||` は短絡評価します。条件式と論理演算子には bool が必要です。数値や文字列を暗黙に真偽値として扱うことや、緩やかな等値比較はできません。

代入、複合代入、後置インクリメント・デクリメントは、文または for の節として書きます。式の中でその値を使うことはできません。バージョン 1 は、前置インクリメント、ビット演算・シフト、三項演算子、null 合体演算子、パターンマッチング、連鎖代入には対応していません。

算術演算子のオペランドは数値です。`+` は文字列同士の連結にも使えます。数値と文字列を組み合わせるときは、文字列補間または明示的な書式指定を使います。等値比較は、型昇格を伴う数値同士、bool 同士、文字列同士の序数比較、同じ列挙型同士に対応します。リストなどの組み込み値に暗黙の等値判定や文字列化の規則はありません。大小比較は数値に使えます。文字列の判定には文字列操作を明示的に使います。添字付きの代入では添字を一度だけ評価します。複合代入では演算結果とその結果を代入できるかを検査し、C# のような暗黙の縮小変換は行いません。そのため `longValue += 0.5` は実行前にエラーになります。短絡評価で省略されたものを除き、各オペランドの副作用は評価順に生じます。

<a id="numeric-details"></a>

### 数値の詳細

10 進整数リテラルは数字で表し、数字の間には桁区切りのアンダースコアを、末尾には `L` または `l` を任意で付けられます。初期仕様では 16 進数・2 進数リテラルや、他の整数型を示す接尾辞は使えません。double リテラルは、小数点の両側に数字を書くか、指数部を付けて表します。末尾に `D` または `d` を付けることもできます。数字だけの場合も `D` 接尾辞を付ければ double になります。`0.5` と書いてください。`.5` は無効です。桁区切りの位置が不正なリテラル、オーバーフローするリテラル、有限値でないリテラルは実行前にエラーになります。NaN や Infinity を表すリテラルはありません。

正の整数リテラルは long の範囲に収まる必要があります。例外として、10 進の 9223372036854775808 に単項マイナスを直接付けた場合は long.MinValue を表します。マクロ本体でも共有定数ファイルでも、`-09_223_372_036_854_775_808L` と `-9223372036854775808` はこの同じ値です。先頭のゼロ、正しい位置の桁区切り、任意の接尾辞によって、10 進数としての大きさは変わりません。正のままでは範囲外になるリテラルを括弧で囲んでも、有効にはなりません。すでに評価された long.MinValue の符号を反転するとオーバーフローします。算術演算ではオーバーフローを検出します。検査を無効にする指定や、飽和演算、桁あふれ時の折り返しはありません。

マクロのパーサーと共有定数ファイルの読み取り処理では、それぞれの文法に従って符号と括弧の可否を判定します。大きさが 2^63 の整数トークンの解釈には共通の規則を使い、マイナスが直接付いているかどうかをその判定に渡します。マイナスと数字の間に空白やコメントがあっても、直接付いているとみなします。`-(9223372036854775808)` の場合、マクロ本体では最初の数字の位置で `NumericLiteral`、共有定数ファイルでは開き括弧の位置で `ConstantsLiteral` が報告されます。

long 同士の除算はゼロに向かって切り捨てます。剰余の符号は被除数に従います。long.MinValue を -1 で割るとオーバーフローしますが、-1 で割った剰余は 0 と定義されています。除算と剰余演算のいずれでも、除数が long の 0、または double の正もしくは負のゼロであればエラーです。long と double が混在する算術演算や比較では long を double に昇格させるため、丸めが生じることがあります。long 同士の比較は厳密です。たとえば `9007199254740993 == 9007199254740992.0` は昇格後の比較で真になりますが、同じ二つの数をともに long の整数リテラルで書いて比較すると偽になります。これは Q47 で採択された精度の取り決めで、厳密な 10 進演算は導入していません。

double の演算結果は常に有限値でなければなりません。アンダーフローでゼロになることは許容します。結果が NaN または無限大ならエラーです。`+0.0` と `-0.0` は等しいと判定します。double を明示的に long に変換するときは、有限で `-2^63 <= d < +2^63` の範囲にあることを確認してから、ゼロに向かって切り捨てます。丸めが生じた `(double)long.MaxValue` を「その値を含む上限」として使うことはできません。キャストで文字列を数値として解析したり、真偽値を変換したりすることはできません。リテラル自体の妥当性は実行前に検査します。型が正しい式でのゼロ除算、キャストの失敗、値の解析エラーは、オペランドが定数でも実行時の値エラーになります。最適化してもこの扱いは変わりません。

<a id="strings"></a>

### 文字列

通常の文字列 `"text"`、逐語的文字列 `@"text"`、補間文字列 `$"text"`、補間付きの逐語的文字列 `$@"text"` および `@$"text"` を使えます。三つの引用符で囲む生文字列と文字リテラルには対応していません。通常の文字列の途中に実際の改行を書くことはできません。逐語的文字列では CR、LF など、ソースに書いた内容がそのまま保持されます。引用符一つは `""` と書きます。バックスラッシュはそのままの文字として扱います。

通常の文字列で使えるエスケープは `\0`、`\a`、`\b`、`\f`、`\n`、`\r`、`\t`、`\v`、`\\`、`\'`、`\"` と、16 進数を使う 3 種類です。`\x` の後には ASCII の 16 進数字をちょうど 2 桁、`\u` には 4 桁、`\U` には 8 桁書きます。`\U` の値は Unicode スカラー値でなければなりません。`\u` ではサロゲートを含む UTF-16 コード単位を指定できます。データの送信、パスの使用、UI への受け渡しの時点で対になっていないサロゲートがあると、置換せずエラーになります。

2 桁のエスケープが終わった直後から通常の文字として読みます。`"\x1BD"` は U+001B と D、`"\x01BD"` は U+0001 と B と D、`"\u01BD"` は U+01BD を表します。A～F は大文字・小文字のどちらも使えます。必要な 2 桁が欠けている、1 桁しかない、またはどちらかが 16 進数字でない場合は、TabTty と VS Code の両方がバックスラッシュの位置にソースエラーを報告します。この規則は、通常の文字列、通常形式の補間文字列のテキスト部分、ディレクティブ内で引用符で囲むパス、共有定数に適用します。逐語的文字列 `@"\x1BD"` では 5 文字すべてがそのまま残ります。未知のエスケープは実行前の検査でエラーになります。ソースのデコード時に不正な Unicode バイト列を受け入れることもありません。

文字列補間は `{expression}` と書きます。波括弧そのものは `{{` と `}}` で表します。埋め込んだ式にも通常と同じ型やメンバーの規則が適用され、左から右に評価されます。補間できるのは string、bool、long、double、Instant、およびこのリファレンスに記載された列挙型の値です。bool は小文字、long は 10 進数、double は元の値に戻せるラウンドトリップ形式、Instant は ISO 形式、列挙値は固定の識別子になります。いずれも UI の言語・地域設定に依存しません。任意のオブジェクト、認証情報、リスト、エラーを補間することはできません。

補間内の位置揃えと `:format` 書式指定は将来の拡張です。書式を指定するには、補間式の中で `FormatNumber` または `FormatTime` を明示的に呼び出します。

字句解析器は、補間文字列を種類別の部分に分け、順序を保ってパーサーに渡します。各部分はリテラルのテキストまたは埋め込み式のトークン列です。埋め込み式は元の UTF-16 ソース位置を保持します。構文の複雑さは外側の構文と同じ上限に算入します。`$"{a}{b}"` の連続する式の間など、空のテキスト部分も保持し、その構文ノードを同じ上限に数えます。補間式の型検査と評価にも、これらの部分がソース順に渡されます。

文字列は変更できない UTF-16 コード単位の列です。長さ、添字、部分文字列のオフセット、一致位置は UTF-16 コード単位で数えます。画面上のセル数や書記素クラスタ数は使いません。`text[index]` が返す文字列の長さは 1 コード単位で、絵文字全体を取り出せるとは限りません。添字や範囲が無効な場合のエラーはすべて捕捉可能です。文字列の等値判定と既定の検索は序数比較で、Unicode 正規化や前後の空白の除去は行いません。文字列検索の IgnoreCase を明示すると、大文字・小文字を区別しない序数比較になります。これは、正規表現がカルチャに依存しない設定で行う大文字・小文字の照合とは異なります。日時や数値の解析も UI の言語・地域設定に依存しません。

<a id="lists-and-read-only-api-values"></a>

### リストと読み取り専用の API 値

リスト型の変数やパラメーターは、同じリストへの参照を共有します。`var b = a; Add(b, x);` とすると、`a` から見えるリストにも要素が追加されます。`Clone(list)` は浅いコピーを作ります。コピー先のリストは独立しています。添字による書き込み、Add、Insert、RemoveAt、Clear を行うとリストのリビジョンが変わります。`foreach` は開始時にリストの識別情報とリビジョンを記録し、ループ変数を読み取り専用にします。リストが変更されると、次の反復に進む前に `CollectionModified` を発生させます。別の変数や補助関数を経由した変更や、要素の置き換えも該当します。要素が別のリストを参照している場合、その内側のリストの変更で外側のリビジョンは変わりません。リストの使用量と所有関係は、リソース上限の計算に含めます。

リストが内容、リビジョン、確保済みの容量を管理します。Add と Insert は空き領域を先に使います。4 要素分を確保して 3 要素を入れた整数リストでは、既存の 96 バイトの予約だけで 4 要素目を追加できます。領域を拡張するためのメモリが足りない場合は、内容、リビジョン、予約を変更する前に `MemoryLimit` で実行を終了します。65,536 要素の上限には、引き続き捕捉可能な `PayloadLimit` を適用します。失敗した変更ではリビジョンを保ちます。[#193](https://github.com/TabTty/TabTty/issues/193)

組み込みの値型は `Instant`、`WaitPattern`、`WaitMatch`、`CaptureGroup`、`DialogResult`、`MacroError`、`ErrorCode`、`DialogButtons`、`DialogButton` です。公開するプロパティは下表に記すものだけで、読み取り専用です。結果を返す API から得るリストは、サイズに上限がある新しい可変コピーです。コピーを変更しても、元の結果や実行状態は変わりません。`Macro.Args` は、起動時の引数をコピーした、その実行専用の可変リストです。ユーザーコードが組み込みのレコード値を作成できるのは、所定の生成関数を通じてのみです。

添字付きの代入先には、リスト変数、リスト型のパラメーター、または `Macro.Args` を起点にし、要素をたどる添字を任意の段数だけ続けられます。起点と各添字は左から右に一度ずつ評価します。各添字の角括弧の位置で、次の要素に進む前に添字を検査し、最後に到達したリストとその要素位置を保持します。

複合代入では、右辺を評価する前に元の要素を一度だけ読み取ります。右辺を一度だけ評価して代入時の型変換を適用し、一度だけ書き込みます。右辺の評価でリストの長さが変化した場合、保持していた要素位置を再検査してから書き込みます。添字・型変換・算術演算のいずれかでエラーになった場合は書き込みを行いません。それまでの評価で生じた副作用は取り消しません。

`List<List<long>> rows = [[1, 2]]; var row = rows[0];` の後で `rows[0][1] = 4; rows[0][1] += 3;` を実行すると、`row[1]` の値は 7 になります。外側の添字が無効なら、内側の添字や右辺を評価する前に最初の `[` でエラーになり、内側の添字が無効なら二つ目の `[` でエラーになります。起動時に渡された引数は、その実行内で `Macro.Args[0] = "changed";` のように置き換えられます。`Clone(row)[0]` や `match.Groups[0].Captures[0]` など、一時値を経由した直接の書き込みは、TabTty と VS Code の両方の検査でエラーになります。キャプチャしたテキストを編集するには、`var copy = match.Groups[0].Captures;` のように変数に格納してから `copy[0]` に書き込みます。変更されるのはこのコピーだけです。リストを経由してアクセスしても、文字列自体は変更できません。

<a id="control-and-result-types"></a>

## 制御と結果の型

| 型 | 使用できるプロパティ・値 |
| --- | --- |
| メタデータの起点 `Macro` | 予約済みの起点。値として受け渡したり、変数の型として宣言したりすることはできない。読み取り専用の文字列 FullPath、FileName、BaseName、RunId と、`List<string>` 型の Args を持つ。BaseName の規則は[マクロの読み込みと識別](macro-specification.md#loading-launch-and-source-identity)を参照。 |
| `Instant` | 取得した時点の日時とオフセットを保持する。内部構造は非公開。操作には日時関数を使う。 |
| `WaitPattern` | 検証済みのリテラルまたは正規表現の待機候補。内部構造および照合エンジンのオブジェクトは非公開。 |
| `WaitMatch` | `string AlternativeName`、`string Text`、`long Index`、`long Length`、`List<CaptureGroup> Groups` |
| `CaptureGroup` | `long Number`、`string Name`、`bool Success`、`string Value`、`long Index`、`long Length`、`List<string> Captures` |
| `DialogButtons` | `OK`、`OKCancel`、`YesNo`、`YesNoCancel` |
| `DialogButton` | `OK`、`Cancel`、`Yes`、`No` |
| `DialogResult` | `DialogButton Button`、`bool HasText`、`string Text` |
| `MacroError` | `ErrorCode Code`、`string Message`、`long Line`、`long Column` |

Q72 の決定に従い、WaitMatch.Index と Length、および一致したグループの位置は、照合時の元の受信行を基準とします。AlternativeName は、候補が複数ある待機では採用された候補のラベル、単一パターンの待機では空文字列です。

正規表現の Groups にはグループ 0 と番号付きの各グループが番号順に入り、それぞれ .NET のグループ名を持ちます。名前を指定しなかったグループの名前は番号の文字列表現です。一致しなかったグループでは Success=false、Value=""、Index=-1、Length=0 で、Captures は空です。そのため、空文字列に一致した場合と区別できます。Value、Index、Length は最後のキャプチャを表し、Captures はそのグループがキャプチャした値をエンジンの順序で保持します。リテラルの一致結果に含まれるのはグループ 0 だけです。これらは照合時点のスナップショットで、実行中の .NET オブジェクトは公開しません。

HasText=false のときに DialogResult.Text を読むと `NoTextValue` が発生します。空の入力を確定した場合は HasText=true、Text="" になります。キャンセルボタン、閉じるボタン（X）、Esc で閉じた場合、それまでにテキストを入力していても HasText=false です。MacroError.Message は表示言語に応じた説明文で、秘密の値は含みません。スクリプトの条件分岐には必ず Code を使います。補助関数の呼び出しを含め、ソース位置は失敗した操作が書かれたソースファイル上の位置を示します。

<a id="source-positions-for-member-and-index-access"></a>

### メンバー・添字アクセスのソース位置

共通パーサーは、各メンバー名の開始位置と終了位置、および各添字の開き角括弧の位置を保持します。未知のメンバーがある場合、TabTty の起動時の診断はメンバー名の先頭文字を指し、VS Code は名前全体に下線を引きます。実行時にメンバーの読み取りで失敗した場合も、同じ先頭文字を指します。アクセスが連続するときは個々のアクセスがそれぞれ位置を持ち、複数の行にまたがっても区別できます。

添字が範囲外の場合、読み取り・書き込みのどちらでも、失敗したアクセスの開き角括弧 `[` を指します。アクセス対象や添字の式自体を評価する途中で失敗した場合は、そのエラーを発生させた演算の位置を保持します。同じ実行時エラーについて、捕捉したときの `MacroError.Line`、`MacroError.Column` と、捕捉しなかったときに表示される位置には、共通の[ソース座標の規則](macro-specification.md#loading-launch-and-source-identity)を適用します。

```text
#tttym 1
var x = Macro
    .Args
    .Wrong;
```

| 確認例 | 診断が指す位置 |
| --- | --- |
| 上のマクロを VS Code と起動時の検査にかける。 | VS Code は `Wrong` の 5 文字すべてに下線を引く。TabTty は `W` のある 4 行 6 列を報告する。 |
| テキストを持たないダイアログの結果から `result.Text` を読む。 | `NoTextValue` は `Text` の `T` を指す。catch で捕捉した場合と、捕捉せずにエラーを表示した場合の両方でこの位置になる。 |
| `rows[1 / divisor]` を読み、同じ式をリストの代入先にしてもう一度試す。 | 除数が 0 なら `/` を指す。除算が成功しても、その結果がリストの範囲外なら `[` を指す。角括弧と除算を別々の行に移しても、読み取り・書き込みの両方でこの違いが保たれる。 |

<a id="builtin-signatures"></a>

## 組み込み関数一覧

シグネチャにある `T` は、組み込みのリスト操作と、ここに記載するオーバーロードの説明にだけ使います。ユーザー定義のジェネリックは使えません。メンバーは明記されているものに限ります。引数は位置で指定します。ただし、以下で名前付きと明記するオプションは名前で指定します。

`Connect` と `WriteLine` の組み込み関数定義には、各名前付きオプションの名前、型、対応するパラメーターをまとめて保持します。パーサーとエディターは、オプションを入力している途中でもこの定義を使います。指定された引数式はソースに書かれた順序で保持し、それぞれを対応するパラメーターと結び付けます。これにより、検査器は期待する型を決定でき、評価器は各式を一度だけ評価して値を正しいパラメーターに渡せます。

<a id="session-paths-input-waits-and-window"></a>

### セッション、パス、送信、受信待機、ウィンドウ

| シグネチャ | 動作 |
| --- | --- |
| `void Connect(string profilePath)`。省略可能な文字列の名前付き引数は `logDirectory`、`logFilenameTemplate`、`logCollision` | 明示的な接続を 1 回試みます。[接続時の記録オプション](#connection-recording-options)を参照してください。 |
| `void SetBaseDirectory(string path)` | 今回のマクロ実行で相対パスの基準となるディレクトリを変更します。 |
| `void ResetBaseDirectory()` | 基準を起動時に取得したソースファイルのディレクトリに戻します。 |
| `string BaseDirectory()` | 今回の実行で現在使っている基準ディレクトリを絶対パスで返します。 |
| `void Write(string text)` | 呼び出し順にテキストを送信します。末尾に改行は追加しません。 |
| `void WriteLine(string text)`。省略可能な名前付き引数は `markReceiveBoundary`（bool） | 接続時に取得した Enter キーの改行設定に従い、末尾に改行を 1 回分追加して送信します。次の待機の検索対象を、今回の送信時点からの受信内容に限定できます。 |
| `void WritePassword()` / `void WritePassword(string name)` | 引数を省略すると、接続プロファイルの読み込み時に取得した項目名で、vault のパスワードを送信します。引数で vault の項目名を直接指定することもできます。 |
| `void WritePasswordLine()` / `void WritePasswordLine(string name)` | 同じパスワードの末尾に、接続時に取得した Enter キーの改行設定に従って改行を追加し、送信します。 |
| `void MarkReceiveBoundary()` | 一致の開始位置の下限を、すでに取り込んだ受信データの末尾まで進めます。 |
| `WaitPattern Literal(string text)` / `Literal(string text, bool ignoreCase)` | 空でない文字列をそのまま照合する候補を作ります。序数比較を使い、既定では大文字・小文字を区別します。引数で区別しない設定にできます。 |
| `WaitPattern Regex(string pattern)` / `Regex(string pattern, bool ignoreCase)` | 上限の適用される正規表現パターンをコンパイルして検証します。既定では大文字・小文字を区別します。 |
| `WaitMatch WaitLine(WaitPattern pattern)` / `WaitLine(WaitPattern pattern, long timeoutMs)` | 行末が確定した行で一致を待ちます。待機時間は無期限か、明示したタイムアウトまでです。 |
| `WaitMatch WaitPrompt(WaitPattern pattern)` / `WaitPrompt(WaitPattern pattern, long timeoutMs)` | 行末がまだ確定していない行も含めて一致を待ちます。 |
| `WaitAnyLine([name: pattern, ...])` / `long timeoutMs` を取るオーバーロード | 行末が確定した行で、採用された名前付き候補の `WaitMatch` を返します。 |
| `WaitAnyPrompt([name: pattern, ...])` / `long timeoutMs` を取るオーバーロード | 採用された候補の `WaitMatch` を返します。行末がまだ確定していない現在の行も対象です。 |
| `void Sleep(long milliseconds)` | マクロの一時停止を考慮してローカルで待機します。0 を指定すると、実行権をいったん譲ってキャンセルの有無を確認します。 |
| `void MinimizeWindow()` / `void RestoreWindow()` | 今回の実行タブを収容するウィンドウを最小化／復元します。ダイアログ表示を保留する規則も適用されます。 |

文字列を `WaitPattern` に暗黙変換することはできません。複数パターンを待つ関数は、呼び出し時点でラベルと評価済みの候補を確定します。タイムアウト引数を省略すると無期限に待機します。有限のタイムアウトには、上限以内の非負の値を指定します。0 を指定すると、すでに取り込んだ履歴のうち対象になる部分を 1 回だけ検索し、今後の入力は待ちません。この場合も正規表現の評価には上限があり、検索中の割り込みも可能です。`Connect` にはプロファイルとアプリの接続タイムアウトが適用されます。応答待ちの設定は接続タイムアウトを変更しません。`Sleep` に負の値を指定して無期限に待つことはできません。

セッションを必要とする関数を `Connect` の前やセッションが使用可能になる前に呼び出すと、`NotConnected` が発生します。`MarkReceiveBoundary` にも、この実行で使用可能になったセッションが必要です。最初の待機では、開始時の受信履歴をそのまま利用できます。

vault のコピー操作で得られる文字列リテラルは、次のように引数に渡せます。

```csharp
WritePassword("Production password");
```

vault 項目名は、大文字・小文字も含めて正確に指定します。[vault の形式](../specification/vault-format-ja-jp.md#暗号化ペイロードのスキーマと上限)

<a id="starting-a-receive-window-at-submission"></a>

### 送信時点を受信待機の起点にする

`WriteLine("status", markReceiveBoundary: true); WaitLine(Literal("OK"), 2000);` は、送信前に届いていてまだ検索で消費されていない `OK` を無視します。送信準備が終わり、データを送信処理に渡す直前に、そのマクロが受信データを観測するのと同じ順序で、すでに取り込んだデータの末尾の位置を記録します。送信が成功すると、その位置が一致の開始位置の下限として確定します。送信中に新たに到着して取り込まれた同じ内容の行は、待機を後から開始しても対象になります。

この境界が示すのはローカルで観測した順序です。リモート側のどの操作が応答を生じさせたかは判定できません。

`markReceiveBoundary` を省略するか `false` にすると、待機のカーソルは移動しません。`Connect` 後に通常の待機を最初に呼び出すと、起動時の履歴も検索します。開始位置の下限は、確定行、プロンプト、名前付きの複数候補のすべての待機に適用されます。タイムアウトが無期限、有限、0 のいずれでも、通常の照合規則に従います。

データを一切送信処理に渡していないと確認できた場合は、仮の境界を破棄し、既定の回復可能な送信エラーを発生させます。直前の待機カーソルは通常の履歴保持規則の範囲で維持されます。一部だけを渡した場合や、どこまで渡ったか不明な場合は、マクロの実行を終了します。その後の待機や自動再送は行いません。

セッションが正常なら手動操作を続けられます。正規表現で前後の文脈を参照できるよう、元の行は保持します。

<a id="connection-recording-options"></a>

### 接続時の記録オプション

各行は、それぞれ独立した接続の例です。

```csharp
Connect("lab.ttty");
Connect("lab.ttty", logCollision: "error");
Connect("lab.ttty", logDirectory: @"C:\Logs");
Connect("lab.ttty", logFilenameTemplate: "lab-<serial:1,3>");
Connect("lab.ttty", logFilenameTemplate: "latest", logCollision: "overwrite");
Connect("lab.ttty", logCollision: "append", logDirectory: @"C:\Logs", logFilenameTemplate: "lab-<datetime:yyyy-MM-dd>");
```

必須のプロファイルパスに続けて、各オプションを任意の順序で 1 回ずつ指定できます。オプションはすべて省略可能です。各値は記述順に一度だけ評価します。ディレクトリやファイル名テンプレートを省略するか、空文字列または空白だけの文字列を指定すると、その項目には記録仕様で定めた設定を引き継ぎます。空白以外を含む値を渡せば、プロファイルの値と同じ文字列でも明示指定として扱います。ログ形式には、引き続きプロファイルの設定を使います。

| ディレクトリとファイル名テンプレートの指定 | `logCollision` を省略 | `logCollision` を指定 |
| --- | --- | --- |
| 両方の値を継承 | プロファイルのファイル名衝突時の処理を使います。既定値は `error` です。 | 指定値を使います。 |
| ディレクトリまたはテンプレートを明示指定 | `error` を使います。 | 指定値を使います。 |

アプリのログ命名設定から使われる既定値は、ディレクトリまたはテンプレートだけです。指定のファイルがすでに存在するときは、`"error"` は記録開始時にエラーとなり、`"overwrite"` はファイルの内容を置き換え、`"append"` はファイルの末尾に追記します。ファイルが存在しなければ、どの指定でも新しく作成します。`<serial:start,width>` を含むテンプレートでは、どの指定でも次の空いている連番を割り当てます。書き込み中のファイル、複数のログ形式をまとめて記録する場合、再開、再試行の扱いは[記録仕様](receive-logging-issue.md#log-filename-template-redesign)に従います。

プロファイルの読み取り、接続、記録の前に、ソース全体を検査します。TabTty と VS Code はどちらも、未知のオプションはその名前の位置、重複は 2 回目の名前の位置、型違いは指定した値の位置に診断を表示します。以前の真偽値オプション `allowLogOverwrite` は未知の名前になります。VS Code は入力中に、まだ使っていないオプション名とその型を候補として表示します。

`Connect` が実行時に `logCollision` として受け付ける文字列は `"error"`、`"overwrite"`、`"append"` の 3 つだけです。文字列リテラルにも、計算して得た文字列にも同じ検査を行います。空文字列、空白だけの文字列、大文字・小文字が異なる文字列、前後に空白を含む文字列など、それ以外の値は、記録ファイルを開いたり通信を開始したりする前に `InvalidArgument` を発生させます。

<a id="named-wait-alternatives"></a>

### 名前付きの待機候補

`WaitAnyLine` と `WaitAnyPrompt` の第 1 引数には、`name: WaitPattern` の組を 1 つ以上、順序を付けて並べたリテラルを渡します。名前には予約されていない ASCII 識別子を使います。同じ呼び出し内では、大文字・小文字を区別する序数比較で一意でなければなりません。名前にはソースに書いた綴りをそのまま使い、ローカル変数や UI 言語は影響しません。TabTty と VS Code の検査器はどちらも、ラベルの欠落や不正をラベルが必要な位置に示します。名前が重複すると、2 回目の名前の位置に `DuplicateAlternative` を報告します。パターンは呼び出し時にソースに書かれた順で一度ずつ評価します。ラベルはパターンの検証に影響せず、入力も消費しません。

```csharp
var result = WaitAnyPrompt([password: Literal("Password:"), ready: Literal("$ ")], 10000);
if (result.AlternativeName == "password") { WritePasswordLine(); }
else if (result.AlternativeName == "ready") { WriteLine("status"); }
```

先頭に `notice: Literal("Notice:")` を追加しても、例の 2 つの分岐はそのまま動作します。一致が見つかった対象行のうち最も早い受信行を選び、その行内では開始位置が最も早い一致を選びます。開始位置が同じ場合は、先に書いた候補が選ばれ、その名前が返ります。プロンプト待機なら、まだ行末が確定していない `$ ` の行でも `"ready"` を返せます。確定行を待つ場合は行区切りが届くまで待ちます。複数パターンを指定できる構文は、この名前付きリテラルだけです。結果の `Index` は、受信行の先頭から一致までのオフセットです。

<a id="file-path-parts"></a>

### ファイルパスの構成要素

| シグネチャ | 戻り値 |
| --- | --- |
| `string GetParentDirectory(string path)` | パスのディレクトリ部分。なければ `""`。 |
| `string GetFileBaseName(string path)` | 最後の構成要素から拡張子を取り除いた名前。最後の構成要素がなければ `""`。 |
| `string GetFileExtension(string path)` | 先頭のドットを含む最後の拡張子。なければ `""`。 |

これらの関数は、ホスト OS のパス規則に従って文字列を解釈します。ディレクトリ区切り文字や、ルートまたはボリュームを表す接頭辞の判定には、プラットフォームのパス処理機能を使います。末尾の区切り文字の判定も同じ規則に従います。大文字・小文字と区切り文字の表記は保持します。ファイルシステムにはアクセスせず、今回の実行の基準ディレクトリからのパス解決も行いません。`.` や `..` などの構成要素もそのまま保持します。

Windows では `\` と `/` の両方がディレクトリ区切り文字です。ドライブ接頭辞（`C:`）、ドライブまたは現在のドライブのルート（`C:\` または `\`）、UNC 共有ルート（`\\server\share`）は、ファイル名に含めません。拡張形式のドライブパスと UNC パスにも同じ規則が適用されます。`/` だけを区切り文字とするホストでは、`\` は構成要素内の通常の文字として扱います。

ルートまたは接頭辞の後に続く部分があるパスは、最後の区切り文字で分割します。区切り文字がなければ、ボリューム接頭辞の直後で分割します。ディレクトリ部分の末尾にある区切り文字は、ルートの一部であるものを除いて取り除きます。入力が区切り文字で終わる場合、最後の構成要素は空で、ディレクトリ部分は末尾の連続した区切り文字よりも前の部分です。空の入力、ルートだけの入力、ボリューム接頭辞だけの入力では、3 つの関数すべてが `""` を返します。

空でない構成要素では、最後のドットが先頭でも末尾でもない場合に限り、その位置から拡張子が始まります。`GetFileBaseName` と `Macro.BaseName` は、どのホストでも同じ規則を使います。次の表は Windows のパス規則を使った例です。パスは文字列リテラル用にエスケープせず、実際の文字で示します。

| パス | 親ディレクトリ | 拡張子を除いた名前 | 拡張子 |
| --- | --- | --- | --- |
| `C:\ops\trace.raw.log` | `C:\ops` | `trace.raw` | `.log` |
| `C:\ops\archive\` | `C:\ops\archive` | `""` | `""` |
| `config/.gitignore` | `config` | `.gitignore` | `""` |
| `.env.local` | `""` | `.env` | `.local` |
| `report.` | `""` | `report.` | `""` |
| `C:\report` | `C:\` | `report` | `""` |
| `C:report` | `C:` | `report` | `""` |
| `\\server\share\report` | `\\server\share` | `report` | `""` |

<a id="dialog-and-ordinary-data-operations"></a>

### ダイアログとデータ操作

| シグネチャ | 動作 |
| --- | --- |
| `DialogResult Dialog(string title, string message, DialogButtons buttons)` | テキスト入力欄のないダイアログでユーザーの選択を受け取ります。 |
| `DialogResult Dialog(string title, string message, DialogButtons buttons, bool inputEnabled, string initialText)` | 必要に応じて 1 行のテキスト入力を受け付けます。入力を無効にする場合、`initialText` は空でなければなりません。 |
| `long Length(string text)` / `long Count(List<T> items)` | それぞれ UTF-16 コード単位の数、リストの要素数を返します。 |
| `string Substring(string text, long start, long length)` | 範囲内の指定区間を正確に切り出します。 |
| `bool Contains/StartsWith/EndsWith(string text, string value)` | 大文字・小文字を区別する序数比較を使います。それぞれに、最後の引数に `bool ignoreCase` を取るオーバーロードがあります。 |
| `long IndexOf(string text, string value)` | 序数比較で最初に見つかった位置を返します。見つからなければ -1 です。最後に ignoreCase 引数を取るオーバーロードがあります。 |
| `string Replace(string text, string oldValue, string newValue)` | リテラルを序数比較して置換します。`oldValue` には空でない文字列を指定します。 |
| `List<string> Split(string text, string separator)` | 空でないリテラルを区切りに分割します。空の要素も保持します。 |
| `string Join(string separator, List<string> items)` | 要素を順番に連結します。空のリストからは空文字列を返します。 |
| `string Trim(string text)` | 両端にある ASCII スペース、タブ（TAB）、CR、LF だけを取り除きます。 |
| `string ToUpperInvariant(string text)` / `ToLowerInvariant(string text)` | .NET 10 のカルチャに依存しない規則で、大文字または小文字に変換します。 |
| `void Add(List<T> items, T value)` / `Insert(List<T> items, long index, T value)` | 代入できる型か、使用量の上限内かを検査して、リストに追加または挿入します。 |
| `void RemoveAt(List<T> items, long index)` / `Clear(List<T> items)` | リストの指定位置の要素、または全要素を削除します。位置を指定する場合は範囲を検査します。 |
| `List<T> Clone(List<T> items)` | リストの浅いコピーを返します。 |
| `long ParseLong(string text)` / `double ParseDouble(string text)` | カルチャに依存しない規則で文字列を数値に明示変換します。空白、桁区切り、NaN、無限大は使えません。 |
| `string FormatNumber(long value, string format)` / double 版のオーバーロード | 対応する書式を使って、カルチャに依存しない規則で数値を文字列に変換します。 |
| `Instant Now()` / `Instant UtcNow()` | それぞれ現在のローカルの UTC オフセット、またはオフセット 0 で現在日時を取得します。 |
| `string FormatTime(Instant value)` / `FormatTime(Instant value, string format)` | 既定の ISO 形式、または明示した対応書式で日時を文字列に変換します。 |
| `long UnixMilliseconds(Instant value)` | Unix エポックからのミリ秒数を返します。端数は負の無限大の方向に丸めます。 |
| `long ElapsedMilliseconds()` | 実行が受け付けられた時点を起点に、単調時計で測った経過時間をミリ秒で返します。一時停止中の時間も含みます。 |

`Contains` や `IndexOf` などには空の検索文字列を指定でき、通常の文字列検索と同じく検索開始位置で一致します。端末の受信待機に渡すリテラルには、引き続き空でない文字列が必要です。`Replace` と `Split` は正規表現を実行しません。`ParseLong` は、省略可能な符号に続いて十進数字を読み取ります。`ParseDouble` は、省略可能な符号、数字、省略可能な小数部、省略可能な指数部を読み取ります。指数部にも符号を付けられます。整数部、小数部、指数部のうち、記述する各部分には数字が必要です。変換する文字列に、数値リテラルの接尾辞や数字区切りは使えません。有限値のオーバーフローはエラーになります。有限値のアンダーフローでは 0 になることがあります。どちらの変換も前後の空白を自動的に取り除きません。

`FormatNumber` の long 版は `G`（一般）、`D` と `D1`～`D19`、`X` と `X1`～`X16` を受け付けます。double 版は `G`、`R`、`F0`～`F15`、`E0`～`E15` を受け付けます。書式の大文字・小文字は区別します。符号付き整数の 16 進表現も含め、.NET 10 のカルチャに依存しない書式規則に従います。通貨書式、桁区切り、カスタム書式は使えません。数値を文字列に補間する際の既定の表現は、long が十進数、double が `R` です。

`Instant` は時刻と UTC オフセットを取得した時点の値として保持します。後から OS のタイムゾーンや時計が変わっても、すでに取得した値は変わりません。既定の時刻書式は `yyyy-MM-dd'T'HH:mm:ss.fffffffzzz` で、UTC の場合も `+00:00` を付けます。小数秒を 7 桁で表記しますが、ハードウェア時計の精度が 100 ナノ秒であることを保証するものではありません。

明示的な時刻書式には、`yyyy`、`MM`、`dd`、`HH`、`mm`、`ss`、`zzz` を使えます。小数秒は `f` で表し、必要な桁数に応じて `f` を 1～7 個並べます。単一引用符で囲んだリテラル文字列と、引用符で囲まない ASCII の句読記号・スペースも使えます。それ以外の英字の書式トークンはエラーです。トークンの意味は .NET 10 のカルチャに依存しない書式規則に従います。初期版の API では、任意のタイムゾーンデータベース、日付の解析、暦に基づく演算は利用できません。

ダイアログのタイトルとメッセージは通常の Unicode テキストとして扱い、メッセージには改行を含められます。タイトルと `initialText` に CR、LF、NUL などの C0 制御文字は使えません。入力は 1 行で、同じ制御文字の制限が適用されます。

Tab キーはコントロール間のフォーカス移動に使います。不正な `initialText` を渡すと `InvalidArgument` が発生します。ユーザーが不正な文字列を貼り付けた場合は、入力欄の元の値を保持し、UI 言語に応じた検証メッセージを表示します。文字を黙って取り除いて別の値を送信することはありません。通常のキーボードナビゲーションと、入力に適用される上限は維持されます。文字列はいずれもマークアップとして解釈しません。

<a id="error-model"></a>

## エラー処理

<a id="rethrowing-a-caught-error"></a>

### 捕捉したエラーの再スロー

`catch` の中で `throw;` を実行すると、処理中の `MacroError` を再スローし、外側の `catch` に処理を委ねます。元のエラーコード、メッセージ、行番号、UTF-16 単位の列番号はそのままです。補助関数も、自身の `catch` の中で再スローできます。呼び出し元が `catch` の中にいても、呼び出した関数の本体までその `catch` に含まれることはありません。

`catch` が入れ子になっている場合、`throw;` は現在実行中の最も内側の `catch` が捕捉したエラーを再スローします。内側の `catch` が正常に終わると、外側で捕捉していたエラーが再び対象になります。再スローすると、その `catch` から抜けます。同じ `catch` がそのエラーをもう一度捕捉することはありません。どこでも捕捉されなければ、TabTty は最初に失敗した操作の位置を表示します。

```csharp
bool IsReady() {
    try { WaitLine(Literal("ready"), 250); return true; }
    catch (MacroError e) {
        if (e.Code == ErrorCode.WaitTimeout) { return false; }
        throw;
    }
}
try { var ready = IsReady(); }
catch (MacroError outer) { throw; }
```

この例では、待機がタイムアウトすると `IsReady()` は `false` を返します。待機が回復可能な `EndOfInput` で終わった場合、外側の `catch` には、元のエラーコードと `WaitLine` の位置が伝わります。もう一度再スローしても、表示される位置は同じです。

関数のすべての実行経路で値を返すかを検査する際、有効な再スローに達した経路はそこで終了したものと扱います。同じ関数の `catch` ブロックの外に `throw;` があると、たとえ到達できない分岐でも、TabTty と VS Code は `throw` の位置に `RethrowOutsideCatch` を報告します。ユーザーによるマクロの終了など、実行を打ち切る条件は `catch` で捕捉できません。

一つの `try` に書けるのは一つの `catch (MacroError e)` です。必要に応じて `try` ブロックを入れ子にできます。再スローには引数のない `throw;` を使います。CLR のすべての例外を捕捉する構文、例外フィルター、式を指定する `throw`、`finally` は使用できません。`catch` は操作が失敗した後に実行されます。それまでに完了した送信などの操作は取り消されません。`ErrorCode` には次のコードが定義されています。

| 分類 | エラーコード |
| --- | --- |
| 値の処理・型の制約 | `InvalidArgument`, `NumericOverflow`, `DivideByZero`, `NumberFormat`, `Format`, `IndexOutOfRange`, `CollectionModified`, `NoTextValue` |
| プロファイルの読み込み・接続準備 | `ProfileLoad`, `ConnectionAlreadyAttempted`, `ConnectionFailed`, `UserCancelled`, `NotConnected`, `Path` |
| 認証情報の送信 | `MissingProfilePassword`, `CredentialUnavailable`, `CredentialKindMismatch`, `VaultStale`, `VaultUnlockCancelled` |
| 待機・正規表現 | `WaitTimeout`, `EndOfInput`, `InvalidPattern`, `RegexTimeout`, `ZeroLengthMatch`, `CaptureLimit`, `MatchWorkLimit` |
| 操作の受け付け | `Encoding`, `PayloadLimit`, `PatternLimit`, `OperationUnavailable` |

ソース内の不正なエスケープやリテラル、未対応の識別子など、ソースそのものから実行前に特定できる誤りは、ソースエラーとして報告されます。`MacroError` として捕捉することはできません。

一方、`Regex` と `Literal` に渡すパターンは引数の値です。ソースに直接書いた場合も、式で組み立てた場合も、検査するのは関数の実行時です。失敗すると `InvalidPattern` または `InvalidArgument` を捕捉できます。リテラルだけを事前評価して、エラーを捕捉できるかどうかが変わることはありません。`1 / 0` のように式の型が正しく値の計算に失敗する場合も、評価時にエラーを捕捉できます。送信データを一切引き渡していないと確認できる場合、より具体的なコードが上の表にない限り、送信エラーは `OperationUnavailable` になります（決定 Q76）。

ユーザーによるマクロの終了、切断、代替画面への切り替え、インタープリターが保持できる状態量の上限到達では、`catch` を通らずにマクロが終了します。記録や受信履歴の問題で実行が保留された場合は、一時的な保留状態として扱います。例外は発生しません。

送信が一部だけ行われた場合や、どこまで送信したか判別できない場合は、Q76 に従って実行を終了します。ランタイムによる自動再試行はありません。エラー画面およびエラーの `Message` には、管理対象の秘密情報、送信テキスト、引数の値、正規表現でキャプチャした内容を含めません。

<a id="readable-example"></a>

## 応答に合わせて分岐する例

次の例は、保存済みプロファイルで接続し、受信したプロンプトに応じて処理を分岐します。待機する文字列は特定のシェルを想定しています。実際に使うときは、接続先でログインやコマンドの成功を判断できる文字列を選んでください。送信後にマクロが終了しても、接続が正常なら引き続きそのタブで端末を操作できます。

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
WriteLine("sudo -v", markReceiveBoundary: true);

try
{
    var result = WaitAnyPrompt([password: Literal("[sudo] password:"), ready: Literal("$ ")], 10000);
    if (result.AlternativeName == "password")
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

この例では、引数を数値に変換してからコマンドを組み立てています。任意の文字列を使ってコマンドを組み立てる場合は、接続先のプロトコルやシェルに合わせて、引用符やエスケープを使ってください。マクロ言語は接続先のシェルを推測せず、コマンドの組み立てだけでインジェクションを防ぐ仕組みもありません。
