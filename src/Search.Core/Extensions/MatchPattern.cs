using System.Text;
using System.Text.RegularExpressions;

namespace Search.Core.Extensions;

/// Chrome's match patterns (<all_urls>, *://*.example.com/*), as regular
/// expressions that mean the same in .NET and in JavaScript, so the page's
/// content-script loader and the browser agree on what matches.
public static class MatchPattern
{
    public static string ToRegex(string pattern)
    {
        if (pattern == "<all_urls>") return "^(https?|wss?|ftp|file|data):";
        var m = Regex.Match(pattern, @"^(\*|https?|wss?|ftp|file|urn|data):\/\/([^/]*)(\/.*)?$");
        if (!m.Success)
        {
            // urn: and data: have no //
            return pattern.StartsWith("file:") ? "^file:" : "^(?!)";
        }
        string scheme = m.Groups[1].Value == "*" ? "(https?|wss?)" : Regex.Escape(m.Groups[1].Value);
        string host = m.Groups[2].Value;
        string path = m.Groups[3].Success ? m.Groups[3].Value : "/";
        var sb = new StringBuilder("^").Append(scheme).Append(":\\/\\/");
        if (host == "*") sb.Append("[^/]*");
        else if (host.StartsWith("*.")) sb.Append("([^/]*\\.)?").Append(Escape(host[2..]));
        else sb.Append(Escape(host));
        // A port is allowed after the host unless the pattern names one.
        if (!host.Contains(':') && host != "*") sb.Append("(:\\d+)?");
        sb.Append(Glob(path)).Append('$');
        return sb.ToString();
    }

    /// The include_globs / exclude_globs syntax: * and ? only.
    public static string GlobToRegex(string glob) => "^" + Glob(glob) + "$";

    static string Glob(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else sb.Append(Escape(c.ToString()));
        }
        return sb.ToString();
    }

    static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if ("\\^$.|?*+()[]{}/".Contains(c)) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    public static bool Matches(string pattern, string url) =>
        Regex.IsMatch(url, ToRegex(pattern), RegexOptions.IgnoreCase);
}
