const Module = require('module');
const path = require('path');
const assert = require('assert');
const providers = {};
let onDocumentChange, onDiagnostics;
class Position {
    constructor(line, character) { this.line = line; this.character = character; }
    translate(line, character) { return new Position(this.line + line, this.character + character); }
}
class Range { constructor(start, end) { this.start = start; this.end = end; } }
class Diagnostic { constructor(range, message) { this.range = range; this.message = message; } }
class CompletionItem { constructor(label, kind) { this.label = label; this.kind = kind; } }
class SignatureInformation { constructor(label) { this.label = label; } }
class ParameterInformation { constructor(label) { this.label = label; } }
class CodeAction { constructor(title, kind) { this.title = title; this.kind = kind; } }
class WorkspaceEdit { replace(uri, range, text) { this.replacement = { uri, range, text }; } }
const doc = {
    languageId: 'tabtty-macro', version: 1, isClosed: false,
    uri: { scheme: 'file', fsPath: 'C:\\temp\\demo.tttym', toString: () => 'demo' },
    text: '#tttym 1\nlong before = 1;\nWaitPrompt(',
    getText(range) {
        if (!range) return this.text;
        return this.text.slice(this.offsetAt(range.start), this.offsetAt(range.end));
    },
    offsetAt(pos) {
        const lines = this.text.split('\n');
        return lines.slice(0, pos.line).reduce((total, line) => total + line.length + 1, 0) + pos.character;
    },
    positionAt(offset) {
        const lines = this.text.slice(0, offset).split('\n');
        return new Position(lines.length - 1, lines[lines.length - 1].length);
    },
    lineAt(line) { return { text: this.text.split('\n')[line] }; },
    getWordRangeAtPosition(pos) {
        const text = this.lineAt(pos.line).text;
        const left = text.slice(0, pos.character).search(/[A-Za-z_]\w*$/);
        if (left < 0) return undefined;
        const match = /^[A-Za-z_]\w*/.exec(text.slice(left));
        return new Range(new Position(pos.line, left), new Position(pos.line, left + match[0].length));
    }
};
const noop = () => ({ dispose() {} });
const vscode = {
    Position, Range, Diagnostic, CompletionItem, SignatureInformation, ParameterInformation,
    SignatureHelp: class {}, SnippetString: class { constructor(value) { this.value = value; } },
    CodeAction, WorkspaceEdit,
    Hover: class { constructor(contents) { this.contents = contents; } },
    Location: class { constructor(uri, position) { this.uri = uri; this.position = position; } },
    Uri: { file: fsPath => ({ fsPath }) },
    CompletionItemKind: { Function: 1, Variable: 2, Constant: 3, Snippet: 4 },
    env: { language: 'en' },
    workspace: { isTrusted: false, textDocuments: [doc], onDidOpenTextDocument: noop,
        onDidChangeTextDocument: callback => { onDocumentChange = callback; return noop(); },
        onDidCloseTextDocument: noop, onDidGrantWorkspaceTrust: noop },
    languages: {
        createDiagnosticCollection: () => ({ set(uri, diagnostics) { onDiagnostics?.(diagnostics); },
            delete() {}, dispose() {} }),
        registerCompletionItemProvider: (_, provider) => (providers.completion = provider, noop()),
        registerDefinitionProvider: (_, provider) => (providers.definition = provider, noop()),
        registerHoverProvider: (_, provider) => (providers.hover = provider, noop()),
        registerSignatureHelpProvider: (_, provider) => (providers.signature = provider, noop()),
        registerCodeActionsProvider: (_, provider) => (providers.action = provider, noop())
    },
    CodeActionKind: { QuickFix: 1 }
};
const original = Module._load;
Module._load = function(request, ...args) {
    if (request === 'vscode') return vscode;
    return original.call(this, request, ...args);
};
const extension = require(process.env.TABTTY_MACRO_EXTENSION ||
    '../../vscode/tabtty-macro/extension.js');
const context = { subscriptions: [], asAbsolutePath: () =>
    path.resolve(process.env.TABTTY_MACRO_EDITOR ||
        'src/TabTty.MacroEditor/bin/Release/net10.0/win-x64/publish/TabTty.MacroEditor.exe') };
