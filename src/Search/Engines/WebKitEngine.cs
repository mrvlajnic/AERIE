using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Search.Core;

namespace Search;

/// WebKit, through WebKit's own Windows port (WinCairo) — the engine Search
/// uses on the Mac, built for Windows. Its files ship beside the app.
static class WebKit
{
    public static string Folder => AppContext.BaseDirectory;
    public static bool Available => File.Exists(Path.Combine(Folder, "WebKit2.dll"));
}

sealed class WebKitEngine : IEngine
{
    public string Name => "WebKit";
    public string? Version
    {
        get
        {
            try
            {
                var v = FileVersionInfo.GetVersionInfo(Path.Combine(WebKit.Folder, "WebKit2.dll"));
                return string.IsNullOrWhiteSpace(v.ProductVersion) ? "(Windows port)" : v.ProductVersion;
            }
            catch { return null; }
        }
    }

    public BrowserWindow Browser { get; private set; } = null!;
    public Func<IEnumerable<string>> DocumentScripts { get; set; } = () => Array.Empty<string>();
    public IExtensionHost Extensions => extensions;
    internal WebKitExtensions extensions = null!;
    internal readonly List<WebKitPage> Pages = new();
    internal IntPtr Context, Preferences;
    internal IntPtr Parking;            // a hidden window pages wait in before they have a place
    readonly Dictionary<string, IntPtr> stores = new();
    IntPtr privateStore, contentStore, shield;
    internal readonly Dictionary<string, IntPtr> ExtensionFilters = new();
    HwndSource? parkingSource;

    /// What pages tell sites they are. Safari's own words, so sites send
    /// what they send Safari rather than a fallback for an unknown browser.
    public const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15";

    public async Task Start(BrowserWindow browser)
    {
        Browser = browser;
        Trace.Log("webkit: start in " + WebKit.Folder);
        WK.SetDllDirectory(WebKit.Folder);
        var s = browser.Settings;

        // Memory first: no spare processes kept warm, no cache of dead ones.
        var cc = WK.WKContextConfigurationCreate();
        WK.WKContextConfigurationSetUsesWebProcessCache(cc, false);
        WK.WKContextConfigurationSetPrewarmsProcessesAutomatically(cc, false);
        if (s.SharedProcess) WK.WKContextConfigurationSetProcessSwapsOnNavigation(cc, false);
        Context = WK.WKContextCreateWithConfiguration(cc);
        Trace.Log("webkit: context made");
        WK.WKRelease(cc);
        // DocumentViewer keeps almost nothing in memory between pages.
        WK.WKContextSetCacheModel(Context, s.Lean ? 0u : 2u);

        Preferences = WK.WKPreferencesCreate();
        WK.WKPreferencesSetDeveloperExtrasEnabled(Preferences, true);
        WK.WKPreferencesSetFullScreenEnabled(Preferences, true);
        WK.WKPreferencesSetMediaSourceEnabled(Preferences, true);
        WK.WKPreferencesSetAllowsPictureInPictureMediaPlayback(Preferences, true);
        // The back-forward cache keeps whole pages alive to go back to: lean mode does without.
        WK.WKPreferencesSetPageCacheEnabled(Preferences, !s.Lean);
        WK.WKPreferencesSetHiddenPageDOMTimerThrottlingEnabled(Preferences, true);
        WK.WKPreferencesSetHiddenPageDOMTimerThrottlingAutoIncreases(Preferences, true);
        if (s.Lean)
        {
            // GPU compositing keeps a texture per layer, per page, in the GPU
            // process. Drawn in software instead, as Playwright's own Windows
            // shell can: less memory, a little less smooth.
            WK.WKPreferencesSetAcceleratedCompositingEnabled(Preferences, false);
            WK.WKPreferencesSetAccelerated2DCanvasEnabled(Preferences, false);
            WK.WKPreferencesSetAcceleratedDrawingEnabled(Preferences, false);
        }
        Trace.Log("webkit: lean " + s.Lean);

        parkingSource = new HwndSource(new HwndSourceParameters("Search parking") { Width = 1, Height = 1, WindowStyle = unchecked((int)0x80000000) /* WS_POPUP */ });
        Parking = parkingSource.Handle;

        var rules = Path.Combine(Store.Folder, "WebKit", "ContentRules");
        Directory.CreateDirectory(rules);
        var path = WK.Str(rules);
        contentStore = WK.WKUserContentExtensionStoreCreate(path);
        WK.WKRelease(path);

        extensions = new WebKitExtensions(this);
        Trace.Log("webkit: compiling the ad blocker");
        if (Environment.GetCommandLineArgs().Any(a => a.StartsWith("--bench=")))
        {
            // Does this build's content blocker compile anything at all?
            var probe = await Compile("probe", "[{\"trigger\":{\"url-filter\":\"example\"},\"action\":{\"type\":\"block\"}}]");
            Trace.Log("webkit: probe rule " + (probe != IntPtr.Zero ? "compiled" : "failed"));
            if (probe != IntPtr.Zero)
            {
                var each = System.Text.Json.Nodes.JsonNode.Parse(ContentRules.Shield(s.ShieldPaused))!.AsArray();
                for (int i = 0; i < each.Count; i++)
                    if (await Compile("probe" + i, "[" + each[i]!.ToJsonString() + "]") == IntPtr.Zero)
                        Trace.Log($"webkit: rule {i} won't compile: {each[i]!.ToJsonString()}");
            }
        }
        await ApplyShield(s.ShieldOn, s.ShieldPaused);
        Trace.Log("webkit: ad blocker " + (shield != IntPtr.Zero ? "ready" : "failed"));
        await extensions.Start();
        Trace.Log("webkit: started");
    }

