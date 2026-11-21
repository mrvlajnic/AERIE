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
    };

    public static T Load<T>(string name) where T : new()
    {
        try
        {
            var path = Path.Combine(Folder, name);
            if (File.Exists(path)) return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? new T();
        }
        catch { /* a damaged file starts over rather than stopping the app */ }
        return new T();
    }

    /// Written beside and moved into place, so a crash mid-write never leaves half a file.
    public static void Save<T>(string name, T value)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, name);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));
        File.Move(temp, path, true);
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
    /// Background tabs are frozen this many seconds after you leave them.
    public int FreezeSeconds { get; set; } = 20;
    /// Chromium's low-memory mode. Smaller caches; gradients can band a little.
    public bool Lean { get; set; } = true;
    /// "webkit", "webview2", or empty for whichever the build carries.
    public string EngineName { get; set; } = "";
    /// WebKit: every tab of a space in one page process. Least memory; one
    /// page crashing takes the others with it, and sites are not isolated.
    public bool SharedProcess { get; set; }
    public bool FloatOnLeave { get; set; }
    public bool SavePasswords { get; set; } = true;
    /// WebView2 DevTools (F12). Off by default; a debugger attach still enables them.
    public bool DevTools { get; set; }
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
