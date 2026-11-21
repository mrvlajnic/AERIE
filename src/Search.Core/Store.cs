using System.Text.Json;
using System.Text.Json.Serialization;

namespace Search.Core;

/// Everything Search keeps lives in one folder, as plain JSON:
/// %LOCALAPPDATA%\Search. Nothing leaves the PC.
public static class Store
{
    public static string Folder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Search");

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Hand-edited or older files may use any casing.
        PropertyNameCaseInsensitive = true,
    };

    /// The last damaged file kept aside, for anyone who wants to look at it.
    public static string? LastQuarantine { get; private set; }

    public static T Load<T>(string name) where T : new()
    {
        var path = Path.Combine(Folder, name);
        if (!File.Exists(path)) return new T();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(text, Options) ?? new T();
        }
        catch
        {
            // A damaged file is kept, not overwritten by defaults on the next
            // save: the history or session in it may still be recoverable.
            Quarantine(path);
            return new T();
        }
    }

    static void Quarantine(string path)
    {
        try
        {
            var kept = path + ".unreadable-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..4];
            File.Move(path, kept);
            LastQuarantine = kept;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetDirectoryName(path)!, "errors.log"),
                    $"{DateTime.Now:u} store: {Path.GetFileName(path)} was unreadable; kept as {Path.GetFileName(kept)}\n");
            }
            catch { }
        }
        catch { /* an unreadable or locked file is left where it is */ }
    }

    /// Written beside and moved into place, so a crash mid-write never leaves half a file.
    public static void Save<T>(string name, T value) => Write(name, value, Folder);

    static readonly object queue = new();
    static Task writing = Task.CompletedTask;

    /// Serializes and writes on a worker, one file at a time, so a large
    /// history never blocks the UI. `value` must be a snapshot the caller will
    /// not touch again.
    public static void SaveInBackground<T>(string name, T value)
    {
        var folder = Folder;
        lock (queue)
        {
            writing = writing.ContinueWith(_ =>
            {
                try { Write(name, value, folder); }
                catch { /* one failed save must not stop the queue */ }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// Waits for queued background saves: on exit, and in tests.
    public static void Flush(TimeSpan timeout)
    {
        Task wait;
        lock (queue) wait = writing;
        try { wait.Wait(timeout); } catch { }
    }

    static void Write<T>(string name, T value, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        // Unique per write: two saves (or two windows) must not share a temp.
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
            File.Move(temp, path, true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}

public sealed class Settings
{
    public bool TabsOnSide { get; set; } = true;
    public bool SideFolded { get; set; }
    public string Look { get; set; } = "system";          // system | light | dark
    public string Engine { get; set; } = Core.Engine.Standard;
    public string CustomEngine { get; set; } = "";
    public bool ShieldOn { get; set; } = true;
    public List<string> ShieldPaused { get; set; } = new();
    public int SleepMinutes { get; set; } = 10;            // 0 = never
    /// Background pages kept awake across all spaces; the rest sleep in LRU
    /// order. 0 = no cap. WebView2 is one controller per awake tab.
    public int MaxAwakeTabs { get; set; } = 10;
    /// Background tabs are frozen this many seconds after you leave them.
    public int FreezeSeconds { get; set; } = 20;
    /// Chromium's low-memory mode. Smaller caches; gradients can band a little.
    public bool Lean { get; set; } = true;
    /// Same-site pages in one renderer. Less memory, weaker isolation.
    public bool ProcessPerSite { get; set; }
    /// "webkit", "webview2", or empty for whichever the build carries.
    public string EngineName { get; set; } = "";
    /// WebKit: every tab of a space in one page process. Least memory; one
    /// page crashing takes the others with it, and sites are not isolated.
    public bool SharedProcess { get; set; }
    public bool FloatOnLeave { get; set; }
    public bool SavePasswords { get; set; } = true;
    /// WebView2 DevTools (F12). Off by default; a debugger attach still enables them.
    public bool DevTools { get; set; }
    /// What each site may use: host -> permission kind -> allowed.
    public Dictionary<string, Dictionary<string, bool>> SitePermissions { get; set; } = new();
    public bool SpaceSwipe { get; set; } = true;
    public double Width { get; set; } = 1280;
    public double Height { get; set; } = 820;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public bool Maximized { get; set; }
    public double SideWidth { get; set; } = 240;
}

public sealed class SavedTab
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public string? Name { get; set; }       // a name given by hand
    public string? Home { get; set; }       // a pin's own address
}

public sealed class SavedSpace
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Home";
    public string Icon { get; set; } = "";
    /// Fresh spaces keep their own cookies and sign-ins.
    public bool Fresh { get; set; }
    public List<SavedTab> Pins { get; set; } = new();
    public List<SavedTab> Tabs { get; set; } = new();
    public int Selected { get; set; } = -1;   // index into pins, then tabs
}

public sealed class Session
{
    public List<SavedSpace> Spaces { get; set; } = new();
    public int Current { get; set; }
}
