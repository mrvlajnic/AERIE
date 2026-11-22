namespace AERIE.Core;

/// What you type has to be a place. This either hands back an address or
/// nothing; the field then searches instead. Ported from Address.swift.
public static class Address
{
    static readonly HashSet<string> Ours = new(StringComparer.OrdinalIgnoreCase)
        { "http", "https", "file", "about", "data", "edge", "chrome-extension" };

    /// A path on this PC, C:\notes\page.html or \\server\share\page.html,
    /// only when it really is a file. Committed navigation only.
    public static Uri? File(string typed)
    {
        var text = typed.Trim().Trim('"');
        bool drive = text.Length > 2 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/');
        bool unc = text.StartsWith(@"\\");
        if (!drive && !unc) return null;
        try { return System.IO.File.Exists(text) ? new Uri(Path.GetFullPath(text)) : null; }
        catch { return null; }
    }

    public static Uri? Url(string typed)
    {
        var text = typed.Trim();
        if (text.Length == 0) return null;

        // Written with a scheme, it is taken at its word. A file URL may
        // contain spaces ("file:///C:/My Documents/a.html"); other schemes
        // may not.
        int split = text.IndexOf("://", StringComparison.Ordinal);
        if (split > 0)
        {
            var scheme = text[..split];
            if (!Ours.Contains(scheme)) return null;
            bool spacesAllowed = scheme.Equals("file", StringComparison.OrdinalIgnoreCase)
                || scheme.Equals("data", StringComparison.OrdinalIgnoreCase);
            if (text.Contains(' ') && !spacesAllowed) return null;
            return Uri.TryCreate(text, UriKind.Absolute, out var u) ? u : null;
        }
        var lower = text.ToLowerInvariant();
        if (lower.StartsWith("about:") || lower.StartsWith("data:") || lower.StartsWith("edge:"))
            return Uri.TryCreate(text, UriKind.Absolute, out var u) ? u : null;

        if (text.Contains(' ')) return null;

        // Everything else has to look like a host first. "hello world" is
        // not a website, and neither is "todo".
        int end = text.IndexOfAny(new[] { '/', '?', '#' });
        var head = end < 0 ? text : text[..end];
        if (head.Contains('@')) return null;          // an email address

        // Any IPv6 literal, not only loopback.
        if (head.StartsWith('['))
        {
            int close = head.IndexOf(']');
            if (close < 0 || !System.Net.IPAddress.TryParse(head[1..close], out var ip)) return null;
            bool local6 = System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
            return Make((local6 ? "http://" : "https://") + text);
        }

        var host = head.Split(':')[0];
        if (!LooksLikeHost(host)) return null;
        // A port has to be a number: "note:to-self" is not an address.
        var colon = head.IndexOf(':');
        if (colon >= 0 && !int.TryParse(head[(colon + 1)..], out _)) return null;

        // Local servers almost never have certificates.
        bool local = host == "localhost" || host.EndsWith(".localhost") || host.EndsWith(".local")
            || host == "127.0.0.1" || host == "0.0.0.0" || PrivateRange(host);
        return Make((local ? "http://" : "https://") + text);
    }