    public IPage Create(PageOwner owner) => new WebKitPage(this, owner, IntPtr.Zero);

    internal unsafe IntPtr DataStore(PageOwner owner)
    {
        if (owner.Private)
        {
            if (privateStore == IntPtr.Zero) privateStore = WK.WKWebsiteDataStoreCreateNonPersistentDataStore();
            return privateStore;
        }
        if (stores.TryGetValue(owner.Profile, out var existing)) return existing;
        var root = Path.Combine(Store.Folder, "WebKit", owner.Profile);
        Directory.CreateDirectory(root);
        var c = WK.WKWebsiteDataStoreConfigurationCreate();
        void Set(delegate*<IntPtr, IntPtr, void> setter, string sub) { var p = WK.Str(Path.Combine(root, sub)); setter(c, p); WK.WKRelease(p); }
        Set(&WK.WKWebsiteDataStoreConfigurationSetNetworkCacheDirectory, "Cache");
        Set(&WK.WKWebsiteDataStoreConfigurationSetCacheStorageDirectory, "CacheStorage");
        Set(&WK.WKWebsiteDataStoreConfigurationSetIndexedDBDatabaseDirectory, "IndexedDB");
        Set(&WK.WKWebsiteDataStoreConfigurationSetLocalStorageDirectory, "LocalStorage");
        Set(&WK.WKWebsiteDataStoreConfigurationSetGeneralStorageDirectory, "Storage");
        Set(&WK.WKWebsiteDataStoreConfigurationSetMediaKeysStorageDirectory, "MediaKeys");
        Set(&WK.WKWebsiteDataStoreConfigurationSetResourceLoadStatisticsDirectory, "ResourceLoadStatistics");
        Set(&WK.WKWebsiteDataStoreConfigurationSetServiceWorkerRegistrationDirectory, "ServiceWorkers");
        Set(&WK.WKWebsiteDataStoreConfigurationSetCookieStorageFile, "cookies.db");
        var store = WK.WKWebsiteDataStoreCreateWithConfiguration(c);
        WK.WKRelease(c);
        stores[owner.Profile] = store;
        return store;
    }

    /// A page's configuration: its space's data, the shared preferences, and
    /// a user content controller of its own (scripts, rules, messages).
    internal IntPtr Configuration(PageOwner owner, WebKitPage page, IntPtr existing = default)
    {
        var config = existing != IntPtr.Zero ? existing : WK.WKPageConfigurationCreate();
        if (existing == IntPtr.Zero)
        {
            WK.WKPageConfigurationSetContext(config, Context);
            WK.WKPageConfigurationSetWebsiteDataStore(config, DataStore(owner));
            WK.WKPageConfigurationSetPreferences(config, Preferences);
            if (Browser.Settings.SharedProcess && !owner.Private)
            {
                // Least memory: every tab of a space in the one page process.
                var related = Pages.FirstOrDefault(p => p != page && p.Owner is { } o && o.Profile == owner.Profile && !o.Private && p.PageRef != IntPtr.Zero);
                if (related != null)
                {
                    Trace.Log("webkit: sharing the page process of " + related.Url);
                    WK.WKPageConfigurationSetRelatedPage(config, related.PageRef);
                }
            }
        }
        var ucc = WK.WKUserContentControllerCreate();
        WK.WKPageConfigurationSetUserContentController(config, ucc);
        page.Controller = ucc;
        Scripts(page);
        Filters(page);
        page.Listen();
        return config;
    }

