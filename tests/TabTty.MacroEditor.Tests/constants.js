const assert = require('assert');

module.exports = async function checkConstants(providers, workspace, doc, shared, diagnostics) {
    const header = '#tttym 1\n#constants Common "shared.tttyc"\n';
    const declarations = header + '#constants Second "./shared.tttyc"\n';
    const cursor = text => doc.positionAt(doc.text.lastIndexOf(text) + 1);
    const complete = () => providers.completion.provideCompletionItems(doc, doc.positionAt(doc.text.length));
    const hover = text => providers.hover.provideHover(doc, cursor(text));
    const definition = text => providers.definition.provideDefinition(doc, cursor(text));
    const source = shared.getText;
    const many = Array.from({ length: 2049 }, (_, i) => 'long Constant' + i + ' = 0;').join('\n');
    let reads = 0;
    shared.getText = () => { reads++; return many; };
    doc.text = declarations + 'Sleep(Second.';
    const members = await complete();
    assert.strictEqual(reads, 1);
    assert.strictEqual(members.length, 2049);
    assert(members.some(member => member.label === 'Constant2048'));
    doc.text = declarations + 'Sleep(Common.Constant2048); Sleep(Second.Constant2048);';
    assert.deepStrictEqual(await diagnostics(), []);

    const other = { ...shared, uri: { scheme: 'file', fsPath: shared.uri.fsPath.replace('shared', 'Shared') } };
    workspace.textDocuments.push(other);
    doc.text = doc.text.replace('./shared.tttyc', 'Shared.tttyc');
    assert((await diagnostics()).some(item => item.code === 'ConstantsLimit'));
    workspace.textDocuments.pop();

    // Change an unsaved dependency while its first snapshot is in flight.
    reads = 0;
    shared.version = 1;
    shared.getText = () => {
        if (++reads === 1) {
            queueMicrotask(() => shared.version++);
            return 'long Old = 1;';
        }
        return 'long Current = 1;';
    };
    doc.text = declarations + 'Sleep(Second.';
    assert.deepStrictEqual((await complete()).map(item => item.label), ['Current']);
    assert.strictEqual(reads, 2);

    shared.getText = source;
    doc.text = header + 'string TimeoutMs = "local";\nSleep(Common /* alias */\n . /* member */ TimeoutMs);';
    assert.strictEqual((await hover('TimeoutMs')).contents, 'long TimeoutMs');
    assert.strictEqual((await definition('TimeoutMs')).uri.fsPath, shared.uri.fsPath);
    const alias = await definition('Common');
    assert.strictEqual(alias.uri, doc.uri);
    assert.deepStrictEqual(alias.position, doc.positionAt(header.indexOf('Common')));
    doc.text += '\nWrite(TimeoutMs);';
    assert.strictEqual((await hover('TimeoutMs')).contents, 'string TimeoutMs');
    doc.text += '\nWrite($"{Common.TimeoutMs}");';
    assert.strictEqual((await hover('TimeoutMs')).contents, 'long TimeoutMs');
    doc.text = header + 'string Unknown = "";\nWrite(Common.Unknown);';
    assert.strictEqual(await definition('Unknown'), undefined);
    assert.strictEqual(await hover('Unknown'), undefined);

    doc.text = doc.text.replace('shared.tttyc', 'unavailable.tttyc');
    assert.strictEqual(await definition('Unknown'), undefined);
    assert.strictEqual(await hover('Unknown'), undefined);
    assert.deepStrictEqual((await diagnostics()).map(item => item.code), ['ConstantsOpenRequired']);
    shared.getText = () => 'long Broken = "wrong";';
    doc.text = declarations.replace('#constants Common "shared.tttyc"', '#constants Common "unavailable.tttyc"') +
        '#constants Third "shared.tttyc"\nSleep(Second.Broken);';
    const dependencyErrors = (await diagnostics()).map(item => [item.range.start.line, item.code]).sort();
    assert.deepStrictEqual(dependencyErrors, [[1, 'ConstantsOpenRequired'], [2, 'ConstantsInvalid'], [3, 'ConstantsInvalid']]);
    doc.text = header.replace('shared.tttyc', 'unavailable.tttyc');
    doc.text += '\nSleep(Common /* comment */\n.';
    assert.deepStrictEqual(await complete(), []);

    reads = 0;
    shared.getText = () => { reads++; return source(); };
    for (const body of ['Sleep(0);\n#constants Late "shared.tttyc"', '#constants Bad "shared.tttyc";']) {
        doc.text = '#tttym 1\n' + body;
        const errors = await diagnostics();
        assert(errors.length && !errors.some(item => item.code === 'ConstantsOpenRequired'));
    }
    assert.strictEqual(reads, 0);
    shared.getText = source;

    for (const languageId of ['tabtty-macro', 'tabtty-constants']) {
        doc.languageId = languageId;
        const prefix = languageId === 'tabtty-macro' ? '#tttym 1\n' : '';
        doc.text = prefix + 'long Min = -09_223_372_036_854_775_808L;';
        assert.deepStrictEqual(await diagnostics(), []);
        doc.text = prefix + String.raw`string Bad = "😀\x1G";`;
        const [error] = await diagnostics();
        assert.strictEqual(error.code, 'StringEscape');
        assert.deepStrictEqual(error.range.start, doc.positionAt(doc.text.indexOf('\\')));
    }
    doc.languageId = 'tabtty-macro';
};
