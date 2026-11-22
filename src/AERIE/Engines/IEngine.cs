using System.Windows;
using System.Windows.Media;
using AERIE.Core;

namespace AERIE;

/// A page, whichever engine draws it. The window only ever talks to this.
interface IPage : IDisposable
{
    FrameworkElement Element { get; }
    string Url { get; }
    string Title { get; }
    bool Loading { get; }
    bool Audible { get; }
    bool Muted { get; set; }
    bool CanGoBack { get; }
    bool CanGoForward { get; }

    /// Address, title, loading or sound changed: read them again.
    event Action? Changed;
    event Action<ImageSource?>? IconChanged;
    /// A navigation finished: (address, title, succeeded).
    event Action<string, string, bool>? Finished;
    /// A message the page posted to the browser (JSON).
    event Action<string>? Message;
    /// The page opened another: (new page, address, in the background).
    event Action<IPage, string, bool>? Opened;
    event Action? CloseRequested;
    event Action<bool>? FullScreen;

    void Navigate(string url);
    void Back();
    void Forward();
    void Reload();
    /// Runs a script in the main frame; the result as JSON, or null.
    Task<string?> Eval(string script);
    /// The same, as if the person had clicked: what picture-in-picture needs.
    Task<string?> EvalWithGesture(string script);
    /// The scripts that run before each document: replaced as a set.
    void RefreshScripts();
    void SetShown(bool shown);
    /// Frozen in the background: no scripts, no timers, memory handed back.
    Task Freeze();
    void OpenDevTools();
    void Focus();
    /// Finds text in the page; the engine's own highlighter where it has one.
    void Find(string text, bool backwards) { }
}

/// Who a page belongs to: its tab's space (for sign-ins) and whether it's private.
sealed record PageOwner(string Profile, bool Private, Tab Tab);

/// The engine: makes pages and owns what they share.
interface IEngine
{
    string Name { get; }
    string? Version { get; }
    Task Start(BrowserWindow browser);
    IPage Create(PageOwner owner);
    /// The scripts a document for this host runs before it draws, besides the
    /// engine's own.
    Func<string, IEnumerable<string>> DocumentScripts { set; }
    Task ApplyShield(bool on, IReadOnlyCollection<string> paused);
    void Recolour();
    Task ClearData();
    IExtensionHost Extensions { get; }
    /// Removes a space's profile data when that space is deleted.
    void DeleteProfile(string profile) { }
}

sealed record ExtensionInfo(string Id, string Name, string Folder, string? StoreId, bool HasPopup, bool Enabled);

/// Chrome extensions, as each engine runs them.
interface IExtensionHost
{
    /// False when this engine cannot run extensions safely.
    bool Enabled => true;
    IReadOnlyList<ExtensionInfo> All { get; }
    string About { get; }
    Task<string> InstallFromStore(string link);
    Task<string> AddFolder(string folder);
    Task Remove(ExtensionInfo e);
    Task Enable(ExtensionInfo e, bool on);
    void OpenPopup(BrowserWindow browser, ExtensionInfo e);
    /// What happened to a tab, for tabs.onUpdated and the like: created, updated, activated, removed.
    void Tabs(string what, Tab t) { }
}

static class Engines
{
    /// WebView2 unless WebKit is chosen: measured on the same machine and
    /// sites, WebView2 used about a fifth of the memory (see README).
    public static string Default(Settings s)
    {
        foreach (var a in Environment.GetCommandLineArgs())
            if (a.StartsWith("--engine=")) return a[9..];
        if (s.EngineName is "webkit" or "webview2") return s.EngineName;
        return "webview2";
    }

    public static IEngine Make(string name) => name == "webkit" && WebKit.Available ? new WebKitEngine() : new WebView2Engine();
}
