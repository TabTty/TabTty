'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const lines = [];
const filler = 'X'.repeat(96);
for (let index = 0; index < 4096; index++) {
    const digits = String(index).padStart(4, '0');
    lines.push([
        `INFO status index=${digits} ${filler}\n`,
        `WARN count=${digits} ${filler}\r\n`,
        `ERROR code=${digits} ${filler}\n`,
        `status index=${digits} 接続状態を確認しています ${filler}\n`,
        `\x1b[31mERROR\x1b[0m status index=${digits} ${filler}\n`,
        `progress count=${digits} 10%\rprogress count=${digits} 100% ${filler}\r\n`,
    ][index % 6]);
}
const cycle = Buffer.from(lines.join(''), 'utf8');
const output = process.argv[2];
if (!output) throw new Error('Pass an output filename stem.');
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output + '.bin', cycle);
const digest = { lines: 4096, bytes: cycle.length, sha256: crypto.createHash('sha256').update(cycle).digest('hex') };
fs.writeFileSync(output + '.json', JSON.stringify(digest, null, 2) + '\n');
console.log(JSON.stringify(digest));
