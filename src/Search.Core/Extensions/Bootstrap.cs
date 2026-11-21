using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Search.Core.Extensions;

/// The JavaScript that brings an extension to life on WebKit: the chrome.*
/// runtime for its own pages (background, popup, options), and the loader
/// that runs its content scripts in the pages they match.
public static class Bootstrap
{
    static string Json(object? o) => JsonSerializer.Serialize(o);

    public static string Context(Manifest m, string kind, string baseUrl, string language)
    {
        var ctx = new JsonObject
        {
            ["id"] = m.Id,
            ["kind"] = kind,
            ["base"] = baseUrl,
            ["manifest"] = m.Raw.DeepClone(),
            ["messages"] = JsonNode.Parse(m.Locale.ToJson()),
            ["language"] = language,
        };
        return ctx.ToJsonString();
    }

    /// Served at /__search/runtime.js and loaded first by every extension page.
    public static string PageRuntime(Manifest m, string kind, string baseUrl, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Scripts.Transport.Value);
        sb.AppendLine(Scripts.ExtensionApi.Value);
        sb.Append("(function(){var ctx=").Append(Context(m, kind, baseUrl, language)).AppendLine(";");
        sb.AppendLine("""
          if (!__searchTransport) return;
          var call = function (api, args) { return __searchTransport({ ext: ctx.id, kind: ctx.kind, api: api, args: args === undefined ? null : args }); };
          var made = __searchMakeChrome(ctx, call);
          window.chrome = made.chrome; window.browser = made.chrome;
          Object.defineProperty(window, '__searchExtDispatch', { value: made.dispatch });
          if (ctx.kind === 'background') {
            // A service worker's world, as far as a page can play it.
            var importScripts = function () {
              for (var i = 0; i < arguments.length; i++) {
                var x = new XMLHttpRequest();
                x.open('GET', new URL(arguments[i], location.href).href, false);
                x.send();
                if (x.status >= 400) throw new Error('importScripts failed: ' + arguments[i]);
                (0, eval)(x.responseText + '\n//# sourceURL=' + arguments[i]);
              }
            };
            window.importScripts = importScripts;
            var registration = { scope: location.origin + '/', active: { state: 'activated' }, showNotification: function (t, o) { return made.chrome.notifications.create({ title: t, message: (o && o.body) || '' }); }, unregister: function () { return Promise.resolve(true); } };
            try { Object.defineProperty(window, 'registration', { value: registration }); } catch (e) {}
            window.clients = { matchAll: function () { return Promise.resolve([]); }, claim: function () { return Promise.resolve(); }, openWindow: function (u) { return made.chrome.tabs.create({ url: u }); } };
            window.skipWaiting = function () { return Promise.resolve(); };
          }
          call('_hello', { url: location.href });
        })();
        """);
        return sb.ToString();
    }

    /// The background page for an extension: its own page, its scripts, or
    /// its service worker loaded as a script.
    public static string BackgroundHtml(Manifest m)
    {
        var sb = new StringBuilder("<!doctype html><meta charset=utf-8><title>background</title><script src=\"/__search/runtime.js?kind=background\"></script>");
        if (m.BackgroundWorker != null)
            sb.Append("<script src=\"/").Append(m.BackgroundWorker).Append('"').Append(m.BackgroundIsModule ? " type=module" : "").Append("></script>");
        foreach (var s in m.BackgroundScripts) sb.Append("<script src=\"/").Append(s).Append("\"></script>");
        return sb.ToString();
    }

    /// The runtime script put at the top of an extension page's <head>.
    public static string InjectRuntime(string html, string kind)
    {
        var tag = "<script src=\"/__search/runtime.js?kind=" + kind + "\"></script>";
        var lower = html.ToLowerInvariant();
        int head = lower.IndexOf("<head", StringComparison.Ordinal);
        if (head >= 0)
        {
            int close = html.IndexOf('>', head);
            if (close > 0) return html.Insert(close + 1, tag);
        }
        int doctype = lower.IndexOf("<!doctype", StringComparison.Ordinal);
        if (doctype >= 0)
        {
            int close = html.IndexOf('>', doctype);
            if (close > 0) return html.Insert(close + 1, tag);
        }
        return tag + html;
    }

    public sealed record Installed(Manifest Manifest, string BaseUrl, Func<string, string?> Read);

    /// One user script for every page: for each extension, the content
    /// scripts whose patterns match where the page is, run at their time.
    public static string ContentLoader(IEnumerable<Installed> extensions, string language)
    {
        var sb = new StringBuilder();
        sb.AppendLine("(function(){'use strict';");
        sb.AppendLine("var __t=(function(){var __searchTransport;" + Scripts.Transport.Value + "return __searchTransport;})();");
        sb.AppendLine("if(!__t)return;");
        sb.AppendLine("var __make=(function(){var __searchMakeChrome;" + Scripts.ExtensionApi.Value + "return __searchMakeChrome;})();");
        sb.AppendLine("""
          var href = location.href, isTop = window.top === window, registry = {};
          function any(list, s) { for (var i = 0; i < list.length; i++) if (new RegExp(list[i], 'i').test(s)) return true; return false; }
          function matches(s) {
            if (!s.all && !isTop) return false;
            if (!any(s.m, href) || any(s.x, href)) return false;
            if (s.ig.length && !any(s.ig, href)) return false;
            if (s.xg.length && any(s.xg, href)) return false;
            return true;
          }
          function style(css) {
            var el = document.createElement('style');
            el.textContent = css;
            var root = document.head || document.documentElement;
            if (root) root.appendChild(el);
            else new MutationObserver(function (_, o) { if (document.documentElement) { o.disconnect(); document.documentElement.appendChild(el); } }).observe(document, { childList: true });
          }
          function when(at, fn) {
            function go() { try { fn(); } catch (e) { console.error('[extension]', e); } }
            if (at === 'document_start') return go();
            if (at === 'document_end') {
              if (document.readyState !== 'loading') return go();
              return document.addEventListener('DOMContentLoaded', go, { once: true });
            }
            if (document.readyState === 'complete') return setTimeout(go, 0);
            document.addEventListener('DOMContentLoaded', function () { setTimeout(go, 0); }, { once: true });
          }
          var exts = [
          """);
        foreach (var ext in extensions)
        {
            var m = ext.Manifest;
            if (m.ContentScripts.Count == 0) continue;
            sb.Append("{id:").Append(Json(m.Id)).Append(",ctx:").Append(Context(m, "content", ext.BaseUrl, language)).AppendLine(",scripts:[");
            foreach (var cs in m.ContentScripts)
            {
                sb.Append("{m:").Append(Json(cs.Matches.Select(MatchPattern.ToRegex)))
                  .Append(",x:").Append(Json(cs.ExcludeMatches.Select(MatchPattern.ToRegex)))
                  .Append(",ig:").Append(Json(cs.IncludeGlobs.Select(MatchPattern.GlobToRegex)))
                  .Append(",xg:").Append(Json(cs.ExcludeGlobs.Select(MatchPattern.GlobToRegex)))
                  .Append(",at:").Append(Json(cs.RunAt)).Append(",all:").Append(cs.AllFrames ? "true" : "false")
                  .Append(",main:").Append(cs.World == "MAIN" ? "true" : "false")
                  .Append(",css:").Append(Json(cs.Css.Select(f => ext.Read(f) ?? "")))
                  .AppendLine(",run:function(chrome,browser){");
                foreach (var f in cs.Js)
                {
                    var code = ext.Read(f);
                    if (code == null) continue;
                    sb.AppendLine(code);
                    sb.AppendLine(";");
                }
                sb.AppendLine("}},");
            }
            sb.AppendLine("]},");
        }
        sb.AppendLine("""
          ];
          exts.forEach(function (e) {
            var mine = e.scripts.filter(matches);
            if (!mine.length) return;
            var made = __make(e.ctx, function (api, args) {
              return __t({ ext: e.id, kind: 'content', api: api, args: args === undefined ? null : args, top: isTop });
            });
            registry[e.id] = made.dispatch;
            mine.forEach(function (s) {
              s.css.forEach(style);
              when(s.at, function () { s.main ? s.run.call(window, undefined, undefined) : s.run.call(window, made.chrome, made.chrome); });
            });
          });
          var ids = Object.keys(registry);
          if (!ids.length || !isTop) return;
          var name = '__search' + Math.random().toString(36).slice(2);
          Object.defineProperty(window, name, { value: function (id, evt) { var d = registry[id]; if (d) d(evt); } });
          __t({ ext: '', kind: 'content', api: '_register', args: { name: name, exts: ids }, top: true }).catch(function () {});
        })();
        """);
        return sb.ToString();
    }
}
