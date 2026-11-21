// Floating video: the page's video in Chromium's own picture-in-picture
// window, which stays above every other app. `mode` is 'toggle' (the
// shortcut) or 'leave' (switching away from a tab with a video playing).
(async function (mode) {
  if (document.pictureInPictureElement) {
    if (mode === 'toggle') { await document.exitPictureInPicture(); return 'closed'; }
    return 'already';
  }
  function all(root, out) {
    root.querySelectorAll('video').forEach(function (v) { out.push(v); });
    root.querySelectorAll('*').forEach(function (el) { if (el.shadowRoot) all(el.shadowRoot, out); });
    return out;
  }
  var videos = all(document, []).filter(function (v) {
    return v.readyState >= 2 && !v.disablePictureInPicture && v.getBoundingClientRect().width > 80;
  });
  if (mode === 'leave') videos = videos.filter(function (v) { return !v.paused && !v.muted && !v.ended; });
  if (!videos.length) return 'none';
  videos.sort(function (a, b) {
    var pa = a.paused ? 0 : 1, pb = b.paused ? 0 : 1;
    if (pa !== pb) return pb - pa;
    var ra = a.getBoundingClientRect(), rb = b.getBoundingClientRect();
    return rb.width * rb.height - ra.width * ra.height;
  });
  try { await videos[0].requestPictureInPicture(); return 'floating'; }
  catch (e) { return 'refused: ' + e.message; }
})
