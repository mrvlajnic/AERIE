namespace Search.Core;

public sealed class Visit
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Last { get; set; }
    public int Count { get; set; }
}

/// Where you have been, on this PC only. The field finishes addresses from it.
public sealed class History
{
    public List<Visit> Visits { get; set; } = new();
    /// The Mac app keeps 2,000 pages; a 20,000-entry file was written whole.
    public const int Cap = 2000;
    const double FadeDays = 30;

    /// URL identity ignores fragments and credentials, lower-cases only the
    /// scheme and host, keeps the path and query spelling, and treats no path
    /// as "/". Two spellings of the same page are one visit.
    public static string Identity(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        try
        {
            var b = new UriBuilder(u)
            {
                Fragment = "",
                UserName = "",
                Password = "",
                Scheme = u.Scheme.ToLowerInvariant(),
                Host = u.Host.ToLowerInvariant(),
            };
            if (u.IsDefaultPort) b.Port = -1;
            if (string.IsNullOrEmpty(b.Path)) b.Path = "/";
            return b.Uri.AbsoluteUri;
        }
        catch { return url; }
    }

    static bool IsWeb(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public void Record(string url, string title, DateTime when)
    {
        if (!IsWeb(url)) return;
        var kept = Identity(url);
        var found = Visits.FindIndex(v => v.Url == kept || Identity(v.Url) == kept);
        if (found >= 0)
        {
            var v = Visits[found];
            v.Url = kept;
            v.Count = Math.Min(v.Count + 1, 1_000_000);
            v.Last = when;
            if (!string.IsNullOrWhiteSpace(title)) v.Title = title;
            Visits.RemoveAt(found);
            Visits.Add(v);
        }
        else Visits.Add(new Visit { Url = kept, Title = title ?? "", Last = when, Count = 1 });
        if (Visits.Count > Cap) Visits.RemoveRange(0, Visits.Count - Cap);
    }

    public void Retitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        var kept = Identity(url);
        for (int i = Visits.Count - 1; i >= 0; i--)
            if (Visits[i].Url == kept || Identity(Visits[i].Url) == kept)
            {
                Visits[i].Url = kept;
                Visits[i].Title = title;
                return;
            }
    }

    // ---------------------------------------------------------------- text

    static string Bare(string url) =>
        url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url[8..]
        : url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? url[7..]
        : url;

    /// The address as a person writes it: no scheme, no www, no trailing slash
    /// on a front door.
    static string Editable(string url)
    {
        var s = Bare(url).ToLowerInvariant();
        int slash = s.IndexOf('/');
        if (slash == s.Length - 1) s = s[..^1];
        return s;
    }

    static string Shown(string url) => Address.WithoutWww(Editable(url));

    static string HostOf(string shown)
    {
        int end = shown.IndexOfAny(new[] { '/', '?', '#' });
        return end < 0 ? shown : shown[..end];
    }

    static double Popularity(Visit v, DateTime now) =>
        v.Count * Math.Exp(-Math.Max(0, (now - v.Last).TotalDays) / FadeDays);

    /// 4 the address starts with it, 3 a word of the host does, 2 every word
    /// starts a word of the title or address, 1 found inside a word.
    static double Fit(string needle, string[] words, string shown, string findable)
    {
        if (shown.StartsWith(needle, StringComparison.Ordinal)) return 4;
        var host = HostOf(shown);
        if (words.All(w => host.Split('.').Any(label => label.StartsWith(w, StringComparison.Ordinal)))) return 3;
        if (needle.Length < 3) return 0;
        if (words.All(w => StartsWord(findable, w))) return 2;
        return 1;
    }

    static bool StartsWord(string text, string word)
    {
        int at = 0;
        while ((at = text.IndexOf(word, at, StringComparison.Ordinal)) >= 0)
        {
            if (at == 0 || !char.IsLetterOrDigit(text[at - 1])) return true;
            at += word.Length;
        }
        return false;
    }

    static bool Holds(string[] words, string findable) =>
        words.All(w => findable.Contains(w, StringComparison.Ordinal));

    /// What the field offers for what has been typed: match quality ×
    /// log(2 + frecency). A one-typo pass runs only when room is left.
    public List<Visit> Suggest(string typed, int limit, DateTime now)
    {
        var needle = typed.Trim().ToLowerInvariant();
        if (needle.Length == 0) return new();
        var words = needle.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return new();

        var scored = new List<(Visit V, double Score, string Key)>();
        foreach (var v in Visits)
        {
            var shown = Shown(v.Url);
            var findable = v.Title.ToLowerInvariant() + " " + shown;
            if (!Holds(words, findable)) continue;
            var fit = Fit(needle, words, shown, findable);
            if (fit <= 0) continue;
            scored.Add((v, fit * Math.Log(2 + Popularity(v, now)), shown));
        }

        if (scored.Count < limit && needle.Length >= 3)
        {
            var seen = scored.Select(x => x.V).ToHashSet();
            foreach (var v in Visits)
            {
                if (seen.Contains(v)) continue;
                var shown = Shown(v.Url);
                if (!Fuzzy.Nearly(words, v.Title.ToLowerInvariant() + " " + shown)) continue;
                scored.Add((v, Math.Log(2 + Popularity(v, now)), shown));
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Key.Length)
            .Take(limit)
            .Select(x => x.V)
            .ToList();
    }

    /// The rest of the best address that carries on from what was typed, or
    /// null. One letter finishes only the top visited front door, and the
    /// completed address must be the same page.
    public string? Complete(string typed, DateTime now)
    {
        var t = typed.Trim().ToLowerInvariant();
        if (t.Length == 0 || t.Contains(' ')) return null;
        bool single = t.Length == 1;
        foreach (var v in Suggest(t, single ? 1 : 8, now))
        {
            foreach (var (key, target) in Endings(v))
            {
                if (single && (key.Contains('/') || key.Contains('?') || key.Contains('#'))) continue;
                if (!key.StartsWith(t, StringComparison.OrdinalIgnoreCase) || key.Length <= t.Length) continue;
                var rest = key[t.Length..];
                var completed = Address.Url(t + rest);
                if (completed is null) continue;
                if (Identity(completed.AbsoluteUri) != target) continue;
                return rest;
            }
        }
        return null;
    }

    static IEnumerable<(string Key, string Target)> Endings(Visit v)
    {
        var full = Editable(v.Url);
        yield return (full, Identity(v.Url));
        if (full.StartsWith("www.", StringComparison.Ordinal))
            yield return (full[4..], Identity(v.Url));
    }

    public void Forget(Func<Visit, bool> which) => Visits.RemoveAll(v => which(v));
}
