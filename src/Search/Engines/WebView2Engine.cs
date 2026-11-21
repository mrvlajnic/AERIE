using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Search.Core;

namespace Search;

/// Microsoft Edge WebView2: the Chromium-based engine already on every
/// Windows 10 and 11 PC. One environment; one profile for signed-in spaces
/// and one for each fresh space.
sealed class WebView2Engine : IEngine
{
    public string Name => "WebView2";
    public CoreWebView2Environment Env { get; private set; } = null!;
    public BrowserWindow Browser { get; private set; } = null!;
    public Func<IEnumerable<string>> DocumentScripts { get; set; } = () => Array.Empty<string>();
    public IExtensionHost Extensions { get; }
    readonly Dictionary<string, CoreWebView2Profile> profiles = new();
    readonly HashSet<string> synced = new();
    internal readonly List<WebView2Page> Pages = new();

    public WebView2Engine() { Extensions = new WebView2Extensions(this); }

    public string? Version
    {
        get { try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); } catch { return null; } }
    }

    /// Chromium's switches for using less memory: same-site tabs share one
    /// page process; lean mode shrinks caches as on a low-memory device.
    public static string Arguments(bool lean) => "--process-per-site" + (lean ? " --enable-low-end-device-mode" : "");

    public async Task Start(BrowserWindow browser)
    {
        Browser = browser;
        var options = new CoreWebView2EnvironmentOptions
        {
            AreBrowserExtensionsEnabled = true,
            AdditionalBrowserArguments = Arguments(browser.Settings.Lean),
        };
        Env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Store.Folder, "Web"), options);
    }

    public IPage Create(PageOwner owner) => new WebView2Page(this, owner);

    public IEnumerable<CoreWebView2Profile> Profiles => profiles.Values;

    public void Know(CoreWebView2Profile profile, bool isPrivate)
    {
        profile.PreferredColorScheme = Scheme;
        if (isPrivate) return;
        profiles[profile.ProfileName] = profile;
        if (synced.Add(profile.ProfileName)) _ = ((WebView2Extensions)Extensions).Sync(profile);
    }

    static CoreWebView2PreferredColorScheme Scheme => Theme.Look switch
    {
        "dark" => CoreWebView2PreferredColorScheme.Dark,
        "light" => CoreWebView2PreferredColorScheme.Light,
        _ => CoreWebView2PreferredColorScheme.Auto,
    };

    public void Recolour()
    {
        foreach (var p in profiles.Values) p.PreferredColorScheme = Scheme;
        foreach (var p in Pages) p.Recolour();
    }

    public async Task ApplyShield(bool on, IReadOnlyCollection<string> paused)
    {
        foreach (var p in Pages.ToList()) await p.RefreshShield();
    }

    public async Task ClearData()
    {
        foreach (var p in profiles.Values) await p.ClearBrowsingDataAsync();
    }

    public void DeleteProfile(string profile)
    {
        if (!profiles.Remove(profile, out var p)) return;
        try { p.Delete(); }
        catch
        {
            // Older runtimes may not allow deleting a profile still in memory.
            try { Directory.Delete(Path.Combine(Store.Folder, "Web", "EBWebView", profile), true); } catch { }
        }
    }
}

sealed class WebView2Page : IPage
{
    readonly WebView2Engine engine;
    readonly PageOwner owner;
    readonly WebView2 view;
    readonly Task ready;
    string? pending;
    string? shieldScript;
    readonly List<string> scriptIds = new();
    bool disposed;
    int crashReloads;
    DateTime lastCrash;

    /// Browser shortcuts pressed inside a page go to the page's process. The
    /// WPF WebView2 control exposes no accelerator event, so a capture listener
    /// posts only the keys the window's own table already owns.
    const string KeyBridge = """
        (function () {
          if (window.__searchKeyBridge) return;
          window.__searchKeyBridge = true;
          var has = function (list, k) { return list.indexOf(k) >= 0; };
          window.addEventListener('keydown', function (e) {
            var k = (e.key || '').toLowerCase();
            var take = false;
            if (e.ctrlKey && !e.altKey && !e.shiftKey)
              take = has('t n l w k s r d h y , [ ] 1 2 3 4 5 6 7 8 9 tab pagedown pageup', k);
            else if (e.ctrlKey && e.shiftKey && !e.altKey)
              take = has('t n r p h u s tab [ ]', k);
            else if (e.ctrlKey && e.altKey && !e.shiftKey)
              take = has('arrowleft arrowright c i', k);
            else if (e.altKey && !e.ctrlKey && !e.shiftKey)
              take = has('arrowleft arrowright d home', k);
            else if (!e.ctrlKey && !e.altKey && !e.shiftKey)
              take = has('f5 f6 f11', k);
            if (!take) return;
            e.preventDefault();
            try {
              window.chrome.webview.postMessage({ kind: 'key', key: k, ctrl: e.ctrlKey, shift: e.shiftKey, alt: e.altKey });
            } catch (_) { }
          }, true);
        })();
        """;

