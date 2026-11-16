'use strict';
// Apply the compatibility corrections only to the ignored test peer.
const fs = require('node:fs');
const path = require('node:path');
const folder = path.join(__dirname, 'node_modules/ssh2/lib/protocol');
const protocolFile = path.join(folder, 'Protocol.js');
const protocol = fs.readFileSync(protocolFile, 'utf8');
const missingComma = "'ecdsa-sha2-nistp256,ecdsa-sha2-nistp384,ecdsa-sha2-nistp521'";
if (protocol.includes(missingComma))
  fs.writeFileSync(protocolFile, protocol.replace(missingComma, missingComma.slice(0, -1) + ",'"));

const file = path.join(folder, 'kex.js');
let source = fs.readFileSync(file, 'utf8');
if (!source.includes('class TabTtyHybridFixture')) {
  const hybrid = `
  // Test-only RFC 10042 peer. Node supplies independent ML-KEM and X25519 primitives.
  class TabTtyHybridFixture extends KeyExchange {
    constructor(...args) { super(...args); this.type = 'hybrid'; this.hashName = 'sha256'; }
    convertPublicKey(value) { return value; }
    getPublicKey() { return this._public; }
    computeSecret(value) {
      const c = require('node:crypto');
      try {
        if (value.length !== 1216) throw Error('Invalid hybrid client size');
        const kem = c.createPublicKey({format:'der',type:'spki',key:Buffer.concat([
          Buffer.from('308204b2300b0609608648016503040402038204a100','hex'),value.subarray(0,1184)])});
        const result = c.encapsulate(kem);
        const pair = c.generateKeyPairSync('x25519');
        const remote = c.createPublicKey({format:'der',type:'spki',key:Buffer.concat([
          Buffer.from('302a300506032b656e032100','hex'),value.subarray(1184)])});
        const classical = c.diffieHellman({privateKey:pair.privateKey,publicKey:remote});
        if (classical.equals(Buffer.alloc(32))) throw Error('Invalid X25519 secret');
        this._public = Buffer.concat([result.ciphertext,pair.publicKey.export({format:'der',type:'spki'}).subarray(-32)]);
        const secret = c.createHash('sha256').update(result.sharedKey).update(classical).digest();
        result.sharedKey.fill(0); classical.fill(0);
        return secret;
      } catch (error) { return error; }
    }
  }
`;
  source = source.replace('  class Curve25519Exchange extends KeyExchange {', hybrid + '\n  class Curve25519Exchange extends KeyExchange {');
  source = source.replace("        case 'curve25519-sha256':",
    "        case 'mlkem768x25519-sha256': return new TabTtyHybridFixture(...args);\n        case 'curve25519-sha256':");
  fs.writeFileSync(file, source);
  const constants = path.join(folder, 'constants.js');
  let lists = fs.readFileSync(constants, 'utf8');
  lists = lists.replace('const SUPPORTED_KEX = DEFAULT_KEX.concat([', "DEFAULT_KEX.unshift('mlkem768x25519-sha256');\nconst SUPPORTED_KEX = DEFAULT_KEX.concat([");
  fs.writeFileSync(constants, lists);
}
