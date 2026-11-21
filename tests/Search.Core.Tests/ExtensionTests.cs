using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Search.Core;
using Search.Core.Extensions;
using Xunit;

public class ContentRulesTests
{
    [Fact]
    public void ShieldIsValidJsonWithPausedExceptions()
    {
        var rules = JsonNode.Parse(ContentRules.Shield(new[] { "example.org" }))!.AsArray();
        Assert.True(rules.Count > Shield.Unwanted.Length);
        var first = rules[0]!["trigger"]!;
        Assert.Equal("^https?://([^/]+\\.)?doubleclick\\.net[:/]", first["url-filter"]!.GetValue<string>());
        Assert.Equal("*example.org", first["unless-domain"]![0]!.GetValue<string>());
        Assert.Matches(first["url-filter"]!.GetValue<string>(), "https://securepubads.g.doubleclick.net/tag.js");
        Assert.DoesNotMatch(first["url-filter"]!.GetValue<string>(), "https://notdoubleclick.net/x");
    }

    [Fact]
    public void OneRulePerCosmeticSelector()
    {
        var rules = JsonNode.Parse(ContentRules.Shield(Array.Empty<string>()))!.AsArray();
        int css = rules.Count(r => r!["action"]!["type"]!.GetValue<string>() == "css-display-none");
        Assert.Equal(Shield.Slots.Length + Shield.SlotsBySite.Length, css);
    }
}

public class DnrTests
{
    [Theory]
    [InlineData("||ads.example.com^", "https://ads.example.com/x.js", true)]
    [InlineData("||ads.example.com^", "https://sub.ads.example.com/x.js", true)]
    [InlineData("||ads.example.com^", "https://ads.example.community/x.js", true)] // trailing ^ left open: WebKit has no "or end"
    [InlineData("||example.com^track", "https://example.com/track", true)]
    [InlineData("||example.com^track", "https://example.comtrack", false)]
    [InlineData("|https://a.com/x|", "https://a.com/x", true)]
    [InlineData("|https://a.com/x|", "https://a.com/xy", false)]
    [InlineData("/banner/*/ad.png", "https://e.com/banner/12/ad.png", true)]
    public void UrlFilters(string filter, string url, bool match) =>
        Assert.Equal(match, Regex.IsMatch(url, Dnr.UrlFilter(filter), RegexOptions.IgnoreCase));

    [Fact]
    public void ConvertsAndOrders()
    {
        var json = """
        [
          {"id":1,"priority":1,"action":{"type":"block"},"condition":{"urlFilter":"||tracker.com^","resourceTypes":["script","xmlhttprequest"]}},
          {"id":2,"priority":2,"action":{"type":"allow"},"condition":{"urlFilter":"||tracker.com/ok","initiatorDomains":["good.com"]}},
          {"id":3,"action":{"type":"redirect","redirect":{"url":"https://x"}},"condition":{"urlFilter":"x"}},
          {"id":4,"action":{"type":"block"},"condition":{"regexFilter":"^https://(a|b)\\.com"}},
          {"id":5,"action":{"type":"block"},"condition":{"requestDomains":["a.com","b.com"],"domainType":"thirdParty"}},
          {"id":6,"action":{"type":"upgradeScheme"},"condition":{"urlFilter":"|http://","resourceTypes":["main_frame"]}}
        ]
        """;
        var r = Dnr.Convert(json);
        Assert.Equal(2, r.Skipped);          // redirect, and a regex with alternation
        var rules = JsonNode.Parse(r.Json)!.AsArray();
        Assert.Equal(5, rules.Count);         // 1 + 2 (two domains) + 1 + 1
        Assert.Equal("ignore-previous-rules", rules[^1]!["action"]!["type"]!.GetValue<string>());   // the allow, last
        Assert.Contains(rules, x => x!["action"]!["type"]!.GetValue<string>() == "make-https"
            && x["trigger"]!["load-context"]![0]!.GetValue<string>() == "top-frame");
        var types = rules[0]!["trigger"]!["resource-type"]!.AsArray().Select(t => t!.GetValue<string>()).ToList();
        Assert.Contains("script", types);
        Assert.Contains("raw", types);
        Assert.Contains(rules, x => x!["trigger"]!["load-type"]?[0]?.GetValue<string>() == "third-party");
    }

