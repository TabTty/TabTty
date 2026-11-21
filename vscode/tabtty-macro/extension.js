'use strict';

const vscode = require('vscode');
const path = require('path');
const { spawn } = require('child_process');

function activate(context) {
    const executable = context.asAbsolutePath(path.join('server', 'TabTty.MacroEditor.exe'));
    const diagnostics = vscode.languages.createDiagnosticCollection('tabtty');
    const japanese = vscode.env.language.toLowerCase().startsWith('ja');
    const parameterNames = {
        Literal: ['text', 'ignoreCase'], Regex: ['pattern', 'ignoreCase'],
        WaitLine: ['pattern', 'timeoutMs'], WaitPrompt: ['pattern', 'timeoutMs'],
        WaitAnyLine: ['patterns', 'timeoutMs'], WaitAnyPrompt: ['patterns', 'timeoutMs'],
        Write: ['text'], WritePassword: ['name'], WritePasswordLine: ['name'],
        FormatNumber: ['value', 'format'], FormatTime: ['value', 'format'],
        Sleep: ['milliseconds'], SetBaseDirectory: ['path'], Length: ['text'], Count: ['items'],
        Substring: ['text', 'start', 'length'], Dialog: ['title', 'message', 'buttons', 'inputEnabled', 'initialText']
    };
    const descriptions = {
        Connect: ['Connect using a saved profile.', '保存済みプロファイルを使って接続します。'],
        Write: ['Send text without a line ending.', '改行を付けずに文字列を送信します。'],
        WriteLine: ['Send text followed by the Enter ending.', '文字列の後に Enter の改行を送信します。'],
        Literal: ['Create a literal text pattern.', '文字列そのものに一致するパターンを作ります。'],
        Regex: ['Create a regular expression pattern.', '正規表現のパターンを作ります。'],
        WaitLine: ['Match a completed received line.', '受信が完了した行を照合します。'],
        WaitPrompt: ['Match an unfinished received line. Timeout is in milliseconds.',
            '受信中の行も照合します。タイムアウトはミリ秒です。'],
        WaitAnyLine: ['Match one of the patterns against a completed line.', '受信が完了した行に複数のパターンを照合します。'],
        WaitAnyPrompt: ['Match one of the patterns against an unfinished line.', '受信中の行に複数のパターンを照合します。'],
        Sleep: ['Wait for the specified milliseconds.', '指定したミリ秒だけ待機します。'],
        Now: ['Get the current local time.', '現在のローカル時刻を取得します。'],
        UtcNow: ['Get the current UTC time.', '現在の UTC 時刻を取得します。'],
        Dialog: ['Show a dialog and return the decision.', 'ダイアログを表示して選択結果を返します。'],
        SetBaseDirectory: ['Set the base directory for relative paths.', '相対パスの基準ディレクトリを設定します。'],
        ResetBaseDirectory: ['Restore the original base directory.', '基準ディレクトリを元に戻します。'],
        BaseDirectory: ['Get the current base directory.', '現在の基準ディレクトリを取得します。'],
        GetParentDirectory: ['Get a path’s parent directory.', 'パスの親ディレクトリを取得します。'],
        GetFileBaseName: ['Get the filename without its extension.', '拡張子を除いたファイル名を取得します。'],
        GetFileExtension: ['Get the filename extension.', 'ファイルの拡張子を取得します。'],
        WritePassword: ['Send a referenced password.', '参照したパスワードを送信します。'],
        WritePasswordLine: ['Send a referenced password followed by Enter.', '参照したパスワードと Enter を送信します。'],
        MarkReceiveBoundary: ['Advance the receive matching boundary.', '受信照合の開始位置を進めます。'],
        MinimizeWindow: ['Minimize the window hosting this run.', '実行中のウィンドウを最小化します。'],
        RestoreWindow: ['Restore the window hosting this run.', '実行中のウィンドウを元に戻します。'],
        Length: ['Get the length of a string.', '文字列の長さを取得します。'],
        Count: ['Get the number of list items.', 'リストの要素数を取得します。'],
        Substring: ['Extract part of a string.', '文字列の一部を取り出します。'],
        Contains: ['Check whether text contains a value.', '文字列に値が含まれるか確認します。'],
        StartsWith: ['Check the start of text.', '文字列の先頭を確認します。'],
        EndsWith: ['Check the end of text.', '文字列の末尾を確認します。'],
        IndexOf: ['Find the first occurrence of text.', '文字列が最初に現れる位置を取得します。'],
        Replace: ['Replace matching text.', '一致する文字列を置換します。'],
        Split: ['Split text using a separator.', '区切り文字で文字列を分割します。'],
        Join: ['Join strings using a separator.', '区切り文字を挟んで文字列を結合します。'],
        Trim: ['Remove surrounding whitespace.', '前後の空白を取り除きます。'],
        ToUpperInvariant: ['Convert text to invariant uppercase.', '文字列をカルチャ非依存の大文字に変換します。'],
        ToLowerInvariant: ['Convert text to invariant lowercase.', '文字列をカルチャ非依存の小文字に変換します。'],
        Add: ['Add an item to a list.', 'リストに要素を追加します。'],
        Insert: ['Insert an item into a list.', 'リストに要素を挿入します。'],
        RemoveAt: ['Remove a list item at an index.', '指定位置のリスト要素を削除します。'],
        Clear: ['Remove all list items.', 'リストの要素をすべて削除します。'],
        Clone: ['Copy a list.', 'リストをコピーします。'],
        ParseLong: ['Parse text as a long integer.', '文字列を long 整数に変換します。'],
        ParseDouble: ['Parse text as a double.', '文字列を double に変換します。'],
        FormatNumber: ['Format a number.', '数値を書式化します。'],
        FormatTime: ['Format a time value.', '時刻を書式化します。'],
        UnixMilliseconds: ['Get the Unix timestamp in milliseconds.', 'Unix 時刻をミリ秒で取得します。'],
        ElapsedMilliseconds: ['Get elapsed run time in milliseconds.', '実行開始からの経過時間をミリ秒で取得します。']
    };
    function parameterLabel(name, type, index, namedParameters) {
        const label = (namedParameters ?? parameterNames[name])?.[index];
        if (index > 0 && namedParameters) return '[' + label + ': ' + type + ']';
        return label ? type + ' ' + label : type;
    }
    const messages = {
        ConstantsMissing: ['Shared constants file was not found.', '共有定数ファイルが見つかりません。'],
        ConstantsInvalid: ['The shared constants file contains an error.', '共有定数ファイルにエラーがあります。'],
        ConstantsTooLarge: ['The shared constants file is too large.', '共有定数ファイルのサイズが大きすぎます。'],
        ConstantsLimit: ['The declared constants exceed the permitted limit.', '宣言した定数が上限を超えています。'],
        NumericLiteral: ['The numeric literal is malformed or out of range.', '数値リテラルの形式または範囲が正しくありません。'],
        StringEscape: ['Invalid string escape. Use two hexadecimal digits after \\x.', 'エスケープが不正です。\\x の後には16進数字を2桁指定してください。'],
        ConstantsUnreadable: ['The shared constants file could not be read.', '共有定数ファイルを読み取れませんでした。'],
        ConstantsOpenRequired: ['Open the shared constants file to analyze it.', '共有定数ファイルを開いてください。'],
        VersionLine: ['A supported #tttym 1 version declaration is required.', '対応する #tttym 1 バージョン宣言が必要です。'],
        TypeMismatch: ['The value has an incompatible type.', '値の型が一致しません。'],
        NamedOptionExpected: ['A named option is required here.', '名前付きオプションを指定してください。'],
        UnknownOption: ['This option is not defined for this call.', 'この呼び出しでは使用できないオプションです。'],
        DuplicateOption: ['This option has already been supplied.', 'このオプションは既に指定されています。'],
        AlternativeNameExpected: ['Give each wait pattern a non-reserved name followed by a colon.', '各待機パターンに予約語以外の名前とコロンを指定してください。'],
        DuplicateAlternative: ['This wait already has an alternative with this name.', 'この名前の待機候補は既に指定されています。'],
        UnknownName: ['This name is not defined.', 'この名前は定義されていません。'],
        UnknownMember: ['This member is not defined.', 'このメンバーは定義されていません。'],
        RethrowOutsideCatch: ['Rethrow is only valid inside a catch in this function.', '再送出は同じ関数の catch 内でのみ使用できます。'],
        WaitPatternRequired: ['WaitPrompt requires a WaitPattern. Use Literal or Regex.',
            'WaitPrompt の引数には WaitPattern が必要です。Literal または Regex を使用してください。'],
        ConstantsType: ['The value does not match the declared type.', '値が宣言された型と一致しません。'],
        ConstantsLiteral: ['A literal value is required.', 'リテラル値を指定してください。'],
        ConstantsDefinition: ['The constant declaration is incomplete or invalid.', '定数の宣言が不完全か、不正です。'],
        ConstantsName: ['The constant name is invalid or duplicated.', '定数名が不正か、重複しています。'],
        UnknownFunction: ['This function is not defined.', 'この関数は定義されていません。'],
        ExpectedToken: ['A required token is missing.', '必要な記号または語がありません。'],
        ExpressionExpected: ['An expression is required.', '式を指定してください。'],
        IdentifierExpected: ['A name is required.', '名前を指定してください。'],
        StatementExpected: ['A statement is required.', '文を指定してください。'],
        UnterminatedString: ['The string is not closed.', '文字列が閉じられていません。'],
        UnterminatedComment: ['The comment is not closed.', 'コメントが閉じられていません。']
    };
    context.subscriptions.push(diagnostics);
    let server;
    let pending = [];
    let buffer = '';
    const generations = new Map();
    const timers = new Map();

    function request(payload) {
        if (!server) {
            const process = spawn(executable, [], { windowsHide: true });
            server = process;
            buffer = '';
            process.stdout.setEncoding('utf8');
            process.stdout.on('data', chunk => {
                buffer += chunk;
                let end;
                while ((end = buffer.indexOf('\n')) >= 0) {
                    const line = buffer.slice(0, end);
                    buffer = buffer.slice(end + 1);
                    const item = pending.shift();
                    if (item) {
                        try {
                            const result = JSON.parse(line);
                            if (result.error) item.reject(new Error(result.error));
                            else item.resolve(result);
                        }
                        catch (error) { item.reject(error); }
                    }
                }
            });
            process.on('error', error => {
                if (server !== process) return;
                for (const item of pending.splice(0)) item.reject(error);
                server = undefined;
            });
            process.on('exit', () => {
                if (server !== process) return;
                for (const item of pending.splice(0)) item.reject(new Error('Macro analysis stopped'));
                server = undefined;
            });
            process.stdin.on('error', error => {
                if (server !== process) return;
                for (const item of pending.splice(0)) item.reject(error);
            });
        }
        return new Promise((resolve, reject) => {
            pending.push({ resolve, reject });
            server.stdin.write(JSON.stringify(payload) + '\n');
        });
    }

    async function analyze(document, position) {
        const version = document.version;
        const payload = {
            path: document.uri.fsPath,
            source: document.getText(),
            offset: position && document.offsetAt(position),
            constantsFile: document.languageId === 'tabtty-constants',
            constants: Object.create(null)
        };
        const initial = await request(payload);
        if (document.isClosed || document.version !== version) return {};
        if (payload.constantsFile) return initial;
        if (initial.unsupportedVersion) {
            initial.functions = {};
            return initial;
        }
        const dependencies = initial.dependencies || [];
        if (!dependencies.length) return initial;
        const targets = [...new Set(dependencies.map(dependency => dependency.FullPath).filter(Boolean))];
        function openCandidates(target) {
            const key = target.toUpperCase();
            return vscode.workspace.textDocuments.filter(item =>
                item.uri.scheme === 'file' && item.uri.fsPath.toUpperCase() === key);
        }
        async function chooseOpen(target, candidates) {
            const exact = candidates.find(item => item.uri.fsPath === vscode.Uri.file(target).fsPath);
            if (exact || !candidates.length) return exact;
            // A case variant can denote a different file. Compare only ordinary local
            // file metadata before using the text of an already open document.
            const { match } = await request({ path: target, source: '', operation: 'matchOpenLocal',
                candidates: candidates.map(item => item.uri.fsPath) });
            return candidates.find(item => item.uri.fsPath === match);
        }
        // Each alias observes the same saved or unsaved snapshot of its file.
        while (!document.isClosed && document.version === version) {
            const snapshots = new Map();
            const trusted = vscode.workspace.isTrusted;
            const constants = Object.create(null);
            for (const target of targets) {
                const candidates = openCandidates(target);
                const open = await chooseOpen(target, candidates);
                const snapshot = { candidates, open, version: open?.version, status: 'openRequired' };
                snapshots.set(target, snapshot);
                if (open) {
                    constants[target] = open.getText();
                    snapshot.status = 'ok';
                } else if (document.uri.scheme === 'file' && trusted) {
                    const read = await request({ path: target, source: '', operation: 'readLocal' });
                    snapshot.status = read.status;
                    if (read.status === 'ok') constants[target] = read.text;
                }
            }
            const result = Object.keys(constants).length ? await request({ ...payload, constants }) : initial;
            if (document.isClosed || document.version !== version) return {};
            if (trusted !== vscode.workspace.isTrusted || targets.some(target => {
                const current = openCandidates(target);
                const snapshot = snapshots.get(target);
                return current.length !== snapshot.candidates.length ||
                    current.some((item, index) => item !== snapshot.candidates[index]) ||
                    snapshot.open?.version !== snapshot.version;
            })) continue;
            const codes = { missing: 'ConstantsMissing', tooLarge: 'ConstantsTooLarge',
                unreadable: 'ConstantsUnreadable', openRequired: 'ConstantsOpenRequired' };
            const unavailable = dependencies.filter(dependency =>
                dependency.FullPath && snapshots.get(dependency.FullPath).status !== 'ok');
            if (unavailable.length) result.diagnostics = [
                ...(result.diagnostics || []).filter(item => item.Code !== 'ConstantsRead'),
                ...unavailable.map(dependency => ({ ...dependency.Position,
                    Code: codes[snapshots.get(dependency.FullPath).status] || 'ConstantsUnreadable' }))
            ];
            return result;
        }
        return {};
    }

    function referencedConstant(result) {
        const reference = result.constantReference;
        const dependency = reference && result.dependencies?.find(entry => entry.Alias === reference.Alias);
        if (!dependency) return undefined;
        const members = result.constantSymbols?.[dependency.FullPath] || [];
        return { dependency, reference, members, member: members.find(entry => entry.Name === reference.Member) };
    }

    function symbolDetail(symbol) {
        const parameters = symbol.Parameters?.map(p => p.Type + ' ' + p.Name).join(', ');
        return parameters === undefined ? symbol.Type : symbol.Type + ' ' + symbol.Name + '(' + parameters + ')';
    }

    async function update(document) {
        if (!['tabtty-macro', 'tabtty-constants'].includes(document.languageId)) return;
        const version = document.version;
        const key = document.uri.toString();
        const generation = (generations.get(key) || 0) + 1;
        generations.set(key, generation);
        try {
            const result = await analyze(document);
            if (document.isClosed || document.version !== version || generations.get(key) !== generation) return;
            diagnostics.set(document.uri, (result.diagnostics || []).map(item => {
                const position = new vscode.Position(Math.max(0, item.Line - 1), Math.max(0, item.Column - 1));
                const call = result.patternFix?.Call;
                const isWaitPattern = item.Code === 'TypeMismatch' &&
                    item.Line === call?.Line && item.Column === call.Column;
                const text = messages[isWaitPattern ? 'WaitPatternRequired' : item.Code];
                const fallback = japanese ? 'マクロの構文または型にエラーがあります。' : 'The macro has a syntax or type error.';
                const diagnostic = new vscode.Diagnostic(new vscode.Range(position, position.translate(0, item.Length ?? 1)),
                    text ? text[japanese ? 1 : 0] : fallback);
                diagnostic.code = item.Code;
                return diagnostic;
            }));
        } catch (error) {
            console.error('TabTty macro analysis failed', error);
        }
    }

    function scheduleUpdate(document) {
        const key = document.uri.toString();
        generations.set(key, (generations.get(key) || 0) + 1);
        clearTimeout(timers.get(key));
        timers.set(key, setTimeout(() => {
            timers.delete(key);
            update(document);
        }, 150));
    }

    const macroSelector = { language: 'tabtty-macro' };
    context.subscriptions.push(vscode.languages.registerCompletionItemProvider(macroSelector, {
        async provideCompletionItems(document, position) {
            const result = await analyze(document, position);
            if (result.unsupportedVersion) return [];
            const items = [];
            const call = result.activeCall;
            const signature = result.functions?.[call?.name]?.at(-1);
            if (call?.completeOption && signature?.NamedParameters) {
                return signature.NamedParameters.flatMap((name, slot) => {
                    if (!slot || call.usedSlots.includes(slot)) return [];
                    const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Property);
                    item.detail = signature.Parameters[slot];
                    item.insertText = call.hasOptionColon ? name : name + ': ';
                    return [item];
                });
            }
            for (const [name, signatures] of Object.entries(result.functions || {})) {
                const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Function);
                item.detail = signatures.map(signature =>
                    signature.Result + ' ' + name + '(' + signature.Parameters.map((type, index) =>
                        parameterLabel(name, type, index, signature.NamedParameters)).join(', ') + ')').join('\n');
                items.push(item);
            }
            for (const entry of result.symbols || []) {
                const item = new vscode.CompletionItem(entry.Name,
                    entry.Parameters ? vscode.CompletionItemKind.Function : vscode.CompletionItemKind.Variable);
                item.detail = symbolDetail(entry);
                items.push(item);
            }
            const constant = referencedConstant(result);
            if (constant?.reference.OnMember) {
                return constant.members.map(entry => {
                    const item = new vscode.CompletionItem(entry.Name, vscode.CompletionItemKind.Constant);
                    item.detail = entry.Type;
                    return item;
                });
            }
            if (Number.isInteger(result.versionInsertion)) {
                const header = new vscode.CompletionItem('#tttym 1', vscode.CompletionItemKind.Snippet);
                header.insertText = new vscode.SnippetString('#tttym 1\n$0');
                header.range = new vscode.Range(document.positionAt(result.versionInsertion), position);
                items.push(header);
            }
            return items;
        }
    }, '#', '.', ','));

    context.subscriptions.push(vscode.languages.registerDefinitionProvider(macroSelector, {
        async provideDefinition(document, position) {
            const word = document.getWordRangeAtPosition(position);
            if (!word) return undefined;
            const name = document.getText(word);
            const result = await analyze(document, position);
            const constant = referencedConstant(result);
            if (constant) {
                const { reference, dependency, member } = constant;
                const target = reference.OnMember ? member?.Position : dependency.AliasPosition;
                if (!target) return undefined;
                return new vscode.Location(reference.OnMember ? vscode.Uri.file(dependency.FullPath) : document.uri,
                    new vscode.Position(target.Line - 1, target.Column - 1));
            }
            const entry = result.symbols?.findLast(item => item.Name === name);
            return entry ? new vscode.Location(document.uri,
                new vscode.Position(entry.Position.Line - 1, entry.Position.Column - 1)) : undefined;
        }
    }));

    context.subscriptions.push(vscode.languages.registerHoverProvider(macroSelector, {
        async provideHover(document, position) {
            const range = document.getWordRangeAtPosition(position);
            if (!range) return undefined;
            const name = document.getText(range);
            const result = await analyze(document, position);
            const constant = referencedConstant(result);
            if (constant?.reference.OnMember) {
                const member = constant.member;
                return member ? new vscode.Hover(member.Type + ' ' + member.Name, range) : undefined;
            }
            const signatures = result.functions?.[name];
            if (signatures) {
                const detail = signatures.map(signature =>
                    signature.Result + ' ' + name + '(' + signature.Parameters.map((type, index) =>
                        parameterLabel(name, type, index, signature.NamedParameters)).join(', ') + ')').join('\n');
                const description = descriptions[name]
                    ? '\n' + descriptions[name][japanese ? 1 : 0]
                    : (japanese ? '\nTabTty の組み込み関数です。' : '\nTabTty built-in function.');
                return new vscode.Hover(detail + description, range);
            }
            const entry = result.symbols?.findLast(item => item.Name === name);
            if (!entry) return undefined;
            const detail = entry.Parameters ? symbolDetail(entry) : entry.Type + ' ' + entry.Name;
            return new vscode.Hover(detail, range);
        }
    }));

    context.subscriptions.push(vscode.languages.registerSignatureHelpProvider(macroSelector, {
        async provideSignatureHelp(document, position) {
            const result = await analyze(document, position);
            if (result.unsupportedVersion) return undefined;
            const call = result.activeCall;
            if (!call?.name) return undefined;
            let signatures = result.functions && result.functions[call.name];
            if (!signatures) {
                const helper = result.symbols?.find(entry => entry.Parameters && entry.Name === call.name);
                if (helper) signatures = [{ Result: helper.Type,
                    Parameters: helper.Parameters.map(p => p.Type + ' ' + p.Name) }];
            }
            if (!signatures) return undefined;
            const help = new vscode.SignatureHelp();
            help.signatures = signatures.map(signature => {
                const parameters = signature.Parameters.map((type, index) => parameterLabel(call.name, type, index, signature.NamedParameters));
                const info = new vscode.SignatureInformation(
                    signature.Result + ' ' + call.name + '(' + parameters.join(', ') + ')');
                info.parameters = parameters.map(parameter => new vscode.ParameterInformation(parameter));
                return info;
            });
            const parameter = call.namedSlot ?? call.parameter;
            const matching = signatures.findIndex(signature => signature.Parameters.length > parameter);
            help.activeSignature = Math.max(0, matching);
            help.activeParameter = parameter;
            return help;
        }
    }, '(', ','));

    context.subscriptions.push(vscode.languages.registerCodeActionsProvider(macroSelector, {
        async provideCodeActions(document, range, actionContext) {
            const candidates = actionContext.diagnostics.filter(item => item.code === 'TypeMismatch');
            if (!candidates.length) return [];
            const result = await analyze(document);
            const fix = result.patternFix;
            if (!fix || !candidates.some(item => {
                const start = (item.range || range).start;
                return start.line === fix.Call.Line - 1 && start.character === fix.Call.Column - 1;
            })) return [];
            const argumentRange = new vscode.Range(document.positionAt(fix.Start), document.positionAt(fix.End));
            const text = document.getText(argumentRange);
            return ['Literal', 'Regex'].map(name => {
                const title = name === 'Literal'
                    ? (japanese ? '文字列そのものを待つ' : 'Match literal text')
                    : (japanese ? '正規表現として待つ' : 'Match a regular expression');
                const action = new vscode.CodeAction(title, vscode.CodeActionKind.QuickFix);
                action.edit = new vscode.WorkspaceEdit();
                action.edit.replace(document.uri, argumentRange, name + '(' + text + ')');
                return action;
            });
        }
    }, { providedCodeActionKinds: [vscode.CodeActionKind.QuickFix] }));

    function refreshMacros() {
        for (const document of vscode.workspace.textDocuments) {
            if (document.languageId === 'tabtty-macro') scheduleUpdate(document);
        }
    }
    context.subscriptions.push(vscode.workspace.onDidOpenTextDocument(document => {
        scheduleUpdate(document);
        refreshMacros();
    }));
    context.subscriptions.push(vscode.workspace.onDidChangeTextDocument(event => {
        scheduleUpdate(event.document);
        for (const document of vscode.workspace.textDocuments) {
            if (document !== event.document && document.languageId === 'tabtty-macro') scheduleUpdate(document);
        }
    }));
    context.subscriptions.push(vscode.workspace.onDidCloseTextDocument(document => {
        diagnostics.delete(document.uri);
        clearTimeout(timers.get(document.uri.toString()));
        timers.delete(document.uri.toString());
        generations.delete(document.uri.toString());
        refreshMacros();
    }));
    context.subscriptions.push(vscode.workspace.onDidGrantWorkspaceTrust(refreshMacros));
    context.subscriptions.push({ dispose: () => { if (server) server.kill(); } });
    for (const document of vscode.workspace.textDocuments) update(document);
}

exports.activate = activate;
