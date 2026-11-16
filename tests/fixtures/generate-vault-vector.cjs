'use strict';
// Fixed public test data. Never use these salts, nonces, passwords or keys for user data.
const crypto = require('node:crypto');
const fs = require('node:fs');
const password = 'independent-vector-password';
const uuid = Buffer.from('00112233445546778899aabbccddeeff', 'hex');
const root = Buffer.from(Array.from({ length: 32 }, (_, i) => i));
const header = Buffer.alloc(184);
header.write('TTTYKEY\0', 0, 'ascii');
header.writeUInt16BE(1, 8);
header.writeUInt32BE(184, 12);
uuid.copy(header, 16);
for (let offset = 32; offset < 40; offset += 2) header.writeUInt16BE(1, offset);
header.writeUInt32BE(65536, 40);
header.writeUInt32BE(3, 44);
header.writeUInt32BE(4, 48);
Buffer.alloc(16, 0x11).copy(header, 52);
Buffer.alloc(12, 0x22).copy(header, 68);
const kek = crypto.argon2Sync('argon2id', { message: password, nonce: header.subarray(52, 68),
    parallelism: 4, tagLength: 32, memory: 65536, passes: 3 });
function encrypt(key, nonce, plaintext, aad) {
    const cipher = crypto.createCipheriv('aes-256-gcm', key, nonce);
    cipher.setAAD(aad);
    const body = Buffer.concat([cipher.update(plaintext), cipher.final()]);
    return { body, tag: cipher.getAuthTag() };
}
const wrapped = encrypt(kek, header.subarray(68, 80), root,
    Buffer.concat([Buffer.from('TabTty/vault/v1/wrap\0'), header.subarray(0, 80)]));
wrapped.body.copy(header, 80);
wrapped.tag.copy(header, 112);
Buffer.alloc(32, 0x33).copy(header, 128);
Buffer.alloc(12, 0x44).copy(header, 160);
const payload = Buffer.from('{"schemaVersion":1,"vaultId":"00112233-4455-4677-8899-aabbccddeeff","entries":[]}');
header.writeBigUInt64BE(BigInt(payload.length), 172);
const info = Buffer.concat([Buffer.from('TabTty/vault/v1/payload-key\0'), header.subarray(8, 10),
    header.subarray(34, 38), header.subarray(16, 32)]);
const key = Buffer.from(crypto.hkdfSync('sha256', root, header.subarray(128, 160), info, 32));
const encrypted = encrypt(key, header.subarray(160, 172), payload,
    Buffer.concat([Buffer.from('TabTty/vault/v1/payload\0'), header]));
const output = { generator: `Node ${process.version}, OpenSSL ${process.versions.openssl}`, password,
    root: root.toString('hex'), kek: kek.toString('hex'), plaintext: payload.toString(),
    file: Buffer.concat([header, encrypted.body, encrypted.tag]).toString('hex') };
fs.writeFileSync(require('node:path').join(__dirname, 'vault-v1-vector.json'), JSON.stringify(output, null, 2) + '\n');
root.fill(0);
kek.fill(0);
key.fill(0);
