using System.Collections.Concurrent;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AERIE.Core;

namespace AERIE;

/// One WebKit page in the window: WebKit's own view (a child window), hosted
/// in WPF. Everything WebKit tells it comes through the C callbacks below.
sealed class WebKitPage : IPage
{
    readonly WebKitEngine engine;
    public PageOwner? Owner { get; }
    /// An extension's own page (background, popup, options): its id.
    public string? ExtensionId { get; init; }
    readonly Host host;
    internal IntPtr Controller;
    IntPtr config, view;
    public IntPtr PageRef { get; private set; }
    readonly GCHandle self;
    WK.Client? stateClient, navigationClient, uiClient;
    string? pending;
    bool disposed, shown, loading;
    string lastIconFor = "";

    public WebKitPage(WebKitEngine engine, PageOwner? owner, IntPtr adoptConfiguration, string? extensionId = null)
    {
        this.engine = engine;
        Owner = owner;
        ExtensionId = extensionId;
        self = GCHandle.Alloc(this);
        host = new Host(this) { Visibility = Visibility.Hidden };
        engine.Pages.Add(this);
        if (adoptConfiguration != IntPtr.Zero)
        {
            // A page another page opened: WebKit needs it to exist right now.
            config = engine.Configuration(owner!, this, WK.WKRetain(adoptConfiguration));
            MakeView(engine.Parking);
        }
        else config = engine.Configuration(owner ?? new PageOwner("Default", false, null!), this);
    }

    public FrameworkElement Element => host;
    public string Url { get; private set; } = "";
    public string Title { get; private set; } = "";
    public bool Loading => loading;
    public bool Audible => PageRef != IntPtr.Zero && WK.WKPageIsPlayingAudio(PageRef);
    bool muted;
    public bool Muted
    {
        get => muted;
        set { muted = value; if (PageRef != IntPtr.Zero) WK.WKPageSetMuted(PageRef, value ? 1u : 0u); Changed?.Invoke(); }
    }
    public bool CanGoBack => PageRef != IntPtr.Zero && WK.WKPageCanGoBack(PageRef);
    public bool CanGoForward => PageRef != IntPtr.Zero && WK.WKPageCanGoForward(PageRef);

    public event Action? Changed;
    public event Action<ImageSource?>? IconChanged;
    public event Action<string, string, bool>? Finished;
    public event Action<string>? Message;
    public event Action<IPage, string, bool>? Opened;
    public event Action? CloseRequested;
    public event Action<bool>? FullScreen { add { } remove { } }

    IntPtr Info => GCHandle.ToIntPtr(self);
    int crashReloads;
    DateTime lastCrash;
    static int uiThread;

    /// Every callback from WebKit runs through here: an exception thrown
    /// back into native code would end the process, so it is written down
    /// instead, along with any callback that arrives off the UI thread.
    static void Safe(string where, Action act)
    {
        var app = Application.Current;
        if (app == null) return;
        if (uiThread == 0) uiThread = app.Dispatcher.Thread.ManagedThreadId;
        if (Environment.CurrentManagedThreadId != uiThread)
        {
            // WebKit may call back on its own threads. Touching WPF or the tab
            // model there is a data race, so hop to the dispatcher and return.
            Trace.Log("callback off the UI thread, marshalling: " + where);
            app.Dispatcher.BeginInvoke(() => Safe(where, act));
            return;
        }
        Trace.Log("cb " + where);
        try { act(); }
        catch (Exception e)
        {
            Trace.Log($"callback {where} threw: {e.GetType().Name}: {e.Message}");
            try { File.AppendAllText(Path.Combine(Store.Folder, "errors.log"), $"{DateTime.Now:u} {where}: {e}\n\n"); } catch { }
        }
    }
    static WebKitPage From(IntPtr info) => (WebKitPage)GCHandle.FromIntPtr(info).Target!;

