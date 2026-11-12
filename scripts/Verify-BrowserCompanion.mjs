import assert from 'node:assert/strict';
import { readFile, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import vm from 'node:vm';
import { fileURLToPath } from 'node:url';

// Run the shipped service worker in an isolated JS realm. This fixture opens
// no browser, uses no personal profile, and makes no native or network calls.
const root = fileURLToPath(new URL('../', import.meta.url));
const source = await readFile(path.join(root, 'src/Winisland/Assets/BrowserExtension/background.js'), 'utf8');
const checks = [];
function check(condition, name) { assert.ok(condition, name); checks.push({ passed: true, check: name }); }
class Event {
  listeners = [];
  addListener(listener) { this.listeners.push(listener); }
  emit(...args) { for (const listener of this.listeners) listener(...args); }
}
function fixture() {
  let now = 100000;
  const messages = [];
  const onMessage = new Event(), onRemoved = new Event(), onUpdated = new Event();
  let sendMessage = async () => ({ success: false });
  const ports = [];
  const timers = new Map(), delays = [];
  let timerId = 0, connectFailure = false;
  const context = vm.createContext({
    navigator: { userAgent: 'Chrome/156' }, Date: { now: () => now },
    setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, callback); delays.push(delay); return id; },
    clearTimeout: id => timers.delete(id),
    chrome: {
      runtime: { onMessage, onStartup: new Event(), onInstalled: new Event(),
        connectNative: () => { if (connectFailure) throw new Error('Host unavailable'); const port = { onMessage: new Event(), onDisconnect: new Event(), postMessage: message => messages.push(message), disconnect() { this.onDisconnect.emit(); } }; ports.push(port); return port; } },
      action: { setBadgeText() {}, setBadgeBackgroundColor() {} },
      tabs: { onRemoved, onUpdated, sendMessage: (...args) => sendMessage(...args) }
    }
  });
  vm.runInContext(source, context);
  const evaluate = expression => vm.runInContext(expression, context);
  const state = (volume = 62) => ({ id: 'm1', title: 'Fixture player', hasMetadata: true, volume, muted: false, playing: true });
  const report = (tabId, documentId = 'document-a', frameId = 0, volume = 62) => onMessage.emit({ type: 'player-states', states: [state(volume)] },
    { tab: { id: tabId, title: 'Fixture player' }, frameId, documentId, url: 'https://fixture.invalid/' }, () => {});
  return { messages, ports, evaluate, report, state, onMessage, onUpdated, onRemoved, delays,
    retry: () => { const [id, callback] = timers.entries().next().value; timers.delete(id); callback(); },
    failConnect: value => { connectFailure = value; },
    advance: milliseconds => { now += milliseconds; }, setSend: action => { sendMessage = action; } };
}

const backoff = fixture();
for (let attempt = 0; attempt < 7; attempt++) {
  backoff.ports.at(-1).disconnect();
  const count = backoff.ports.length;
  backoff.report(1);
  check(backoff.ports.length === count, 'Media heartbeats respect the pending reconnect delay');
  backoff.retry();
}
check(JSON.stringify(backoff.delays) === JSON.stringify([1000, 2000, 4000, 8000, 16000, 30000, 30000]),
  'Asynchronous native connection failures exponentially back off and cap at thirty seconds');
backoff.ports.at(-1).onMessage.emit({ type: 'ready', protocol: 99 });
backoff.ports.at(-1).disconnect();
check(backoff.delays.at(-1) === 30000, 'An unsupported handshake does not reset connection backoff');
backoff.retry();
backoff.ports.at(-1).onMessage.emit({ type: 'ready', protocol: 1 });
backoff.ports.at(-1).disconnect();
check(backoff.delays.at(-1) === 1000, 'A successful protocol handshake restores prompt future recovery');
backoff.failConnect(true);
backoff.retry();
backoff.retry();
check(backoff.delays.at(-1) === 4000, 'Synchronous host failures preserve the same increasing backoff');
backoff.failConnect(false);
backoff.onMessage.emit({ type: 'companion-reconnect' }, {}, () => {});
check(backoff.evaluate('nativePort !== null && reconnectTimer === 0 && retryDelay === 1000'),
  'Manual reconnect cancels a delayed retry and connects immediately');

const capacity = fixture();
check(capacity.evaluate('clipText("x".repeat(511) + "😀",512).length') === 511,
  'Native metadata size limits preserve complete Unicode surrogate pairs');
for (let tabId = 1; tabId <= 128; tabId++) capacity.report(tabId);
check(capacity.evaluate('players.size') === 128, 'The fixture fills the actual companion capacity');
capacity.advance(31000);
capacity.report(129);
check(capacity.evaluate('players.size') === 1 && capacity.evaluate('players.has("129:0:document-a:m1")'),
  'Expired documents release capacity before a new media player is admitted');
