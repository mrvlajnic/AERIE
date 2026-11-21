using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Search.Core;

/// Chrome extensions from the Chrome Web Store: the store's package is a
/// zip with a signed header in front (CRX2 or CRX3). The header is taken
/// off and the zip unpacked into a folder WebView2 can load.
public static partial class Crx
{
    [GeneratedRegex("(?:^|[/=])([a-p]{32})(?:$|[/?#&])")]
    private static partial Regex IdPattern();

    /// The extension's id from a store link, or the bare id itself.
    public static string? IdFrom(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 32 && text.All(c => c >= 'a' && c <= 'p')) return text;
        if (!text.Contains("chromewebstore.google.com") && !text.Contains("chrome.google.com/webstore")) return null;
        var m = IdPattern().Match(text);
        return m.Success ? m.Groups[1].Value : null;
    }

    public static Uri DownloadUrl(string id, string chromeVersion = "130.0.0.0") =>
        new($"https://clients2.google.com/service/update2/crx?response=redirect&prodversion={chromeVersion}" +
            $"&acceptformat=crx2,crx3&x=id%3D{id}%26uc");

    /// Where the zip starts inside a .crx, or 0 when it is already a zip.
    public static int ZipOffset(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4 && data[0] == 'P' && data[1] == 'K') return 0;
        if (data.Length < 16 || data[0] != 'C' || data[1] != 'r' || data[2] != '2' || data[3] != '4')
            throw new InvalidDataException("Not a Chrome extension package.");
        uint version = BitConverter.ToUInt32(data[4..8]);
        if (version == 3) return checked(12 + (int)BitConverter.ToUInt32(data[8..12]));
        if (version == 2) return checked(16 + (int)BitConverter.ToUInt32(data[8..12]) + (int)BitConverter.ToUInt32(data[12..16]));
        throw new InvalidDataException($"Unknown package version {version}.");
    }

    /// Unpacks into `folder`, replacing what was there.
    public static void Unpack(byte[] crx, string folder)
    {
        int offset = ZipOffset(crx);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        using var zip = new ZipArchive(new MemoryStream(crx, offset, crx.Length - offset), ZipArchiveMode.Read);
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(folder, entry.FullName));
            if (!target.StartsWith(root, StringComparison.Ordinal)) continue; // no escaping the folder
            if (entry.FullName.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
        }
        // Chrome's store signature; only the store checks it, and it stops
        // an unpacked load in some engines.
        var meta = Path.Combine(folder, "_metadata");
        if (Directory.Exists(meta)) Directory.Delete(meta, true);
    }

    /// The popup page a toolbar button would open, from manifest.json.
    public static string? Popup(string folder)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            foreach (var key in new[] { "action", "browser_action", "page_action" })
                if (doc.RootElement.TryGetProperty(key, out var a) && a.TryGetProperty("default_popup", out var p) && p.GetString() is { Length: > 0 } s)
                    return s.TrimStart('/');
        }
        catch { }
        return null;
    }

    public static string? OptionsPage(string folder)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")));
            if (doc.RootElement.TryGetProperty("options_page", out var o) && o.GetString() is { Length: > 0 } s) return s.TrimStart('/');
            if (doc.RootElement.TryGetProperty("options_ui", out var ui) && ui.TryGetProperty("page", out var p) && p.GetString() is { Length: > 0 } t) return t.TrimStart('/');
        }
        catch { }
        return null;
    }
}
