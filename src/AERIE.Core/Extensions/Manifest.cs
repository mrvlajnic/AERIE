using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AERIE.Core.Extensions;

public sealed record ContentScript(
    string[] Matches, string[] ExcludeMatches, string[] IncludeGlobs, string[] ExcludeGlobs,
    string[] Js, string[] Css, string RunAt, bool AllFrames, bool MatchAboutBlank, string World);

public sealed record Ruleset(string Id, bool Enabled, string Path);

/// An extension's manifest.json, MV2 or MV3, read for the parts Search runs.
public sealed class Manifest
{
    public string Id { get; init; } = "";
    public string Folder { get; init; } = "";
    public JsonObject Raw { get; init; } = new();
    public int Version => Raw["manifest_version"]?.GetValue<int>() ?? 2;
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string VersionName => Raw["version"]?.GetValue<string>() ?? "0";
    public string? BackgroundWorker { get; init; }
    public string[] BackgroundScripts { get; init; } = Array.Empty<string>();
    public string? BackgroundPage { get; init; }
    public bool BackgroundIsModule { get; init; }
    public bool HasBackground => BackgroundWorker != null || BackgroundScripts.Length > 0 || BackgroundPage != null;
    public List<ContentScript> ContentScripts { get; init; } = new();
    public string? Popup { get; init; }
    public string? ActionTitle { get; init; }
    public string? OptionsPage { get; init; }
    public string[] Permissions { get; init; } = Array.Empty<string>();
    public string[] HostPermissions { get; init; } = Array.Empty<string>();
    public List<(string[] Resources, string[] Matches)> WebAccessible { get; init; } = new();
    public List<Ruleset> Rulesets { get; init; } = new();
    public string? Icon { get; init; }
    public Locale Locale { get; init; } = new(new());

    public bool Has(string permission) => Permissions.Contains(permission);