check(capacity.messages.filter(message => message.type === 'remove').length === 128,
  'Expiry also removes obsolete native bridge targets');
capacity.advance(31000);
capacity.ports[0].disconnect();
const reconnectStart = capacity.messages.length;
capacity.evaluate('connect()');
check(!capacity.messages.slice(reconnectStart).some(message => message.type === 'state'),
  'Reconnecting never republishes expired players as fresh state');

const navigation = fixture();
navigation.report(1); navigation.report(1, 'frame-document', 2); navigation.report(2);
navigation.onUpdated.emit(1, { status: 'loading' });
check(navigation.evaluate('players.size') === 1 && navigation.evaluate('Array.from(players.values())[0].tabId') === 2,
  'Navigating a tab removes its parent and iframe players while preserving other tabs');
navigation.onRemoved.emit(2);
check(navigation.evaluate('players.size') === 0, 'Closing a tab removes its remaining targets');

const race = fixture();
race.report(1);
let finish;
race.setSend((tabId, message, options) => {
  check(options.documentId === 'document-a', 'A command is restricted to its originating browser document');
  return new Promise(resolve => { finish = resolve; });
});
const oldCommand = race.evaluate('handleCommand({type:"command",id:"1:0:document-a:m1",requestId:"0123456789abcdef0123456789abcdef",volume:40,muted:false})');
race.onUpdated.emit(1, { status: 'loading' });
race.report(1, 'document-b', 0, 85);
finish({ success: true, state: race.state(40) });
await oldCommand;
check(race.evaluate('players.size') === 1 && race.evaluate('players.get("1:0:document-b:m1").volume') === 85,
  'A delayed old-document acknowledgement cannot resurrect a player or overwrite the replacement document');
check(race.messages.filter(message => message.type === 'result').at(-1).success === false,
  'An obsolete document cannot confirm a successful native command');

const heartbeat = fixture();
heartbeat.report(1);
heartbeat.setSend(() => new Promise(resolve => { finish = resolve; }));
const activeCommand = heartbeat.evaluate('handleCommand({type:"command",id:"1:0:document-a:m1",requestId:"0123456789abcdef0123456789abcdef",volume:40,muted:false})');
heartbeat.report(1, 'document-a', 0, 39);
finish({ success: true, state: heartbeat.state(40) });
await activeCommand;
check(heartbeat.evaluate('players.get("1:0:document-a:m1").volume') === 40
  && heartbeat.messages.filter(message => message.type === 'result').at(-1).success === true,
  'Normal state heartbeats during a command retain precise successful readback');

const mainSource = await readFile(path.join(root, 'src/Winisland/Assets/BrowserExtension/player-main.js'), 'utf8');
const mainEvents = new Map(), emitted = [];
const brokenApi = { setVolume() {}, getVolume() { throw new Error('Player is rebuilding its API'); } };
const mediaElement = api => ({ isConnected: true, volume: .62, muted: false, paused: false, ended: false,
  duration: 180, currentTime: 12, closest: () => api, addEventListener() {} });
const mainContext = vm.createContext({
  window: { addEventListener: (name, handler) => mainEvents.set(name, handler),
    dispatchEvent: event => { emitted.push(event.detail); mainEvents.get(event.type)?.(event); } },
  document: { title: 'x'.repeat(511) + '😀', querySelectorAll: selector => selector === 'video, audio' ? [mediaElement(brokenApi), mediaElement(null)] : [], addEventListener() {} },
  navigator: {}, performance: { now: () => 1000 },
  CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
  MutationObserver: class { observe() {} }, setTimeout: () => 1, clearTimeout() {}, setInterval: () => 1, clearInterval() {}
});
vm.runInContext(mainSource, mainContext);
mainEvents.get('winisland-player-command-v1')({ detail: { type: 'scan' } });
check(emitted.find(message => message.type === 'states')?.states.length === 1
  && emitted.find(message => message.type === 'states')?.states[0].volume === 62,
  'A temporarily broken page player API cannot prevent another media element from reporting volume');
check(emitted.find(message => message.type === 'states')?.states[0].title.length === 511,
  'Page metadata clipping cannot emit a split Unicode surrogate to the native host');

const outputDirectory = path.join(root, 'artifacts/browser-companion');
await mkdir(outputDirectory, { recursive: true });
const reportPath = path.join(outputDirectory, 'results.json');
await writeFile(reportPath, JSON.stringify({ passed: true, checks }, null, 2));
console.log(JSON.stringify({ passed: true, checks: checks.length, reportPath }));
