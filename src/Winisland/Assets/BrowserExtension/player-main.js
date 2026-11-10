"use strict";

(() => {
  if (window.__winislandMainCompanion) return;
  window.__winislandMainCompanion = true;
  const COMMAND_EVENT = "winisland-player-command-v1";
  const STATE_EVENT = "winisland-player-state-v1";
  const ids = new WeakMap();
  const media = new Map();
  const watching = new WeakSet();
  let sequence = 0;
  let scanTimer = 0;
  let heartbeat = 0;
  let lastReport = 0;

  function emit(message) { window.dispatchEvent(new CustomEvent(STATE_EVENT, { detail: message })); }
  function playerApi(element) {
    const parent = element.closest?.(".html5-video-player, #movie_player");
    return parent && typeof parent.setVolume === "function" && typeof parent.getVolume === "function" ? parent : null;
  }
  function metadata() {
    const metadata = navigator.mediaSession?.metadata;
    return { title: clipText(metadata?.title || document.title || "", 512), artist: clipText(metadata?.artist || "", 256), hasMetadata: !!metadata?.title };
  }
  function clipText(value, maximum) {
    let text = value.slice(0, maximum);
    const last = text.charCodeAt(text.length - 1);
    if (last >= 0xD800 && last <= 0xDBFF) text = text.slice(0, -1);
    return text;
  }
  function stateOf(id, element) {
    try {
      if (!element.isConnected) return null;
      const api = playerApi(element);
      const level = api ? Number(api.getVolume()) : element.volume * 100;
      if (!Number.isFinite(level) || level < 0 || level > 100) return null;
      return { id, ...metadata(), volume: level, muted: api && typeof api.isMuted === "function" ? api.isMuted() : element.muted,
        playing: !element.paused && !element.ended, duration: Number.isFinite(element.duration) ? element.duration : 0,
        position: Number.isFinite(element.currentTime) ? element.currentTime : 0 };
    } catch { return null; }
  }
  function report(force = false) {
    const now = performance.now();
    if (!force && now - lastReport < 75) return;
    lastReport = now;
    const states = [];
    for (const [id, element] of media) {
      const state = stateOf(id, element);
      if (state) states.push(state); else media.delete(id);
    }
    emit({ type: "states", states });
  }
  function collect(root, output, depth = 0) {
    if (depth > 3 || output.length >= 16) return;
    for (const element of root.querySelectorAll("video, audio")) {
      if (output.length >= 16) break;
      output.push(element);
    }
    // Common players inside open shadow roots are still their own HTML5 media.
    for (const element of root.querySelectorAll("*")) {
      if (element.shadowRoot) collect(element.shadowRoot, output, depth + 1);
      if (output.length >= 16) break;
    }
  }
  function scan() {
    scanTimer = 0;
    const found = [];
    collect(document, found);
    const retained = new Set();
    for (const element of found) {
      if (!ids.has(element)) ids.set(element, `m${++sequence}`);
      const id = ids.get(element);
      retained.add(id); media.set(id, element);
      if (!watching.has(element)) {
        watching.add(element);
        for (const event of ["play", "pause", "ended", "loadedmetadata", "volumechange", "emptied"])
          element.addEventListener(event, () => report());
      }
    }
    for (const id of media.keys()) if (!retained.has(id)) media.delete(id);
    if (media.size && !heartbeat) heartbeat = setInterval(() => report(true), 8000);
    if (!media.size && heartbeat) { clearInterval(heartbeat); heartbeat = 0; }
    report(true);
  }
  function scheduleScan() { if (!scanTimer) scanTimer = setTimeout(scan, 300); }

  window.addEventListener(COMMAND_EVENT, async event => {
    const command = event.detail;
    if (!command || typeof command !== "object") return;
    if (command.type === "scan") { scan(); return; }
    if (command.type !== "volume" || !/^m\d+$/.test(command.id ?? "") || !/^[a-f0-9]{32}$/.test(command.requestId ?? "")
      || !Number.isFinite(command.volume) || command.volume < 0 || command.volume > 100) return;
    const element = media.get(command.id);
    if (!element?.isConnected) { emit({ type: "result", requestId: command.requestId, success: false }); return; }
    try {
      const api = playerApi(element);
      if (api) {
        api.setVolume(Math.round(command.volume));
        if (command.muted && typeof api.mute === "function") api.mute();
        else if (!command.muted && typeof api.unMute === "function") api.unMute();
      } else {
        // A brief linear ramp removes audible steps without delaying the notch's
        // pointer feedback. The final readback always uses the actual element.
        const from = element.volume;
        const target = command.volume / 100;
        element.muted = command.muted === true;
        const start = performance.now();
        await new Promise(resolve => {
          let complete = false;
          let finish = 0;
          function frame(now) {
            if (complete) return;
            const progress = Math.min(1, Math.max(0, (now - start) / 80));
            element.volume = from + (target - from) * progress;
            if (progress < 1 && element.isConnected) requestAnimationFrame(frame);
            else { complete = true; clearTimeout(finish); resolve(); }
          }
          // Hidden tabs can throttle requestAnimationFrame to minutes. Timed
          // completion preserves background-player volume responsiveness.
          finish = setTimeout(() => { complete = true; if (element.isConnected) element.volume = target; resolve(); }, 100);
          if (document.visibilityState === "hidden") { complete = true; element.volume = target; clearTimeout(finish); resolve(); }
          else requestAnimationFrame(frame);
        });
        element.volume = target;
      }
      report(true);
      emit({ type: "result", requestId: command.requestId, success: true, state: stateOf(command.id, element) });
    } catch { emit({ type: "result", requestId: command.requestId, success: false }); }
  });

  new MutationObserver(records => {
    for (const record of records) for (const node of [...record.addedNodes, ...record.removedNodes]) {
      if (node.nodeType === 1 && (node.matches?.("video,audio") || node.querySelector?.("video,audio") || node.shadowRoot)) { scheduleScan(); return; }
    }
  }).observe(document, { childList: true, subtree: true });
  document.addEventListener("DOMContentLoaded", scan, { once: true });
  scheduleScan();
})();
