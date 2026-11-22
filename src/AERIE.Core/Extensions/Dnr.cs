using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AERIE.Core.Extensions;

/// declarativeNetRequest, as WebKit's content blocker understands it. This
/// is how Safari runs Chrome's network rules too: both are lists of
/// declarative rules, so most translate one for one. What doesn't —
/// redirects, header changes, regexes WebKit can't compile — is counted and
/// left out rather than approximated into something that blocks the wrong thing.
public static class Dnr
{
    public sealed record Result(string Json, int Converted, int Skipped);

    static readonly Dictionary<string, string[]> Types = new()
    {
        ["main_frame"] = new[] { "document" },
        ["sub_frame"] = new[] { "document" },
        ["stylesheet"] = new[] { "style-sheet" },
        ["script"] = new[] { "script" },
        ["image"] = new[] { "image" },
        ["font"] = new[] { "font" },
        ["object"] = new[] { "other" },
        ["xmlhttprequest"] = new[] { "raw" },
        ["ping"] = new[] { "ping" },
        ["csp_report"] = new[] { "other" },
        ["media"] = new[] { "media" },
        ["websocket"] = new[] { "raw" },
        ["webtransport"] = new[] { "raw" },
        ["webbundle"] = new[] { "other" },
        ["other"] = new[] { "other" },
    };

    /// Chrome's default when a rule names no types: everything but the page itself.
    static readonly string[] AllButMainFrame =
        { "sub_frame", "stylesheet", "script", "image", "font", "object", "xmlhttprequest", "ping", "csp_report", "media", "websocket", "other" };

    /// rules: a JSON array of DNR rules, from every enabled ruleset, static and dynamic.
    public static Result Convert(IEnumerable<JsonNode?> rules)
    {
        int skipped = 0;
        var ordered = new List<(int Priority, int Allow, JsonObject Rule)>();
        foreach (var node in rules)
        {
            if (node is not JsonObject rule) { skipped++; continue; }
            var converted = One(rule);
            if (converted.Count == 0) { skipped++; continue; }
            int priority = rule["priority"]?.GetValue<int>() ?? 1;
            var actionType = rule["action"]?["type"]?.GetValue<string>();
            int allow = actionType is "allow" or "allowAllRequests" ? 1 : 0;
            foreach (var c in converted) ordered.Add((priority, allow, c));
        }
        // WebKit applies rules in order and an "ignore-previous-rules" undoes
        // what came before it: so lower priority first, and at the same
        // priority blocks before allows, as Chrome ranks them.
        var array = new JsonArray(ordered.OrderBy(o => o.Priority).ThenBy(o => o.Allow).Select(o => (JsonNode)o.Rule).ToArray());
        return new Result(array.ToJsonString(), ordered.Count, skipped);
    }

    public static Result Convert(string json) =>
        Convert(JsonNode.Parse(json) is JsonArray a ? a : new JsonArray());

