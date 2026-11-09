const host = navigator.userAgent.includes('Edg/') ? 'com.janela.edge' : 'com.janela.chrome';
const frames = new Map();
let port;
let last = false;
let generation = 0;
let retryAt = 0;

function publish(playing) {
  if (playing && !port && Date.now() >= retryAt) {
    port = chrome.runtime.connectNative(host);
    port.onDisconnect.addListener(() => {
      void chrome.runtime.lastError;
      port = undefined;
      last = false;
      retryAt = Date.now() + 3000;
    });
  }
  if (port && (playing || last)) {
    try {
      port.postMessage({ playing });
      last = playing;
    } catch {
      port = undefined;
      last = false;
    }
  }
}

async function refresh() {
  const request = ++generation;
  let window, tab;
  try {
    window = await chrome.windows.getLastFocused();
    if (window.focused) [tab] = await chrome.tabs.query({ active: true, windowId: window.id });
  } catch { /* Window may have closed between calls. */ }
  if (request !== generation) return;

  const cutoff = Date.now() - 1900;
  let playing = false;
  for (const [key, frame] of frames) {
    if (frame.at < cutoff) frames.delete(key);
    else if (frame.tab === tab?.id) playing = true;
  }
  publish(playing);
}

chrome.runtime.onMessage.addListener((message, sender) => {
  if (sender.id !== chrome.runtime.id || sender.tab?.id === undefined ||
      typeof message?.playing !== 'boolean') return;
  const key = sender.tab.id + ':' + sender.frameId;
  if (message.playing && sender.tab.active) frames.set(key, { tab: sender.tab.id, at: Date.now() });
  else frames.delete(key);
  void refresh();
});

function clearTab(tab) {
  for (const [key, frame] of frames) if (frame.tab === tab) frames.delete(key);
  void refresh();
}

chrome.tabs.onActivated.addListener(() => {
  frames.clear();
  publish(false);
  void refresh();
});
chrome.windows.onFocusChanged.addListener(() => {
  frames.clear();
  publish(false);
  void refresh();
});
chrome.tabs.onRemoved.addListener(clearTab);
chrome.tabs.onUpdated.addListener((tab, change) => {
  if (change.status === 'loading') clearTab(tab);
});
setInterval(() => { if (last) void refresh(); }, 1000);
