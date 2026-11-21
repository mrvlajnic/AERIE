using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;
using Search.Core;
using Search.Core.Extensions;

namespace Search;

/// Chrome extensions on WebKit for Windows, which has no extension engine:
/// Search is the engine. Each extension gets its own origin (a loopback
/// port), a background page standing in for its service worker, its content
/// scripts in every page they match, and the chrome.* API, answered here.
sealed class WebKitExtensions : IExtensionHost
{
    sealed class Record
    {
        public string Id { get; set; } = "";
        public string Folder { get; set; } = "";
        public string? StoreId { get; set; }
        public bool Enabled { get; set; } = true;
        public int Port { get; set; }
    }
    sealed class Records { public List<Record> Items { get; set; } = new(); }

    sealed class Loaded
    {
        public required Record Record;
        public required Manifest Manifest;
        public required ExtensionServer Server;
        public WebKitPage? Background;
        public readonly Dictionary<string, JsonObject> Storage = new();
        public readonly Dictionary<string, DispatcherTimer> Alarms = new();
        public readonly Dictionary<string, JsonObject> AlarmInfo = new();
        public string Badge = "";
        public string? Title, Popup;
        public JsonObject Dnr = new() { ["dynamic"] = new JsonArray(), ["enabled"] = null };
        public JsonArray SessionRules = new();
        public readonly List<JsonObject> Menus = new();
        public string Base => Server.Base;
    }

    readonly WebKitEngine engine;
    readonly Records records = Store.Load<Records>("webkit-extensions.json");
    readonly Dictionary<string, Loaded> loaded = new();
    readonly Dictionary<WebKitPage, (string Name, HashSet<string> Exts)> registered = new();
    readonly Dictionary<int, (TaskCompletionSource<JsonNode?> Done, int Expected, int None)> pending = new();
    readonly Dictionary<string, (WebKitPage A, string? AName, WebKitPage? B, string? BName, string Ext)> ports = new();
    readonly Dictionary<Tab, int> tabIds = new();
    int nextTab = 1, nextReply = 1;
    string? loader;
    static readonly HttpClient http = new();

    public WebKitExtensions(WebKitEngine engine) { this.engine = engine; }

    BrowserWindow Browser => engine.Browser;
    static string DataFolder(string id) => Path.Combine(Store.Folder, "ExtensionData", id);
    string Language => System.Globalization.CultureInfo.CurrentUICulture.Name;

    /// Off: WebKit's content scripts share the page's JavaScript world, so any
    /// page could speak as an installed extension. WebView2 is the supported
    /// path; this runtime stays for a future isolated-world design.
    public bool Enabled => false;

    public string About => Enabled
        ? "Chrome extensions run on Search's own extension runtime."
        : "Extensions are disabled on the experimental WebKit engine: its page-world bridge would let any page " +
          "act as an installed extension. Switch to the WebView2 engine to use Chrome extensions.";

    static InvalidOperationException Disabled() =>
        new("Extensions are disabled on the WebKit engine. Switch to WebView2 to use Chrome extensions.");

    public IReadOnlyList<ExtensionInfo> All => !Enabled ? Array.Empty<ExtensionInfo>() : records.Items.Select(r =>
    {
        loaded.TryGetValue(r.Id, out var l);
        var name = l?.Manifest.Name ?? Path.GetFileName(r.Folder);
        if (l != null && l.Badge.Length > 0) name += $" ({l.Badge})";
        return new ExtensionInfo(r.Id, name, r.Folder, r.StoreId, l?.Manifest.Popup != null || l?.Popup != null, r.Enabled);
    }).ToList();

    public async Task Start()
    {
        if (!Enabled) return;
        foreach (var r in records.Items.ToList())
            if (r.Enabled) try { await Load(r); } catch (Exception e) { Log(r.Id, e); }
    }

    static void Log(string id, Exception e) =>
        File.AppendAllText(Path.Combine(Store.Folder, "errors.log"), $"{DateTime.Now:u} extension {id}: {e}\n\n");

    async Task Load(Record r)
    {
        var m = Manifest.Load(r.Folder, r.Id);
        var l = new Loaded { Record = r, Manifest = m, Server = null! };
        l.Server = new ExtensionServer(m, r.Port, kind => Bootstrap.PageRuntime(m, kind, l.Base, Language));
        r.Port = l.Server.Port;
        Store.Save("webkit-extensions.json", records);
        var dnr = Path.Combine(DataFolder(r.Id), "dnr.json");
        if (File.Exists(dnr)) try { l.Dnr = JsonNode.Parse(File.ReadAllText(dnr)) as JsonObject ?? l.Dnr; } catch { }
        loaded[r.Id] = l;
        var alarms = Path.Combine(DataFolder(r.Id), "alarms.json");
        if (File.Exists(alarms))
            try
            {
                var array = JsonNode.Parse(File.ReadAllText(alarms)) as JsonArray ?? new JsonArray();
                foreach (var node in array)
                    if (node is JsonObject o && o["name"]?.GetValue<string>() is { Length: > 0 } name)
                        Alarm(l, name, o);
            }
            catch { }
        await CompileRules(l);
        if (m.HasBackground)
        {
            l.Background = new WebKitPage(engine, null, IntPtr.Zero, r.Id);
            l.Background.MakeView(engine.Parking);
            l.Background.Navigate(l.Server.BackgroundUrl);
        }
        Changed();
    }

