"use strict";

const HOST = "com.winisland.player";
const players = new Map();
let nativePort = null;
let nativeReady = false;
let reconnectTimer = 0;
let retryDelay = 1000;
let connectionStatus = "Open Winisland to connect.";

function browserName() {
  const agent = navigator.userAgent;
  if (/Comet|Perplexity/i.test(agent)) return "comet";
  if (/Edg\//.test(agent)) return "edge";
  if (/OPR\//.test(agent)) return "opera";
  if (/Vivaldi\//.test(agent)) return "vivaldi";
  if (navigator.brave) return "brave";
  return "chrome";
}

function sendNative(message) {
  if (!nativePort) return false;
  try { nativePort.postMessage(message); return true; }
  catch { return false; }
}

function prunePlayers() {
  const now = Date.now();
  for (const [id, state] of players) {
    if (now - state.seen > 30000) { players.delete(id); sendNative({ type: "remove", id }); }
  }
}

function removeTabPlayers(tabId) {
  const prefix = `${tabId}:`;
  for (const id of players.keys()) if (id.startsWith(prefix)) { players.delete(id); sendNative({ type: "remove", id }); }
}

function connect() {
  if (nativePort) return;
  clearTimeout(reconnectTimer);
  reconnectTimer = 0;
  prunePlayers();
  try {
    const port = chrome.runtime.connectNative(HOST);
    nativePort = port;
    port.onMessage.addListener(handleCommand);
    port.onDisconnect.addListener(() => {
      if (nativePort !== port) return;
      const unavailable = chrome.runtime.lastError;
      nativePort = null;
      nativeReady = false;
      connectionStatus = unavailable ? "Open Winisland, then reconnect this companion." : "Winisland is closed.";
      chrome.action.setBadgeText({ text: "" });
      reconnectTimer = setTimeout(connect, retryDelay);
      retryDelay = Math.min(30000, retryDelay * 2);
    });
    sendNative({ type: "hello", browser: browserName() });
    for (const state of players.values()) sendNative({ type: "state", ...state });
    connectionStatus = "Connecting locally to Winisland…";
    chrome.action.setBadgeBackgroundColor({ color: "#3A8DFF" });
    chrome.action.setBadgeText({ text: "" });
  } catch {
    nativePort = null;
    connectionStatus = "The native player bridge is unavailable.";
    reconnectTimer = setTimeout(connect, retryDelay);
    retryDelay = Math.min(30000, retryDelay * 2);
  }
}

function safeState(raw, sender) {
  if (!raw || typeof raw.id !== "string" || !/^m\d+$/.test(raw.id) || raw.id.length > 16) return null;
  if (typeof raw.volume !== "number" || !Number.isFinite(raw.volume) || raw.volume < 0 || raw.volume > 100) return null;
  return {
    id: `${sender.tab.id}:${sender.frameId}:${sender.documentId ? sender.documentId + ":" : ""}${raw.id}`,
    tabId: sender.tab.id,
    tabTitle: clipText(sender.tab.title, 512),
    frameId: sender.frameId,
    documentId: sender.documentId ?? "",
    elementId: raw.id,
    title: raw.hasMetadata !== true && sender.frameId !== 0 && typeof sender.tab.title === "string" ? clipText(sender.tab.title, 512)
      : clipText(raw.title, 512),
    artist: clipText(raw.artist, 256),
    volume: raw.volume,
    muted: raw.muted === true,
    playing: raw.playing === true,
    duration: Number.isFinite(raw.duration) && raw.duration >= 0 ? raw.duration : 0,
    position: Number.isFinite(raw.position) && raw.position >= 0 ? raw.position : 0,
    seen: Date.now()
  };
}

function clipText(value, maximum) {
  if (typeof value !== "string") return "";
  let text = value.slice(0, maximum);
  // Native JSON decoding rejects a surrogate pair split by the size limit.
  const last = text.charCodeAt(text.length - 1);
  if (last >= 0xD800 && last <= 0xDBFF) text = text.slice(0, -1);
  return text;
}

async function handleCommand(message) {
  if (message?.type === "ready" && message.protocol === 1) { nativeReady = true; retryDelay = 1000; connectionStatus = "Connected locally to Winisland."; return; }
  if (!message || message.type !== "command" || typeof message.requestId !== "string" || !/^[a-f0-9]{32}$/.test(message.requestId)) return;
  prunePlayers();
  const target = players.get(message.id);
  if (!target || Date.now() - target.seen > 30000 || !Number.isFinite(message.volume) || message.volume < 0 || message.volume > 100) {
    sendNative({ type: "result", requestId: message.requestId, id: message.id, success: false });
    return;
  }
  try {
    const result = await chrome.tabs.sendMessage(target.tabId, { type: "winisland-volume", id: target.elementId,
      requestId: message.requestId, volume: message.volume, muted: message.muted === true }, { frameId: target.frameId, ...(target.documentId ? { documentId: target.documentId } : {}) });
    const state = result?.state ? safeState(result.state, { tab: { id: target.tabId, title: target.tabTitle }, frameId: target.frameId, documentId: target.documentId }) : null;
    // Navigation/removal can finish while the old document acknowledges a
    // command. Never resurrect that document or overwrite its replacement.
    const current = players.has(message.id) && players.get(message.id).documentId === target.documentId;
    if (state && state.id === message.id && current) { players.set(state.id, state); sendNative({ type: "state", ...state }); }
    sendNative({ type: "result", requestId: message.requestId, id: message.id, success: result?.success === true && !!state && state.id === message.id && current,
      volume: state?.volume ?? target.volume, muted: state?.muted ?? target.muted });
  } catch {
    sendNative({ type: "result", requestId: message.requestId, id: message.id, success: false });
    if (players.get(message.id) === target) { players.delete(message.id); sendNative({ type: "remove", id: message.id }); }
  }
}

chrome.runtime.onMessage.addListener((message, sender, respond) => {
  if (message?.type === "companion-status" && !sender.tab) {
    prunePlayers();
    respond({ connected: nativeReady, status: connectionStatus, players: players.size });
    return;
  }
  if (message?.type === "companion-reconnect" && !sender.tab) {
    if (nativePort) nativePort.disconnect();
    nativePort = null; nativeReady = false; retryDelay = 1000; connect();
    respond({ status: "Reconnecting…" });
    return;
  }
  if (!sender.tab || !Number.isInteger(sender.tab.id) || !/^https?:|^about:blank$/.test(sender.url ?? "")) return;
  if (message?.type === "player-states" && Array.isArray(message.states)) {
    prunePlayers();
    const framePrefix = `${sender.tab.id}:${sender.frameId}:`;
    const known = new Set();
    for (const raw of message.states.slice(0, 16)) {
      const state = safeState(raw, sender);
      if (!state) continue;
      known.add(state.id);
      if (players.size < 128 || players.has(state.id)) { players.set(state.id, state); sendNative({ type: "state", ...state }); }
    }
    for (const id of players.keys()) if (id.startsWith(framePrefix) && !known.has(id)) { players.delete(id); sendNative({ type: "remove", id }); }
    if (!nativePort && !reconnectTimer) connect();
  }
});

chrome.tabs.onRemoved.addListener(removeTabPlayers);
chrome.tabs.onUpdated.addListener((tabId, change) => { if (change.status === "loading") removeTabPlayers(tabId); });

chrome.runtime.onStartup.addListener(connect);
chrome.runtime.onInstalled.addListener(connect);
connect();