    public WebView2Page(WebView2Engine engine, PageOwner owner, bool adopt = false)
    {
        this.engine = engine;
        this.owner = owner;
        var g = Theme.GroundColor;
        view = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(g.R, g.G, g.B), Visibility = Visibility.Hidden };
        engine.Pages.Add(this);
        ready = Init();
    }

    public FrameworkElement Element => view;
    public Task Ready => ready;
    CoreWebView2? Core => disposed ? null : view.CoreWebView2;

    public string Url { get; private set; } = "";
    public string Title { get; private set; } = "";
    public bool Loading { get; private set; }
    public bool Audible => Core?.IsDocumentPlayingAudio ?? false;
    public bool Muted { get => Core?.IsMuted ?? false; set { if (Core is { } c) c.IsMuted = value; } }
    public bool CanGoBack => Core?.CanGoBack ?? false;
    public bool CanGoForward => Core?.CanGoForward ?? false;

    public event Action? Changed;
    public event Action<ImageSource?>? IconChanged;
    public event Action<string, string, bool>? Finished;
    public event Action<string>? Message;
    public event Action<IPage, string, bool>? Opened;
    public event Action? CloseRequested;
    public event Action<bool>? FullScreen;

    async Task Init()
    {
        try { await InitCore(); }
        catch (Exception e)
        {
            // A failed controller used to leave a blank tab with no trace:
            // ready was discarded and the error never observed.
            Trace.Log("page init failed: " + e);
            engine.Browser.Toast("A page failed to start (" + e.GetType().Name + ")");
            Title = "Page failed to start";
            Changed?.Invoke();
        }
    }

    async Task InitCore()
    {
        var o = engine.Env.CreateCoreWebView2ControllerOptions();
        o.ProfileName = owner.Profile;
        o.IsInPrivateModeEnabled = owner.Private;
        await view.EnsureCoreWebView2Async(engine.Env, o);
        var core = view.CoreWebView2;
        var settings = engine.Browser.Settings;
        engine.Know(core.Profile, owner.Private);
        core.Settings.IsPasswordAutosaveEnabled = settings.SavePasswords && !owner.Private;
        core.Settings.IsGeneralAutofillEnabled = !owner.Private;
        core.Settings.IsStatusBarEnabled = true;
        core.Settings.AreDevToolsEnabled = settings.DevTools || System.Diagnostics.Debugger.IsAttached;

        // The ad blocker: only requests to the listed hosts reach the handler.
        foreach (var d in Shield.Unwanted)
        {
            core.AddWebResourceRequestedFilter($"*://*.{d}/*", CoreWebView2WebResourceContext.All);
            core.AddWebResourceRequestedFilter($"*://{d}/*", CoreWebView2WebResourceContext.All);
        }
        core.WebResourceRequested += (_, e) =>
        {
            var b = engine.Browser;
            if (!b.Settings.ShieldOn || !Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var request)) return;
            Uri.TryCreate(Url, UriKind.Absolute, out var page);
            if (page != null && b.Paused(page.Host)) return;
            if (Shield.ShouldBlock(request, page))
                e.Response = engine.Env.CreateWebResourceResponse(null, 403, "Blocked", "");
        };
        await RefreshShield();
        await AddScripts();
        await core.AddScriptToExecuteOnDocumentCreatedAsync(KeyBridge);

        core.SourceChanged += (_, _) => { Url = core.Source; Changed?.Invoke(); };
        core.DocumentTitleChanged += (_, _) => { Title = core.DocumentTitle; Changed?.Invoke(); };
        core.NavigationStarting += (_, _) => { Loading = true; Changed?.Invoke(); };
        core.NavigationCompleted += (_, e) =>
        {
            Loading = false;
            if (e.IsSuccess) crashReloads = 0;
            Changed?.Invoke();
            Finished?.Invoke(core.Source, core.DocumentTitle, e.IsSuccess);
        };
        core.FaviconChanged += async (_, _) => IconChanged?.Invoke(await IconOf(core));
        core.NewWindowRequested += OnNewWindow;
        core.WindowCloseRequested += (_, _) => view.Dispatcher.BeginInvoke(() => CloseRequested?.Invoke());
        core.WebMessageReceived += (_, e) => Message?.Invoke(e.WebMessageAsJson);
        core.ContainsFullScreenElementChanged += (_, _) => FullScreen?.Invoke(core.ContainsFullScreenElement);
        core.IsDocumentPlayingAudioChanged += (_, _) => Changed?.Invoke();
        core.IsMutedChanged += (_, _) => Changed?.Invoke();
        core.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive))
                return;
            // Two chances a minute, then say what happened instead of looping.
            var now = DateTime.UtcNow;
            if ((now - lastCrash).TotalSeconds > 60) crashReloads = 0;
            lastCrash = now;
            if (crashReloads++ < 2)
            {
                try { core.Reload(); } catch { }
                return;
            }
            ShowCrash(core, e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                ? "This page stopped responding."
                : "This page crashed.");
        };

        // Downloads: choose where it goes, and say when it lands.
        core.DownloadStarting += (_, e) =>
        {
            var suggested = Path.GetFileName(e.ResultFilePath);
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save download",
                FileName = string.IsNullOrWhiteSpace(suggested) ? "download" : suggested,
            };
            if (dialog.ShowDialog(engine.Browser) != true) { e.Cancel = true; return; }
            e.ResultFilePath = dialog.FileName;
            e.Handled = true;
            var operation = e.DownloadOperation;
            operation.StateChanged += (_, _) =>
            {
                if (operation.State == CoreWebView2DownloadState.Completed)
                    engine.Browser.Toast("Downloaded " + Path.GetFileName(operation.ResultFilePath));
                else if (operation.State == CoreWebView2DownloadState.Interrupted)
                    engine.Browser.Toast("Download failed");
            };
        };

        core.PermissionRequested += (_, e) => OnPermission(e);
        if (pending != null) { var u = pending; pending = null; Load(core, u); }
    }

    public void Recolour()
    {
        var g = Theme.GroundColor;
        view.DefaultBackgroundColor = System.Drawing.Color.FromArgb(g.R, g.G, g.B);
    }

    internal async Task RefreshShield()
    {
        if (Core is not { } core) return;
        var b = engine.Browser;
        if (shieldScript != null) core.RemoveScriptToExecuteOnDocumentCreated(shieldScript);
        shieldScript = b.Settings.ShieldOn ? await core.AddScriptToExecuteOnDocumentCreatedAsync(Shield.CosmeticScript(b.Settings.ShieldPaused)) : null;
    }

    async Task AddScripts()
    {
        if (Core is not { } core) return;
        foreach (var id in scriptIds) core.RemoveScriptToExecuteOnDocumentCreated(id);
        scriptIds.Clear();
        foreach (var s in engine.DocumentScripts()) scriptIds.Add(await core.AddScriptToExecuteOnDocumentCreatedAsync(s));
    }

    public void RefreshScripts() => _ = AddScripts();

    async void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // The new tab keeps its tie to the page that opened it: sign-in windows need it.
        var deferral = e.GetDeferral();
        e.Handled = true;
        bool background = Native.CtrlHeld;
        var child = new WebView2Page(engine, owner with { Tab = owner.Tab });
        Opened?.Invoke(child, e.Uri, background);
        try
        {
            await child.Ready;
            if (child.Core is { } c) e.NewWindow = c;
        }
        catch { }
        finally { deferral.Complete(); }
    }

    static async Task<BitmapImage?> IconOf(CoreWebView2 core)
    {
        try
        {
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream == null || stream.Length == 0) return null;
            var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 32;
            image.StreamSource = copy;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    /// Camera, microphone, location, notifications: remembered per site, asked
    /// once, never granted silently. Private tabs ask every time.
    void OnPermission(CoreWebView2PermissionRequestedEventArgs e)
    {
        var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ? Hidden.Key(uri.Host) : "";
        var kind = e.PermissionKind.ToString();
        var settings = engine.Browser.Settings;
        if (!owner.Private && host.Length > 0
            && settings.SitePermissions.TryGetValue(host, out var saved)
            && saved.TryGetValue(kind, out var remembered))
        {
            e.State = remembered ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            e.SavesInProfile = false;
            return;
        }

        var answer = MessageBox.Show(
            engine.Browser,
            $"{Address.WithoutWww(host)} wants to use {Friendly(kind)}.\n\nAllow?",
            "Search", MessageBoxButton.YesNo, MessageBoxImage.Question);
        bool allow = answer == MessageBoxResult.Yes;
        e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        if (!owner.Private && host.Length > 0)
        {
            if (!settings.SitePermissions.TryGetValue(host, out var map))
                settings.SitePermissions[host] = map = new();
            map[kind] = allow;
            engine.Browser.SaveSettings();
        }
    }

    static string Friendly(string kind) => kind switch
    {
        "Camera" => "your camera",
        "Microphone" => "your microphone",
        "Geolocation" => "your location",
        "Notifications" => "send notifications",
        "ClipboardRead" => "read your clipboard",
        "MultipleAutomaticDownloads" => "download several files",
        _ => "this permission",
    };

    void ShowCrash(CoreWebView2 core, string why)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Page crashed</title>" +
            "<style>body{font:15px 'Segoe UI',system-ui;margin:16vh auto;max-width:32rem;color:#222;text-align:center}" +
            "p{color:#666;line-height:1.5}</style>" +
            "<h1>This page crashed</h1><p>" + System.Net.WebUtility.HtmlEncode(why) + "</p>" +
            "<p>Press Ctrl+R to try again, or close the tab.</p></body>";
        try { core.NavigateToString(html); } catch { }
    }

    void Load(CoreWebView2 core, string url)
    {
        Url = url;
        try { core.Navigate(url); }
        catch (ArgumentException) { core.Navigate(Engine.Url(url, engine.Browser.SearchTemplate)!.AbsoluteUri); }
    }

    public void Navigate(string url)
    {
        if (Core is { } core) Load(core, url);
        else { pending = url; Url = url; }
    }

    public void Back() { if (Core is { CanGoBack: true } c) c.GoBack(); }
    public void Forward() { if (Core is { CanGoForward: true } c) c.GoForward(); }
    public void Reload() { try { Core?.Reload(); } catch { } }

    public async Task<string?> Eval(string script)
    {
        if (Core is not { } core) return null;
        try { return await core.ExecuteScriptAsync(script); } catch { return null; }
    }

    public async Task<string?> EvalWithGesture(string script)
    {
        if (Core is not { } core) return null;
        try
        {
            var call = JsonSerializer.Serialize(new { expression = script, userGesture = true, awaitPromise = true, returnByValue = true });
            var result = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", call);
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.GetRawText() : null;
        }
        catch { return null; }
    }

    public void SetShown(bool shown)
    {
        if (Core is { } core)
        {
            core.MemoryUsageTargetLevel = shown ? CoreWebView2MemoryUsageTargetLevel.Normal : CoreWebView2MemoryUsageTargetLevel.Low;
            // Resume before the controller becomes visible, as WebView2 requires.
            if (shown && core.IsSuspended) core.Resume();
        }
        view.Visibility = shown ? Visibility.Visible : Visibility.Hidden;
    }

    public async Task Freeze()
    {
        if (Core is not { IsSuspended: false } core) return;
        core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
        try { await core.TrySuspendAsync(); } catch { }
    }

    public void OpenDevTools() => Core?.OpenDevToolsWindow();
    public void Focus() => view.Focus();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        engine.Pages.Remove(this);
        view.Dispose();
    }
}