    void Unload(string id)
    {
        if (!loaded.Remove(id, out var l)) return;
        l.Background?.Dispose();
        l.Server.Dispose();
        foreach (var t in l.Alarms.Values) t.Stop();
        engine.ExtensionFilters.Remove(id);
        foreach (var p in engine.Pages) engine.Filters(p);
        Changed();
    }

    /// Something about the set of extensions changed: new content scripts for new pages.
    void Changed()
    {
        loader = null;
        foreach (var p in engine.Pages) if (p.Owner != null) p.RefreshScripts();
    }

    /// The content-script loader every tab runs before its document.
    public IEnumerable<string> ContentScripts()
    {
        if (!Enabled) yield break;
        if (loader == null)
        {
            var with = loaded.Values.Where(l => l.Manifest.ContentScripts.Count > 0).ToList();
            loader = with.Count == 0 ? "" : Bootstrap.ContentLoader(with.Select(l => new Bootstrap.Installed(l.Manifest, l.Base, f =>
            {
                var p = Path.GetFullPath(Path.Combine(l.Manifest.Folder, f.TrimStart('/')));
                return p.StartsWith(Path.GetFullPath(l.Manifest.Folder)) && File.Exists(p) ? File.ReadAllText(p) : null;
            })), Language);
        }
        if (loader.Length > 0) yield return loader;
    }

    // Installing ------------------------------------------------------------------

    public async Task<string> InstallFromStore(string link)
    {
        if (!Enabled) throw Disabled();
        var id = Crx.IdFrom(link) ?? throw new InvalidOperationException("That isn't a Chrome Web Store link.");
        var bytes = await http.GetByteArrayAsync(Crx.DownloadUrl(id));
        var folder = Path.Combine(Store.Folder, "Extensions", id);
        await Task.Run(() => Crx.Install(bytes, folder, id));
        return await Add(folder, id, id);
    }

    public Task<string> AddFolder(string folder) =>
        Enabled ? Add(folder, null, null) : Task.FromException<string>(Disabled());

    async Task<string> Add(string folder, string? id, string? storeId)
    {
        if (!Enabled) throw Disabled();
        var m = Manifest.Load(folder, id);
        Unload(m.Id);
        records.Items.RemoveAll(r => r.Id == m.Id);
        var r = new Record { Id = m.Id, Folder = folder, StoreId = storeId };
        records.Items.Add(r);
        Store.Save("webkit-extensions.json", records);
        Directory.CreateDirectory(DataFolder(m.Id));
        await Load(r);
        return m.Name;
    }

    public Task Remove(ExtensionInfo e)
    {
        if (!Enabled) return Task.CompletedTask;
        Unload(e.Id);
        records.Items.RemoveAll(r => r.Id == e.Id);
        Store.Save("webkit-extensions.json", records);
        var root = Path.Combine(Store.Folder, "Extensions");
        if (e.Folder.StartsWith(root)) try { Directory.Delete(e.Folder, true); } catch { }
        try { Directory.Delete(DataFolder(e.Id), true); } catch { }
        return Task.CompletedTask;
    }

    public async Task Enable(ExtensionInfo e, bool on)
    {
        if (!Enabled) return;
        var r = records.Items.FirstOrDefault(x => x.Id == e.Id);
        if (r == null) return;
        r.Enabled = on;
        Store.Save("webkit-extensions.json", records);
        if (on) await Load(r); else Unload(e.Id);
    }

    public void OpenPopup(BrowserWindow browser, ExtensionInfo e)
    {
        if (!Enabled) { browser.Toast("Extensions are disabled on the WebKit engine."); return; }
        if (!loaded.TryGetValue(e.Id, out var l)) { browser.Toast(e.Name + " is turned off"); return; }
        var popup = l.Popup ?? l.Manifest.Popup;
        if (popup == null)
        {
            if (l.Manifest.OptionsPage != null) browser.Open(l.Base + l.Manifest.OptionsPage);
            else Dispatch(l, Event("action.onClicked", TabObject(browser.Current)));
            return;
        }
        var w = PopupWindow.Make(browser, l.Manifest.Name);
        var page = new WebKitPage(engine, null, IntPtr.Zero, l.Record.Id);
        w.Content = page.Element;
        page.SetShown(true);
        // Chrome sizes a popup to what it shows; so does this, within bounds.
        page.Finished += async (_, _, _) =>
        {
            var size = await page.Eval("[document.documentElement.scrollWidth, document.documentElement.scrollHeight]");
            try
            {
                var a = JsonSerializer.Deserialize<double[]>(size ?? "")!;
                w.Width = Math.Clamp(a[0] + 16, 200, 800);
                w.Height = Math.Clamp(a[1] + 40, 120, 600);
            }
            catch { }
        };
        page.CloseRequested += () => w.Close();
        w.Closed += (_, _) => page.Dispose();
        w.Show();
        page.Navigate(l.Base + popup);
    }

    // Messages from pages ------------------------------------------------------------