    static Uri? Make(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    static bool PrivateRange(string host)
    {
        var parts = host.Split('.');
        if (parts.Length != 4) return false;
        var o = new byte[4];
        for (int i = 0; i < 4; i++) if (!byte.TryParse(parts[i], out o[i])) return false;
        return o[0] == 10 || (o[0] == 192 && o[1] == 168) || (o[0] == 172 && o[1] >= 16 && o[1] <= 31);
    }

    /// Dev servers print 0.0.0.0; Chromium would refuse it, so it is opened as localhost.
    public static Uri Reachable(Uri url)
    {
        if (url.Host == "0.0.0.0" && (url.Scheme == "http" || url.Scheme == "https"))
            return new UriBuilder(url) { Host = "localhost" }.Uri;
        return url;
    }

    static bool LooksLikeHost(string host)
    {
        if (host == "localhost") return true;
        var labels = host.Split('.');
        if (labels.Length == 4 && labels.All(l => byte.TryParse(l, out _))) return true;
        if (labels.Length < 2) return false;
        foreach (var l in labels)
        {
            if (l.Length == 0 || l.StartsWith('-') || l.EndsWith('-')) return false;
            if (!l.All(c => char.IsLetterOrDigit(c) || c == '-')) return false;
        }
        // A dotted thing ending in letters is a domain; ending in digits, a version number.
        var tld = labels[^1];
        return tld.Length >= 2 && tld.All(char.IsLetter);
    }

    /// The address as the field shows it for editing.
    public static string Editable(Uri page)
    {
        var full = page.AbsoluteUri;
        var prefix = page.Scheme + "://";
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return full;
        var shortForm = full[prefix.Length..];
        var candidates = new List<string> { shortForm };
        if (page.AbsolutePath == "/" && page.Query.Length == 0 && page.Fragment.Length == 0 && shortForm.EndsWith('/'))
            candidates.Insert(0, shortForm[..^1]);
        foreach (var c in candidates)
        {
            var back = Url(c);
            if (back is null) continue;
            if (back.AbsoluteUri == full || back.AbsoluteUri + "/" == full) return c;
        }
        return full;
    }

    public static string WithoutWww(string address) =>
        address.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? address[4..] : address;

    /// What a tab says before the page has told us its title.
    public static string Pretty(Uri url)
    {
        if (string.IsNullOrEmpty(url.Host)) return url.AbsoluteUri;
        var bare = WithoutWww(url.Host);
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        return string.IsNullOrEmpty(path) || path == "/" ? bare : bare + path;
    }

    /// The registrable part of a host, closely enough to tell first from third
    /// parties. A compact public-suffix list: the common multi-label ccTLDs and
    /// the private suffixes people publish under.
    public static string Registrable(string host)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        var labels = host.Split('.');
        if (labels.Length <= 2) return host;
        if (labels.Length == 4 && labels.All(l => byte.TryParse(l, out _))) return host;
        int longest = Math.Min(3, labels.Length - 1);
        for (int take = longest; take >= 2; take--)
        {
            var suffix = string.Join('.', labels[^take..]);
            if (MultiLabel.Contains(suffix) || PrivateSuffix.Contains(suffix))
                return string.Join('.', labels[^(take + 1)..]);
        }
        return labels[^2] + "." + labels[^1];
    }

    static readonly HashSet<string> MultiLabel = new(StringComparer.OrdinalIgnoreCase)
    {
        "co.uk", "org.uk", "ac.uk", "gov.uk", "me.uk", "ltd.uk", "plc.uk",
        "com.au", "net.au", "org.au", "edu.au", "gov.au", "id.au",
        "co.nz", "net.nz", "org.nz", "govt.nz",
        "co.jp", "ne.jp", "or.jp", "ac.jp", "go.jp",
        "co.kr", "or.kr", "ne.kr",
        "com.cn", "net.cn", "org.cn", "gov.cn",
        "com.hk", "org.hk", "net.hk",
        "com.tw", "org.tw", "net.tw",
        "com.sg", "net.sg", "org.sg",
        "co.in", "net.in", "org.in", "gov.in",
        "co.za", "org.za", "net.za",
        "com.br", "net.br", "org.br",
        "com.mx", "org.mx",
        "com.tr", "net.tr", "org.tr",
        "co.il", "org.il",
        "com.ar", "com.co", "com.pe", "com.ve",
        "co.id", "or.id", "web.id",
        "com.my", "com.ph", "com.vn", "com.pk",
        "co.th", "in.th",
        "com.eg", "com.sa", "com.ua",
    };

    /// The PSL's private section, abridged.
    static readonly HashSet<string> PrivateSuffix = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.io", "githubusercontent.com", "blogspot.com", "herokuapp.com",
        "vercel.app", "netlify.app", "pages.dev", "web.app", "firebaseapp.com",
        "azurewebsites.net", "cloudfront.net", "s3.amazonaws.com",
        "appspot.com", "workers.dev", "pages.github.com", "gitlab.io",
        "repl.co", "glitch.me", "onrender.com", "fly.dev", "deno.dev",
    };
}