extension.activate(context);
(async () => {
    const position = new Position(2, 'WaitPrompt('.length);
    const completion = await providers.completion.provideCompletionItems(doc, position);
    assert(completion.some(item => item.label === 'WaitPrompt'));
    assert(completion.some(item => item.label === 'before'));
    const signature = await providers.signature.provideSignatureHelp(doc, position);
    assert(signature.signatures[0].label.includes('WaitPattern pattern'));
    doc.text = '#tttym 1\nWriteLine("command", mark';
    const writeOptions = await providers.completion.provideCompletionItems(doc, doc.positionAt(doc.text.length));
    assert.deepStrictEqual(writeOptions.map(item => [item.label, item.detail, item.insertText]),
        [['markReceiveBoundary', 'bool', 'markReceiveBoundary: ']]);
    doc.text = '#tttym 1\nConnect("profile", logCollision: "error", /* path */ ';
    const connectOptions = await providers.completion.provideCompletionItems(doc, doc.positionAt(doc.text.length));
    assert.deepStrictEqual(connectOptions.map(item => [item.label, item.detail]),
        [['logDirectory', 'string'], ['logFilenameTemplate', 'string']]);
    doc.text = '#tttym 1\nConnect("profile", logCollision: "append", logDirectory: "logs");';
    const permissionPosition = doc.positionAt(doc.text.indexOf('Collision'));
    const replacementOptions = await providers.completion.provideCompletionItems(doc, permissionPosition);
    assert.deepStrictEqual(replacementOptions.map(item => [item.insertText, item.detail]),
        [['logFilenameTemplate', 'string'], ['logCollision', 'string']]);
    const namedSignature = await providers.signature.provideSignatureHelp(doc, doc.positionAt(doc.text.indexOf('append') + 2));
    assert.strictEqual(namedSignature.activeParameter, 3);
    doc.text = '#tttym 1\nlong before = 1;\nSleep(before);';
    const definition = await providers.definition.provideDefinition(doc, new Position(2, 8));
    assert.strictEqual(definition.position.line, 1);
    const constants = { languageId: 'tabtty-constants', uri: { scheme: 'file', fsPath: 'C:\\temp\\shared.tttyc' },
        getText: () => 'long TimeoutMs = 5000;' };
    vscode.workspace.textDocuments.push(constants);
    doc.text = '#tttym 1\n#constants Common "shared.tttyc"\nSleep(Common.';
    const members = await providers.completion.provideCompletionItems(doc, new Position(2, 13));
    assert(members.some(item => item.label === 'TimeoutMs' && item.detail === 'long'));
    doc.text = '#tttym 1\n#constants Common "shared.tttyc"\nSleep(Common.TimeoutMs);';
    const target = await providers.definition.provideDefinition(doc, new Position(2, 18));
    assert(target.uri.fsPath.toLowerCase().endsWith('shared.tttyc'));
    assert.strictEqual(target.position.line, 0);
    async function getDiagnostics() {
        doc.version++;
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => reject(new Error('Timed out waiting for diagnostics')), 10000);
            onDiagnostics = items => { onDiagnostics = undefined; clearTimeout(timer); resolve(items); };
            onDocumentChange({ document: doc });
        });
    }
    await require('./constants')(providers, vscode.workspace, doc, constants, getDiagnostics);
    await require('./constants-casing')(providers, vscode.workspace, doc);
    doc.text = '#tttym 1\nvar x = Macro\n    .Args\n    .Wrong;';
    const memberError = (await getDiagnostics()).find(item => item.code === 'UnknownMember');
    assert.deepStrictEqual(memberError.range, new Range(new Position(3, 5), new Position(3, 10)));
    doc.text = '#tttym 1\nWaitPrompt("Ready>");';
    const actions = await providers.action.provideCodeActions(doc,
        new Range(new Position(1, 0), new Position(1, 1)),
        { diagnostics: [{ code: 'TypeMismatch' }] });
    assert.strictEqual(actions.length, 2);
    assert.strictEqual(actions[0].edit.replacement.text, 'Literal("Ready>")');
    assert.strictEqual(actions[1].edit.replacement.text, 'Regex("Ready>")');
    doc.text = '#tttym 1\nWaitPrompt(\n  "Ready>");';
    const multilineActions = await providers.action.provideCodeActions(doc,
        new Range(new Position(1, 0), new Position(1, 1)),
        { diagnostics: [{ code: 'TypeMismatch' }] });
    assert.strictEqual(multilineActions[0].edit.replacement.text, 'Literal("Ready>")');
    doc.text = '#';
    const header = await providers.completion.provideCompletionItems(doc, new Position(0, 1));
    const version = header.find(item => item.label === '#tttym 1');
    assert(version);
    assert.strictEqual(version.range.start.character, 0);
    doc.text = '#tttym 2\nWaitPrompt(';
    const unsupported = await providers.completion.provideCompletionItems(doc, new Position(1, 11));
    assert.strictEqual(unsupported.length, 0);
    doc.text = '#tttym 2\nWriteLine(' + '$"{'.repeat(129);
    const [versionDiagnostic] = await getDiagnostics();
    assert.strictEqual(versionDiagnostic.code, 'VersionLine');
    assert.deepStrictEqual(versionDiagnostic.range.start, new Position(0, 7));
    doc.text = '#tttym 1\nHelper();\nvoid Helper() {\n}';
    const helperTarget = await providers.definition.provideDefinition(doc, new Position(1, 3));
    assert.strictEqual(helperTarget.position.line, 2);
    doc.text = '#tttym 1\nlong top = 1;\nvoid Helper(long input) {\nSleep(';
    const local = await providers.completion.provideCompletionItems(doc, new Position(3, 6));
    assert(local.some(item => item.label === 'input'));
    assert(!local.some(item => item.label === 'top'));
    function positionOf(text, within = 0) {
        const offset = doc.text.indexOf(text);
        assert(offset >= 0, text);
        return doc.positionAt(offset + within);
    }
    async function checkVariable(name, use, expectedType, declaration) {
        const items = await providers.completion.provideCompletionItems(doc, use);
        assert.strictEqual(items.find(item => item.label === name)?.detail, expectedType);
        const hover = await providers.hover.provideHover(doc, use);
        assert.strictEqual(hover?.contents, expectedType && expectedType + ' ' + name);
        const location = await providers.definition.provideDefinition(doc, use);
        assert.deepStrictEqual(location?.position, declaration);
    }
    doc.text = '#tttym 1\ntry {} catch (MacroError err) {\nvar code = err.Code;\n}\nWaitPrompt(err';
    await checkVariable('err', positionOf('err.Code', 2), 'MacroError', positionOf('err)'));
    await checkVariable('err', positionOf('WaitPrompt(err', 13), undefined, undefined);
    doc.text = '#tttym 1\r\ntry {} catch (MacroError err) {} try {} catch (MacroError err) {\r\nvar code = err.Code;';
    await checkVariable('err', positionOf('err.Code', 2), 'MacroError',
        doc.positionAt(doc.text.lastIndexOf('err)')));
    doc.text = '#tttym 1\nvar ratio = 1 + 0.5;\ndouble copy = ratio;\nWaitPrompt(';
    for (const [source, type] of [['1 + 0.5', 'double'], ['1 + 2', 'long'], ['-1D', 'double']]) {
        doc.text = doc.text.replace(/var ratio = [^;]+;/, 'var ratio = ' + source + ';');
        await checkVariable('ratio', positionOf('= ratio;', 4), type, positionOf('ratio ='));
    }
    for (const [declaration, expectedType] of [['long retries = ;', 'long'], ['var retries = ;', '?'],
        ['var retries = 1D + ;', '?']]) {
        doc.text = '#tttym 1\n' + declaration + '\nSleep(retries);';
        const diagnostics = await getDiagnostics();
        assert(diagnostics.some(item => item.code === 'ExpressionExpected' &&
            item.range.start.line === 1 && item.range.start.character === declaration.indexOf(';')));
        await checkVariable('retries', positionOf('Sleep(retries', 8), expectedType, positionOf('retries ='));
        const initializer = await providers.completion.provideCompletionItems(doc, positionOf('= ', 2));
        assert(!initializer.some(item => item.label === 'retries'));
    }
    doc.text = '#tttym 1\nfor (long i = 1; i < 2; i++) { Sleep(i); }\nWaitPrompt(i';
    await checkVariable('i', positionOf('Sleep(i', 7), 'long', positionOf('i ='));
    await checkVariable('i', positionOf('WaitPrompt(i', 12), undefined, undefined);
    for (const [source, name, parameter, parameterName] of [
        ['WaitAnyPrompt([a: Literal("A"), b: Literal("B|")], 1000);', 'Literal', 0, 'text'],
        ['WaitAnyPrompt([a: Literal("A"), |b: Literal("B")], 1000);', 'WaitAnyPrompt', 0, 'patterns'],
        ['WaitAnyPrompt([a: Literal("A")],|', 'WaitAnyPrompt', 1, 'timeoutMs'],
        ['Sleep((|1 + 2));', 'Sleep', 0, 'milliseconds'],
        ['WriteLine($"{{n}},| {FormatNumber(n, "D")}");', 'WriteLine', 0, 'text'],
        ['WriteLine($"{{n}}, {FormatNumber(n,|', 'FormatNumber', 1, 'format'],
        ['WriteLine($"{{n}}, {FormatNumber(n, |"D")}");', 'FormatNumber', 1, 'format'],
        ['WriteLine($"{FormatNumber(n /* } , ( */, |"D")}");', 'FormatNumber', 1, 'format'],
        ['WriteLine($"{FormatNumber(n, "},(|")}");', 'FormatNumber', 1, 'format'],
        ['WriteLine($"{FormatNumber(n, "D")|}");', 'WriteLine', 0, 'text'],
        ['WriteLine(' + '$"{'.repeat(48) + 'FormatNumber(n,|', 'FormatNumber', 1, 'format']
    ]) {
        const prefix = '#tttym 1\nlong n = 42;\n';
        doc.text = prefix + source.replace('|', '');
        const help = await providers.signature.provideSignatureHelp(doc, doc.positionAt(prefix.length + source.indexOf('|')));
        const signature = help?.signatures[help.activeSignature];
        assert(signature?.label.includes(' ' + name + '('), source + ': ' + signature?.label);
        assert.strictEqual(help.activeParameter, parameter, source);
        assert(signature.parameters[parameter].label.endsWith(' ' + parameterName), source);
    }
    for (const expression of ['"ready" /*keep*/ + ">"', '( "ready" + ">" )', 'Trim(" ready> ")']) {
        const line = 'WaitPrompt( ' + expression + ' , 1000);';
        doc.text = '#tttym 1\n' + line;
        const originalText = doc.text;
        const callRange = new Range(new Position(1, 0), new Position(1, 1));
        const args = { diagnostics: await getDiagnostics() };
        assert(args.diagnostics.some(item => item.message.includes('WaitPattern')));
        const actions = await providers.action.provideCodeActions(doc, callRange, args);
        assert.strictEqual(actions.length, 2);
        for (const [index, kind] of ['Literal', 'Regex'].entries()) {
            doc.text = originalText;
            const { range, text } = actions[index].edit.replacement;
            const result = doc.text.slice(0, doc.offsetAt(range.start)) + text + doc.text.slice(doc.offsetAt(range.end));
            assert.strictEqual(result, doc.text.replace(expression, kind + '(' + expression + ')'));
            doc.text = result;
            assert.deepStrictEqual(await getDiagnostics(), []);
        }
    }
    for (const line of ['WaitPrompt("ready" + , 1000);', 'WaitPrompt(42);', 'WaitPrompt(Literal("ok"), "1000");']) {
        doc.text = '#tttym 1\n' + line;
        const actions = await providers.action.provideCodeActions(doc, new Range(new Position(1, 0), new Position(1, 1)),
            { diagnostics: [{ code: 'TypeMismatch' }] });
        assert.strictEqual(actions.length, 0, line);
    }
    for (const [marked, replaced] of [
        ['|', ''], ['  |   Sleep(0);', ''], ['|Sleep(0);', ''], ['/*memo*/ #|Sleep(0);', '#'],
        ['// memo|', null], ['// memo|\n', null], ['/* me|mo */', null], ['/* memo|', null],
        ['// memo\r|', ''], ['// memo\n|', ''], ['// memo\r\n|', ''], ['/* memo */|', ''],
        ['|\n#tttym 1', null], ['|\n#tttym 2', null], ['|Sleep(0);\n#tttym 1', null],
        ['|?\n#tttym 2', null], ['/* #tttym 2 */|Sleep(0);', '']
    ]) {
        const offset = marked.indexOf('|');
        doc.text = marked.replace('|', '');
        const items = await providers.completion.provideCompletionItems(doc, doc.positionAt(offset));
        const header = items.find(item => item.label === '#tttym 1');
        if (replaced === null) assert(!header, marked);
        else {
            assert(header, marked);
            assert.strictEqual(doc.getText(header.range), replaced, marked);
            assert.strictEqual(doc.offsetAt(header.range.end), offset, marked);
            assert.strictEqual(header.insertText.value, '#tttym 1\n$0');
        }
    }
    console.log('Macro completion, signature help, and definition passed.');
})().then(() => {
    for (const subscription of context.subscriptions) subscription.dispose();
}, error => {
    console.error(error);
    for (const subscription of context.subscriptions) subscription.dispose();
    process.exitCode = 1;
});
