'use strict';

const vscode = require('vscode');
const path = require('path');
const { spawn } = require('child_process');

function activate(context) {
    const executable = context.asAbsolutePath(path.join('server', 'TabTty.MacroEditor.exe'));
    const diagnostics = vscode.languages.createDiagnosticCollection('tabtty');
    const japanese = vscode.env.language.toLowerCase().startsWith('ja');
    const parameterNames = {
        Connect: ['profilePath', 'logDirectory', 'logFilenameTemplate', 'allowLogOverwrite'],
        Literal: ['text', 'ignoreCase'], Regex: ['pattern', 'ignoreCase'],
        WaitLine: ['pattern', 'timeoutMs'], WaitPrompt: ['pattern', 'timeoutMs'],
        WaitAnyLine: ['patterns', 'timeoutMs'], WaitAnyPrompt: ['patterns', 'timeoutMs'],
        Write: ['text'], WriteLine: ['text'], WritePassword: ['name'], WritePasswordLine: ['name'],
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
    function parameterLabel(name, type, index) {
        const label = parameterNames[name]?.[index];
        return label ? type + ' ' + label : type;
    }
    const messages = {
        ConstantsMissing: ['Shared constants file was not found.', '共有定数ファイルが見つかりません。'],
        ConstantsInvalid: ['The shared constants file contains an error.', '共有定数ファイルにエラーがあります。'],
        ConstantsTooLarge: ['The shared constants file is too large.', '共有定数ファイルのサイズが大きすぎます。'],
        ConstantsUnreadable: ['The shared constants file could not be read.', '共有定数ファイルを読み取れませんでした。'],
        ConstantsOpenRequired: ['Open the shared constants file to analyze it.', '共有定数ファイルを開いてください。'],
        VersionLine: ['A supported #tttym 1 version declaration is required.', '対応する #tttym 1 バージョン宣言が必要です。'],
        TypeMismatch: ['The value has an incompatible type.', '値の型が一致しません。'],
        UnknownName: ['This name is not defined.', 'この名前は定義されていません。'],
        UnknownMember: ['This member is not defined.', 'このメンバーは定義されていません。'],
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

    async function analyze(document) {
        const payload = {
            path: document.uri.fsPath,
            source: document.getText(),
            constantsFile: document.languageId === 'tabtty-constants',
            constants: Object.create(null)
        };
        const initial = await request(payload);
        if (payload.constantsFile) return initial;
        const extra = [];
        const locations = Object.create(null);
        const constantTypes = Object.create(null);
        const tokens = initial.tokens || [];
        if (tokens[0]?.Text === '#' && tokens[1]?.Text === 'tttym' &&
            tokens[2]?.Kind === 'num' && tokens[2].Text !== '1') {
            initial.functions = {};
            initial.diagnostics = [{ Code: 'VersionLine', Line: tokens[2].Line, Column: tokens[2].Column }];
            initial.unsupportedVersion = true;
            return initial;
        }
        for (let index = 0; index + 3 < tokens.length; index++) {
            const [hash, keyword, alias, literal] = tokens.slice(index, index + 4);
            if (hash.Text !== '#' || keyword.Text !== 'constants' ||
                alias.Kind !== 'id' || literal.Kind !== 'str' || typeof literal.Value !== 'string') continue;
            const target = path.resolve(path.dirname(document.uri.fsPath), literal.Value);
            locations[alias.Text] = target;
            const openDocument = vscode.workspace.textDocuments.find(item =>
                item.uri.scheme === 'file' && item.uri.fsPath.toLowerCase() === target.toLowerCase());
            let contents;
            if (openDocument) {
                contents = openDocument.getText();
            } else if (document.uri.scheme === 'file' && vscode.workspace.isTrusted) {
                const read = await request({ path: target, source: '', operation: 'readLocal' });
                if (read.status === 'ok') contents = read.text;
                else {
                    const codes = { missing: 'ConstantsMissing', tooLarge: 'ConstantsTooLarge',
                        unreadable: 'ConstantsUnreadable', openRequired: 'ConstantsOpenRequired' };
                    extra.push({ Code: codes[read.status] || 'ConstantsUnreadable',
                        Line: literal.Line, Column: literal.Column });
                }
            } else {
                extra.push({ Code: 'ConstantsOpenRequired', Line: literal.Line, Column: literal.Column });
            }
            if (contents !== undefined) {
                payload.constants[alias.Text] = contents;
                const checked = await request({ path: target, source: contents, constantsFile: true });
                constantTypes[alias.Text] = checked.members || {};
            }
        }
        const result = await request(payload);
        if (extra.length) result.diagnostics = [
            ...(result.diagnostics || []).filter(item => item.Code !== 'ConstantsRead'),
            ...extra
        ];
        result.constants = payload.constants;
        result.constantPaths = locations;
        result.constantTypes = constantTypes;
        return result;
    }

    function declarations(tokens, offset, functions = {}, constantTypes = {}, inferred = []) {
        const types = new Set(['var', 'bool', 'long', 'double', 'string', 'void',
            'WaitPattern', 'WaitMatch', 'Instant', 'DialogResult', 'CaptureGroup',
            'MacroError', 'ErrorCode', 'DialogButtons', 'DialogButton']);
        const scopes = [[]];
        const helpers = [];
        const functionParameters = new Map();
        let functionScope = -1;
        function typeAt(index) {
            if (types.has(tokens[index]?.Text)) return { text: tokens[index].Text, next: index + 1 };
            if (tokens[index]?.Text !== 'List' || tokens[index + 1]?.Text !== '<') return undefined;
            const element = typeAt(index + 2);
            if (!element || tokens[element.next]?.Text !== '>') return undefined;
            return { text: 'List<' + element.text + '>', next: element.next + 1 };
        }
        function enclosingLoopEnd(index) {
            let open = index - 1;
            while (open >= 0 && tokens[open].Text !== '(' && tokens[open].Text !== ';' &&
                tokens[open].Text !== '{' && tokens[open].Text !== '}') open--;
            if (open < 1 || !['for', 'foreach'].includes(tokens[open - 1].Text)) return undefined;
            let depth = 1;
            let close = open + 1;
            while (close < tokens.length && depth > 0) {
                if (tokens[close].Text === '(') depth++;
                else if (tokens[close].Text === ')') depth--;
                close++;
            }
            if (tokens[close]?.Text !== '{') return undefined;
            depth = 1;
            close++;
            while (close < tokens.length && depth > 0) {
                if (tokens[close].Text === '{') depth++;
                else if (tokens[close].Text === '}') depth--;
                close++;
            }
            return depth === 0 ? tokens[close - 1].Offset : undefined;
        }
        let depth = 0;
        for (let index = 0; index + 2 < tokens.length; index++) {
            const token = tokens[index];
            if (token.Text === '{') { depth++; continue; }
            if (token.Text === '}') { depth--; continue; }
            const type = typeAt(index);
            if (depth !== 0 || !type || tokens[type.next]?.Kind !== 'id' ||
                tokens[type.next + 1]?.Text !== '(') continue;
            let end = type.next + 2;
            while (end < tokens.length && tokens[end].Text !== ')') end++;
            if (tokens[end + 1]?.Text !== '{') continue;
            const parameters = [];
            for (let part = type.next + 2; part + 1 < end; part++) {
                const parameterType = typeAt(part);
                if (parameterType && tokens[parameterType.next]?.Kind === 'id') {
                    parameters.push({ name: tokens[parameterType.next].Text, type: parameterType.text,
                        token: tokens[parameterType.next], isFunction: false });
                    part = parameterType.next;
                }
            }
            const helper = { name: tokens[type.next].Text, type: type.text,
                token: tokens[type.next], isFunction: true, parameters };
            helpers.push(helper);
            functionParameters.set(tokens[end + 1].Offset, parameters);
            index = end;
        }
        for (let index = 0; index < tokens.length; index++) {
            const token = tokens[index];
            if (token.Offset >= offset) break;
            if (token.Text === '{') {
                scopes.push([...(functionParameters.get(token.Offset) || [])]);
                if (functionParameters.has(token.Offset)) functionScope = scopes.length - 1;
            } else if (token.Text === '}' && scopes.length > 1) {
                if (functionScope === scopes.length - 1) functionScope = -1;
                scopes.pop();
            }
            else if (typeAt(index) && tokens[typeAt(index).next]?.Kind === 'id') {
                const parsedType = typeAt(index);
                const name = tokens[parsedType.next];
                const isFunction = tokens[parsedType.next + 1]?.Text === '(' && scopes.length === 1;
                if (isFunction) {
                    while (index < tokens.length && tokens[index].Text !== ')') index++;
                } else {
                    const loopEnd = enclosingLoopEnd(index);
                    if (loopEnd !== undefined && offset > loopEnd) {
                        index = parsedType.next;
                        continue;
                    }
                    let type = parsedType.text;
                    const checked = inferred.find(item => item.Name === name.Text &&
                        item.Line === token.Line && item.Column <= token.Column);
                    if (checked) type = checked.Type;
                    if (type === 'var' && tokens[parsedType.next + 1]?.Text === '=') {
                        const value = tokens[parsedType.next + 2];
                        if (value?.Kind === 'str') type = 'string';
                        else if (value?.Kind === 'num') type = /[.eE]/.test(value.Text) ? 'double' : 'long';
                        else if (value?.Text === 'true' || value?.Text === 'false') type = 'bool';
                        else if (value?.Kind === 'id' && tokens[parsedType.next + 3]?.Text === '.')
                            type = constantTypes[value.Text]?.[tokens[parsedType.next + 4]?.Text] || type;
                        else if (value?.Kind === 'id' && tokens[parsedType.next + 3]?.Text === '(')
                            type = functions[value.Text]?.[0]?.Result ||
                                helpers.find(helper => helper.name === value.Text)?.type || type;
                    }
                    scopes[scopes.length - 1].push({ name: name.Text, type, token: name, isFunction: false });
                    index = parsedType.next;
                }
            }
        }
        return [...helpers, ...scopes.slice(functionScope < 0 ? 0 : functionScope).flat()];
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
                const call = (result.tokens || []).findIndex(token => token.Text === 'WaitPrompt' &&
                    token.Line === item.Line && token.Column === item.Column);
                const isWaitPattern = item.Code === 'TypeMismatch' && call >= 0 &&
                    result.tokens[call + 1]?.Text === '(' &&
                    result.tokens[call + 2]?.Kind === 'str' &&
                    typeof result.tokens[call + 2]?.Value === 'string';
                const text = messages[isWaitPattern ? 'WaitPatternRequired' : item.Code];
                const fallback = japanese ? 'マクロの構文または型にエラーがあります。' : 'The macro has a syntax or type error.';
                const diagnostic = new vscode.Diagnostic(new vscode.Range(position, position.translate(0, 1)),
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
            const result = await analyze(document);
            if (result.unsupportedVersion) return [];
            const items = [];
            for (const [name, signatures] of Object.entries(result.functions || {})) {
                const item = new vscode.CompletionItem(name, vscode.CompletionItemKind.Function);
                item.detail = signatures.map(signature =>
                    signature.Result + ' ' + name + '(' + signature.Parameters.map((type, index) =>
                        parameterLabel(name, type, index)).join(', ') + ')').join('\n');
                items.push(item);
            }
            const offset = document.offsetAt(position);
            for (const entry of declarations(result.tokens || [], offset, result.functions, result.constantTypes, result.inferred)) {
                const item = new vscode.CompletionItem(entry.name,
                    entry.isFunction ? vscode.CompletionItemKind.Function : vscode.CompletionItemKind.Variable);
                item.detail = entry.isFunction
                    ? entry.type + ' ' + entry.name + '(' + entry.parameters.map(parameter =>
                        parameter.type + ' ' + parameter.name).join(', ') + ')'
                    : entry.type;
                items.push(item);
            }
            const current = document.lineAt(position.line).text.slice(0, position.character);
            const member = /([A-Za-z_]\w*)\.(?:[A-Za-z_]\w*)?$/.exec(current);
            if (member && result.constants && result.constants[member[1]] !== undefined) {
                const constantText = result.constants[member[1]];
                const response = await request({ path: result.constantPaths[member[1]], source: constantText, constantsFile: true });
                return declarations(response.tokens || [], constantText.length + 1).map(entry => {
                    const item = new vscode.CompletionItem(entry.name, vscode.CompletionItemKind.Constant);
                    item.detail = response.members?.[entry.name] || entry.type;
                    return item;
                });
            }
            const before = document.getText(new vscode.Range(new vscode.Position(0, 0), position));
            if (/^(?:\s|\/\/[^\r\n]*(?:\r?\n|$)|\/\*[\s\S]*?\*\/)*#?$/.test(before)) {
                const header = new vscode.CompletionItem('#tttym 1', vscode.CompletionItemKind.Snippet);
                header.insertText = new vscode.SnippetString('#tttym 1\n$0');
                if (before.endsWith('#')) header.range = new vscode.Range(position.translate(0, -1), position);
                items.push(header);
            }
            return items;
        }
    }, '#', '.'));

    context.subscriptions.push(vscode.languages.registerDefinitionProvider(macroSelector, {
        async provideDefinition(document, position) {
            const word = document.getWordRangeAtPosition(position);
            if (!word) return undefined;
            const name = document.getText(word);
            const textBefore = document.lineAt(position.line).text.slice(0, word.start.character);
            const member = /([A-Za-z_]\w*)\.$/.exec(textBefore);
            const result = await analyze(document);
            if (member && result.constants?.[member[1]] !== undefined) {
                const source = result.constants[member[1]];
                const target = result.constantPaths[member[1]];
                const response = await request({ path: target, source, constantsFile: true });
                const entry = declarations(response.tokens || [], source.length + 1).find(item => item.name === name);
                if (entry) return new vscode.Location(vscode.Uri.file(target),
                    new vscode.Position(entry.token.Line - 1, entry.token.Column - 1));
                return undefined;
            }
            const entries = declarations(result.tokens || [], document.offsetAt(position), result.functions, result.constantTypes, result.inferred);
            const entry = entries.reverse().find(item => item.name === name);
            return entry ? new vscode.Location(document.uri,
                new vscode.Position(entry.token.Line - 1, entry.token.Column - 1)) : undefined;
        }
    }));

    context.subscriptions.push(vscode.languages.registerHoverProvider(macroSelector, {
        async provideHover(document, position) {
            const range = document.getWordRangeAtPosition(position);
            if (!range) return undefined;
            const name = document.getText(range);
            const result = await analyze(document);
            const before = document.lineAt(position.line).text.slice(0, range.start.character);
            const member = /([A-Za-z_]\w*)\.$/.exec(before);
            if (member && result.constants?.[member[1]] !== undefined) {
                const response = await request({ path: result.constantPaths[member[1]],
                    source: result.constants[member[1]], constantsFile: true });
                const type = response.members?.[name];
                return type ? new vscode.Hover(type + ' ' + name, range) : undefined;
            }
            const signatures = result.functions?.[name];
            if (signatures) {
                const detail = signatures.map(signature =>
                    signature.Result + ' ' + name + '(' + signature.Parameters.map((type, index) =>
                        parameterLabel(name, type, index)).join(', ') + ')').join('\n');
                const description = descriptions[name]
                    ? '\n' + descriptions[name][japanese ? 1 : 0]
                    : (japanese ? '\nTabTty の組み込み関数です。' : '\nTabTty built-in function.');
                return new vscode.Hover(detail + description, range);
            }
            const entry = declarations(result.tokens || [], document.offsetAt(position), result.functions, result.constantTypes, result.inferred).reverse()
                .find(item => item.name === name);
            if (!entry) return undefined;
            const detail = entry.isFunction
                ? entry.type + ' ' + entry.name + '(' + entry.parameters.map(parameter =>
                    parameter.type + ' ' + parameter.name).join(', ') + ')'
                : entry.type + ' ' + entry.name;
            return new vscode.Hover(detail, range);
        }
    }));

    context.subscriptions.push(vscode.languages.registerSignatureHelpProvider(macroSelector, {
        async provideSignatureHelp(document, position) {
            const result = await analyze(document);
            if (result.unsupportedVersion) return undefined;
            const tokens = (result.tokens || []).filter(token => token.Offset < document.offsetAt(position));
            const stack = [];
            for (let index = 0; index < tokens.length; index++) {
                const token = tokens[index];
                if (token.Text === '(') {
                    const previous = tokens[index - 1];
                    stack.push({ name: previous?.Kind === 'id' ? previous.Text : undefined, parameter: 0 });
                } else if (token.Text === ')') {
                    stack.pop();
                } else if (token.Text === ',' && stack.length) {
                    stack[stack.length - 1].parameter++;
                }
            }
            const call = stack[stack.length - 1];
            if (!call?.name) return undefined;
            let signatures = result.functions && result.functions[call.name];
            if (!signatures) {
                const helper = declarations(result.tokens || [], document.offsetAt(position))
                    .find(entry => entry.isFunction && entry.name === call.name);
                if (helper) signatures = [{ Result: helper.type,
                    Parameters: helper.parameters.map(parameter => parameter.type + ' ' + parameter.name) }];
            }
            if (!signatures) return undefined;
            const help = new vscode.SignatureHelp();
            help.signatures = signatures.map(signature => {
                const parameters = signature.Parameters.map((type, index) => parameterLabel(call.name, type, index));
                const info = new vscode.SignatureInformation(
                    signature.Result + ' ' + call.name + '(' + parameters.join(', ') + ')');
                info.parameters = parameters.map(parameter => new vscode.ParameterInformation(parameter));
                return info;
            });
            const matching = signatures.findIndex(signature => signature.Parameters.length > call.parameter);
            help.activeSignature = Math.max(0, matching);
            help.activeParameter = call.parameter;
            return help;
        }
    }, '(', ','));

    context.subscriptions.push(vscode.languages.registerCodeActionsProvider(macroSelector, {
        async provideCodeActions(document, range, actionContext) {
            if (!actionContext.diagnostics.some(item => item.code === 'TypeMismatch')) return [];
            const result = await analyze(document);
            const tokens = result.tokens || [];
            const call = tokens.findIndex(token => token.Text === 'WaitPrompt' &&
                token.Line - 1 === range.start.line && token.Column - 1 === range.start.character);
            const literal = tokens[call + 2];
            if (call < 0 || tokens[call + 1]?.Text !== '(' || literal?.Kind !== 'str' ||
                typeof literal.Value !== 'string' || literal.EndOffset < 0) return [];
            const literalRange = new vscode.Range(document.positionAt(literal.Offset),
                document.positionAt(literal.EndOffset));
            const text = document.getText(literalRange);
            return ['Literal', 'Regex'].map(name => {
                const title = name === 'Literal'
                    ? (japanese ? '文字列そのものを待つ' : 'Match literal text')
                    : (japanese ? '正規表現として待つ' : 'Match a regular expression');
                const action = new vscode.CodeAction(title, vscode.CodeActionKind.QuickFix);
                action.edit = new vscode.WorkspaceEdit();
                action.edit.replace(document.uri, literalRange, name + '(' + text + ')');
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
