// How an extension's code reaches the browser on WebKit: one script message
// handler, `searchExt`, whose reply is the answer. Taken at document start,
// before the page's own scripts run, so a page can't swap it out later.
var __searchTransport = (function () {
  var h = window.webkit && window.webkit.messageHandlers && window.webkit.messageHandlers.searchExt;
  if (!h) return null;
  var send = h.postMessage.bind(h);
  var parse = JSON.parse, stringify = JSON.stringify;
  return function (envelope) {
    return send(stringify(envelope)).then(function (reply) {
      var o = typeof reply === 'string' ? parse(reply) : reply;
      if (o && o.error) throw new Error(o.error);
      return o ? o.value : undefined;
    });
  };
})();
