namespace Search.Core;

/// Letters in order, closer together and at word starts scoring higher:
/// "gml" finds Gmail. Used by the tab switcher.
public static class Fuzzy
{
    public static int Score(string query, string text)
    {
        if (string.IsNullOrWhiteSpace(query)) return 1;
        var q = query.Trim().ToLowerInvariant();
        var t = text.ToLowerInvariant();
        int idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 1000 - idx + (idx == 0 || !char.IsLetterOrDigit(t[idx - 1]) ? 200 : 0);
        int score = 0, last = -1;
        foreach (var c in q)
        {
            if (c == ' ') continue;
            int at = t.IndexOf(c, last + 1);
            if (at < 0) return 0;
            score += at == last + 1 ? 8 : 1;
            if (at == 0 || !char.IsLetterOrDigit(t[at - 1])) score += 6;
            last = at;
        }
        return score;
    }
}
