// Executed in the extension's isolated world. Never reads media metadata.
const positions = new WeakMap();
let lastPlaying = false;

function sample(force = false) {
  const now = performance.now();
  let playing = false;
  for (const video of document.querySelectorAll('video')) {
    const before = positions.get(video);
    const at = video.currentTime;
    const advancing = before && at !== before.at && !video.seeking;
    const lastAdvance = advancing ? now : before?.lastAdvance ?? -Infinity;
    positions.set(video, { at, lastAdvance });
    playing ||= !document.hidden && !video.paused && !video.ended &&
      !video.seeking && video.readyState >= 2 && now - lastAdvance < 1400;
  }
  if (playing || playing !== lastPlaying || force) {
    chrome.runtime.sendMessage({ playing }).catch(() => {});
    lastPlaying = playing;
  }
}

for (const event of ['pause', 'ended', 'seeking', 'visibilitychange']) {
  document.addEventListener(event, () => sample(true), true);
}
setInterval(sample, 750);