    [Theory]
    [InlineData("^https://ads\\.", true)]
    [InlineData("a|b", false)]
    [InlineData("\\d+", false)]
    [InlineData("x{2}", false)]
    [InlineData("(?=x)", false)]
    public void WebKitDialect(string regex, bool ok) => Assert.Equal(ok, Dnr.WebKitCanCompile(regex));
}

public class MatchPatternTests
{
    [Theory]
    [InlineData("<all_urls>", "https://x.com/a", true)]
    [InlineData("<all_urls>", "chrome://settings", false)]
    [InlineData("*://*.example.com/*", "https://example.com/", true)]
    [InlineData("*://*.example.com/*", "http://a.b.example.com/p?q", true)]
    [InlineData("*://*.example.com/*", "https://example.com.evil.net/", false)]
    [InlineData("https://mail.google.com/*", "https://mail.google.com/mail/u/0", true)]
    [InlineData("https://mail.google.com/*", "http://mail.google.com/", false)]
    [InlineData("*://*/*", "https://anything.org:8443/x", true)]
    [InlineData("https://e.com/docs/*", "https://e.com/other", false)]
    [InlineData("http://localhost/*", "http://localhost:3000/a", true)]
    [InlineData("file:///*", "file:///C:/a.html", true)]
    public void Matches(string pattern, string url, bool expected) => Assert.Equal(expected, MatchPattern.Matches(pattern, url));

    [Fact]
    public void Globs() => Assert.Matches(MatchPattern.GlobToRegex("*nytimes.com/???s/*"), "https://www.nytimes.com/arts/x");
}

public class ManifestTests
{
    static string Folder(string manifest, Action<string>? more = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest);
        more?.Invoke(dir);
        return dir;
    }

    [Fact]
    public void ReadsMv3()
    {
        var dir = Folder("""
        { // comments are allowed, as Chrome allows them
          "manifest_version": 3, "name": "__MSG_appName__", "version": "1.2",
          "default_locale": "en",
          "background": {"service_worker": "/bg.js", "type": "module"},
          "action": {"default_popup": "popup.html", "default_title": "T"},
          "options_ui": {"page": "opts.html"},
          "permissions": ["storage", "declarativeNetRequest"],
          "host_permissions": ["<all_urls>"],
          "content_scripts": [{"matches": ["*://*/*"], "js": ["c.js"], "css": ["c.css"], "run_at": "document_start", "all_frames": true}],
          "web_accessible_resources": [{"resources": ["img/*"], "matches": ["<all_urls>"]}],
          "declarative_net_request": {"rule_resources": [{"id": "r1", "enabled": true, "path": "rules.json"}]},
          "key": "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEA",
        }
        """, d =>
        {
            Directory.CreateDirectory(Path.Combine(d, "_locales", "en"));
            File.WriteAllText(Path.Combine(d, "_locales", "en", "messages.json"),
                """{"appName":{"message":"Tidy"},"hello":{"message":"Hi $who$, $1","placeholders":{"who":{"content":"$1"}}}}""");
        });
        try
        {
            var m = Manifest.Load(dir);
            Assert.Equal("Tidy", m.Name);
            Assert.Equal(3, m.Version);
            Assert.Equal("bg.js", m.BackgroundWorker);
            Assert.True(m.BackgroundIsModule);
            Assert.Equal("popup.html", m.Popup);
            Assert.Equal("opts.html", m.OptionsPage);
            Assert.True(m.Has("storage"));
            Assert.Single(m.ContentScripts);
            Assert.True(m.ContentScripts[0].AllFrames);
            Assert.Equal("document_start", m.ContentScripts[0].RunAt);
            Assert.Single(m.Rulesets);
            Assert.Equal(32, m.Id.Length);
            Assert.All(m.Id, c => Assert.InRange(c, 'a', 'p'));
            Assert.Equal("Hi Ann, Ann", m.Locale.Get("hello", new[] { "Ann" }));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadsMv2()
    {
        var dir = Folder("""{"manifest_version":2,"name":"Old","version":"1","background":{"scripts":["a.js","b.js"]},"browser_action":{"default_popup":"p.html"},"permissions":["tabs","https://*.x.com/*"],"web_accessible_resources":["f.png"]}""");
        try
        {
            var m = Manifest.Load(dir);
            Assert.Equal(new[] { "a.js", "b.js" }, m.BackgroundScripts);
            Assert.Equal("p.html", m.Popup);
            Assert.Contains("https://*.x.com/*", m.HostPermissions);
            Assert.Single(m.WebAccessible);
        }
        finally { Directory.Delete(dir, true); }
    }
}
