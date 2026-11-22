// Hide Elements: point at a thing, click, and it is gone on this site for
// good. Up arrow widens to the parent, down arrow narrows back, Esc stops.
// Toggled: running it again while picking stops picking.
(function () {
  var post = function (m) {
    var s = JSON.stringify(m);
    try { window.chrome.webview.postMessage(s); } catch (e) {}
    try { window.webkit.messageHandlers.search.postMessage(s); } catch (e) {}
  };
  if (window.__officeHider) { window.__officeHider.stop(); return 'off'; }

  var box = document.createElement('div');
  box.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;border:2px solid #f59e0b;' +
    'background:rgba(245,158,11,.12);border-radius:3px;transition:all .06s ease-out;display:none';
  var tag = document.createElement('div');
  tag.style.cssText = 'position:fixed;z-index:2147483647;pointer-events:none;font:12px/1.4 "Segoe UI",sans-serif;' +
    'background:#171717;color:#fff;padding:3px 7px;border-radius:4px;display:none;white-space:nowrap';
  var hint = document.createElement('div');
  hint.textContent = 'Click something to hide it on this site · ↑ widen · Esc to stop';
  hint.style.cssText = 'position:fixed;z-index:2147483647;left:50%;bottom:18px;transform:translateX(-50%);' +
    'font:13px/1.4 "Segoe UI",sans-serif;background:#171717;color:#fff;padding:8px 14px;border-radius:8px;' +
    'box-shadow:0 4px 18px rgba(0,0,0,.25);pointer-events:none';
  document.documentElement.append(box, tag, hint);

  var current = null, trail = [];

  function stable(c) { return /^[a-zA-Z_][\w-]{0,40}$/.test(c) && !/\d{3,}/.test(c) && !/^(is-|has-)?(hover|active|focus|open|selected)/.test(c); }
  function esc(s) { return window.CSS && CSS.escape ? CSS.escape(s) : s; }
  function step(el) {
    var s = el.tagName.toLowerCase();
    var classes = Array.prototype.filter.call(el.classList, stable).slice(0, 2);
    if (classes.length) s += '.' + classes.map(esc).join('.');
    var parent = el.parentElement;
    if (parent) {
      var same = Array.prototype.filter.call(parent.children, function (c) { return c.tagName === el.tagName; });
      if (same.length > 1 && !classes.length) s += ':nth-of-type(' + (same.indexOf(el) + 1) + ')';
    }
    return s;
  }
  function selector(el) {
    if (el.id && stable(el.id) && document.querySelectorAll('#' + esc(el.id)).length === 1) return '#' + esc(el.id);
    var parts = [], node = el;
    while (node && node !== document.documentElement && parts.length < 7) {
      if (node !== el && node.id && stable(node.id)) { parts.unshift('#' + esc(node.id)); break; }
      parts.unshift(step(node));
      var sel = parts.join(' > ');
      try { if (document.querySelectorAll(sel).length === 1) return sel; } catch (e) {}
      node = node.parentElement;
    }
    return parts.join(' > ');
  }
  function label(el) {
    var name = el.getAttribute('aria-label') || el.getAttribute('alt') || el.getAttribute('title') ||
      (el.innerText || '').replace(/\s+/g, ' ').trim();
    var kind = el.tagName.toLowerCase();
    if (kind === 'iframe') kind = 'frame';
    else if (kind === 'img') kind = 'picture';
    return (name ? name.slice(0, 60) : kind) + (name && name.length > 60 ? '…' : '');
  }
  function show(el) {
    current = el;
    if (!el) { box.style.display = tag.style.display = 'none'; return; }
    var r = el.getBoundingClientRect();
    box.style.display = tag.style.display = 'block';
    box.style.left = r.left + 'px'; box.style.top = r.top + 'px';
    box.style.width = r.width + 'px'; box.style.height = r.height + 'px';
    tag.textContent = step(el) + '  ' + Math.round(r.width) + '×' + Math.round(r.height);
    tag.style.left = Math.max(4, r.left) + 'px';
    tag.style.top = (r.top > 26 ? r.top - 24 : r.bottom + 4) + 'px';
  }
  function over(e) {
    var el = e.target;
    if (!el || el === box || el === tag || el === hint || el === document.documentElement || el === document.body) return;
    trail = []; show(el);
  }
  function swallow(e) { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); }
  function click(e) {
    swallow(e);
    if (!current) return;
    var sel = selector(current);
    post({ kind: 'hide', selector: sel, label: label(current) });
    var style = document.getElementById('office-hidden');
    if (!style) { style = document.createElement('style'); style.id = 'office-hidden'; (document.head || document.documentElement).appendChild(style); }
    style.textContent += '\n' + sel + '{display:none !important}';
    show(null);
  }
  function key(e) {
    if (e.key === 'Escape') { swallow(e); stop(); }
    else if (e.key === 'ArrowUp' && current && current.parentElement && current.parentElement !== document.body) {
      swallow(e); trail.push(current); show(current.parentElement);
    } else if (e.key === 'ArrowDown' && trail.length) { swallow(e); show(trail.pop()); }
  }
  function stop() {
    document.removeEventListener('mouseover', over, true);
    document.removeEventListener('click', click, true);
    document.removeEventListener('mousedown', swallow, true);
    document.removeEventListener('mouseup', swallow, true);
    document.removeEventListener('keydown', key, true);
    box.remove(); tag.remove(); hint.remove();
    delete window.__officeHider;
    post({ kind: 'hiding', on: false });
  }
  document.addEventListener('mouseover', over, true);
  document.addEventListener('click', click, true);
  document.addEventListener('mousedown', swallow, true);
  document.addEventListener('mouseup', swallow, true);
  document.addEventListener('keydown', key, true);
  window.__officeHider = { stop: stop };
  post({ kind: 'hiding', on: true });
  return 'on';
})();