    /// Who is calling: an extension's own page (known by its origin), or a
    /// content script in a tab (which names its extension, and may use
    /// only what a content script may).
    public async void Receive(WebKitPage from, string body, string origin, bool mainFrame, Action<string> reply)
    {
        if (!Enabled) { reply("null"); return; }
        JsonObject env;
        try { env = JsonNode.Parse(body) as JsonObject ?? new JsonObject(); }
        catch { reply(Error("bad message")); return; }
        var api = env["api"]?.GetValue<string>() ?? "";
        var args = env["args"] as JsonObject ?? new JsonObject();

        Loaded? l = from.ExtensionId != null && loaded.TryGetValue(from.ExtensionId, out var own) ? own
            : loaded.Values.FirstOrDefault(x => origin.TrimEnd('/') == x.Base.TrimEnd('/'));
        bool content = l == null;
        if (content)
        {
            if (api == "_register")
            {
                if (mainFrame) registered[from] = (args["name"]?.GetValue<string>() ?? "", (args["exts"] as JsonArray ?? new()).Select(x => x!.GetValue<string>()).ToHashSet());
                reply(Ok(null)); return;
            }
            var claimed = env["ext"]?.GetValue<string>() ?? "";
            if (!loaded.TryGetValue(claimed, out l) || l.Manifest.ContentScripts.Count == 0) { reply(Error("unknown extension")); return; }
            if (!ContentMay(api)) { reply(Error($"{api} isn't available to content scripts")); return; }
        }
        try { reply(Ok(await Answer(l!, from, content, api, args))); }
        catch (Exception e) { reply(Error(e.Message)); }
    }

    static bool ContentMay(string api) =>
        api.StartsWith("storage.") || api is "runtime.sendMessage" or "runtime.reply" or "runtime.connect" or "port.post" or "port.close" or "events.listen" or "_hello";

    static string Ok(JsonNode? value) => new JsonObject { ["value"] = value?.DeepClone() }.ToJsonString();
    static string Error(string message) => new JsonObject { ["error"] = message }.ToJsonString();

