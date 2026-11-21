using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Search;

/// Windows lets an app offer itself as a browser; only the person can pick
/// it, in Settings › Apps › Default apps. This registers Search for the
/// current user (no administrator needed) and opens that page.
static class DefaultBrowser
{
    const string ProgId = "SearchHTM";
    const string Client = @"Software\Clients\StartMenuInternet\Search";

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    public static void Register()
    {
        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Search.exe");
        var open = $"\"{exe}\" \"%1\"";
        using (var prog = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + ProgId))
        {
            prog.SetValue("", "Search HTML Document");
            prog.SetValue("FriendlyTypeName", "Search HTML Document");
            prog.CreateSubKey("DefaultIcon").SetValue("", exe + ",0");
            prog.CreateSubKey(@"shell\open\command").SetValue("", open);
            using var app = prog.CreateSubKey("Application");
            app.SetValue("ApplicationName", "Search");
            app.SetValue("ApplicationIcon", exe + ",0");
        }
        using (var client = Registry.CurrentUser.CreateSubKey(Client))
        {
            client.SetValue("", "Search");
            client.CreateSubKey("DefaultIcon").SetValue("", exe + ",0");
            client.CreateSubKey(@"shell\open\command").SetValue("", $"\"{exe}\"");
            using var caps = client.CreateSubKey("Capabilities");
            caps.SetValue("ApplicationName", "Search");
            caps.SetValue("ApplicationDescription", "A small, fast, quiet web browser.");
            caps.SetValue("ApplicationIcon", exe + ",0");
            using (var urls = caps.CreateSubKey("URLAssociations"))
                foreach (var scheme in new[] { "http", "https" }) urls.SetValue(scheme, ProgId);
            using (var files = caps.CreateSubKey("FileAssociations"))
                foreach (var ext in new[] { ".htm", ".html", ".shtml", ".xhtml", ".svg", ".webp", ".pdf" }) files.SetValue(ext, ProgId);
            caps.CreateSubKey("StartMenu").SetValue("StartMenuInternet", "Search");
        }
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            registered.SetValue("Search", Client + @"\Capabilities");
        SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
    }

    public static void OpenSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps?registeredAppUser=Search") { UseShellExecute = true }); }
        catch { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
    }

    public static bool IsDefault()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            return key?.GetValue("ProgId") as string == ProgId;
        }
        catch { return false; }
    }
}