/// Extensions on WebView2: the engine's own Chrome extension support.
sealed class WebView2Extensions : IExtensionHost
{
    readonly WebView2Engine engine;
    readonly Search.Extensions installed = Store.Load<Search.Extensions>("extensions.json");
    static readonly HttpClient http = new();

    public WebView2Extensions(WebView2Engine engine) { this.engine = engine; }

    public string About => "Chrome extensions run in the engine's own extension support. Paste a Chrome Web Store link, or load a folder you are building.";

    public IReadOnlyList<ExtensionInfo> All => installed.Items
        .Select(e => new ExtensionInfo(e.StoreId ?? e.Name, e.Name, e.Folder, e.StoreId, Crx.Popup(e.Folder) != null, true)).ToList();

    internal async Task Sync(CoreWebView2Profile profile)
    {
        try
        {
            var have = await profile.GetBrowserExtensionsAsync();
            foreach (var ext in installed.Items.ToList())
                if (Directory.Exists(ext.Folder) && !have.Any(h => h.Name == ext.Name))
                    try { await profile.AddBrowserExtensionAsync(ext.Folder); } catch { }
        }
        catch { }
    }

    public async Task<string> InstallFromStore(string link)
    {
        var id = Crx.IdFrom(link) ?? throw new InvalidOperationException("That isn't a Chrome Web Store link.");
        var version = engine.Version?.Split(' ')[0] ?? "130.0.0.0";
        var bytes = await http.GetByteArrayAsync(Crx.DownloadUrl(id, version));
        var folder = Path.Combine(Store.Folder, "Extensions", id);
        // Verify the CRX3 signature and id before anything is written.
        await Task.Run(() => Crx.Install(bytes, folder, id));
        return await AddFolder(folder, id);
    }

