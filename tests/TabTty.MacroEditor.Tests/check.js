const Module = require('module');
const path = require('path');
const assert = require('assert');
const providers = {};
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
        onDidChangeTextDocument: noop, onDidCloseTextDocument: noop, onDidGrantWorkspaceTrust: noop },
    languages: {
        createDiagnosticCollection: () => ({ set() {}, delete() {}, dispose() {} }),
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
    doc.text = '#tttym 1\nHelper();\nvoid Helper() {\n}';
    const helperTarget = await providers.definition.provideDefinition(doc, new Position(1, 3));
    assert.strictEqual(helperTarget.position.line, 2);
    doc.text = '#tttym 1\nlong top = 1;\nvoid Helper(long input) {\nSleep(';
    const local = await providers.completion.provideCompletionItems(doc, new Position(3, 6));
    assert(local.some(item => item.label === 'input'));
    assert(!local.some(item => item.label === 'top'));
    console.log('Macro completion, signature help, and definition passed.');
})().then(() => {
    for (const subscription of context.subscriptions) subscription.dispose();
}, error => {
    console.error(error);
    for (const subscription of context.subscriptions) subscription.dispose();
    process.exitCode = 1;
});