    async Task<JsonNode?> Answer(Loaded l, WebKitPage from, bool content, string api, JsonObject a)
    {
        var tab = TabOf(from);
        switch (api)
        {
            case "_hello":
            case "events.listen":
                return null;

            // runtime -----------------------------------------------------------
            case "runtime.sendMessage":
            {
                var targets = new List<(WebKitPage Page, string? Name)>();
                if (l.Background != null && from != l.Background) targets.Add((l.Background, null));
                foreach (var p in ExtensionPages(l)) if (p != from) targets.Add((p, null));
                if (targets.Count == 0) throw new Exception("Could not establish connection. Receiving end does not exist.");
                var sender = Sender(l, from, content);
                return await Ask(l, targets, a["message"], sender);
            }
            case "runtime.reply":
            {
                int id = a["replyId"]!.GetValue<int>();
                if (pending.TryGetValue(id, out var p))
                {
                    if (a["none"]?.GetValue<bool>() == true)
                    {
                        p.None++;
                        pending[id] = p;
                        if (p.None >= p.Expected) { pending.Remove(id); p.Done.TrySetResult(null); }
                    }
                    else { pending.Remove(id); p.Done.TrySetResult(a["value"]?.DeepClone()); }
                }
                return null;
            }
            case "runtime.connect":
            {
                var portId = a["portId"]!.GetValue<string>();
                WebKitPage? target; string? name = null;
                if (a["tabId"]?.GetValue<int?>() is int tid)
                {
                    var t = TabById(tid) ?? throw new Exception("No tab with id " + tid);
                    if (t.Page is not WebKitPage tp || !registered.TryGetValue(tp, out var reg) || !reg.Exts.Contains(l.Record.Id))
                        throw new Exception("Could not establish connection. Receiving end does not exist.");
                    target = tp; name = reg.Name;
                }
                else target = from == l.Background ? ExtensionPages(l).FirstOrDefault() : l.Background;
                if (target == null) throw new Exception("Could not establish connection. Receiving end does not exist.");
                var fromName = content && registered.TryGetValue(from, out var mine) ? mine.Name : null;
                ports[portId] = (from, fromName, target, name, l.Record.Id);
                await Deliver(l, target, name, new JsonObject { ["type"] = "connect", ["portId"] = portId, ["name"] = a["name"]?.DeepClone(), ["sender"] = Sender(l, from, content) });
                return null;
            }
            case "port.post":
            case "port.close":
            {
                var portId = a["portId"]!.GetValue<string>();
                if (!ports.TryGetValue(portId, out var port)) return null;
                var (other, otherName) = from == port.A ? (port.B, port.BName) : (port.A, port.AName);
                if (api == "port.close") ports.Remove(portId);
                if (other != null)
                    await Deliver(l, other, otherName, api == "port.post"
                        ? new JsonObject { ["type"] = "port.message", ["portId"] = portId, ["message"] = a["message"]?.DeepClone() }
                        : new JsonObject { ["type"] = "port.close", ["portId"] = portId });
                return null;
            }
            case "runtime.openOptionsPage":
                if (l.Manifest.OptionsPage != null) Browser.Open(l.Base + l.Manifest.OptionsPage);
                return null;
            case "runtime.reload":
                l.Background?.Reload();
                return null;

            // storage -----------------------------------------------------------
            case "storage.get": return StorageGet(l, Area(a), a["keys"]);
            case "storage.bytes": return JsonValue.Create(Encoding(StorageGet(l, Area(a), a["keys"])));
            case "storage.set":
            case "storage.remove":
            case "storage.clear":
                await StorageChange(l, Area(a), api, a);
                return null;

            // tabs -------------------------------------------------------------------
            case "tabs.query": return new JsonArray(Query(a["query"] as JsonObject ?? new()).Select(t => (JsonNode)TabObject(t)!).ToArray());
            case "tabs.get": return TabObject(TabById(a["tabId"]!.GetValue<int>()) ?? throw new Exception("No tab with id " + a["tabId"]));
            case "tabs.getCurrent": return tab == null ? null : TabObject(tab);
            case "tabs.create":
            {
                var p = a["props"] as JsonObject ?? new();
                var url = Resolve(l, p["url"]?.GetValue<string>() ?? "about:blank");
                bool active = p["active"]?.GetValue<bool>() ?? true;
                var t = Browser.Open(url, select: active, index: p["index"]?.GetValue<int>());
                return TabObject(t);
            }
            case "tabs.update":
            {
                var t = (a["tabId"]?.GetValue<int?>() is int id ? TabById(id) : Browser.Current) ?? throw new Exception("No such tab");
                var p = a["props"] as JsonObject ?? new();
                if (p["url"]?.GetValue<string>() is { } url) Browser.Navigate(t, Resolve(l, url));
                if (p["active"]?.GetValue<bool>() == true) await Browser.Select(t);
                if (p["muted"]?.GetValue<bool>() is bool m && t.Page != null) t.Page.Muted = m;
                if (p["pinned"]?.GetValue<bool>() is bool pin && pin != t.Pinned) Browser.TogglePin(t);
                return TabObject(t);
            }
            case "tabs.remove":
                foreach (var id in (a["tabIds"] as JsonArray ?? new()).Select(x => x!.GetValue<int>()))
                    if (TabById(id) is { } t) Browser.Close(t);
                return null;
            case "tabs.reload":
                ((a["tabId"]?.GetValue<int?>() is int rid ? TabById(rid) : Browser.Current)?.Page)?.Reload();
                return null;
            case "tabs.duplicate":
            {
                var t = TabById(a["tabId"]!.GetValue<int>()) ?? throw new Exception("No such tab");
                return TabObject(Browser.Open(t.Url));
            }
            case "tabs.sendMessage":
            {
                var t = TabById(a["tabId"]!.GetValue<int>()) ?? throw new Exception("No tab with id " + a["tabId"]);
                if (t.Page is not WebKitPage tp || !registered.TryGetValue(tp, out var reg) || !reg.Exts.Contains(l.Record.Id))
                    throw new Exception("Could not establish connection. Receiving end does not exist.");
                return await Ask(l, new() { (tp, reg.Name) }, a["message"], Sender(l, from, content));
            }
            case "tabs.executeScript":
            case "scripting.executeScript":
            {
                bool mv3 = api.StartsWith("scripting");
                var target = mv3 ? a["target"] as JsonObject : null;
                var t = (mv3 ? TabById(target?["tabId"]?.GetValue<int>() ?? -1) : a["tabId"]?.GetValue<int?>() is int id ? TabById(id) : Browser.Current)
                    ?? throw new Exception("No such tab");
                var details = mv3 ? a : a["details"] as JsonObject ?? new();
                string code;
                if (details["code"]?.GetValue<string>() is { } c) code = mv3 ? c : "(function(){ return eval(" + JsonSerializer.Serialize(c) + "); })()";
                else
                {
                    var files = (details["files"] as JsonArray)?.Select(x => x!.GetValue<string>())
                        ?? (details["file"]?.GetValue<string>() is { } f ? new[] { f } : Array.Empty<string>());
                    code = "(function(){\n" + string.Join("\n;\n", files.Select(f => File.ReadAllText(Path.Combine(l.Manifest.Folder, f.TrimStart('/'))))) + "\n})()";
                }
                var result = t.Page == null ? null : await t.Page.Eval(code);
                var value = result == null ? null : JsonNode.Parse(result);
                return mv3 ? new JsonArray(new JsonObject { ["frameId"] = 0, ["result"] = value?.DeepClone(), ["documentId"] = "0" }) : new JsonArray(value?.DeepClone());
            }
            case "tabs.insertCSS":
            case "scripting.insertCSS":
            case "scripting.removeCSS":
            {
                bool mv3 = api.StartsWith("scripting");
                var target = mv3 ? a["target"] as JsonObject : null;
                var t = (mv3 ? TabById(target?["tabId"]?.GetValue<int>() ?? -1) : a["tabId"]?.GetValue<int?>() is int id ? TabById(id) : Browser.Current)
                    ?? throw new Exception("No such tab");
                var details = mv3 ? a : a["details"] as JsonObject ?? new();
                var css = details["css"]?.GetValue<string>() ?? details["code"]?.GetValue<string>()
                    ?? string.Join("\n", ((details["files"] as JsonArray)?.Select(x => x!.GetValue<string>()) ?? (details["file"]?.GetValue<string>() is { } f ? new[] { f } : Array.Empty<string>()))
                        .Select(f => File.ReadAllText(Path.Combine(l.Manifest.Folder, f.TrimStart('/')))));
                var key = JsonSerializer.Serialize(l.Record.Id + ":" + css.GetHashCode());
                var script = api == "scripting.removeCSS"
                    ? $"document.querySelectorAll('style[data-search-css=' + JSON.stringify({key}) + ']').forEach(function(s){{s.remove()}})"
                    : $"(function(){{var s=document.createElement('style');s.dataset.searchCss={key};s.textContent={JsonSerializer.Serialize(css)};(document.head||document.documentElement).appendChild(s)}})()";
                if (t.Page != null) await t.Page.Eval(script);
                return null;
            }
            case "tabs.setZoom": return null;
            case "tabs.captureVisibleTab": throw new Exception("Capturing a tab isn't supported in Search yet");

            // alarms -------------------------------------------------------------
            case "alarms.create": Alarm(l, a["name"]?.GetValue<string>() ?? "", a["info"] as JsonObject ?? new()); return null;
            case "alarms.get": return l.AlarmInfo.TryGetValue(a["name"]?.GetValue<string>() ?? "", out var info) ? info.DeepClone() : null;
            case "alarms.getAll": return new JsonArray(l.AlarmInfo.Values.Select(v => v.DeepClone()).ToArray());
            case "alarms.clear":
            {
                var name = a["name"]?.GetValue<string>() ?? "";
                bool had = l.Alarms.Remove(name, out var timer);
                timer?.Stop(); l.AlarmInfo.Remove(name);
                SaveAlarms(l);
                return had;
            }
            case "alarms.clearAll":
                foreach (var t in l.Alarms.Values) t.Stop();
                l.Alarms.Clear(); l.AlarmInfo.Clear();
                SaveAlarms(l);
                return true;

            // action -------------------------------------------------------------
            case "action.set":
            {
                var what = a["what"]?.GetValue<string>();
                var d = a["details"] as JsonObject ?? new();
                if (what == "badgeText") l.Badge = d["text"]?.GetValue<string>() ?? "";
                if (what == "title") l.Title = d["title"]?.GetValue<string>();
                if (what == "popup") l.Popup = d["popup"]?.GetValue<string>() is { Length: > 0 } pp ? pp.TrimStart('/') : null;
                return null;
            }
            case "action.get":
                return a["what"]?.GetValue<string>() switch
                {
                    "badgeText" => l.Badge,
                    "title" => l.Title ?? l.Manifest.ActionTitle ?? l.Manifest.Name,
                    "popup" => (l.Popup ?? l.Manifest.Popup) is { } pp ? l.Base + pp : "",
                    _ => null,
                };
            case "action.openPopup":
                _ = Browser.Dispatcher.BeginInvoke(() => OpenPopup(Browser, All.First(x => x.Id == l.Record.Id)));
                return null;

            // context menus: remembered; Search doesn't show them yet.
            case "menus.create": l.Menus.Add(a["props"] as JsonObject ?? new()); return null;
            case "menus.update": return null;
            case "menus.remove": l.Menus.RemoveAll(m => m["id"]?.ToJsonString() == a["id"]?.ToJsonString()); return null;
            case "menus.removeAll": l.Menus.Clear(); return null;

            // declarativeNetRequest ------------------------------------------------
            case "dnr.update":
            {
                var scope = a["scope"]?.GetValue<string>();
                var o = a["options"] as JsonObject ?? new();
                var list = scope == "session" ? l.SessionRules : (JsonArray)l.Dnr["dynamic"]!;
                var remove = (o["removeRuleIds"] as JsonArray ?? new()).Select(x => x!.GetValue<int>()).ToHashSet();
                foreach (var gone in list.Where(r => remove.Contains(r!["id"]!.GetValue<int>())).ToList()) list.Remove(gone);
                foreach (var add in o["addRules"] as JsonArray ?? new()) list.Add(add!.DeepClone());
                await CompileRules(l);
                return null;
            }
            case "dnr.get":
                return (a["scope"]?.GetValue<string>() == "session" ? l.SessionRules : (JsonArray)l.Dnr["dynamic"]!).DeepClone();
            case "dnr.rulesets":
            {
                var o = a["options"] as JsonObject ?? new();
                var enabled = EnabledRulesets(l).ToHashSet();
                foreach (var x in o["disableRulesetIds"] as JsonArray ?? new()) enabled.Remove(x!.GetValue<string>());
                foreach (var x in o["enableRulesetIds"] as JsonArray ?? new()) enabled.Add(x!.GetValue<string>());
                l.Dnr["enabled"] = new JsonArray(enabled.Select(e => (JsonNode)e).ToArray());
                await CompileRules(l);
                return null;
            }
            case "dnr.enabled": return new JsonArray(EnabledRulesets(l).Select(e => (JsonNode)e).ToArray());

            case "notifications.create":
            {
                var o = a["options"] as JsonObject ?? new();
                Browser.Toast((o["title"]?.GetValue<string>() ?? l.Manifest.Name) + " — " + (o["message"]?.GetValue<string>() ?? ""));
                var id = a["id"]?.GetValue<string>() is { Length: > 0 } nid ? nid : Guid.NewGuid().ToString("N");
                return id;
            }
            case "notifications.clear": return true;

            case "webNavigation.getFrame":
            {
                var t = TabById(a["tabId"]?.GetValue<int>() ?? -1);
                return t == null ? null : new JsonObject { ["frameId"] = 0, ["parentFrameId"] = -1, ["url"] = t.Url, ["errorOccurred"] = false };
            }
            case "webNavigation.getAllFrames":
            {
                var t = TabById(a["tabId"]?.GetValue<int>() ?? -1);
                return t == null ? null : new JsonArray(new JsonObject { ["frameId"] = 0, ["parentFrameId"] = -1, ["url"] = t.Url, ["errorOccurred"] = false });
            }
            case "identity.launchWebAuthFlow": throw new Exception("Signing in through an extension isn't supported in Search yet");
            case "scripting.register":
            case "scripting.unregister":
                throw new Exception("Registering content scripts at run time isn't supported in Search yet");
            case "scripting.registered": return new JsonArray();
        }
        throw new Exception($"Not supported in Search yet: {api}");
    }

