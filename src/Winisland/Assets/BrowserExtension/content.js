"use strict";

(() => {
  if (window.__winislandIsolatedCompanion) return;
  window.__winislandIsolatedCompanion = true;
  const COMMAND_EVENT = "winisland-player-command-v1";
  const STATE_EVENT = "winisland-player-state-v1";
  const pending = new Map();
  let lastStates = [];
  let reportTimer = 0;

  function sendStates() {
    reportTimer = 0;
    try { chrome.runtime.sendMessage({ type: "player-states", states: lastStates }, () => void chrome.runtime.lastError); }
    catch { /* Extension reloads invalidate old content-script contexts. */ }
  }

  window.addEventListener(STATE_EVENT, event => {
    const message = event.detail;
    if (!message || typeof message !== "object") return;
    if (message.type === "states" && Array.isArray(message.states)) {
      lastStates = message.states.slice(0, 16);
      if (!reportTimer) reportTimer = setTimeout(sendStates, 75);
    } else if (message.type === "result" && typeof message.requestId === "string") {
      const request = pending.get(message.requestId);
      if (!request) return;
      pending.delete(message.requestId);
      clearTimeout(request.timer);
      request.respond({ success: message.success === true, state: message.state });
    }
  });

  chrome.runtime.onMessage.addListener((message, sender, respond) => {
    if (sender.id !== chrome.runtime.id || message?.type !== "winisland-volume" || typeof message.id !== "string"
      || !/^m\d+$/.test(message.id) || !/^[a-f0-9]{32}$/.test(message.requestId ?? "") || !Number.isFinite(message.volume)
      || message.volume < 0 || message.volume > 100) return;
    if (pending.size >= 8) { respond({ success: false }); return; }
    const timer = setTimeout(() => { pending.delete(message.requestId); respond({ success: false }); }, 1600);
    pending.set(message.requestId, { respond, timer });
    window.dispatchEvent(new CustomEvent(COMMAND_EVENT, { detail: { type: "volume", id: message.id,
      requestId: message.requestId, volume: message.volume, muted: message.muted === true } }));
    return true;
  });

  // Main-world APIs stay behind a narrow volume-only event bridge. No page URL,
  // cookies, credentials, browsing history or arbitrary script goes to the app.
  window.dispatchEvent(new CustomEvent(COMMAND_EVENT, { detail: { type: "scan" } }));
  setInterval(() => window.dispatchEvent(new CustomEvent(COMMAND_EVENT, { detail: { type: "scan" } })), 10000);
  window.addEventListener("pagehide", () => { lastStates = []; sendStates(); });
})();
