const assert = require('assert');
const fs = require('fs');
const os = require('os');
const path = require('path');

module.exports = async function checkOpenConstantsCasing(providers, workspace, doc) {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'tabtty-constants-case-'));
    const saved = path.join(directory, 'mixed.tttyc');
    const variant = path.join(directory, 'Mixed.tttyc');
    const originalUri = doc.uri;
    const source = doc.text;
    const opened = { uri: { scheme: 'file', fsPath: saved }, version: 1, getText: () => 'long Unsaved = 1;' };
    const complete = async () => (await providers.completion.provideCompletionItems(doc, doc.positionAt(doc.text.length)))
        .map(item => item.label);
    let separateFile = false;
    try {
        fs.writeFileSync(saved, 'long OnDisk = 0;');
        doc.uri = { ...originalUri, fsPath: path.join(directory, 'test.tttym') };
        doc.text = '#tttym 1\n#constants Common "Mixed.tttyc"\nSleep(Common.';
        assert.strictEqual(workspace.isTrusted, false);
        assert.deepStrictEqual(await complete(), []);
        workspace.textDocuments.push(opened);
        if (fs.existsSync(variant)) {
            assert.deepStrictEqual(await complete(), ['Unsaved']);
        } else {
            fs.writeFileSync(variant, 'long Distinct = 0;');
            separateFile = true;
            assert.deepStrictEqual(await complete(), []);
        }
        doc.text = doc.text.replace('Mixed.tttyc', 'mixed.tttyc');
        assert.deepStrictEqual(await complete(), ['Unsaved']);
    } finally {
        const index = workspace.textDocuments.indexOf(opened);
        if (index >= 0) workspace.textDocuments.splice(index, 1);
        doc.uri = originalUri;
        doc.text = source;
        if (separateFile) fs.unlinkSync(variant);
        fs.unlinkSync(saved);
        fs.rmdirSync(directory);
    }
};