    // Messaging ---------------------------------------------------------------------

    /// A message to some pages; the first real answer wins.
    async Task<JsonNode?> Ask(Loaded l, List<(WebKitPage Page, string? Name)> targets, JsonNode? message, JsonObject sender)
    {
        int id = nextReply++;
        var done = new TaskCompletionSource<JsonNode?>();
        pending[id] = (done, targets.Count, 0);
        foreach (var (page, name) in targets)
            await Deliver(l, page, name, new JsonObject { ["type"] = "message", ["message"] = message?.DeepClone(), ["sender"] = sender.DeepClone(), ["replyId"] = id });
        return await done.Task;
    }

    async Task Deliver(Loaded l, WebKitPage page, string? contentName, JsonObject evt)
    {
        var json = evt.ToJsonString();
        var script = contentName != null
            ? $"(function(){{var d=window[{JsonSerializer.Serialize(contentName)}];if(d)d({JsonSerializer.Serialize(l.Record.Id)},{json});}})()"
            : $"(function(){{if(window.__searchExtDispatch)window.__searchExtDispatch({json});}})()";
        await page.Eval(script);
    }

    static JsonObject Event(string name, params JsonNode?[] args) =>
        new() { ["type"] = "event", ["name"] = name, ["args"] = new JsonArray(args.Select(a => a?.DeepClone()).ToArray()) };

