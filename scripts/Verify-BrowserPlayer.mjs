import fs from 'node:fs';
import path from 'node:path';
import http from 'node:http';
import { setTimeout as delay } from 'node:timers/promises';

const harnessData = path.resolve(process.argv[2]);
const debuggingPort = Number(process.argv[3] || 9439);
const resultPath = path.resolve(process.argv[4] || 'artifacts/player-volume/comet-results.json');
const checks = [];
function assert(condition, check) { checks.push({ passed: !!condition, check }); if (!condition) throw new Error(check); }
async function waitUntil(test, timeout = 15000) { const start = Date.now(); while (Date.now() - start < timeout) { const result = await test(); if (result) return result; await delay(100); } throw new Error('Timed out waiting for browser integration.'); }

class CDP {
  constructor(url) {
    this.socket = new WebSocket(url); this.nextId = 0; this.pending = new Map();
    this.open = new Promise((resolve, reject) => { this.socket.addEventListener('open', resolve, { once: true }); this.socket.addEventListener('error', reject, { once: true }); });
    this.socket.addEventListener('message', event => { const message = JSON.parse(event.data); const pending = this.pending.get(message.id); if (!pending) return;
      this.pending.delete(message.id); clearTimeout(pending.timer); if (message.error) pending.reject(new Error(message.error.message)); else pending.resolve(message.result); });
  }
  async send(method, params = {}, sessionId) {
    await this.open; const id = ++this.nextId;
    return new Promise((resolve, reject) => { const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`${method} timed out`)); }, 15000);
      this.pending.set(id, { resolve, reject, timer }); this.socket.send(JSON.stringify({ id, method, params, ...(sessionId ? { sessionId } : {}) })); });
  }
  async evaluate(expression, sessionId, contextId) {
    const result = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true, ...(contextId ? { contextId } : {}) }, sessionId);
    if (result.exceptionDetails) throw new Error(JSON.stringify(result.exceptionDetails)); return result.result.value;
  }
}