    static List<JsonObject> One(JsonObject rule)
    {
        var output = new List<JsonObject>();
        var action = rule["action"]?["type"]?.GetValue<string>();
        string? kind = action switch
        {
            "block" => "block",
            "allow" or "allowAllRequests" => "ignore-previous-rules",
            "upgradeScheme" => "make-https",
            _ => null,   // redirect, modifyHeaders: WebKit's rules can't
        };
        if (kind == null) return output;
        var condition = rule["condition"] as JsonObject ?? new JsonObject();

        string? filter;
        if (condition["regexFilter"]?.GetValue<string>() is { } regex)
        {
            if (!WebKitCanCompile(regex)) return output;
            filter = regex;
        }
        else filter = condition["urlFilter"]?.GetValue<string>() is { } f ? UrlFilter(f) : null;

        var requestDomains = Strings(condition["requestDomains"]);
        var filters = new List<string>();
        if (requestDomains.Count > 0)
        {
            // One rule per domain: WebKit's regexes have no alternation.
            foreach (var d in requestDomains)
                filters.Add("^[^:]+://([^/]+\\.)?" + Escape(d) + "[:/]" + (filter != null ? ".*" + StripStart(filter) : ""));
        }
        else filters.Add(filter ?? ".*");

        var types = Strings(condition["resourceTypes"]);
        var excludedTypes = Strings(condition["excludedResourceTypes"]);
        if (types.Count == 0) types = AllButMainFrame.Where(t => !excludedTypes.Contains(t)).ToList();
        var webkitTypes = types.SelectMany(t => Types.TryGetValue(t, out var w) ? w : Array.Empty<string>()).Distinct().ToList();
        bool top = types.Contains("main_frame"), sub = types.Contains("sub_frame");

        var ifDomains = Strings(condition["initiatorDomains"]).Concat(Strings(condition["domains"])).Select(d => "*" + d).ToList();
        var unlessDomains = Strings(condition["excludedInitiatorDomains"]).Concat(Strings(condition["excludedDomains"])).Select(d => "*" + d).ToList();

        foreach (var f in filters)
        {
            var trigger = new JsonObject { ["url-filter"] = f };
            if (condition["isUrlFilterCaseSensitive"]?.GetValue<bool>() == true) trigger["url-filter-is-case-sensitive"] = true;
            if (webkitTypes.Count > 0) trigger["resource-type"] = new JsonArray(webkitTypes.Select(t => (JsonNode)t).ToArray());
            if (webkitTypes.SequenceEqual(new[] { "document" }) && top != sub)
                trigger["load-context"] = new JsonArray(top ? "top-frame" : "child-frame");
            var domainType = condition["domainType"]?.GetValue<string>();
            if (domainType == "firstParty") trigger["load-type"] = new JsonArray("first-party");
            if (domainType == "thirdParty") trigger["load-type"] = new JsonArray("third-party");
            // WebKit takes one of the two, not both: the narrower wins.
            if (ifDomains.Count > 0) trigger["if-domain"] = new JsonArray(ifDomains.Select(d => (JsonNode)d).ToArray());
            else if (unlessDomains.Count > 0) trigger["unless-domain"] = new JsonArray(unlessDomains.Select(d => (JsonNode)d).ToArray());
            output.Add(new JsonObject { ["trigger"] = trigger, ["action"] = new JsonObject { ["type"] = kind } });
        }

        // Excluded request domains: allowed again after the rule, for the same pages.
        if (kind == "block")
            foreach (var d in Strings(condition["excludedRequestDomains"]))
            {
                var trigger = new JsonObject { ["url-filter"] = "^[^:]+://([^/]+\\.)?" + Escape(d) + "[:/]" };
                if (ifDomains.Count > 0) trigger["if-domain"] = new JsonArray(ifDomains.Select(x => (JsonNode)x).ToArray());
                output.Add(new JsonObject { ["trigger"] = trigger, ["action"] = new JsonObject { ["type"] = "ignore-previous-rules" } });
            }
        return output;
    }

    static List<string> Strings(JsonNode? node) =>
        node is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrEmpty(x)).Select(x => x!.ToLowerInvariant()).ToList() : new();

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

    static string StripStart(string regex) => regex.StartsWith('^') ? regex[1..] : regex;

    /// DNR's urlFilter (Adblock-style) as a WebKit regex.
    public static string UrlFilter(string filter)
    {
        var sb = new StringBuilder();
        int i = 0;
        if (filter.StartsWith("||")) { sb.Append("^[^:]+://([^/]+\\.)?"); i = 2; }
        else if (filter.StartsWith('|')) { sb.Append('^'); i = 1; }
        bool endAnchor = filter.Length > i && filter.EndsWith('|') && !filter.EndsWith("||");
        int end = endAnchor ? filter.Length - 1 : filter.Length;
        for (; i < end; i++)
        {
            char c = filter[i];
            if (c == '*') sb.Append(".*");
            else if (c == '^')
            {
                // A separator: anything but a letter, digit or _-.%. Every
                // request URL has at least a path, so a trailing one keeps the
                // separator too — "ads.example.com" must not match ".community".
                sb.Append("[^a-zA-Z0-9_.%-]");
            }
            else if ("\\$.|?+()[]{}/".Contains(c)) sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        if (endAnchor) sb.Append('$');
        var s = sb.ToString();
        return s.Length == 0 ? ".*" : s;
    }

    /// WebKit's rule compiler takes a small regex dialect: no alternation,
    /// no counted repetition, no character class shorthands, no lookaround.
    public static bool WebKitCanCompile(string regex)
    {
        if (regex.Contains('|') || regex.Contains('{') || regex.Contains("(?")) return false;
        for (int i = 0; i < regex.Length - 1; i++)
            if (regex[i] == '\\' && "dDwWsSbB123456789".Contains(regex[i + 1])) return false;
        return true;
    }
}