    /// An event for every part of an extension: background, its pages, its content scripts.
    async Task Broadcast(Loaded l, JsonObject evt, bool toContent)
    {
        if (l.Background != null) await Deliver(l, l.Background, null, evt);
        foreach (var p in ExtensionPages(l)) await Deliver(l, p, null, evt);
        if (toContent)
            foreach (var (page, reg) in registered.ToList())
                if (reg.Exts.Contains(l.Record.Id)) await Deliver(l, page, reg.Name, evt);
    }

    void Dispatch(Loaded l, JsonObject evt) { if (l.Background != null) _ = Deliver(l, l.Background, null, evt); }

    IEnumerable<WebKitPage> ExtensionPages(Loaded l) =>
        engine.Pages.Where(p => p != l.Background && (p.ExtensionId == l.Record.Id || p.Url.StartsWith(l.Base)));

    JsonObject Sender(Loaded l, WebKitPage from, bool content)
    {
        var s = new JsonObject { ["id"] = l.Record.Id, ["url"] = from.Url, ["origin"] = Origin(from.Url) };
        if (content && TabOf(from) is { } t) { s["tab"] = TabObject(t); s["frameId"] = 0; }
        return s;
    }

    static string Origin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : "";

    // Storage -----------------------------------------------------------------------

    static string Area(JsonObject a) => a["area"]?.GetValue<string>() is "sync" or "session" or "managed" ? a["area"]!.GetValue<string>() : "local";

    JsonObject Data(Loaded l, string area)
    {
        if (l.Storage.TryGetValue(area, out var d)) return d;
        d = new JsonObject();
        var file = Path.Combine(DataFolder(l.Record.Id), area + ".json");
        if (area is "local" or "sync" && File.Exists(file))
            try { d = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new(); } catch { }
        return l.Storage[area] = d;
    }

    JsonObject StorageGet(Loaded l, string area, JsonNode? keys)
    {
        var data = Data(l, area);
        var result = new JsonObject();
        if (keys == null) { foreach (var kv in data) result[kv.Key] = kv.Value?.DeepClone(); return result; }
        if (keys is JsonValue v && v.TryGetValue<string>(out var one)) { if (data.TryGetPropertyValue(one, out var x)) result[one] = x?.DeepClone(); return result; }
        if (keys is JsonArray arr) { foreach (var k in arr.Select(k => k!.GetValue<string>())) if (data.TryGetPropertyValue(k, out var x)) result[k] = x?.DeepClone(); return result; }
        if (keys is JsonObject defaults)
            foreach (var kv in defaults) result[kv.Key] = (data.TryGetPropertyValue(kv.Key, out var x) ? x : kv.Value)?.DeepClone();
        return result;
    }

    static int Encoding(JsonObject o) => System.Text.Encoding.UTF8.GetByteCount(o.ToJsonString());

