const assert = require('assert');

module.exports = async function checkReferences(providers, doc) {
    const prefix = '#tttym 1\nlong Index = 4;\nvar match = WaitLine(Literal("OK"));\n';
    const local = prefix.indexOf('Index');

    async function check(marked, hoverText, destination, withPrefix = true) {
        const source = (withPrefix ? prefix : '') + marked;
        const offset = source.indexOf('|');
        doc.text = source.replace('|', '');
        const position = doc.positionAt(offset);
        const hover = await providers.hover.provideHover(doc, position);
        if (typeof hoverText === 'string') {
            assert.strictEqual(hover?.contents, hoverText, marked);
            assert(doc.offsetAt(hover.range.start) <= offset && doc.offsetAt(hover.range.end) >= offset, marked);
        } else if (hoverText instanceof RegExp) assert.match(hover?.contents ?? '', hoverText, marked);
        else assert.strictEqual(hover, undefined, marked);
        const definition = await providers.definition.provideDefinition(doc, position);
        if (destination === undefined) assert.strictEqual(definition, undefined, marked);
        else {
            assert.strictEqual(definition?.uri, doc.uri, marked);
            assert.deepStrictEqual(definition.position, doc.positionAt(destination), marked);
        }
    }

    await check('long first = match.|Index;', 'long WaitMatch.Index');
    await check('long size = match.Le|ngth;', 'long WaitMatch.Length');
    await check('long size = match.Groups[0].Len|gth;', 'long CaptureGroup.Length');
    await check('Write("In|dex");', undefined);
    await check('Write(""); // In|dex', undefined);
    await check('/* Len|gth */', undefined);
    await check('Write($"In|dex={Index}");', undefined);
    await check('Write($"Index={In|dex}");', 'long Index', local);
    await check('Sleep(In|dex', 'long Index', local);
    await check('var n = Le|ngth("x");', /long Length\(/);
    await check('string re|tries = "";', 'string retries');
    await check('long Tw|ice(long n) { return n + n; }', 'long Twice(long n)');
    await check('long Twice(long |n) { return n + n; }', 'long n');
    await check('long Twice(long n) { return n + n; }\nSleep(Tw|ice(3));', 'long Twice(long n)', prefix.length + 5);

    doc.languageId = 'tabtty-constants';
    try { await check('long |Index = 4;', 'long Index', undefined, false); }
    finally { doc.languageId = 'tabtty-macro'; }
};
