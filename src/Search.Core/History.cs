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
    public const int Cap = 20000;

    public void Record(string url, string title, DateTime when)
    {
        if (!url.StartsWith("http://") && !url.StartsWith("https://")) return;
        var found = Visits.FindIndex(v => v.Url == url);
        if (found >= 0)
        {
            var v = Visits[found];
            v.Count++; v.Last = when;
            if (!string.IsNullOrWhiteSpace(title)) v.Title = title;
            Visits.RemoveAt(found);
            Visits.Add(v);
        }
        else Visits.Add(new Visit { Url = url, Title = title ?? "", Last = when, Count = 1 });
        if (Visits.Count > Cap) Visits.RemoveRange(0, Visits.Count - Cap);
    }

    public void Retitle(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        for (int i = Visits.Count - 1; i >= 0 && i >= Visits.Count - 50; i--)
            if (Visits[i].Url == url) { Visits[i].Title = title; return; }
    }

    static string Bare(string url)
    {
        var s = url;
        if (s.StartsWith("https://")) s = s[8..]; else if (s.StartsWith("http://")) s = s[7..];
        return Address.WithoutWww(s);
    }

    static double Weight(Visit v, DateTime now) =>
        v.Count / (1 + (now - v.Last).TotalDays / 7);

    /// What the field offers for what has been typed so far.
    public List<Visit> Suggest(string typed, int limit, DateTime now)
    {
        var t = typed.Trim().ToLowerInvariant();
        if (t.Length == 0) return new();
        var bare = Address.WithoutWww(t.StartsWith("https://") ? t[8..] : t.StartsWith("http://") ? t[7..] : t);
        return Visits
            .Select(v => (v, s: Bare(v.Url).ToLowerInvariant().StartsWith(bare) ? 3.0
                               : v.Title.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1.5
                               : v.Url.Contains(t, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0))
            .Where(x => x.s > 0)
            .OrderByDescending(x => x.s * (1 + Weight(x.v, now)))
            .Take(limit).Select(x => x.v).ToList();
    }

    /// The rest of an address that has been typed the start of: "git" →
    /// "hub.com". Only whole hosts, the way Safari finishes them.
    public string? Complete(string typed, DateTime now)
    {
        var t = typed.ToLowerInvariant();
        if (t.Length == 0 || t.Contains(' ')) return null;
        var bareTyped = Address.WithoutWww(t);
        string? best = null; double bestW = 0;
        foreach (var v in Visits)
        {
            var b = Bare(v.Url).ToLowerInvariant();
            int slash = b.IndexOf('/');
            var host = slash < 0 ? b : b[..slash];
            string? candidate = null;
            if (host.StartsWith(bareTyped) && host.Length > bareTyped.Length) candidate = host;
            else if (b.StartsWith(bareTyped) && bareTyped.Contains('/') && b.Length > bareTyped.Length)
            {
                // Stop at the next slash after what was typed.
                int next = b.IndexOf('/', bareTyped.Length);
                candidate = next < 0 ? b : b[..next];
            }
            if (candidate is null) continue;
            var w = Weight(v, now);
            if (best is null || w > bestW || (w == bestW && candidate.Length < best.Length)) { best = candidate; bestW = w; }
        }
        return best is null ? null : best[bareTyped.Length..];
    }

    public void Forget(Func<Visit, bool> which) => Visits.RemoveAll(v => which(v));
}