    async Task StorageChange(Loaded l, string area, string api, JsonObject a)
    {
        if (area == "managed") throw new Exception("This is a read-only store.");
        var data = Data(l, area);
        var changes = new JsonObject();
        if (api == "storage.set")
        {
            foreach (var kv in a["items"] as JsonObject ?? new())
            {
                var change = new JsonObject { ["newValue"] = kv.Value?.DeepClone() };
                if (data.TryGetPropertyValue(kv.Key, out var old)) change["oldValue"] = old?.DeepClone();
                data[kv.Key] = kv.Value?.DeepClone();
                changes[kv.Key] = change;
            }
        }
        else
        {
            var keys = api == "storage.clear" ? data.Select(kv => kv.Key).ToList()
                : a["keys"] is JsonArray arr ? arr.Select(k => k!.GetValue<string>()).ToList()
                : a["keys"]?.GetValue<string>() is { } k1 ? new List<string> { k1 } : new();
            foreach (var k in keys)
                if (data.TryGetPropertyValue(k, out var old)) { changes[k] = new JsonObject { ["oldValue"] = old?.DeepClone() }; data.Remove(k); }
        }
        if (area is "local" or "sync")
        {
            Directory.CreateDirectory(DataFolder(l.Record.Id));
            File.WriteAllText(Path.Combine(DataFolder(l.Record.Id), area + ".json"), data.ToJsonString());
        }
        if (changes.Count > 0) await Broadcast(l, new JsonObject { ["type"] = "storage", ["area"] = area, ["changes"] = changes }, toContent: true);
    }

    // Tabs ---------------------------------------------------------------------------

    Tab? TabOf(WebKitPage page) => Browser.Spaces.SelectMany(s => s.All).FirstOrDefault(t => t.Page == page);
    int IdOf(Tab t) => tabIds.TryGetValue(t, out var id) ? id : tabIds[t] = nextTab++;
    Tab? TabById(int id) => tabIds.FirstOrDefault(kv => kv.Value == id).Key;

    JsonObject? TabObject(Tab? t)
    {
        if (t == null) return null;
        var spaces = Browser.Spaces;
        var list = t.Space.All.ToList();
        return new JsonObject
        {
            ["id"] = IdOf(t), ["index"] = list.IndexOf(t), ["windowId"] = spaces.IndexOf(t.Space) + 1,
            ["active"] = t.Space.Selected == t, ["highlighted"] = t.Space.Selected == t, ["selected"] = t.Space.Selected == t,
            ["pinned"] = t.Pinned, ["audible"] = t.Audible, ["discarded"] = !t.Awake, ["autoDiscardable"] = true,
            ["mutedInfo"] = new JsonObject { ["muted"] = t.Muted }, ["incognito"] = t.Private,
            ["url"] = t.Url, ["title"] = t.Display, ["status"] = t.Loading ? "loading" : "complete",
            ["width"] = 1280, ["height"] = 800, ["groupId"] = -1,
        };
    }

