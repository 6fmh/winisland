import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { mkdir, mkdtemp, readFile, symlink } from 'node:fs/promises';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Exercise the real runner against an unsafe browser endpoint. No browser is
// started; the fixture implements only the read-only CDP command-line query.
const root = path.resolve('artifacts/browser-safety');
await mkdir(root, { recursive: true });
const directory = await mkdtemp(path.join(root, 'run-'));
const runner = fileURLToPath(new URL('./Verify-BrowserPlayer.mjs', import.meta.url));
let browserArguments, calls;
const server = http.createServer((request, response) => {
  response.setHeader('Content-Type', 'application/json');
  response.end(JSON.stringify({ webSocketDebuggerUrl: `ws://127.0.0.1:${server.address().port}/devtools/browser/fixture`, Browser: 'Safety fixture' }));
});
server.on('upgrade', (request, socket) => {
  const accept = createHash('sha1').update(request.headers['sec-websocket-key'] + '258EAFA5-E914-47DA-95CA-C5AB0DC85B11').digest('base64');
  socket.write(`HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: ${accept}\r\n\r\n`);
  let pending = Buffer.alloc(0);
  socket.on('error', () => {});
  socket.on('data', chunk => {
    pending = Buffer.concat([pending, chunk]);
    while (pending.length >= 2) {
      const opcode = pending[0] & 15;
      let length = pending[1] & 127, offset = 2;
      if (length === 126) { if (pending.length < 4) return; length = pending.readUInt16BE(2); offset = 4; }
      if (length === 127) { socket.destroy(); return; }
      const masked = !!(pending[1] & 128);
      if (pending.length < offset + (masked ? 4 : 0) + length) return;
      const mask = masked ? pending.subarray(offset, offset + 4) : null;
      offset += masked ? 4 : 0;
      const payload = Buffer.from(pending.subarray(offset, offset + length));
      pending = pending.subarray(offset + length);
      if (mask) for (let index = 0; index < payload.length; index++) payload[index] ^= mask[index % 4];
      if (opcode === 8) { socket.end(Buffer.from([0x88, 0])); return; }
      if (opcode !== 1) continue;
      const command = JSON.parse(payload);
      calls.push(command.method);
      const body = Buffer.from(JSON.stringify(command.method === 'Browser.getBrowserCommandLine'
        ? { id: command.id, result: { arguments: browserArguments } }
        : { id: command.id, error: { message: 'Safety fixture stopped after successful profile guard' } }));
      const header = Buffer.alloc(body.length < 126 ? 2 : 4);
      header[0] = 0x81;
      if (body.length < 126) header[1] = body.length;
      else { header[1] = 126; header.writeUInt16BE(body.length, 2); }
      socket.write(Buffer.concat([header, body]));
    }
  });
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
try {
  const profile = path.join(directory, 'profile');
  await mkdir(profile);
  const outsideProfile = await mkdtemp(path.join(root, 'outside-profile-'));
  const linkedProfile = path.join(directory, 'linked-profile');
  await symlink(outsideProfile, linkedProfile, process.platform === 'win32' ? 'junction' : 'dir');
  const scenarios = [
    ['visible', ['--enable-automation', `--user-data-dir=${profile}`]],
    ['personal-profile', ['--headless=new', '--enable-automation', `--user-data-dir=${path.dirname(directory)}`]],
    ['profile-root', ['--headless=new', '--enable-automation', `--user-data-dir=${directory}`]],
    ['missing-profile', ['--headless=new', '--enable-automation']],
    ['missing-automation', ['--headless=new', `--user-data-dir=${profile}`]],
    ['duplicate-profile', ['--headless=new', '--enable-automation', `--user-data-dir=${profile}`, `--user-data-dir=${outsideProfile}`]],
    ['profile-junction', ['--headless=new', '--enable-automation', `--user-data-dir=${linkedProfile}`]],
    ['dedicated-profile', ['--headless=new', '--enable-automation', `--user-data-dir=${profile}`], true]
  ];
  for (const [name, arguments_, accepted = false] of scenarios) {
    browserArguments = arguments_; calls = [];
    const reportPath = path.join(directory, `${name}.json`);
    const child = spawn(process.execPath, [runner, directory, String(server.address().port), reportPath], { windowsHide: true, stdio: 'ignore' });
    const timer = setTimeout(() => child.kill(), 10000);
    const code = await new Promise((resolve, reject) => { child.on('error', reject); child.on('exit', resolve); }).finally(() => clearTimeout(timer));
    assert.equal(code, 1, `${name} is rejected`);
    const report = JSON.parse(await readFile(reportPath, 'utf8'));
    assert.equal(report.passed, false);
    if (accepted) {
      assert.match(report.error, /fixture stopped after successful profile guard/);
      assert.deepEqual(calls, ['Browser.getBrowserCommandLine', 'Target.getTargets']);
      console.log(`PASS: ${name} passes the guard before the synthetic endpoint stops`);
    } else {
      assert.match(report.error, /requires headless automation/);
      assert.deepEqual(calls, ['Browser.getBrowserCommandLine'], `${name}: no tab creation, player commands or browser shutdown`);
      console.log(`PASS: ${name} is rejected without mutating the browser`);
    }
  }
} finally { await new Promise(resolve => server.close(resolve)); }
