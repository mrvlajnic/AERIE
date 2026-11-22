namespace AERIE.Core;

/// The search engines the field can send words to. Ported from Engine.swift.
public static class Engine
{
    public static readonly (string Id, string Title, string Template)[] All =
    {
        ("google", "Google", "https://www.google.com/search?q=%s"),
        ("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q=%s"),
        ("bing", "Bing", "https://www.bing.com/search?q=%s"),
        ("ecosia", "Ecosia", "https://www.ecosia.org/search?q=%s"),
        ("startpage", "Startpage", "https://www.startpage.com/sp/search?query=%s"),
        ("kagi", "Kagi", "https://kagi.com/search?q=%s"),
        ("brave", "Brave Search", "https://search.brave.com/search?q=%s"),
        ("qwant", "Qwant", "https://www.qwant.com/?q=%s"),
        ("custom", "Custom", ""),
    };

    public const string Standard = "google";

    public static string Template(string id, string custom)
    {
        if (id == "custom")
        {
            var written = Written(custom);
            return Accepts(written) ? written : Template(Standard, "");
        }
        foreach (var e in All) if (e.Id == id) return e.Template;
        return Template(Standard, "");
    }

    public static string Name(string id, string custom)
    {
        if (id != "custom") { foreach (var e in All) if (e.Id == id) return e.Title; return "Google"; }
        var host = Host(Written(custom));
        return host is null ? "Google" : Address.WithoutWww(host);
    }

    /// A custom engine as people bring it: with {searchTerms}, or without https://.
    public static string Written(string template)
    {
        var text = (template ?? "").Trim().Replace("{searchTerms}", "%s");
        if (text.Length > 0 && !text.Contains("://")) text = "https://" + text;
        return text;
    }

    public static bool Accepts(string template) => Host(template) is not null;

    public static Uri? Url(string words, string template)
    {
        words = words.Trim();
        if (words.Length == 0) return null;
        var s = template.Replace("%s", Uri.EscapeDataString(words));
        return Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;
    }

    static string? Host(string template)
    {
        var t = template.Trim();
        if (!t.Contains("%s")) return null;
        if (!Uri.TryCreate(t.Replace("%s", "a"), UriKind.Absolute, out var a)) return null;
        if (!Uri.TryCreate(t.Replace("%s", "b"), UriKind.Absolute, out var b)) return null;
        if (a.Scheme != "http" && a.Scheme != "https") return null;
        if (string.IsNullOrEmpty(a.Host) || a.Host != b.Host) return null;
        return a.Host.ToLowerInvariant();
    }
}