    IEnumerable<Tab> Query(JsonObject q)
    {
        IEnumerable<Tab> tabs = Browser.Spaces.SelectMany(s => s.All);
        if (q["currentWindow"]?.GetValue<bool>() == true || q["lastFocusedWindow"]?.GetValue<bool>() == true || q["windowId"]?.GetValue<int>() == -2)
            tabs = tabs.Where(t => t.Space == Browser.Space);
        if (q["active"]?.GetValue<bool>() is bool active) tabs = tabs.Where(t => (t.Space.Selected == t) == active);
        if (q["pinned"]?.GetValue<bool>() is bool pinned) tabs = tabs.Where(t => t.Pinned == pinned);
        if (q["audible"]?.GetValue<bool>() is bool audible) tabs = tabs.Where(t => t.Audible == audible);
        if (q["muted"]?.GetValue<bool>() is bool muted) tabs = tabs.Where(t => t.Muted == muted);
        if (q["status"]?.GetValue<string>() is { } status) tabs = tabs.Where(t => (t.Loading ? "loading" : "complete") == status);
        if (q["url"] is { } url)
        {
            var patterns = url is JsonArray arr ? arr.Select(x => x!.GetValue<string>()).ToList() : new List<string> { url.GetValue<string>() };
            tabs = tabs.Where(t => patterns.Any(p => MatchPattern.Matches(p, t.Url)));
        }
        if (q["title"]?.GetValue<string>() is { } title)
            tabs = tabs.Where(t => System.Text.RegularExpressions.Regex.IsMatch(t.Display, MatchPattern.GlobToRegex(title), System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        return tabs.ToList();
    }

    static string Resolve(Loaded l, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out _) ? url : l.Base + url.TrimStart('/');

    /// The browser telling extensions about tabs: tabs.on…, webNavigation.on….
    public void Tabs(string what, Tab t)
    {
        if (!Enabled || loaded.Count == 0) return;
        var obj = TabObject(t);
        if (what == "removed") tabIds.Remove(t);
        foreach (var l in loaded.Values)
        {
            JsonObject evt = what switch
            {
                "created" => Event("tabs.onCreated", obj),
                "activated" => Event("tabs.onActivated", new JsonObject { ["tabId"] = obj!["id"]!.DeepClone(), ["windowId"] = obj["windowId"]!.DeepClone() }),
                "removed" => Event("tabs.onRemoved", obj!["id"]!.DeepClone(), new JsonObject { ["windowId"] = obj["windowId"]!.DeepClone(), ["isWindowClosing"] = false }),
                _ => Event("tabs.onUpdated", obj!["id"]!.DeepClone(), new JsonObject { ["status"] = obj["status"]!.DeepClone(), ["url"] = t.Url }, obj),
            };
            Dispatch(l, evt);
        }
    }

    /// A page committed or finished loading.
    public void Navigated(WebKitPage page, string stage)
    {
        // An extension's background page is up: it hears it was installed, or started.
        foreach (var l in loaded.Values)
            if (page == l.Background && stage == "completed")
            {
                var flag = Path.Combine(DataFolder(l.Record.Id), "installed");
                bool first = !File.Exists(flag);
                Directory.CreateDirectory(DataFolder(l.Record.Id));
                File.WriteAllText(flag, l.Manifest.VersionName);
                Dispatch(l, first ? Event("runtime.onInstalled", new JsonObject { ["reason"] = "install" }) : Event("runtime.onStartup"));
                return;
            }
        if (page.Owner == null || TabOf(page) is not { } t) return;
        if (stage == "committed") registered.Remove(page);
        var details = new JsonObject { ["tabId"] = IdOf(t), ["url"] = page.Url, ["frameId"] = 0, ["parentFrameId"] = -1, ["timeStamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() };
        foreach (var l in loaded.Values)
        {
            Dispatch(l, Event(stage == "committed" ? "webNavigation.onCommitted" : "webNavigation.onCompleted", details));
            if (stage == "completed") Dispatch(l, Event("webNavigation.onDOMContentLoaded", details));
        }
        Tabs("updated", t);
    }

    public void PageGone(WebKitPage page)
    {
        registered.Remove(page);
        foreach (var (id, port) in ports.ToList())
            if (port.A == page || port.B == page)
            {
                ports.Remove(id);
                var other = port.A == page ? port.B : port.A;
                var name = port.A == page ? port.BName : port.AName;
                if (other != null && loaded.TryGetValue(port.Ext, out var l))
                    _ = Deliver(l, other, name, new JsonObject { ["type"] = "port.close", ["portId"] = id });
            }
    }

    // Alarms, rules -------------------------------------------------------------------

    void Alarm(Loaded l, string name, JsonObject info)
    {
        if (l.Alarms.Remove(name, out var old)) old.Stop();
        double? period = info["periodInMinutes"]?.GetValue<double>();
        double now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        // when/delay for a new alarm; scheduledTime when it was restored.
        double delayMs = info["when"]?.GetValue<double>() is double when ? Math.Max(0, when - now)
            : info["scheduledTime"]?.GetValue<double>() is double at ? Math.Max(0, at - now)
            : (info["delayInMinutes"]?.GetValue<double>() ?? period ?? 0) * 60000;
        // Chrome's floor is 30 seconds; so is this one.
        delayMs = Math.Max(delayMs, 30000);
        var scheduled = now + delayMs;
        var record = new JsonObject { ["name"] = name, ["scheduledTime"] = scheduled };
        if (period != null) record["periodInMinutes"] = period;
        l.AlarmInfo[name] = record;
        SaveAlarms(l);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            Dispatch(l, Event("alarms.onAlarm", record.DeepClone()));
            if (period is double p)
            {
                timer.Interval = TimeSpan.FromMinutes(Math.Max(p, 0.5));
                record["scheduledTime"] = DateTimeOffset.Now.ToUnixTimeMilliseconds() + timer.Interval.TotalMilliseconds;
                SaveAlarms(l);
            }
            else { timer.Stop(); l.Alarms.Remove(name); l.AlarmInfo.Remove(name); SaveAlarms(l); }
        };
        l.Alarms[name] = timer;
        timer.Start();
    }

    /// Chrome alarms survive a restart; so do these.
    void SaveAlarms(Loaded l)
    {
        try
        {
            var folder = DataFolder(l.Record.Id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "alarms.json"),
                new JsonArray(l.AlarmInfo.Values.Select(v => (JsonNode)v.DeepClone()).ToArray()).ToJsonString());
        }
        catch { }
    }

    static IEnumerable<string> EnabledRulesets(Loaded l) =>
        l.Dnr["enabled"] is JsonArray chosen ? chosen.Select(x => x!.GetValue<string>()) : l.Manifest.Rulesets.Where(r => r.Enabled).Select(r => r.Id);

    /// Static rulesets, dynamic and session rules, as one WebKit rule list.
    async Task CompileRules(Loaded l)
    {
        Directory.CreateDirectory(DataFolder(l.Record.Id));
        File.WriteAllText(Path.Combine(DataFolder(l.Record.Id), "dnr.json"), l.Dnr.ToJsonString());
        var rules = new List<JsonNode?>();
        foreach (var id in EnabledRulesets(l))
        {
            var set = l.Manifest.Rulesets.FirstOrDefault(r => r.Id == id);
            var file = set == null ? null : Path.Combine(l.Manifest.Folder, set.Path);
            if (file != null && File.Exists(file) && JsonNode.Parse(File.ReadAllText(file)) is JsonArray arr) rules.AddRange(arr);
        }
        rules.AddRange((JsonArray)l.Dnr["dynamic"]!);
        rules.AddRange(l.SessionRules);
        if (rules.Count == 0) { engine.ExtensionFilters.Remove(l.Record.Id); return; }
        var converted = Dnr.Convert(rules);
        var filter = await engine.Compile("ext-" + l.Record.Id, converted.Json);
        if (filter == IntPtr.Zero) engine.ExtensionFilters.Remove(l.Record.Id);
        else engine.ExtensionFilters[l.Record.Id] = filter;
        foreach (var p in engine.Pages) engine.Filters(p);
    }
}
