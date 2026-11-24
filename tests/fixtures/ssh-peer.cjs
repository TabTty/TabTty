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
  let ownedTerminal;
  let clipboardTerminal;
  let pasted = Buffer.alloc(0);
  let pasteObserved;
  let ownedSessions = 0;
  let delayedSessionCount = 0;
  let openLate, closeLate, delayedTerminal;
  client.on('error', error => console.error(error.message));
  client.on('authentication', ctx => {
    username = ctx.username;
    if (username === 'tabtty-auth-fallback') {
      if (ctx.method === 'password' && ctx.password === 'local-test-password') return ctx.accept();
      return ctx.reject(['publickey', 'password']);
    }
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
    if (ctx.method === 'password' && ['tabtty-ownership', 'tabtty-eof', 'tabtty-clipboard',
        'tabtty-send-progress', 'tabtty-paste-progress', 'tabtty-delayed-sftp',
        'tabtty-shell-refused', 'tabtty-shell-cancelled', 'tabtty-stderr-burst'].includes(ctx.username)
        && ctx.password === 'local-test-password')
      return ctx.accept();
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
  client.on('ready', () => client.on('session', function acceptSession(accept, reject, openingLate = false) {
    const ownership = username === 'tabtty-ownership';
    const clipboard = username === 'tabtty-clipboard';
    const pasteProgress = username === 'tabtty-paste-progress';
    const delayedSftp = username === 'tabtty-delayed-sftp';
    if (delayedSftp && !openingLate && delayedSessionCount++ === 1) {
      openLate = () => acceptSession(accept, reject, true);
      delayedTerminal.write('old open pending\n');
      return;
    }
    const originalConfirm = client._protocol.channelOpenConfirm;
    let session;
    if (ownership && ownedSessions++ === 0) {
      client._protocol.channelOpenConfirm = function (recipient, sender, window, limit) {
        return originalConfirm.call(this, recipient, sender, 0, limit);
      };
    }
    if (pasteProgress) {
      client._protocol.channelOpenConfirm = function (recipient, sender, window) {
        return originalConfirm.call(this, recipient, sender, window, 2);
      };
    }
    try { session = accept(); }
    finally { client._protocol.channelOpenConfirm = originalConfirm; }
    if (openingLate) {
      session.once('close', () => {
        closeLate = () => client._protocol.channelClose(session._chanInfo.outgoing.id);
        delayedTerminal.write('old close pending\n');
      });
    }
    session.on('sftp', (acceptSftp, rejectSftp) => {
      if (!ownership && !clipboard && !delayedSftp && username !== 'tabtty-send-progress') {
        if (username !== 'tabtty-sftp') return rejectSftp();
      }
      const channel = acceptSftp();
      if (process.env.TABTTY_INTEROP_SFTP_FRAGMENTS && (username === 'tabtty-sftp' || ownership)) {
        const originalData = client._protocol.channelData;
        function fragmented(recipient, bytes) {
          if (recipient !== channel.outgoing.id || bytes.length <= 4)
            return originalData.call(this, recipient, bytes);
          originalData.call(this, recipient, bytes.subarray(0, 1));
          originalData.call(this, recipient, bytes.subarray(1, 3));
          originalData.call(this, recipient, bytes.subarray(3, 4));
          if (bytes.length > 6) {
            originalData.call(this, recipient, bytes.subarray(4, 6));
            return originalData.call(this, recipient, bytes.subarray(6));
          }
          return originalData.call(this, recipient, bytes.subarray(4));
        }
        client._protocol.channelData = fragmented;
        session.on('close', () => {
          if (client._protocol.channelData === fragmented) client._protocol.channelData = originalData;
        });
      }
      session.on('close', () => channel.destroy());
      const data = new Map();
      const fileTimes = new Map();
      const heldReads = [];
      if (ownership) data.set('/home/held-reads', Buffer.from('01234567'));
      const directories = new Set(['/home']);
      const directory = { mode: 0o40755, uid: 0, gid: 0, size: 0, atime: 1, mtime: 1 };
      const file = (size, path) => ({ ...directory, ...fileTimes.get(path), mode: 0o100644, size });
      const status = (id, code) => channel.status(id, code);
      channel.on('REALPATH', (id, path) => {
        if (username === 'tabtty-sftp' && path === '/fixture-oversize-frame')
          return client._protocol.channelData(channel.outgoing.id, Buffer.from([0, 4, 0, 1]));
        channel.name(id, [{ filename: '/home', attrs: directory }]);
      });
      channel.on('LSTAT', (id, path) => {
        if (ownership && path === '/fixture-held-read-count')
          return channel.attrs(id, file(heldReads.length));
        if (clipboard && path === '/fixture-await-first-paste-unit') {
          const reply = () => channel.attrs(id, file(pasted.length));
          if (pasted.length >= 10) reply(); else pasteObserved = reply;
          clipboardTerminal.write('\r\n' + Array.from({ length: 80 }, (_, row) =>
            'clipboard-retained-' + row + '\r\n').join(''));
          return;
        }
        if (clipboard && path === '/fixture-paste-framing') {
          const expected = Buffer.from('\x1b[200~😀\x1b[201~');
          return channel.attrs(id, file(pasted.equals(expected) ? expected.length : 0));
        }
        if (directories.has(path)) channel.attrs(id, directory);
        else if (data.has(path)) channel.attrs(id, file(data.get(path).length, path));
        else status(id, 2);
      });
      channel.on('MKDIR', (id, path, attrs) => {
        if (attrs.mode !== 0o700) return status(id, 3);
        if (directories.has(path) || data.has(path)) return status(id, 4);
        directories.add(path);
        status(id, 0);
        if (ownership && path === '/home/fixture-input') ownedTerminal.write('marker\r\n\x1b[6n');
        if (ownership && path === '/home/fixture-release-reads')
          for (const complete of heldReads.splice(0).reverse()) complete();
        if (ownership && path === '/home/fixture-rekey')
          client.rekey(error => {
            if (error) return ownedTerminal.destroy(error);
            client._protocol.channelWindowAdjust(ownedTerminal.outgoing.id, 4096);
          });
      });
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
            ({ filename: basename(name), attrs: file(content.length, name) }))]);
      });
      channel.on('OPEN', (id, path, flags, attrs) => {
        if (flags & 8) {
          if (!(flags & 32) || attrs.mode !== 0o600) return status(id, 3);
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
        if (ownership && handle.toString() === '/home/held-reads') {
          heldReads.push(() => channel.data(id, content.subarray(offset, offset + count)));
          return;
        }
        channel.data(id, content.subarray(offset, offset + count));
      });
      channel.on('CLOSE', (id) => status(id, 0));
      channel.on('FSTAT', (id, handle) => {
        const content = data.get(handle.toString());
        if (content) channel.attrs(id, file(content.length, handle.toString()));
        else status(id, 2);
      });
      channel.on('FSETSTAT', (id, handle, attrs) => {
        const path = handle.toString();
        if (!data.has(path)) return status(id, 2);
        fileTimes.set(path, { atime: attrs.atime, mtime: attrs.mtime });
        status(id, 0);
      });
      channel.on('RENAME', (id, from, to) => {
        if (!data.has(from) || data.has(to)) return status(id, 4);
        data.set(to, data.get(from));
        data.delete(from);
        if (fileTimes.has(from)) {
          fileTimes.set(to, fileTimes.get(from));
          fileTimes.delete(from);
        }
        status(id, 0);
      });
      channel.on('REMOVE', (id, path) => {
        fileTimes.delete(path);
        status(id, data.delete(path) ? 0 : 2);
      });
    });
    const testStartup = process.env.TABTTY_INTEROP_EARLY_DATA && username === 'tabtty-interop';
    let latestEofGrid = false;
    session.on('pty', (acceptPty, rejectPty, size) => {
      if (testStartup && (size.cols !== 120 || size.rows !== 40))
        throw Error('Expected the actual 120x40 viewport at PTY startup.');
      acceptPty();
    });
    session.on('window-change', (accept, reject, size) => {
      if (username === 'tabtty-eof') latestEofGrid = size.cols === 132 && size.rows === 50;
      if (accept) accept();
    });
    session.on('shell', (acceptShell, rejectShell) => {
      if (username === 'tabtty-shell-refused' || username === 'tabtty-shell-cancelled') {
        const recipient = session._chanInfo.outgoing.id;
        client._protocol.channelData(recipient, Buffer.from('early data '));
        client._protocol.channelExtData(recipient, Buffer.from('early stderr'), 1);
        if (username === 'tabtty-shell-refused') rejectShell();
        // The cancelled case leaves the request outstanding until connection close.
        return;
      }
      const start = () => {
        const channel = acceptShell();
        if (username === 'tabtty-stderr-burst') {
          channel.once('data', request => {
            if (request.toString() !== 'burst') return channel.close();
            for (let index = 0; index < 1536; index++) {
              if (index === 768) channel.exit(7);
              if (index % 2 === 0) channel.write('A');
              else channel.stderr.write('B');
            }
            channel.write('\r\nburst finished');
            channel.once('data', reverse => {
              if (reverse.toString() === 'reverse') channel.write('\r\nreverse confirmed');
              channel.end();
            });
          });
          return;
        }
        if (delayedSftp) {
          delayedTerminal = channel;
          channel.on('data', bytes => {
            switch (bytes.toString()) {
              case 'release open':
                openLate();
                break;
              case 'release close':
                closeLate();
                break;
              case 'probe':
                channel.write('terminal alive\n');
                break;
            }
          });
          return;
        }
        if (username === 'tabtty-send-progress' || pasteProgress) {
          channel.resume();
          return;
        }
        if (clipboard) {
          clipboardTerminal = channel;
          channel.on('data', data => {
            if (pasted.length < 64) pasted = Buffer.concat([pasted, data]);
            if (pasteObserved && pasted.length >= 10) {
              const notify = pasteObserved;
              pasteObserved = undefined;
              notify();
            }
          });
          channel.write('\x1b[?2004hclipboard fixture ready');
          return;
        }
        if (username === 'tabtty-eof') {
          let input = '';
          channel.on('data', bytes => {
            input += bytes.toString();
            if (input === 'reverse input' && latestEofGrid) channel.close();
          });
          channel.write(Buffer.from([0x74, 0x61, 0x69, 0x6c, 0xe3]));
          channel.eof();
          return;
        }
        if (ownership) {
          ownedTerminal = channel;
          let input = '';
          channel.on('data', bytes => {
            input += bytes.toString();
            const expected = 'manual\x1b[2;1R';
            if (!expected.startsWith(input)) return channel.destroy(new Error('Unexpected committed terminal bytes.'));
            if (input === expected) channel.write('confirmed');
          });
          return;
        }
        if (username.startsWith('tabtty-sftp')) {
          channel.on('data', data => {
            channel.write(data);
            client.rekey(error => { if (error) channel.destroy(error); });
          });
          return;
        }
        channel.once('data', data => {
          const expected = process.env.TABTTY_INTEROP_POST_CONNECTION ? 'go\r' : 'go';
          if (data.toString() !== expected) throw Error('Unexpected terminal input.');
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
