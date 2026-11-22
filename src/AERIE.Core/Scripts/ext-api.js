// The chrome.* API for extensions running on WebKit for Windows.
//
// On the Mac, Search runs Chrome extensions on Safari's own extension engine
// and fills in what it lacks. WebKit for Windows has no extension engine at
// all, so this is one: every chrome.* call becomes a message to the browser
// (`call`), and everything the browser has to tell an extension — a message,
// a storage change, an alarm — comes back through `dispatch`.
//
// __searchMakeChrome(ctx, call) → { chrome, dispatch }
//   ctx:  { id, kind: 'background'|'popup'|'page'|'content', base, manifest, messages, tabId, frameId, language }
//   call: (api, args) → Promise<value>, rejecting with an Error carrying the message
var __searchMakeChrome = function (ctx, call) {
  'use strict';
  var lastError = null;

  // Every API takes a trailing callback and also returns a promise, as
  // Chrome's do since MV3; lastError is set for the callback's duration.
  function api(name, shape) {
    return function () {
      var args = Array.prototype.slice.call(arguments);
      var callback = typeof args[args.length - 1] === 'function' ? args.pop() : null;
      var payload = shape ? shape.apply(null, args) : args;
      var p = call(name, payload);
      if (!callback) return p;
      p.then(function (v) { callback(v); }, function (e) {
        lastError = { message: (e && e.message) || String(e) };
        try { callback(); } finally { lastError = null; }
      });
    };
  }
  function local(fn) {
    return function () {
      var args = Array.prototype.slice.call(arguments);
      var callback = typeof args[args.length - 1] === 'function' ? args.pop() : null;
      var v;
      try { v = fn.apply(null, args); } catch (e) { if (callback) { lastError = { message: e.message }; try { callback(); } finally { lastError = null; } return; } return Promise.reject(e); }
      if (callback) { Promise.resolve(v).then(callback); return; }
      return Promise.resolve(v);
    };
  }

  function Event(name) {
    var listeners = [];
    var e = {
      addListener: function (fn) {
        if (typeof fn === 'function' && listeners.indexOf(fn) < 0) {
          listeners.push(fn);
          if (name && listeners.length === 1) call('events.listen', { event: name }).catch(function () {});
        }
      },
      removeListener: function (fn) { var i = listeners.indexOf(fn); if (i >= 0) listeners.splice(i, 1); },
      hasListener: function (fn) { return listeners.indexOf(fn) >= 0; },
      hasListeners: function () { return listeners.length > 0; },
      _fire: function () {
        var args = arguments, results = [];
        listeners.slice().forEach(function (fn) {
          try { results.push(fn.apply(null, args)); } catch (err) { console.error('[' + ctx.id + '] listener error', err); }
        });
        return results;
      },
      _count: function () { return listeners.length; },
    };
    return e;
  }

  var events = {};
  function event(name) { return events[name] || (events[name] = Event(name)); }

  // runtime ------------------------------------------------------------------

  function senderFor(s) { return s || { id: ctx.id }; }

  // A message for every onMessage listener; the first sendResponse wins, and
  // a listener returning true (or a promise) keeps the channel open.
  function deliverMessage(evt) {
    var answered = false, keep = false;
    function respond(value) {
      if (answered) return;
      answered = true;
      call('runtime.reply', { replyId: evt.replyId, value: value === undefined ? null : value }).catch(function () {});
    }
    var results = event('runtime.onMessage')._fire(evt.message, senderFor(evt.sender), respond);
    results.forEach(function (r) {
      if (r === true) keep = true;
      else if (r && typeof r.then === 'function') { keep = true; r.then(respond, function () { respond(undefined); }); }
    });
    if (!keep && !answered) call('runtime.reply', { replyId: evt.replyId, value: null, none: true }).catch(function () {});
  }

  var ports = {};
  function Port(portId, name, sender) {
    var port = {
      name: name || '',
      sender: sender,
      onMessage: Event(null),
      onDisconnect: Event(null),
      postMessage: function (msg) {
        if (!ports[portId]) throw new Error('Attempting to use a disconnected port object');
        call('port.post', { portId: portId, message: msg }).catch(function () {});
      },
      disconnect: function () {
        if (!ports[portId]) return;
        delete ports[portId];
        call('port.close', { portId: portId }).catch(function () {});
      },
    };
    ports[portId] = port;
    return port;
  }

  function connect(extensionId, info) {
    if (typeof extensionId === 'object') { info = extensionId; extensionId = null; }
    info = info || {};
    var portId = ctx.id + ':' + Math.random().toString(36).slice(2);
    var port = Port(portId, info.name, { id: ctx.id });
    call('runtime.connect', { portId: portId, name: info.name || '', tabId: null }).catch(function () {
      delete ports[portId]; port.onDisconnect._fire(port);
    });
    return port;
  }

  var runtime = {
    id: ctx.id,
    get lastError() { return lastError || undefined; },
    getURL: function (path) { return ctx.base + String(path || '').replace(/^\//, ''); },
    getManifest: function () { return JSON.parse(JSON.stringify(ctx.manifest)); },
    getPlatformInfo: local(function () { return { os: 'win', arch: 'x86-64', nacl_arch: 'x86-64' }; }),
    getBrowserInfo: local(function () { return { name: 'Search', vendor: 'Search', version: '1.0', buildID: '1' }; }),
    sendMessage: api('runtime.sendMessage', function (a, b) {
      // (message) | (message, options) | (extensionId, message[, options])
      var addressed = typeof a === 'string' && /^[a-p]{32}$/.test(a) && b !== undefined;
      return { message: addressed ? b : a };
    }),
    connect: connect,
    openOptionsPage: api('runtime.openOptionsPage'),
    setUninstallURL: local(function () {}),
    reload: function () { call('runtime.reload', {}).catch(function () {}); },
    requestUpdateCheck: local(function () { return { status: 'no_update' }; }),
    getContexts: local(function () { return []; }),
    onMessage: event('runtime.onMessage'),
    onMessageExternal: Event(null),
    onConnect: event('runtime.onConnect'),
    onConnectExternal: Event(null),
    onInstalled: event('runtime.onInstalled'),
    onStartup: event('runtime.onStartup'),
    onSuspend: Event(null),
    onUpdateAvailable: Event(null),
    PlatformOs: { WIN: 'win', MAC: 'mac', LINUX: 'linux' },
    OnInstalledReason: { INSTALL: 'install', UPDATE: 'update', CHROME_UPDATE: 'chrome_update' },
  };
  if (ctx.kind !== 'content') {
    runtime.getBackgroundPage = local(function () { return null; });
  } else {
    // A content script sees only part of the runtime, as in Chrome.
    delete runtime.openOptionsPage; delete runtime.onInstalled; delete runtime.onStartup; delete runtime.reload;
  }

  // storage ------------------------------------------------------------------

  function area(name) {
    var a = {
      get: api('storage.get', function (keys) { return { area: name, keys: keys === undefined ? null : keys }; }),
      set: api('storage.set', function (items) { return { area: name, items: items }; }),
      remove: api('storage.remove', function (keys) { return { area: name, keys: keys }; }),
      clear: api('storage.clear', function () { return { area: name }; }),
      getBytesInUse: api('storage.bytes', function (keys) { return { area: name, keys: keys === undefined ? null : keys }; }),
      setAccessLevel: local(function () {}),
      onChanged: Event(null),
      QUOTA_BYTES: name === 'sync' ? 102400 : 10485760,
    };
    if (name === 'sync') { a.QUOTA_BYTES_PER_ITEM = 8192; a.MAX_ITEMS = 512; a.MAX_WRITE_OPERATIONS_PER_HOUR = 1800; a.MAX_WRITE_OPERATIONS_PER_MINUTE = 120; }
    return a;
  }
  var storage = {
    local: area('local'), sync: area('sync'), session: area('session'), managed: area('managed'),
    onChanged: event('storage.onChanged'),
  };

  // tabs, windows -----------------------------------------------------------------

  var tabs = {
    TAB_ID_NONE: -1,
    query: api('tabs.query', function (q) { return { query: q || {} }; }),
    get: api('tabs.get', function (id) { return { tabId: id }; }),
    getCurrent: api('tabs.getCurrent', function () { return {}; }),
    create: api('tabs.create', function (p) { return { props: p || {} }; }),
    update: api('tabs.update', function (id, p) { if (typeof id === 'object') { p = id; id = null; } return { tabId: id, props: p || {} }; }),
    remove: api('tabs.remove', function (ids) { return { tabIds: [].concat(ids) }; }),
    reload: api('tabs.reload', function (id) { return { tabId: typeof id === 'number' ? id : null }; }),
    duplicate: api('tabs.duplicate', function (id) { return { tabId: id }; }),
    sendMessage: api('tabs.sendMessage', function (id, message, opts) { return { tabId: id, message: message, frameId: opts && opts.frameId }; }),
    connect: function (tabId, info) {
      info = info || {};
      var portId = ctx.id + ':' + Math.random().toString(36).slice(2);
      var port = Port(portId, info.name, { id: ctx.id });
      call('runtime.connect', { portId: portId, name: info.name || '', tabId: tabId }).catch(function () {
        delete ports[portId]; port.onDisconnect._fire(port);
      });
      return port;
    },
    executeScript: api('tabs.executeScript', function (id, details) { if (typeof id === 'object') { details = id; id = null; } return { tabId: id, details: details }; }),
    insertCSS: api('tabs.insertCSS', function (id, details) { if (typeof id === 'object') { details = id; id = null; } return { tabId: id, details: details }; }),
    captureVisibleTab: api('tabs.captureVisibleTab'),
    setZoom: api('tabs.setZoom', function (id, z) { return { tabId: id, zoom: z }; }),
    getZoom: local(function () { return 1; }),
    detectLanguage: local(function () { return 'und'; }),
    onCreated: event('tabs.onCreated'),
    onUpdated: event('tabs.onUpdated'),
    onActivated: event('tabs.onActivated'),
    onRemoved: event('tabs.onRemoved'),
    onReplaced: Event(null),
    onHighlighted: Event(null),
    onMoved: Event(null),
    onAttached: Event(null),
    onDetached: Event(null),
    onZoomChange: Event(null),
  };
  var oneWindow = { id: 1, focused: true, top: 0, left: 0, width: 1280, height: 800, incognito: false, type: 'normal', state: 'normal', alwaysOnTop: false };
  var windows = {
    WINDOW_ID_NONE: -1, WINDOW_ID_CURRENT: -2,
    get: local(function () { return oneWindow; }),
    getCurrent: local(function () { return oneWindow; }),
    getLastFocused: local(function () { return oneWindow; }),
    getAll: local(function () { return [oneWindow]; }),
    create: api('tabs.create', function (p) { p = p || {}; return { props: { url: [].concat(p.url || 'about:blank')[0], active: true } }; }),
    update: local(function () { return oneWindow; }),
    remove: local(function () {}),
    onFocusChanged: Event(null), onCreated: Event(null), onRemoved: Event(null), onBoundsChanged: Event(null),
  };

  // scripting ------------------------------------------------------------------

  var scripting = {
    executeScript: api('scripting.executeScript', function (inj) {
      var d = { target: inj.target, files: inj.files || null, world: inj.world || 'ISOLATED' };
      if (inj.func) { d.code = '(' + inj.func.toString() + ').apply(null, ' + JSON.stringify(inj.args || []) + ')'; }
      return d;
    }),
    insertCSS: api('scripting.insertCSS', function (inj) { return { target: inj.target, css: inj.css || null, files: inj.files || null }; }),
    removeCSS: api('scripting.removeCSS', function (inj) { return { target: inj.target, css: inj.css || null, files: inj.files || null }; }),
    registerContentScripts: api('scripting.register', function (s) { return { scripts: s }; }),
    unregisterContentScripts: api('scripting.unregister', function (f) { return { filter: f || null }; }),
    getRegisteredContentScripts: api('scripting.registered', function () { return {}; }),
    updateContentScripts: api('scripting.register', function (s) { return { scripts: s, update: true }; }),
    ExecutionWorld: { ISOLATED: 'ISOLATED', MAIN: 'MAIN' },
  };

  // i18n: answered here, from the extension's own _locales.

  function message(name, subs) {
    var m = ctx.messages && ctx.messages[String(name).toLowerCase()];
    if (!m) {
      if (name === '@@extension_id') return ctx.id;
      if (name === '@@ui_locale') return (ctx.language || 'en').replace('-', '_');
      if (name === '@@bidi_dir') return 'ltr';
      if (name === '@@bidi_reversed_dir') return 'rtl';
      if (name === '@@bidi_start_edge') return 'left';
      if (name === '@@bidi_end_edge') return 'right';
      return '';
    }
    subs = subs === undefined ? [] : [].concat(subs).map(String);
    var text = m.message || '';
    function positional(s) { return s.replace(/\$([1-9])/g, function (_, n) { return subs[n - 1] !== undefined ? subs[n - 1] : ''; }); }
    if (m.placeholders) Object.keys(m.placeholders).forEach(function (key) {
      var content = positional((m.placeholders[key] || {}).content || '');
      text = text.replace(new RegExp('\\$' + key.replace(/[.*+?^${}()|[\]\\]/g, '\\$&') + '\\$', 'gi'), function () { return content; });
    });
    return positional(text).replace(/\$\$/g, '$');
  }
  var i18n = {
    getMessage: message,
    getUILanguage: function () { return ctx.language || navigator.language || 'en'; },
    getAcceptLanguages: local(function () { return [ctx.language || navigator.language || 'en']; }),
    detectLanguage: local(function () { return { isReliable: false, languages: [] }; }),
  };

  // The rest -------------------------------------------------------------------------

  var alarms = {
    create: api('alarms.create', function (name, info) { if (typeof name === 'object') { info = name; name = ''; } return { name: name || '', info: info || {} }; }),
    get: api('alarms.get', function (name) { return { name: name || '' }; }),
    getAll: api('alarms.getAll'),
    clear: api('alarms.clear', function (name) { return { name: name || '' }; }),
    clearAll: api('alarms.clearAll'),
    onAlarm: event('alarms.onAlarm'),
  };

  function actionApi() {
    return {
      setBadgeText: api('action.set', function (d) { return { what: 'badgeText', details: d }; }),
      getBadgeText: api('action.get', function (d) { return { what: 'badgeText', details: d || {} }; }),
      setBadgeBackgroundColor: api('action.set', function (d) { return { what: 'badgeColor', details: d }; }),
      getBadgeBackgroundColor: local(function () { return [0, 0, 0, 0]; }),
      setBadgeTextColor: local(function () {}),
      setTitle: api('action.set', function (d) { return { what: 'title', details: d }; }),
      getTitle: api('action.get', function (d) { return { what: 'title', details: d || {} }; }),
      setIcon: local(function () {}),
      setPopup: api('action.set', function (d) { return { what: 'popup', details: d }; }),
      getPopup: api('action.get', function (d) { return { what: 'popup', details: d || {} }; }),
      enable: local(function () {}), disable: local(function () {}),
      isEnabled: local(function () { return true; }),
      openPopup: api('action.openPopup'),
      getUserSettings: local(function () { return { isOnToolbar: true }; }),
      onClicked: event('action.onClicked'),
    };
  }
  var action = actionApi();

  var menus = {
    create: function (props, cb) {
      props = props || {};
      var id = props.id !== undefined ? props.id : Math.floor(Math.random() * 1e9);
      var sent = {}; Object.keys(props).forEach(function (k) { if (k !== 'onclick') sent[k] = props[k]; }); sent.id = id;
      if (typeof props.onclick === 'function') clickHandlers[id] = props.onclick;
      call('menus.create', { props: sent }).then(function () { cb && cb(); }, function (e) { if (cb) { lastError = { message: e.message }; try { cb(); } finally { lastError = null; } } });
      return id;
    },
    update: api('menus.update', function (id, props) { return { id: id, props: props }; }),
    remove: api('menus.remove', function (id) { return { id: id }; }),
    removeAll: api('menus.removeAll'),
    onClicked: event('menus.onClicked'),
    ContextType: { ALL: 'all', PAGE: 'page', SELECTION: 'selection', LINK: 'link', IMAGE: 'image', EDITABLE: 'editable', ACTION: 'action' },
  };
  var clickHandlers = {};

  var dnr = {
    MAX_NUMBER_OF_RULES: 30000, MAX_NUMBER_OF_DYNAMIC_RULES: 30000, MAX_NUMBER_OF_SESSION_RULES: 5000,
    MAX_NUMBER_OF_ENABLED_STATIC_RULESETS: 50, MAX_NUMBER_OF_STATIC_RULESETS: 100, MAX_NUMBER_OF_REGEX_RULES: 1000,
    GUARANTEED_MINIMUM_STATIC_RULES: 30000, DYNAMIC_RULESET_ID: '_dynamic', SESSION_RULESET_ID: '_session',
    updateDynamicRules: api('dnr.update', function (o) { return { scope: 'dynamic', options: o || {} }; }),
    getDynamicRules: api('dnr.get', function (f) { return { scope: 'dynamic', filter: f || {} }; }),
    updateSessionRules: api('dnr.update', function (o) { return { scope: 'session', options: o || {} }; }),
    getSessionRules: api('dnr.get', function (f) { return { scope: 'session', filter: f || {} }; }),
    updateEnabledRulesets: api('dnr.rulesets', function (o) { return { options: o || {} }; }),
    getEnabledRulesets: api('dnr.enabled'),
    updateStaticRules: local(function () {}),
    getDisabledRuleIds: local(function () { return []; }),
    isRegexSupported: local(function (o) {
      var r = o && o.regex || '';
      var ok = !/\||\{|\(\?|\\[dDwWsSbB1-9]/.test(r);
      return ok ? { isSupported: true } : { isSupported: false, reason: 'syntaxError' };
    }),
    getAvailableStaticRuleCount: local(function () { return 30000; }),
    setExtensionActionOptions: local(function () {}),
    getMatchedRules: local(function () { return { rulesMatchedInfo: [] }; }),
    testMatchOutcome: local(function () { return { matchedRules: [] }; }),
    onRuleMatchedDebug: Event(null),
  };

  var permissions = {
    // What the manifest declares, not "yes" to everything: an extension that
    // checks for a permission it never asked for gets the truth.
    contains: local(function (req) {
      var have = (ctx.manifest.permissions || []).concat(ctx.manifest.optional_permissions || []);
      var origins = (ctx.manifest.host_permissions || []).concat(ctx.manifest.optional_host_permissions || []);
      var ps = (req && req.permissions) || [], os = (req && req.origins) || [];
      for (var i = 0; i < ps.length; i++) if (have.indexOf(ps[i]) < 0) return false;
      for (var j = 0; j < os.length; j++) if (origins.indexOf(os[j]) < 0) return false;
      return true;
    }),
    request: local(function (req) { return permissions.contains(req); }),
    remove: local(function () { return false; }),
    getAll: local(function () { return { permissions: ctx.manifest.permissions || [], origins: (ctx.manifest.host_permissions || []) }; }),
    onAdded: Event(null), onRemoved: Event(null),
  };

  var webNavigation = {
    getFrame: api('webNavigation.getFrame', function (d) { return d; }),
    getAllFrames: api('webNavigation.getAllFrames', function (d) { return d; }),
    onBeforeNavigate: event('webNavigation.onBeforeNavigate'),
    onCommitted: event('webNavigation.onCommitted'),
    onDOMContentLoaded: event('webNavigation.onDOMContentLoaded'),
    onCompleted: event('webNavigation.onCompleted'),
    onErrorOccurred: event('webNavigation.onErrorOccurred'),
    onHistoryStateUpdated: event('webNavigation.onHistoryStateUpdated'),
    onReferenceFragmentUpdated: Event(null),
    onCreatedNavigationTarget: Event(null),
    onTabReplaced: Event(null),
  };

  var notifications = {
    create: api('notifications.create', function (id, opts) { if (typeof id === 'object') { opts = id; id = ''; } return { id: id || '', options: opts }; }),
    update: local(function () { return true; }),
    clear: api('notifications.clear', function (id) { return { id: id }; }),
    getAll: local(function () { return {}; }),
    getPermissionLevel: local(function () { return 'granted'; }),
    onClicked: event('notifications.onClicked'), onClosed: Event(null), onButtonClicked: event('notifications.onButtonClicked'),
  };

  var chrome = {
    runtime: runtime,
    storage: storage,
    i18n: i18n,
    extension: {
      getURL: runtime.getURL,
      inIncognitoContext: false,
      getBackgroundPage: function () { return null; },
      getViews: function () { return []; },
      isAllowedIncognitoAccess: local(function () { return false; }),
      isAllowedFileSchemeAccess: local(function () { return false; }),
      sendMessage: runtime.sendMessage,
      onMessage: runtime.onMessage,
    },
  };
  if (ctx.kind !== 'content') {
    chrome.tabs = tabs;
    chrome.windows = windows;
    chrome.scripting = scripting;
    chrome.alarms = alarms;
    chrome.action = action;
    chrome.browserAction = action;
    chrome.pageAction = action;
    chrome.contextMenus = menus;
    chrome.menus = menus;
    chrome.declarativeNetRequest = dnr;
    chrome.permissions = permissions;
    chrome.webNavigation = webNavigation;
    chrome.notifications = notifications;
    chrome.commands = { getAll: local(function () { return []; }), onCommand: event('commands.onCommand') };
    chrome.idle = { queryState: local(function () { return 'active'; }), setDetectionInterval: function () {}, onStateChanged: Event(null) };
    chrome.offscreen = {
      // There is no separate document to make: the background page already has a DOM.
      createDocument: local(function () {}), closeDocument: local(function () {}), hasDocument: local(function () { return false; }),
      Reason: { DOM_PARSER: 'DOM_PARSER', CLIPBOARD: 'CLIPBOARD', AUDIO_PLAYBACK: 'AUDIO_PLAYBACK', BLOBS: 'BLOBS' },
    };
    chrome.sidePanel = { setOptions: local(function () {}), setPanelBehavior: local(function () {}), open: local(function () {}) };
    chrome.identity = {
      getRedirectURL: function (p) { return 'https://' + ctx.id + '.chromiumapp.org/' + (p || ''); },
      launchWebAuthFlow: api('identity.launchWebAuthFlow', function (d) { return { details: d }; }),
    };
    chrome.management = { getSelf: local(function () { return { id: ctx.id, name: ctx.manifest.name, enabled: true, installType: 'normal', version: ctx.manifest.version }; }) };
  } else {
    chrome.runtime.getURL = runtime.getURL;
  }

  // From the browser ---------------------------------------------------------------

  function dispatch(evt) {
    switch (evt.type) {
      case 'message': deliverMessage(evt); break;
      case 'connect': {
        var port = Port(evt.portId, evt.name, evt.sender);
        event('runtime.onConnect')._fire(port);
        break;
      }
      case 'port.message': { var p = ports[evt.portId]; if (p) p.onMessage._fire(evt.message, p); break; }
      case 'port.close': { var q = ports[evt.portId]; if (q) { delete ports[evt.portId]; q.onDisconnect._fire(q); } break; }
      case 'storage': {
        storage[evt.area] && storage[evt.area].onChanged._fire(evt.changes);
        event('storage.onChanged')._fire(evt.changes, evt.area);
        break;
      }
      case 'menus.clicked': {
        var h = clickHandlers[evt.info.menuItemId];
        if (h) try { h(evt.info, evt.tab); } catch (e) { console.error(e); }
        event('menus.onClicked')._fire(evt.info, evt.tab);
        break;
      }
      case 'event': event(evt.name)._fire.apply(null, evt.args || []); break;
    }
  }

  return { chrome: chrome, dispatch: dispatch };
};
