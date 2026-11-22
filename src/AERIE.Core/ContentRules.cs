using System.Text.Json;
using System.Text.Json.Nodes;

namespace AERIE.Core;

/// The ad blocker as WebKit wants it: a content rule list, compiled once and
/// enforced inside WebKit's networking before a request is made. The same
/// rules as Search for Mac (Shield.swift), with the sites it is paused on
/// written in as exceptions.
public static class ContentRules
{
    /// WebKit's canonical URLs end the host with a port or a slash.
    public static string NetworkFilter(string domain) =>
        "^https?://([^/]+\\.)?" + domain.Replace(".", "\\.") + "[:/]";

    public static string Shield(IEnumerable<string> paused)
    {
        var unless = new JsonArray(paused.Select(p => (JsonNode)("*" + p)).ToArray());
        JsonObject Trigger(string filter, string? loadType = null)
        {
            var t = new JsonObject { ["url-filter"] = filter };
            if (loadType != null) t["load-type"] = new JsonArray(loadType);
            if (unless.Count > 0) t["unless-domain"] = unless.DeepClone();
            return t;
        }
        var rules = new JsonArray();
        foreach (var d in Core.Shield.Unwanted)
            rules.Add(new JsonObject { ["trigger"] = Trigger(NetworkFilter(d), "third-party"), ["action"] = new JsonObject { ["type"] = "block" } });
        // One rule per selector: an invalid one must not take the rest with it.
        foreach (var selector in Core.Shield.Slots)
            rules.Add(new JsonObject
            {
                ["trigger"] = Trigger(".*"),
                ["action"] = new JsonObject { ["type"] = "css-display-none", ["selector"] = selector },
            });
        foreach (var (sites, selector) in Core.Shield.SlotsBySite)
        {
            var on = sites.Where(s => !paused.Contains(s)).Select(s => (JsonNode)("*" + s)).ToArray();
            if (on.Length == 0) continue;
            rules.Add(new JsonObject
            {
                ["trigger"] = new JsonObject { ["url-filter"] = ".*", ["if-domain"] = new JsonArray(on) },
                ["action"] = new JsonObject { ["type"] = "css-display-none", ["selector"] = selector },
            });
        }
        return rules.ToJsonString();
    }
}