    /// The view, made in (or moved to) the window it belongs to.
    internal IntPtr MakeView(IntPtr parent)
    {
        if (view != IntPtr.Zero)
        {
            var hwnd = WK.WKViewGetWindow(view);
            WK.SetParent(hwnd, parent);
            WK.WKViewSetParentWindow(view, parent);
            WK.WKViewWindowAncestryDidChange(view);
            return hwnd;
        }
        Trace.Log("page: making view");
        view = WK.WKViewCreate(default, config, parent);
        PageRef = WK.WKViewGetPage(view);
        Trace.Log("page: view made, setting clients");
        Clients();
        Trace.Log("page: clients set");
        var ua = WK.Str(WebKitEngine.UserAgent);
        WK.WKPageSetCustomUserAgent(PageRef, ua);
        WK.WKRelease(ua);
        WK.WKViewSetIsInWindow(view, shown);
        if (pending != null) { var u = pending; pending = null; Navigate(u); }
        return WK.WKViewGetWindow(view);
    }

    internal void Scaled(double dpi)
    {
        // WebKit's Windows port doesn't follow the display's scale on its own.
        if (PageRef != IntPtr.Zero) WK.WKPageSetPageZoomFactor(PageRef, dpi);
    }

    unsafe void Clients()
    {
        stateClient = new WK.Client(WK.StateV0.Version, WK.StateV0.Count, Info);
        stateClient.Set(WK.StateV0.Title, (delegate* unmanaged[Cdecl]<IntPtr, void>)&StateChanged);
        stateClient.Set(WK.StateV0.ActiveURL, (delegate* unmanaged[Cdecl]<IntPtr, void>)&StateChanged);
        stateClient.Set(WK.StateV0.CanGoBack, (delegate* unmanaged[Cdecl]<IntPtr, void>)&StateChanged);
        stateClient.Set(WK.StateV0.CanGoForward, (delegate* unmanaged[Cdecl]<IntPtr, void>)&StateChanged);
        WK.WKPageSetPageStateClient(PageRef, stateClient.Pointer);

        navigationClient = new WK.Client(WK.NavigationV3.Version, WK.NavigationV3.Count, Info);
        navigationClient.Set(WK.NavigationV3.DidStart, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidStart);
        navigationClient.Set(WK.NavigationV3.DidCommit, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidCommit);
        navigationClient.Set(WK.NavigationV3.DidFinish, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFinish);
        navigationClient.Set(WK.NavigationV3.DidFail, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFail);
        navigationClient.Set(WK.NavigationV3.DidFailProvisional, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&DidFail);
        navigationClient.Set(WK.NavigationV3.WebProcessDidCrash, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&Crashed);
        WK.WKPageSetPageNavigationClient(PageRef, navigationClient.Pointer);

        uiClient = new WK.Client(WK.UIV19.Version, WK.UIV19.Count, Info);
        uiClient.Set(WK.UIV19.CreateNewPage, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&CreateNewPage);
        uiClient.Set(WK.UIV19.Close, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&CloseWindow);
        uiClient.Set(WK.UIV19.DidNotHandleKeyEvent, (delegate* unmanaged[Cdecl]<IntPtr, WK.MSG*, IntPtr, void>)&KeyNotHandled);
        uiClient.Set(WK.UIV19.Alert, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&Alert);
        uiClient.Set(WK.UIV19.Confirm, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&Confirm);
        uiClient.Set(WK.UIV19.Prompt, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&PromptFor);
        uiClient.Set(WK.UIV19.RunOpenPanel, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr, void>)&OpenPanel);
        uiClient.Set(WK.UIV19.IsPlayingAudioDidChange, (delegate* unmanaged[Cdecl]<IntPtr, IntPtr, void>)&AudioChanged);
        WK.WKPageSetPageUIClient(PageRef, uiClient.Pointer);
    }

    /// Messages from the page: "search" for the browser's own scripts
    /// (hide elements), "searchExt" for extensions.
    internal unsafe void Listen()
    {
        var a = WK.Str("search");
        WK.WKUserContentControllerAddScriptMessageHandler(Controller, a, &OnMessage, Info);
        WK.WKRelease(a);
        // No extension bridge on WebKit: its page-world runtime is disabled
        // (see WebKitExtensions.Enabled), so a page has nothing to talk to.
        if (engine.Extensions.Enabled)
        {
            var b = WK.Str("searchExt");
            WK.WKUserContentControllerAddScriptMessageHandler(Controller, b, &OnExtensionMessage, Info);
            WK.WKRelease(b);
        }
    }

    // Callbacks -----------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void StateChanged(IntPtr info) => Safe("state", () => From(info).Refresh());

    void Refresh()
    {
        if (PageRef == IntPtr.Zero) return;
        var url = WK.TakeUrl(WK.WKPageCopyActiveURL(PageRef));
        if (url.Length > 0) Url = url;
        Title = WK.Take(WK.WKPageCopyTitle(PageRef));
        Changed?.Invoke();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void DidStart(IntPtr page, IntPtr navigation, IntPtr userData, IntPtr info)
    {
        Safe("DidStart", () =>
        {
            var p = From(info);
            p.loading = true;
            p.Refresh();
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void DidCommit(IntPtr page, IntPtr navigation, IntPtr userData, IntPtr info)
    {
        Safe("DidCommit", () =>
        {
            var p = From(info);
            p.Refresh();
            p.engine.extensions.Navigated(p, "committed");
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void DidFinish(IntPtr page, IntPtr navigation, IntPtr userData, IntPtr info)
    {
        Safe("DidFinish", () =>
        {
            var p = From(info);
            p.loading = false;
            p.crashReloads = 0;
            p.Refresh();
            Trace.Log("page: finished " + p.Url);
            p.Finished?.Invoke(p.Url, p.Title, true);
            p.engine.extensions.Navigated(p, "completed");
            _ = p.FetchIcon();
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void DidFail(IntPtr page, IntPtr navigation, IntPtr error, IntPtr userData, IntPtr info)
    {
        Safe("DidFail", () =>
        {
            var p = From(info);
            p.loading = false;
            p.Refresh();
            p.Finished?.Invoke(p.Url, p.Title, false);
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Crashed(IntPtr page, IntPtr info)
    {
        Safe("Crashed", () =>
        {
            var p = From(info);
            p.loading = false;
            var now = DateTime.UtcNow;
            if ((now - p.lastCrash).TotalSeconds > 60) p.crashReloads = 0;
            p.lastCrash = now;
            if (p.crashReloads++ < 2)
                Application.Current.Dispatcher.BeginInvoke(() => { if (!p.disposed) WK.WKPageReload(p.PageRef); });
            else
                Application.Current.Dispatcher.BeginInvoke(() => { if (!p.disposed) p.ShowCrash("This page crashed."); });
        });
    }

    /// What a page that keeps crashing gets instead of another silent reload.
    internal void ShowCrash(string why)
    {
        var html = "<!doctype html><meta charset=\"utf-8\"><title>Page crashed</title>" +
            "<body style=\"font:15px 'Segoe UI',system-ui;text-align:center;margin-top:16vh;color:#222\">" +
            "<h1>This page crashed</h1><p style=\"color:#666\">" + System.Net.WebUtility.HtmlEncode(why) + "</p>" +
            "<p style=\"color:#666\">Press Ctrl+R to try again, or close the tab.</p></body>";
        Navigate("data:text/html;charset=utf-8," + Uri.EscapeDataString(html));
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static IntPtr CreateNewPage(IntPtr page, IntPtr configuration, IntPtr action, IntPtr features, IntPtr info)
    {
        IntPtr made = IntPtr.Zero;
        Safe("CreateNewPage", () =>
        {
            var p = From(info);
            if (p.Owner == null) return;
            var child = new WebKitPage(p.engine, p.Owner, configuration);
            p.Opened?.Invoke(child, "", Native.CtrlHeld);
            // The caller takes a reference, as the API says.
            made = WK.WKRetain(child.PageRef);
        });
        return made;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void CloseWindow(IntPtr page, IntPtr info)
    {
        Safe("CloseWindow", () =>
        {
            var p = From(info);
            Application.Current.Dispatcher.BeginInvoke(() => p.CloseRequested?.Invoke());
        });
    }

    /// Keys the page didn't use go to the window, as if typed there: Ctrl+T,
    /// Ctrl+L, Ctrl+W and the rest of the shortcuts.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static unsafe void KeyNotHandled(IntPtr page, WK.MSG* msg, IntPtr info)
    {
        try
        {
            var p = From(info);
            if (msg == null) return;
            var main = new WindowInteropHelper(p.engine.Browser).Handle;
            WK.PostMessage(main, msg->message, msg->wParam, msg->lParam);
        }
        catch (Exception e) { Trace.Log("callback KeyNotHandled threw: " + e.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Alert(IntPtr page, IntPtr text, IntPtr frame, IntPtr origin, IntPtr listener, IntPtr info)
    {
        Safe("Alert", () =>
        {
            var p = From(info);
            MessageBox.Show(p.engine.Browser, WK.Read(text), Address.WithoutWww(HostOf(p.Url)), MessageBoxButton.OK);
            WK.WKPageRunJavaScriptAlertResultListenerCall(listener);
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Confirm(IntPtr page, IntPtr text, IntPtr frame, IntPtr origin, IntPtr listener, IntPtr info)
    {
        Safe("Confirm", () =>
        {
            var p = From(info);
            var ok = MessageBox.Show(p.engine.Browser, WK.Read(text), Address.WithoutWww(HostOf(p.Url)), MessageBoxButton.OKCancel) == MessageBoxResult.OK;
            WK.WKPageRunJavaScriptConfirmResultListenerCall(listener, ok);
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void PromptFor(IntPtr page, IntPtr text, IntPtr defaultValue, IntPtr frame, IntPtr origin, IntPtr listener, IntPtr info)
    {
        Safe("PromptFor", () =>
        {
            var p = From(info);
            var answer = Prompt.Ask(p.engine.Browser, WK.Read(text), WK.Read(defaultValue));
            var s = answer == null ? IntPtr.Zero : WK.Str(answer);
            WK.WKPageRunJavaScriptPromptResultListenerCall(listener, s);
            if (s != IntPtr.Zero) WK.WKRelease(s);
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static unsafe void OpenPanel(IntPtr page, IntPtr frame, IntPtr parameters, IntPtr listener, IntPtr info)
    {
        try
        {
            var p = From(info);
            var pick = new Microsoft.Win32.OpenFileDialog { Multiselect = WK.WKOpenPanelParametersGetAllowsMultipleFiles(parameters) };
            if (pick.ShowDialog(p.engine.Browser) != true) { WK.WKOpenPanelResultListenerCancel(listener); return; }
            var urls = pick.FileNames.Select(f => WK.Url(new Uri(f).AbsoluteUri)).ToArray();
            fixed (IntPtr* u = urls)
            {
                var array = WK.WKArrayCreate(u, (nuint)urls.Length);
                WK.WKOpenPanelResultListenerChooseFiles(listener, array, IntPtr.Zero);
                WK.WKRelease(array);
            }
            foreach (var u in urls) WK.WKRelease(u);
        }
        catch (Exception e) { Trace.Log("callback OpenPanel threw: " + e.Message); }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void AudioChanged(IntPtr page, IntPtr info) => Safe("audio", () => From(info).Changed?.Invoke());

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnMessage(IntPtr message, IntPtr reply, IntPtr info)
    {
        Safe("OnMessage", () =>
        {
            var p = From(info);
            var body = WK.Text(WK.WKScriptMessageGetBody(message));
            if (reply != IntPtr.Zero) WK.WKCompletionListenerComplete(reply, IntPtr.Zero);
            if (body != null) p.Message?.Invoke(body);
        });
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void OnExtensionMessage(IntPtr message, IntPtr reply, IntPtr info)
    {
        Safe("OnExtensionMessage", () =>
        {
            var p = From(info);
            var body = WK.Text(WK.WKScriptMessageGetBody(message));
            var frame = WK.WKScriptMessageGetFrameInfo(message);
            bool main = frame == IntPtr.Zero || WK.WKFrameInfoGetIsMainFrame(frame);
            string origin = "";
            if (frame != IntPtr.Zero)
            {
                var o = WK.WKFrameInfoCopySecurityOrigin(frame);
                if (o != IntPtr.Zero) { origin = WK.Take(WK.WKSecurityOriginCopyToString(o)); WK.WKRelease(o); }
            }
            // The answer may take a while (a message to the background and back):
            // the listener is kept until then.
            if (reply != IntPtr.Zero) WK.WKRetain(reply);
            p.engine.extensions.Receive(p, body ?? "", origin, main, json =>
            {
                if (reply == IntPtr.Zero) return;
                var s = WK.Str(json);
                WK.WKCompletionListenerComplete(reply, s);
                WK.WKRelease(s);
                WK.WKRelease(reply);
            });
        });
    }

    static string HostOf(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    // Doing things ---------------------------------------------------------------

    public void Navigate(string url)
    {
        Url = url;
        if (PageRef == IntPtr.Zero) { pending = url; return; }
        Trace.Log("page: load " + url);
        var u = WK.Url(url);
        WK.WKPageLoadURL(PageRef, u);
        WK.WKRelease(u);
    }

    public void Back() { if (CanGoBack) WK.WKPageGoBack(PageRef); }
    public void Forward() { if (CanGoForward) WK.WKPageGoForward(PageRef); }
    public void Reload() { if (PageRef != IntPtr.Zero) WK.WKPageReload(PageRef); }

    /// Runs a script as the body of an async function: the result, JSON-encoded.
    public unsafe Task<string?> Eval(string script)
    {
        if (PageRef == IntPtr.Zero || disposed) return Task.FromResult<string?>(null);
        var expression = script.Trim().TrimEnd(';');
        var body = "return JSON.stringify(await (" + expression + "\n));";
        var done = new TaskCompletionSource<string?>();
        long id = Interlocked.Increment(ref nextEval);
        evals[id] = done;
        var s = WK.Str(body);
        var args = WK.WKDictionaryCreate(null, null, 0);
        Trace.Log("page: eval in " + Url);
        WK.WKPageCallAsyncJavaScript(PageRef, s, args, IntPtr.Zero, (IntPtr)id, &Evaluated);
        WK.WKRelease(s);
        WK.WKRelease(args);
        return done.Task;
    }

    static long nextEval;
    static readonly ConcurrentDictionary<long, TaskCompletionSource<string?>> evals = new();

    /// Keyed by a number, not a handle: a late or repeated answer finds
    /// nothing and is dropped, rather than reaching freed memory.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static void Evaluated(IntPtr result, IntPtr error, IntPtr context)
    {
        Safe("Evaluated", () =>
        {
            if (!evals.TryRemove((long)context, out var done)) { Trace.Log("eval answer with no question"); return; }
            Trace.Log("page: eval done" + (error != IntPtr.Zero ? " with an error" : ""));
            done.TrySetResult(error != IntPtr.Zero ? null : WK.Text(result));
        });
    }

    /// WebKit's call already counts as the person's own action.
    public Task<string?> EvalWithGesture(string script) => Eval(script);

    public void RefreshScripts() { if (!disposed) engine.Scripts(this); }

    public void SetShown(bool on)
    {
        Trace.Log($"page: {(on ? "show" : "hide")} {Url}");
        shown = on;
        host.Visibility = on ? Visibility.Visible : Visibility.Hidden;
        // A page out of the window is throttled by WebKit: timers slowed,
        // layers let go, nothing drawn.
        if (view != IntPtr.Zero) WK.WKViewSetIsInWindow(view, on);
        // Focus is Select()'s job: doing it here stole the omnibox on every switch.
    }

    public Task Freeze()
    {
        Trace.Log("page: freeze " + Url);
        if (view != IntPtr.Zero) WK.WKViewSetIsInWindow(view, false);
        engine.Trim();
        return Task.CompletedTask;
    }

    public void OpenDevTools() { if (PageRef != IntPtr.Zero) WK.WKInspectorShow(WK.WKPageGetInspector(PageRef)); }

    public void Focus()
    {
        if (view != IntPtr.Zero && shown) WK.SetFocus(WK.WKViewGetWindow(view));
    }

    public void Find(string text, bool backwards)
    {
        if (PageRef == IntPtr.Zero) return;
        if (text.Length == 0) { WK.WKPageHideFindUI(PageRef); return; }
        var s = WK.Str(text);
        // Case-insensitive, wrapping, highlighted, the match outlined.
        uint options = 1u | (1u << 4) | (1u << 6) | (1u << 7) | (backwards ? 1u << 3 : 0);
        WK.WKPageFindString(PageRef, s, options, 1000);
        WK.WKRelease(s);
    }

    static readonly HttpClient http = new();
    static readonly Dictionary<string, ImageSource?> icons = new();

    /// The page's icon: what its <link rel=icon> names, or /favicon.ico.
    async Task FetchIcon()
    {
        if (Url == lastIconFor || !Url.StartsWith("http")) return;
        lastIconFor = Url;
        var href = await Eval("(function(){var l=document.querySelector('link[rel~=\"icon\" i]');return l?l.href:location.origin+'/favicon.ico'})()");
        if (href == null) return;
        try { href = JsonSerializer.Deserialize<string>(href); } catch { return; }
        if (string.IsNullOrEmpty(href)) return;
        if (!icons.TryGetValue(href, out var image))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(href);
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.DecodePixelWidth = 32;
                b.StreamSource = new MemoryStream(bytes);
                b.EndInit();
                b.Freeze();
                image = b;
            }
            catch { image = null; }
            icons[href] = image;
        }
        IconChanged?.Invoke(image);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        engine.Pages.Remove(this);
        engine.extensions.PageGone(this);
        if (PageRef != IntPtr.Zero) WK.WKPageClose(PageRef);
        host.Dispose();
        if (config != IntPtr.Zero) WK.WKRelease(config);
        if (Controller != IntPtr.Zero) WK.WKRelease(Controller);
        // WebKit may still call back while it lets go: free these a moment later.
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            stateClient?.Dispose(); navigationClient?.Dispose(); uiClient?.Dispose();
            if (self.IsAllocated) self.Free();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// Where WebKit's window sits in the WPF window.
    sealed class Host : HwndHost
    {
        readonly WebKitPage page;
        public Host(WebKitPage page) { this.page = page; Focusable = true; }

        protected override HandleRef BuildWindowCore(HandleRef parent)
        {
            var hwnd = page.MakeView(parent.Handle);
            page.Scaled(VisualTreeHelper.GetDpi(this).DpiScaleX);
            return new HandleRef(this, hwnd);
        }

        protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
        {
            base.OnDpiChanged(oldDpi, newDpi);
            page.Scaled(newDpi.DpiScaleX);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            // The window goes first, while WebKit's view object is still
            // there to answer its last messages; the view is let go after.
            WK.DestroyWindow(hwnd.Handle);
            var view = page.view;
            page.view = IntPtr.Zero;
            if (view != IntPtr.Zero)
                Dispatcher.BeginInvoke(() => WK.WKRelease(view), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        protected override void OnGotKeyboardFocus(System.Windows.Input.KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            page.Focus();
        }
    }
}