    internal void Scripts(WebKitPage page)
    {
        WK.WKUserContentControllerRemoveAllUserScripts(page.Controller);
        var all = DocumentScripts().ToList();
        // This build may not compile content rules at all: then the ad
        // blocker is the page-level part only (empty ad slots hidden).
        if (shield == IntPtr.Zero && Browser.Settings.ShieldOn) all.Add(Shield.CosmeticScript(Browser.Settings.ShieldPaused));
        if (page.Owner != null && !page.Owner.Private) all.AddRange(extensions.ContentScripts());
        foreach (var source in all)
        {
            var s = WK.Str(source);
            var script = WK.WKUserScriptCreateWithSource(s, 0 /* document start */, false);
            WK.WKUserContentControllerAddUserScript(page.Controller, script);
            WK.WKRelease(script);
            WK.WKRelease(s);
        }
    }

    internal void Filters(WebKitPage page)
    {
        WK.WKUserContentControllerRemoveAllUserContentFilters(page.Controller);
        if (Browser.Settings.ShieldOn && shield != IntPtr.Zero) WK.WKUserContentControllerAddUserContentFilter(page.Controller, shield);
        foreach (var f in ExtensionFilters.Values) WK.WKUserContentControllerAddUserContentFilter(page.Controller, f);
    }

    public async Task ApplyShield(bool on, IReadOnlyCollection<string> paused)
    {
        if (on)
        {
            var compiled = await Compile("search-shield", ContentRules.Shield(paused));
            if (compiled != IntPtr.Zero) shield = compiled;
        }
        foreach (var p in Pages) { Filters(p); Scripts(p); }
    }

    public bool RulesAvailable => shield != IntPtr.Zero;

    /// WebKit's content rule compiler, as a task: the compiled list, or nothing.
    internal unsafe Task<IntPtr> Compile(string id, string json)
    {
        var done = new TaskCompletionSource<IntPtr>();
        var handle = GCHandle.Alloc(done);
        var i = WK.Str(id); var j = WK.Str(json);
        WK.WKUserContentExtensionStoreCompile(contentStore, i, j, GCHandle.ToIntPtr(handle), &Compiled);
        WK.WKRelease(i); WK.WKRelease(j);
        return done.Task;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Compiled(IntPtr filter, uint result, IntPtr context)
    {
        var handle = GCHandle.FromIntPtr(context);
        var done = (TaskCompletionSource<IntPtr>)handle.Target!;
        handle.Free();
        Trace.Log($"webkit: rules compiled, result {result}, list {(filter != IntPtr.Zero ? "made" : "none")}");
        // The list is kept for as long as the app runs.
        done.TrySetResult(result == 0 && filter != IntPtr.Zero ? WK.WKRetain(filter) : IntPtr.Zero);
    }

    public void Recolour() { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Removed(IntPtr context) { }

    unsafe void RemoveMemoryCaches()
    {
        foreach (var s in stores.Values) WK.WKWebsiteDataStoreRemoveMemoryCaches(s, IntPtr.Zero, &Removed);
    }

    public Task ClearData()
    {
        RemoveMemoryCaches();
        // Files in use can't go now: they go before the engine starts next time.
        File.WriteAllText(Path.Combine(Store.Folder, "WebKit", "clear-on-start"), "");
        return Task.CompletedTask;
    }

    /// Minimised, or a tab frozen: what the engine can give back now.
    internal void Trim()
    {
        Trace.Log("webkit: trim");
        WK.WKContextGarbageCollectJavaScriptObjects(Context);
        RemoveMemoryCaches();
    }

    public static void ClearIfAsked()
    {
        var flag = Path.Combine(Store.Folder, "WebKit", "clear-on-start");
        if (!File.Exists(flag)) return;
        foreach (var dir in Directory.GetDirectories(Path.Combine(Store.Folder, "WebKit")))
            if (Path.GetFileName(dir) != "ContentRules") try { Directory.Delete(dir, true); } catch { }
        File.Delete(flag);
    }
}
