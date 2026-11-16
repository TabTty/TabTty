'use strict';
// Loopback-only fixture with synthetic identities. Never use this as an SSH service.
const { Server, utils } = require('ssh2');
const fs = require('node:fs');
const crypto = require('node:crypto');
const publicBytes = Buffer.from('d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a', 'hex');
let publicBlob = Buffer.concat([Buffer.from('0000000b7373682d6564323535313900000020', 'hex'), publicBytes]);
let publicKey = crypto.createPublicKey({ type: 'spki', format: 'der',
  key: Buffer.concat([Buffer.from('302a300506032b6570032100', 'hex'), publicBytes]) });
let fixtureParsed;
if (process.env.TABTTY_INTEROP_USER_KEY) {
  publicBlob = fs.readFileSync(process.env.TABTTY_INTEROP_USER_KEY + '.pubblob');
  const typeLength = publicBlob.readUInt32BE(0);
  const type = publicBlob.subarray(4, 4 + typeLength).toString();
  fixtureParsed = utils.parseKey(type + ' ' + publicBlob.toString('base64'));
  if (fixtureParsed instanceof Error) throw fixtureParsed;
}
const key = fs.readFileSync(require('node:path').join(__dirname, process.env.TABTTY_INTEROP_HOST_FILE || 'fixture_host'));
const algorithms = {};
if (process.env.TABTTY_INTEROP_KEX) algorithms.kex = [process.env.TABTTY_INTEROP_KEX];
if (process.env.TABTTY_INTEROP_HOST_SIGNATURE) algorithms.serverHostKey = [process.env.TABTTY_INTEROP_HOST_SIGNATURE];
if (process.env.TABTTY_INTEROP_CIPHER) algorithms.cipher = [process.env.TABTTY_INTEROP_CIPHER];
if (process.env.TABTTY_INTEROP_MAC) algorithms.hmac = [process.env.TABTTY_INTEROP_MAC];
const server = new Server({ hostKeys: [key], algorithms }, client => {
  let username;
  client.on('error', error => console.error(error.message));
  client.on('authentication', ctx => {
    username = ctx.username;
    if (ctx.username === 'tabtty-key') {
      if (ctx.method !== 'publickey' || !ctx.key.data.equals(publicBlob)) {
        if (ctx.method === 'publickey') console.error('Fixture public key mismatch', ctx.key.algo, ctx.key.data.length, publicBlob.length);
        return ctx.reject(['publickey']);
      }
      // ssh2 1.17 normalizes this name before PK_OK. Echo the requested RSA SHA-2 name.
      if (!ctx.signature && ctx.key.algo === 'ssh-rsa')
        ctx.key.algo = ctx.hashAlgo === 'sha512' ? 'rsa-sha2-512' : 'rsa-sha2-256';
      const verified = !ctx.signature || (fixtureParsed ? fixtureParsed.verify(ctx.blob, ctx.signature, ctx.hashAlgo) === true :
          crypto.verify(null, ctx.blob, publicKey, ctx.signature));
      if (verified)
        return ctx.accept();
      console.error('Fixture signature verification failed', ctx.key.algo, ctx.hashAlgo, ctx.signature.length);
      return ctx.reject(['publickey']);
    }
    if (ctx.method === 'password' && ['tabtty-interop', 'tabtty-sftp', 'tabtty-sftp-denied'].includes(ctx.username) && ctx.password === 'local-test-password')
      ctx.accept();
    else if (ctx.username === 'tabtty-interactive') {
      if (ctx.method !== 'keyboard-interactive') return ctx.reject(['keyboard-interactive']);
      ctx.prompt([{ prompt: 'User', echo: true }, { prompt: 'Optional', echo: false }], 'Example', 'First round', answers => {
        if (answers[0] !== 'local-user' || answers[1] !== '') return ctx.reject(['keyboard-interactive']);
        ctx.prompt([{ prompt: 'Code', echo: false }], 'Example', 'Second round', responses => {
          if (responses[0] === 'local-response') ctx.accept();
          else ctx.reject(['keyboard-interactive']);
        });
      });
    } else ctx.reject(['password']);
  });
  client.on('ready', () => client.on('session', accept => {
    const session = accept();
    session.on('sftp', (acceptSftp, rejectSftp) => {
      if (username !== 'tabtty-sftp') return rejectSftp();
      const channel = acceptSftp();
      session.on('close', () => channel.destroy());
      const data = new Map();
      const directories = new Set(['/home']);
      const directory = { mode: 0o40755, uid: 0, gid: 0, size: 0, atime: 1, mtime: 1 };
      const file = size => ({ ...directory, mode: 0o100644, size });
      const status = (id, code) => channel.status(id, code);
      channel.on('REALPATH', (id) => channel.name(id, [{ filename: '/home', attrs: directory }]));
      channel.on('LSTAT', (id, path) => directories.has(path) ? channel.attrs(id, directory) :
        data.has(path) ? channel.attrs(id, file(data.get(path).length)) : status(id, 2));
      channel.on('MKDIR', (id, path) => { directories.add(path); status(id, 0); });
      channel.on('RMDIR', (id, path) => status(id, directories.delete(path) ? 0 : 2));
      const lists = new Map();
      let nextList = 0;
      channel.on('OPENDIR', (id, path) => {
        if (!directories.has(path)) return status(id, 2);
        const handle = `dir:${++nextList}`;
        lists.set(handle, path);
        channel.handle(id, Buffer.from(handle));
      });
      channel.on('READDIR', (id, handle) => {
        const path = lists.get(handle.toString());
        if (!path) return status(id, 1);
        lists.delete(handle.toString());
        const parent = require('node:path').posix.dirname;
        const basename = require('node:path').posix.basename;
        channel.name(id, [{ filename: '.', attrs: directory }, ...Array.from(directories)
          .filter(name => parent(name) === path).map(name => ({ filename: basename(name), attrs: directory })),
          ...Array.from(data).filter(([name]) => parent(name) === path).map(([name, content]) =>
            ({ filename: basename(name), attrs: file(content.length) }))]);
      });
      channel.on('OPEN', (id, path, flags) => {
        if (flags & 8) {
          if (data.has(path)) return status(id, 4);
          data.set(path, Buffer.alloc(0));
        }
        if (!data.has(path)) return status(id, 2);
        channel.handle(id, Buffer.from(path));
      });
      channel.on('WRITE', (id, handle, offset, chunk) => {
        const path = handle.toString();
        if (offset !== data.get(path).length) return status(id, 4);
        data.set(path, Buffer.concat([data.get(path), chunk]));
        status(id, 0);
      });
      channel.on('READ', (id, handle, offset, count) => {
        const content = data.get(handle.toString());
        if (!content) return status(id, 2);
        if (offset >= content.length) return status(id, 1);
        channel.data(id, content.subarray(offset, offset + count));
      });
      channel.on('CLOSE', (id) => status(id, 0));
      channel.on('FSTAT', (id, handle) => {
        const content = data.get(handle.toString());
        if (content) channel.attrs(id, file(content.length));
        else status(id, 2);
      });
      channel.on('FSETSTAT', (id) => status(id, 0));
      channel.on('RENAME', (id, from, to) => {
        if (!data.has(from) || data.has(to)) return status(id, 4);
        data.set(to, data.get(from));
        data.delete(from);
        status(id, 0);
      });
      channel.on('REMOVE', (id, path) => status(id, data.delete(path) ? 0 : 2));
    });
    const testStartup = process.env.TABTTY_INTEROP_EARLY_DATA && username === 'tabtty-interop';
    session.on('pty', (acceptPty, rejectPty, size) => {
      if (testStartup && (size.cols !== 120 || size.rows !== 40))
        throw Error('Expected the actual 120x40 viewport at PTY startup.');
      acceptPty();
    });
    session.on('shell', acceptShell => {
      const start = () => {
        const channel = acceptShell();
        if (username.startsWith('tabtty-sftp')) {
          channel.on('data', data => {
            channel.write(data);
            client.rekey(error => { if (error) channel.destroy(error); });
          });
          return;
        }
        channel.once('data', data => {
          if (data.toString() !== 'go') throw Error('Unexpected terminal input.');
          channel.write(Buffer.from([0x82, 0xa0]));
          client.rekey(error => {
            if (error) throw error;
            channel.stderr.write(Buffer.from('!'));
            channel.end();
            channel.close();
          });
        });
      };
      if (testStartup) {
        client._protocol.channelData(session._chanInfo.outgoing.id, Buffer.from('early '));
        setTimeout(start, 2500);
      } else start();
    });
  }));
});
server.on('error', error => { console.error(error); process.exitCode = 1; });
server.listen(0, '127.0.0.1', () => fs.writeFileSync(process.argv[2], String(server.address().port)));
