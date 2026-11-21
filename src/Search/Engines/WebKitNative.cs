using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Search;

/// WebKit's C API, as exported by WebKit2.dll in WebKit's Windows port
/// (the WinCairo build Playwright publishes). Signatures follow the headers
/// at the WebKit revision that build is made from.
static unsafe class WK
{
    const string Dll = "WebKit2.dll";

    // Types, strings, URLs ------------------------------------------------------
    [DllImport(Dll)] public static extern void WKRelease(IntPtr o);
    [DllImport(Dll)] public static extern IntPtr WKRetain(IntPtr o);
    [DllImport(Dll)] public static extern nuint WKGetTypeID(IntPtr o);
    [DllImport(Dll)] public static extern nuint WKStringGetTypeID();
    [DllImport(Dll)] public static extern nuint WKDoubleGetTypeID();
    [DllImport(Dll)] public static extern nuint WKBooleanGetTypeID();
    [DllImport(Dll)] public static extern double WKDoubleGetValue(IntPtr d);
    [DllImport(Dll)] public static extern bool WKBooleanGetValue(IntPtr b);
    [DllImport(Dll)] static extern IntPtr WKStringCreateWithUTF8CString(byte* s);
    [DllImport(Dll)] static extern nuint WKStringGetLength(IntPtr s);
    [DllImport(Dll)] static extern nuint WKStringGetCharacters(IntPtr s, char* buffer, nuint length);
    [DllImport(Dll)] static extern IntPtr WKURLCreateWithUTF8CString(byte* s);
    [DllImport(Dll)] public static extern IntPtr WKURLCopyString(IntPtr url);
    [DllImport(Dll)] public static extern IntPtr WKArrayCreate(IntPtr* values, nuint count);
    [DllImport(Dll)] public static extern IntPtr WKDictionaryCreate(IntPtr* keys, IntPtr* values, nuint count);

