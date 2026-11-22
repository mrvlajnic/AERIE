namespace AERIE.Core;

/// Copy Clean Link: an address without the parameters that only say where
/// a click came from. Ported from CleanLink.swift.
public static class CleanLink
{
    static readonly HashSet<string> Tracking = new()
    {
        "fbclid", "gclid", "dclid", "gbraid", "wbraid", "msclkid", "yclid",
        "twclid", "ttclid", "mc_eid", "igshid", "igsh", "_hsenc", "_hsmi", "mkt_tok",
    };
    static readonly Dictionary<string, string[]> TrackingOn = new()
    {
        ["si"] = new[] { "youtube.com", "youtu.be", "spotify.com" },
    };
    static readonly HashSet<string> Signatures = new() { "x-amz-signature", "x-goog-signature", "signature", "sig" };

    public static string Clean(string address)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var url)) return address;
        if (url.Scheme != "http" && url.Scheme != "https") return address;
        var query = url.Query.TrimStart('?');
        if (query.Length == 0) return address;
        var host = url.Host.ToLowerInvariant();
        var pieces = query.Split('&');
        var names = pieces.Select(p =>
        {
            var raw = p.Split('=', 2)[0].Replace('+', ' ');
            try { return Uri.UnescapeDataString(raw).ToLowerInvariant(); } catch { return raw.ToLowerInvariant(); }
        }).ToArray();
        if (names.Any(Signatures.Contains)) return address;
        var kept = pieces.Where((_, i) => !Tracks(names[i], host)).ToArray();
        if (kept.Length == pieces.Length) return address;
        var b = new UriBuilder(url) { Query = kept.Length == 0 ? "" : string.Join("&", kept) };
        if (url.IsDefaultPort) b.Port = -1;
        return b.Uri.AbsoluteUri;
    }

    public static bool Tracks(string name, string host)
    {
        if (name.StartsWith("utm_") || Tracking.Contains(name)) return true;
        return TrackingOn.TryGetValue(name, out var sites) && sites.Any(s => host == s || host.EndsWith("." + s));
    }
}
