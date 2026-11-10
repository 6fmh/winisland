"use strict";
const status = document.getElementById("status");
chrome.runtime.sendMessage({ type: "companion-status" }, result => {
  if (chrome.runtime.lastError) { status.textContent = "Open Winisland to connect."; return; }
  status.textContent = result?.status ?? "Open Winisland to connect.";
});
document.getElementById("reconnect").addEventListener("click", () => {
  chrome.runtime.sendMessage({ type: "companion-reconnect" }, result => {
    status.textContent = chrome.runtime.lastError ? "Open Winisland to connect." : result?.status ?? "Reconnecting…";
  });
});