    public static IntPtr Str(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes) return WKStringCreateWithUTF8CString(p);
    }

    public static IntPtr Url(string s)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes) return WKURLCreateWithUTF8CString(p);
    }

    public static string Read(IntPtr wkString)
    {
        if (wkString == IntPtr.Zero) return "";
        var n = (int)WKStringGetLength(wkString);
        if (n == 0) return "";
        var buffer = new char[n];
        fixed (char* b = buffer) n = (int)WKStringGetCharacters(wkString, b, (nuint)n);
        return new string(buffer, 0, n);
    }

    /// Reads and releases a copied string.
    public static string Take(IntPtr wkString)
    {
        var s = Read(wkString);
        if (wkString != IntPtr.Zero) WKRelease(wkString);
        return s;
    }

    public static string TakeUrl(IntPtr wkUrl)
    {
        if (wkUrl == IntPtr.Zero) return "";
        var s = Take(WKURLCopyString(wkUrl));
        WKRelease(wkUrl);
        return s;
    }

    /// Any API value as text: strings as they are, numbers and booleans written out.
    public static string? Text(IntPtr value)
    {
        if (value == IntPtr.Zero) return null;
        var type = WKGetTypeID(value);
        if (type == WKStringGetTypeID()) return Read(value);
        if (type == WKDoubleGetTypeID()) return WKDoubleGetValue(value).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (type == WKBooleanGetTypeID()) return WKBooleanGetValue(value) ? "true" : "false";
        return null;
    }

    // Context, data stores, preferences ------------------------------------------
    [DllImport(Dll)] public static extern IntPtr WKContextConfigurationCreate();
    [DllImport(Dll)] public static extern void WKContextConfigurationSetUsesWebProcessCache(IntPtr c, bool v);
    [DllImport(Dll)] public static extern void WKContextConfigurationSetPrewarmsProcessesAutomatically(IntPtr c, bool v);
    [DllImport(Dll)] public static extern void WKContextConfigurationSetProcessSwapsOnNavigation(IntPtr c, bool v);
    [DllImport(Dll)] public static extern IntPtr WKContextCreateWithConfiguration(IntPtr c);
    [DllImport(Dll)] public static extern void WKContextSetCacheModel(IntPtr context, uint model);
    [DllImport(Dll)] public static extern void WKContextGarbageCollectJavaScriptObjects(IntPtr context);
    [DllImport(Dll)] public static extern IntPtr WKWebsiteDataStoreConfigurationCreate();
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetNetworkCacheDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetCacheStorageDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetIndexedDBDatabaseDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetLocalStorageDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetGeneralStorageDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetMediaKeysStorageDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetResourceLoadStatisticsDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetServiceWorkerRegistrationDirectory(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreConfigurationSetCookieStorageFile(IntPtr c, IntPtr s);
    [DllImport(Dll)] public static extern IntPtr WKWebsiteDataStoreCreateWithConfiguration(IntPtr c);
    [DllImport(Dll)] public static extern IntPtr WKWebsiteDataStoreCreateNonPersistentDataStore();
    [DllImport(Dll)] public static extern void WKWebsiteDataStoreRemoveMemoryCaches(IntPtr store, IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, void> done);
    [DllImport(Dll)] public static extern IntPtr WKPreferencesCreate();
    [DllImport(Dll)] public static extern void WKPreferencesSetDeveloperExtrasEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetFullScreenEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetMediaSourceEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetPageCacheEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetHiddenPageDOMTimerThrottlingEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetHiddenPageDOMTimerThrottlingAutoIncreases(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetAllowsPictureInPictureMediaPlayback(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetAcceleratedCompositingEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetAccelerated2DCanvasEnabled(IntPtr p, bool v);
    [DllImport(Dll)] public static extern void WKPreferencesSetAcceleratedDrawingEnabled(IntPtr p, bool v);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr hwnd);

    // Pages and views ------------------------------------------------------------
    [DllImport(Dll)] public static extern IntPtr WKPageConfigurationCreate();
    [DllImport(Dll)] public static extern void WKPageConfigurationSetContext(IntPtr c, IntPtr context);
    [DllImport(Dll)] public static extern void WKPageConfigurationSetWebsiteDataStore(IntPtr c, IntPtr store);
    [DllImport(Dll)] public static extern void WKPageConfigurationSetUserContentController(IntPtr c, IntPtr ucc);
    [DllImport(Dll)] public static extern IntPtr WKPageConfigurationGetUserContentController(IntPtr c);
    [DllImport(Dll)] public static extern void WKPageConfigurationSetPreferences(IntPtr c, IntPtr prefs);
    [DllImport(Dll)] public static extern void WKPageConfigurationSetRelatedPage(IntPtr c, IntPtr page);
    [DllImport(Dll)] public static extern IntPtr WKViewCreate(RECT rect, IntPtr configuration, IntPtr parent);
    [DllImport(Dll)] public static extern IntPtr WKViewGetWindow(IntPtr view);
    [DllImport(Dll)] public static extern IntPtr WKViewGetPage(IntPtr view);
    [DllImport(Dll)] public static extern void WKViewSetParentWindow(IntPtr view, IntPtr parent);
    [DllImport(Dll)] public static extern void WKViewSetIsInWindow(IntPtr view, bool inWindow);
    [DllImport(Dll)] public static extern void WKViewWindowAncestryDidChange(IntPtr view);
    [DllImport(Dll)] public static extern void WKPageLoadURL(IntPtr page, IntPtr url);
    [DllImport(Dll)] public static extern void WKPageReload(IntPtr page);
    [DllImport(Dll)] public static extern void WKPageStopLoading(IntPtr page);
    [DllImport(Dll)] public static extern void WKPageGoBack(IntPtr page);
    [DllImport(Dll)] public static extern void WKPageGoForward(IntPtr page);
    [DllImport(Dll)] public static extern bool WKPageCanGoBack(IntPtr page);
    [DllImport(Dll)] public static extern bool WKPageCanGoForward(IntPtr page);
    [DllImport(Dll)] public static extern void WKPageClose(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr WKPageCopyTitle(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr WKPageCopyActiveURL(IntPtr page);
    [DllImport(Dll)] public static extern double WKPageGetEstimatedProgress(IntPtr page);
    [DllImport(Dll)] public static extern bool WKPageIsPlayingAudio(IntPtr page);
    [DllImport(Dll)] public static extern void WKPageSetMuted(IntPtr page, uint state);
    [DllImport(Dll)] public static extern void WKPageSetPageZoomFactor(IntPtr page, double zoom);
    [DllImport(Dll)] public static extern void WKPageSetCustomUserAgent(IntPtr page, IntPtr ua);
    [DllImport(Dll)] public static extern IntPtr WKPageGetInspector(IntPtr page);
    [DllImport(Dll)] public static extern void WKInspectorShow(IntPtr inspector);
    [DllImport(Dll)] public static extern void WKPageFindString(IntPtr page, IntPtr s, uint options, uint maxMatches);
    [DllImport(Dll)] public static extern void WKPageHideFindUI(IntPtr page);
    [DllImport(Dll)] public static extern IntPtr WKPageGetContext(IntPtr page);
    [DllImport(Dll)] public static extern void WKPageCallAsyncJavaScript(IntPtr page, IntPtr script, IntPtr args, IntPtr frame, IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback);
    [DllImport(Dll)] public static extern void WKPageSetPageStateClient(IntPtr page, IntPtr client);
    [DllImport(Dll)] public static extern void WKPageSetPageNavigationClient(IntPtr page, IntPtr client);
    [DllImport(Dll)] public static extern void WKPageSetPageUIClient(IntPtr page, IntPtr client);

    // Script, content blocking, dialogs --------------------------------------------
    [DllImport(Dll)] public static extern IntPtr WKUserContentControllerCreate();
    [DllImport(Dll)] public static extern void WKUserContentControllerAddUserScript(IntPtr ucc, IntPtr script);
    [DllImport(Dll)] public static extern void WKUserContentControllerRemoveAllUserScripts(IntPtr ucc);
    [DllImport(Dll)] public static extern void WKUserContentControllerAddUserContentFilter(IntPtr ucc, IntPtr filter);
    [DllImport(Dll)] public static extern void WKUserContentControllerRemoveAllUserContentFilters(IntPtr ucc);
    [DllImport(Dll)] public static extern void WKUserContentControllerAddScriptMessageHandler(IntPtr ucc, IntPtr name, delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void> callback, IntPtr context);
    [DllImport(Dll)] public static extern IntPtr WKUserScriptCreateWithSource(IntPtr source, int injectionTime, bool mainFrameOnly);
    [DllImport(Dll)] public static extern IntPtr WKScriptMessageGetBody(IntPtr message);
    [DllImport(Dll)] public static extern IntPtr WKScriptMessageGetFrameInfo(IntPtr message);
    [DllImport(Dll)] public static extern bool WKFrameInfoGetIsMainFrame(IntPtr frame);
    [DllImport(Dll)] public static extern IntPtr WKFrameInfoCopySecurityOrigin(IntPtr frame);
    [DllImport(Dll)] public static extern IntPtr WKSecurityOriginCopyToString(IntPtr origin);
    [DllImport(Dll)] public static extern void WKCompletionListenerComplete(IntPtr listener, IntPtr result);
    [DllImport(Dll)] public static extern IntPtr WKUserContentExtensionStoreCreate(IntPtr path);
    [DllImport(Dll)] public static extern void WKUserContentExtensionStoreCompile(IntPtr store, IntPtr id, IntPtr json, IntPtr context, delegate* unmanaged[Cdecl]<IntPtr, uint, IntPtr, void> callback);
    [DllImport(Dll)] public static extern void WKPageRunJavaScriptAlertResultListenerCall(IntPtr listener);
    [DllImport(Dll)] public static extern void WKPageRunJavaScriptConfirmResultListenerCall(IntPtr listener, bool result);
    [DllImport(Dll)] public static extern void WKPageRunJavaScriptPromptResultListenerCall(IntPtr listener, IntPtr result);
    [DllImport(Dll)] public static extern bool WKOpenPanelParametersGetAllowsMultipleFiles(IntPtr parameters);
    [DllImport(Dll)] public static extern void WKOpenPanelResultListenerChooseFiles(IntPtr listener, IntPtr fileUrls, IntPtr mimeTypes);
    [DllImport(Dll)] public static extern void WKOpenPanelResultListenerCancel(IntPtr listener);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    /// A client struct: { int version; const void* clientInfo; callbacks… },
    /// laid out by hand from the field positions in the headers.
    public sealed class Client : IDisposable
    {
        public IntPtr Pointer { get; }
        public Client(int version, int fields, IntPtr info)
        {
            Pointer = Marshal.AllocHGlobal(16 + 8 * fields);
            new Span<byte>((void*)Pointer, 16 + 8 * fields).Clear();
            *(int*)Pointer = version;
            *(IntPtr*)(Pointer + 8) = info;
        }
        public void Set(int field, void* fn) => *(IntPtr*)(Pointer + 16 + 8 * field) = (IntPtr)fn;
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    // Field positions, from WKPageStateClient.h, WKPageNavigationClient.h and
    // WKPageUIClient.h at the build's revision (newest version of each).
    public static class StateV0 { public const int Version = 0, Count = 21, IsLoading = 1, Title = 3, ActiveURL = 5, Progress = 9, CanGoBack = 11, CanGoForward = 13; }
    public static class NavigationV3 { public const int Version = 3, Count = 27, DidStart = 3, DidFailProvisional = 5, DidCommit = 6, DidFinish = 7, DidFail = 8, WebProcessDidCrash = 15; }
    // The UI client counts its retired void* slots (unused1…unused8) too.
    public static class UIV19 { public const int Version = 19, Count = 80, Close = 2, DidNotHandleKeyEvent = 12, RunOpenPanel = 28, IsPlayingAudioDidChange = 50, CreateNewPage = 57, Alert = 58, Confirm = 59, Prompt = 60; }

    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr hwnd, int x, int y, int w, int h, bool repaint);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool SetDllDirectory(string path);
}