let browser, worker;
const fixtureTargets = [];
const embeddedServer = http.createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
  response.end(request.url === '/untitled' ? '<!doctype html><video controls></video><script>document.querySelector("video").volume=.64;</script>'
    : '<!doctype html><title>Embedded player fixture</title><audio id="embedded" controls></audio><script>document.querySelector("audio").volume=.71;navigator.mediaSession.metadata=new MediaMetadata({title:"Embedded player fixture"});</script>');
});
await new Promise(resolve => embeddedServer.listen(0, '127.0.0.1', resolve));
const embeddedPort = embeddedServer.address().port;
const server = http.createServer((request, response) => {
  response.writeHead(200, { 'Content-Type': 'text/html', 'Cache-Control': 'no-store' });
  if (request.url === '/other') response.end('<!doctype html><title>Another browser player</title><audio controls></audio><script>document.querySelector("audio").volume=.92;navigator.mediaSession.metadata=new MediaMetadata({title:"Another browser player"});</script>');
  else if (request.url === '/movie-frame') response.end(`<!doctype html><title>Movie title</title><iframe src="http://127.0.0.1:${embeddedPort}/untitled"></iframe>`);
  else if (request.url === '/youtube-api') response.end('<!doctype html><title>YouTube API fixture</title><div id="movie_player" class="html5-video-player"><video controls></video></div><script>let level=55,muted=false;Object.assign(document.getElementById("movie_player"),{getVolume:()=>level,setVolume:v=>level=Math.round(v),mute:()=>muted=true,unMute:()=>muted=false,isMuted:()=>muted});navigator.mediaSession.metadata=new MediaMetadata({title:"YouTube API fixture"});</script>');
  else if (request.url === '/api-containment') response.end('<!doctype html><title>Player API containment fixture</title><div id="movie_player" class="html5-video-player"><video controls></video></div><audio id="external" controls></audio><script>document.getElementById("external").volume=.87;let level=55;Object.assign(document.getElementById("movie_player"),{getVolume:()=>level,setVolume:v=>level=Math.round(v),isMuted:()=>false});navigator.mediaSession.metadata=new MediaMetadata({title:"Player API containment fixture"});</script>');
  else response.end(`<!doctype html><title>Comet player fixture</title><video id="main" controls></video><iframe src="http://127.0.0.1:${embeddedPort}/embedded"></iframe><script>document.querySelector("video").volume=.625;navigator.mediaSession.metadata=new MediaMetadata({title:"Comet player fixture"});</script>`);
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const fixturePort = server.address().port;

try {
  const version = await waitUntil(async () => { try { const response = await fetch(`http://127.0.0.1:${debuggingPort}/json/version`); return response.ok && await response.json(); } catch { return null; } });
  browser = new CDP(version.webSocketDebuggerUrl);
  // Inspect the endpoint before creating tabs or touching its companion.
  // Hidden/headless launch flags do not make a personal browser profile safe.
  const { arguments: browserArguments } = await browser.send('Browser.getBrowserCommandLine');
  const profileArguments = browserArguments.filter(argument => argument.startsWith('--user-data-dir='));
  const profileArgument = profileArguments.length === 1 && profileArguments[0];
  const profile = profileArgument && path.resolve(profileArgument.slice('--user-data-dir='.length));
  const relativeProfile = profile && path.relative(harnessData, profile);
  let dedicatedProfile = false;
  if (relativeProfile && relativeProfile !== '..' && !relativeProfile.startsWith(`..${path.sep}`) && !path.isAbsolute(relativeProfile)) {
    try {
      // A lexical child can still be a junction into a personal profile. The
      // running browser has already created its profile, so require real paths.
      const realRelative = path.relative(fs.realpathSync(harnessData), fs.realpathSync(profile));
      dedicatedProfile = !!realRelative && realRelative !== '..' && !realRelative.startsWith(`..${path.sep}`) && !path.isAbsolute(realRelative);
    } catch { /* Missing/unreadable profiles are not safe automation targets. */ }
  }
  assert(browserArguments.some(argument => argument === '--headless' || argument.startsWith('--headless='))
    && browserArguments.includes('--enable-automation')
    && dedicatedProfile,
    'Browser verification requires headless automation and a dedicated profile inside the fixture directory');
  const extension = await waitUntil(async () => { const targets = await browser.send('Target.getTargets'); return targets.targetInfos.find(t => t.type === 'service_worker' && t.url.startsWith('chrome-extension://agpnncgfekilbepnojjaolgpbhefolne/')); });
  worker = (await browser.send('Target.attachToTarget', { targetId: extension.targetId, flatten: true })).sessionId;
  const connected = await waitUntil(async () => await browser.evaluate('typeof nativeReady !== "undefined" && nativeReady', worker));
  assert(connected, 'Real Comet loads the MV3 companion and completes native messaging through the local Windows pipe');
  const runtime = await browser.evaluate('({agent:navigator.userAgent,brands:navigator.userAgentData?.brands,id:chrome.runtime.id})', worker);
  const mainTarget = await createFixtureTarget(`http://127.0.0.1:${fixturePort}/`);
  const main = (await browser.send('Target.attachToTarget', { targetId: mainTarget, flatten: true })).sessionId;
  await browser.send('Page.enable', {}, main);
  const otherTarget = await createFixtureTarget(`http://127.0.0.1:${fixturePort}/other`);
  const other = (await browser.send('Target.attachToTarget', { targetId: otherTarget, flatten: true })).sessionId;
  await waitUntil(async () => await browser.evaluate('players.size>=3', worker));
  const changed = await change('Comet player fixture', 37.5);
  assert(changed.changed && Math.abs(changed.after.Volume - 37.5) < .01, 'A native notch volume command reaches the website player and returns its exact readback');
  const mainVolume = await browser.evaluate('document.querySelector("video").volume', main);
  const otherVolume = await browser.evaluate('document.querySelector("audio").volume', other);
  assert(Math.abs(mainVolume - .375) < .0001 && Math.abs(otherVolume - .92) < .0001, 'The real website media element changes while another Comet tab keeps its original volume');
  const embedded = await change('Embedded player fixture', 48.25);
  assert(embedded.changed && Math.abs(embedded.after.Volume - 48.25) < .01, 'The native bridge targets an embedded cross-origin player independently');
  const frameTree = await browser.send('Page.getFrameTree', {}, main);
  const frameId = frameTree.frameTree.childFrames[0].frame.id;
  const context = await browser.send('Page.createIsolatedWorld', { frameId, worldName: 'Winisland verification' }, main);
  const embeddedVolume = await browser.evaluate('document.querySelector("audio").volume', main, context.executionContextId);
  assert(Math.abs(embeddedVolume - .4825) < .0001, 'The embedded player receives the actual requested fractional volume');
  const missing = await change('A title that does not exist', 10);
  assert(!missing.changed && !missing.current.Available, 'A mismatched current media title cannot change another Comet player');
  const youtubeTarget = await createFixtureTarget(`http://127.0.0.1:${fixturePort}/youtube-api`);
  const youtube = (await browser.send('Target.attachToTarget', { targetId: youtubeTarget, flatten: true })).sessionId;
  await waitUntil(async () => await browser.evaluate('players.size>=4', worker));
  const youtubeChange = await change('YouTube API fixture', 28.6);
  const youtubeVolume = await browser.evaluate('document.getElementById("movie_player").getVolume()', youtube);
  assert(youtubeChange.changed && youtubeVolume === 29, 'The YouTube adapter uses setVolume/getVolume rather than replacing the browser mixer level');
  const containmentTarget = await createFixtureTarget(`http://127.0.0.1:${fixturePort}/api-containment`);
  const containment = (await browser.send('Target.attachToTarget', { targetId: containmentTarget, flatten: true })).sessionId;
  const externalPlayer = await waitUntil(async () => await browser.evaluate('Array.from(players.values()).find(p=>p.title==="Player API containment fixture"&&Math.abs(p.volume-87)<.01)', worker));
  await browser.evaluate(`handleCommand({type:"command",id:${JSON.stringify(externalPlayer.id)},requestId:"0123456789abcdef0123456789abcdef",volume:23.75,muted:false})`, worker);
  const containmentReadback = await browser.evaluate('({external:document.getElementById("external").volume,youtube:document.getElementById("movie_player").getVolume()})', containment);
  assert(Math.abs(containmentReadback.external - .2375) < .0001 && containmentReadback.youtube === 55, 'An unrelated audio element outside a YouTube-style player changes its own volume and preserves the YouTube API volume');
  const movieTarget = await createFixtureTarget(`http://127.0.0.1:${fixturePort}/movie-frame`);
  const movie = (await browser.send('Target.attachToTarget', { targetId: movieTarget, flatten: true })).sessionId;
  await browser.send('Page.enable', {}, movie);
  await waitUntil(async () => await browser.evaluate('Array.from(players.values()).some(p=>p.title==="Movie title")', worker));
  const movieChange = await change('Movie title', 46.75);
  assert(movieChange.changed && movieChange.after.Available && Math.abs(movieChange.after.Volume - 46.75) < .01, 'An untitled movie iframe uses its trusted parent tab title and retains it after volume acknowledgement');
  const movieFrame = (await browser.send('Page.getFrameTree', {}, movie)).frameTree.childFrames[0].frame.id;
  const movieContext = await browser.send('Page.createIsolatedWorld', { frameId: movieFrame, worldName: 'Winisland movie verification' }, movie);
  const movieVolume = await browser.evaluate('document.querySelector("video").volume', movie, movieContext.executionContextId);
  assert(Math.abs(movieVolume - .4675) < .0001 && Math.abs(await browser.evaluate('document.querySelector("audio").volume', other) - .92) < .0001, 'The actual untitled iframe changes while an unrelated parent tab remains unchanged');
  await browser.send('Target.closeTarget', { targetId: mainTarget });
  await waitUntil(async () => await browser.evaluate('!Array.from(players.values()).some(p=>p.title==="Comet player fixture"||p.title==="Embedded player fixture")', worker));
  const gone = await change('Comet player fixture', 10);
  assert(!gone.changed, 'Closing a Comet tab removes its media targets from the native bridge');
  fs.mkdirSync(path.dirname(resultPath), { recursive: true });
  fs.writeFileSync(resultPath, JSON.stringify({ passed: true, browser: version.Browser, runtime, checks }, null, 2));
  console.log(JSON.stringify({ passed: true, checks: checks.length, browser: version.Browser, resultPath }));
} catch (error) {
  let diagnostics;
  try { diagnostics = browser && worker ? await browser.evaluate('({ready:nativeReady,players:Array.from(players.values()).map(p=>({id:p.id,title:p.title,tabTitle:p.tabTitle,volume:p.volume}))})', worker) : undefined; } catch {}
  fs.mkdirSync(path.dirname(resultPath), { recursive: true });
  fs.writeFileSync(resultPath, JSON.stringify({ passed: false, error: error.stack, checks, diagnostics }, null, 2));
  console.error(error.stack); process.exitCode = 1;
} finally {
  if (browser) {
    for (const targetId of fixtureTargets) { try { await browser.send('Target.closeTarget', { targetId }); } catch {} }
    browser.socket.close();
  }
  server.close(); embeddedServer.close();
}

async function createFixtureTarget(url) {
  const { targetId } = await browser.send('Target.createTarget', { url, background: true });
  fixtureTargets.push(targetId);
  return targetId;
}

async function change(title, volume) {
  const id = `${Date.now()}-${Math.random()}`;
  const requestPath = path.join(harnessData, 'request.json');
  const responsePath = path.join(harnessData, 'response.json');
  fs.writeFileSync(`${requestPath}.tmp`, JSON.stringify({ id, title, volume }));
  fs.renameSync(`${requestPath}.tmp`, requestPath);
  return await waitUntil(() => { try { const result = JSON.parse(fs.readFileSync(responsePath, 'utf8')); return result.id === id && result; } catch { return null; } }, 6000);
}