    public Task<string> AddFolder(string folder) => AddFolder(folder, null);

    async Task<string> AddFolder(string folder, string? storeId)
    {
        string? name = null;
        foreach (var p in engine.Profiles)
        {
            var ext = await p.AddBrowserExtensionAsync(folder);
            name ??= ext.Name;
        }
        if (name is null) throw new InvalidOperationException("Open a page first, then add the extension.");
        installed.Items.RemoveAll(e => e.Folder == folder || e.Name == name);
        installed.Items.Add(new InstalledExtension { Name = name, Folder = folder, StoreId = storeId });
        Store.Save("extensions.json", installed);
        return name;
    }

    public async Task Remove(ExtensionInfo info)
    {
        foreach (var p in engine.Profiles)
            foreach (var e in await p.GetBrowserExtensionsAsync())
                if (e.Name == info.Name) await e.RemoveAsync();
        installed.Items.RemoveAll(e => e.Name == info.Name);
        Store.Save("extensions.json", installed);
        var root = Path.Combine(Store.Folder, "Extensions");
        if (info.Folder.StartsWith(root)) try { Directory.Delete(info.Folder, true); } catch { }
    }

    public async Task Enable(ExtensionInfo info, bool on)
    {
        foreach (var p in engine.Profiles)
            foreach (var e in await p.GetBrowserExtensionsAsync())
                if (e.Name == info.Name) await e.EnableAsync(on);
    }

