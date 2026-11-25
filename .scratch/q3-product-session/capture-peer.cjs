const net = require('node:net');
const fs = require('node:fs');
const path = require('node:path');
const esc = String.fromCharCode(27);
const server = net.createServer(socket => {
  const name = socket.localPort === firstPort ? 'Demo B' : 'Demo C';
  socket.on('error', () => {});
  socket.write([
    esc+'[1;36mTabTty — レイアウト確認 '+name+esc+'[0m',
    '',
    esc+'[32m接続済み'+esc+'[0m  localhost  /  UTF-8',
    '',
    '  STATUS   SERVICE       LATENCY',
    '  OK       Terminal       12 ms',
    '  OK       Session        18 ms',
    '  WARN     Demo task      34 ms',
    '',
    ' demo@localhost:~$ '
  ].join('\r\n'));
});
const secondary = net.createServer(socket => server.emit('connection',socket));
let firstPort;
for (const [s,n] of [[server,'Demo B'],[secondary,'Demo C']]) {
  s.listen(0,'127.0.0.1',() => {
    const port = s.address().port;
    if (s===server) firstPort=port;
    const profilePath=path.join(__dirname,n+'.ttty');
    const model=JSON.parse(fs.readFileSync(profilePath,'utf8'));
    model.connection.port=port;
    fs.writeFileSync(profilePath,JSON.stringify(model,null,2));
    console.log(n+' ready on loopback');
  });
}
setTimeout(()=>process.exit(0), 600000).unref();
