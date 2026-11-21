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

    /// True when every word has a word in `text` within one edit — the typo
    /// fallback for the field, after exact and prefix matches have failed.
    public static bool Nearly(IEnumerable<string> needles, string text)
    {
        var words = Tokens(text).ToList();
        foreach (var needle in needles)
        {
            var w = needle.ToLowerInvariant();
            if (w.Length == 0) continue;
            bool found = words.Any(candidate => candidate == w || (w.Length >= 3 && OneEdit(w, candidate)));
            if (!found) return false;
        }
        return true;
    }

    static IEnumerable<string> Tokens(string text)
    {
        int start = -1;
        for (int i = 0; i <= text.Length; i++)
        {
            bool alnum = i < text.Length && char.IsLetterOrDigit(text[i]);
            if (alnum && start < 0) start = i;
            else if (!alnum && start >= 0)
            {
                yield return text[start..i].ToLowerInvariant();
                start = -1;
            }
        }
    }

    /// Distance at most one: substitution, insertion, deletion or a
    /// neighbouring transposition.
    static bool OneEdit(string a, string b)
    {
        if (a == b) return true;
        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > 1) return false;
        if (la == lb)
        {
            int i = 0;
            while (i < la && a[i] == b[i]) i++;
            if (i == la) return true;
            if (i + 1 < la && a[i] == b[i + 1] && a[i + 1] == b[i])
                return a[(i + 2)..] == b[(i + 2)..];
            return a[(i + 1)..] == b[(i + 1)..];
        }
        var (shorter, longer) = la < lb ? (a, b) : (b, a);
        int j = 0;
        while (j < shorter.Length && shorter[j] == longer[j]) j++;
        return shorter[j..] == longer[(j + 1)..];
    }
}