    /// The popup in a small window of its own, signed in as the space you are in.
    public async void OpenPopup(BrowserWindow browser, ExtensionInfo ext)
    {
        var profile = engine.Profiles.FirstOrDefault(p => p.ProfileName == browser.Space.Profile) ?? engine.Profiles.FirstOrDefault();
        if (profile == null) { browser.Toast("Open a page first"); return; }
        var found = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(e => e.Name == ext.Name);
        if (found == null) { browser.Toast(ext.Name + " isn't loaded in this space"); return; }
        var popup = Crx.Popup(ext.Folder);
        if (popup == null)
        {
            var options = Crx.OptionsPage(ext.Folder);
            if (options != null) browser.Open($"chrome-extension://{found.Id}/{options}");
            else browser.Toast(ext.Name + " has nothing to show");
            return;
        }
        var w = PopupWindow.Make(browser, ext.Name);
        var view = new WebView2();
        w.Content = view;
        w.Closed += (_, _) => view.Dispose();
        w.Show();
        var o = engine.Env.CreateCoreWebView2ControllerOptions();
        o.ProfileName = profile.ProfileName;
        await view.EnsureCoreWebView2Async(engine.Env, o);
        view.CoreWebView2.DocumentTitleChanged += (_, _) => { if (view.CoreWebView2.DocumentTitle.Length > 0) w.Title = view.CoreWebView2.DocumentTitle; };
        view.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; browser.Open(e.Uri); };
        view.CoreWebView2.WindowCloseRequested += (_, _) => w.Close();
        view.CoreWebView2.Navigate($"chrome-extension://{found.Id}/{popup}");
    }
}

/// The small window an extension's popup opens in.
static class PopupWindow
{
    public static Window Make(BrowserWindow browser, string title)
    {
        var w = new Window
        {
            Owner = browser, Title = title, Width = 400, Height = 560, ShowInTaskbar = false,
            WindowStyle = WindowStyle.ToolWindow, Background = Theme.Ground,
        };
        var at = browser.Anchor(400);
        w.Left = at.X + 160; w.Top = at.Y - 30;
        w.SourceInitialized += (_, _) => Native.PaintFrame(w);
        w.Deactivated += (_, _) => { try { w.Close(); } catch { } };
        return w;
    }
}