    public static Manifest Load(string folder, string? id = null)
    {
        var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "manifest.json")), documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true,
        }) as JsonObject ?? throw new InvalidDataException("manifest.json is not an object");
        var locale = Locale.Load(folder, raw["default_locale"]?.GetValue<string>());
        string T(string? s) => s == null ? "" : locale.Resolve(s);

        var bg = raw["background"] as JsonObject;
        var scripts = new List<ContentScript>();
        foreach (var cs in (raw["content_scripts"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            scripts.Add(new ContentScript(
                Strings(cs["matches"]), Strings(cs["exclude_matches"]), Strings(cs["include_globs"]), Strings(cs["exclude_globs"]),
                Strings(cs["js"]), Strings(cs["css"]), cs["run_at"]?.GetValue<string>() ?? "document_idle",
                cs["all_frames"]?.GetValue<bool>() ?? false, cs["match_about_blank"]?.GetValue<bool>() ?? false,
                cs["world"]?.GetValue<string>() ?? "ISOLATED"));

        var action = (raw["action"] ?? raw["browser_action"] ?? raw["page_action"]) as JsonObject;
        var war = new List<(string[], string[])>();
        foreach (var w in raw["web_accessible_resources"] as JsonArray ?? new JsonArray())
        {
            if (w is JsonObject o) war.Add((Strings(o["resources"]), Strings(o["matches"])));
            else if (w?.GetValue<string>() is { } s) war.Add((new[] { s }, new[] { "<all_urls>" }));
        }
        var rulesets = new List<Ruleset>();
        foreach (var r in (raw["declarative_net_request"]?["rule_resources"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            rulesets.Add(new Ruleset(r["id"]?.GetValue<string>() ?? "", r["enabled"]?.GetValue<bool>() ?? false, Clean(r["path"]?.GetValue<string>() ?? "")));

        var permissions = Strings(raw["permissions"]);
        var hosts = Strings(raw["host_permissions"]).Concat(Strings(raw["optional_host_permissions"])).ToList();
        if ((raw["manifest_version"]?.GetValue<int>() ?? 2) == 2)
            hosts.AddRange(permissions.Where(p => p.Contains("://") || p == "<all_urls>"));

        string? icon = null;
        if (raw["icons"] is JsonObject icons)
            icon = icons.OrderByDescending(kv => int.TryParse(kv.Key, out var n) ? n : 0).Select(kv => kv.Value?.GetValue<string>()).FirstOrDefault();

        return new Manifest
        {
            Id = id ?? IdFor(raw, folder),
            Folder = folder,
            Raw = raw,
            Name = T(raw["name"]?.GetValue<string>()),
            Description = T(raw["description"]?.GetValue<string>()),
            BackgroundWorker = bg?["service_worker"]?.GetValue<string>() is { } sw ? Clean(sw) : null,
            BackgroundScripts = Strings(bg?["scripts"]).Select(Clean).ToArray(),
            BackgroundPage = bg?["page"]?.GetValue<string>() is { } page ? Clean(page) : null,
            BackgroundIsModule = bg?["type"]?.GetValue<string>() == "module",
            ContentScripts = scripts,
            Popup = action?["default_popup"]?.GetValue<string>() is { Length: > 0 } p ? Clean(p) : null,
            ActionTitle = action?["default_title"]?.GetValue<string>() is { } t ? T(t) : null,
            OptionsPage = (raw["options_ui"]?["page"] ?? raw["options_page"])?.GetValue<string>() is { Length: > 0 } op ? Clean(op) : null,
            Permissions = permissions,
            HostPermissions = hosts.Distinct().ToArray(),
            WebAccessible = war,
            Rulesets = rulesets,
            Icon = icon is null ? null : Clean(icon),
            Locale = locale,
        };
    }

    static string Clean(string path) => path.TrimStart('/').Replace('\\', '/');

    static string[] Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(x => x?.GetValue<string>()).Where(x => x != null).Select(x => x!).ToArray() : Array.Empty<string>();

    /// Chrome's own scheme: from the manifest's key when it has one (so a
    /// store extension keeps its store id), else from where it lives.
    public static string IdFor(JsonObject raw, string folder)
    {
        byte[] input;
        if (raw["key"]?.GetValue<string>() is { Length: > 0 } key)
        {
            try { input = Convert.FromBase64String(key); }
            catch { input = Encoding.UTF8.GetBytes(key); }
        }
        else input = Encoding.Unicode.GetBytes(Path.GetFullPath(folder));
        return FromHash(SHA256.HashData(input));
    }

    static string FromHash(byte[] hash)
    {
        var sb = new StringBuilder(32);
        for (int i = 0; i < 16; i++)
        {
            sb.Append((char)('a' + (hash[i] >> 4)));
            sb.Append((char)('a' + (hash[i] & 0xF)));
        }
        return sb.ToString();
    }
}

/// _locales/…/messages.json: chrome.i18n and the __MSG_name__ strings in the manifest.
public sealed class Locale
{
    public Dictionary<string, JsonObject> Messages { get; }
    public Locale(Dictionary<string, JsonObject> messages) { Messages = messages; }

    public static Locale Load(string folder, string? fallback)
    {
        var all = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        var ui = System.Globalization.CultureInfo.CurrentUICulture.Name.Replace('-', '_');
        foreach (var name in new[] { fallback, ui.Split('_')[0], ui }.Where(n => !string.IsNullOrEmpty(n)))
        {
            var path = Path.Combine(folder, "_locales", name!, "messages.json");
            if (!File.Exists(path)) continue;
            try
            {
                if (JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) is JsonObject o)
                    foreach (var kv in o) if (kv.Value is JsonObject m) all[kv.Key] = m;   // the more specific one wins
            }
            catch { }
        }
        return new Locale(all);
    }

    public string Get(string name, string[]? substitutions = null)
    {
        if (!Messages.TryGetValue(name, out var m)) return "";
        var text = m["message"]?.GetValue<string>() ?? "";
        substitutions ??= Array.Empty<string>();
        if (m["placeholders"] is JsonObject ph)
            foreach (var kv in ph)
            {
                var content = kv.Value?["content"]?.GetValue<string>() ?? "";
                content = Positional(content, substitutions);
                text = System.Text.RegularExpressions.Regex.Replace(text, "\\$" + System.Text.RegularExpressions.Regex.Escape(kv.Key) + "\\$", content.Replace("$", "$$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
        return Positional(text, substitutions).Replace("$$", "$");
    }

    static string Positional(string s, string[] subs) =>
        System.Text.RegularExpressions.Regex.Replace(s, @"\$([1-9])", m =>
        {
            int i = m.Groups[1].Value[0] - '1';
            return i < subs.Length ? subs[i] : "";
        });

    public string Resolve(string s) =>
        System.Text.RegularExpressions.Regex.Replace(s, "__MSG_(\\w+)__", m => Get(m.Groups[1].Value));

    /// Every message, for chrome.i18n inside pages: name → resolved text.
    public string ToJson() => JsonSerializer.Serialize(Messages.ToDictionary(k => k.Key.ToLowerInvariant(), k => k.Value));
}
