using System.IO.Compression;
using Search.Core;
using Xunit;

public class AddressTests
{
    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("example.com/a?b=1", "https://example.com/a?b=1")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("192.168.1.10", "http://192.168.1.10/")]
    [InlineData("10.0.0.2:8080/x", "http://10.0.0.2:8080/x")]
    [InlineData("printer.local", "http://printer.local/")]
    [InlineData("https://x.org/p", "https://x.org/p")]
    [InlineData("about:blank", "about:blank")]
    public void Places(string typed, string expected) => Assert.Equal(expected, Address.Url(typed)?.AbsoluteUri);

    [Theory]
    [InlineData("hello world")]
    [InlineData("todo")]
    [InlineData("1.2.3")]
    [InlineData("me@example.com")]
    [InlineData("mailto://x")]
    [InlineData("note:to-self")]
    [InlineData("search:cats")]
    [InlineData("")]
    public void NotPlaces(string typed) => Assert.Null(Address.Url(typed));

    [Theory]
    [InlineData("[::1]", "http://[::1]/")]
    [InlineData("[::1]:3000", "http://[::1]:3000/")]
    [InlineData("[fe80::1]", "http://[fe80::1]/")]
    [InlineData("[2001:db8::1]", "https://[2001:db8::1]/")]
    public void IpV6(string typed, string expected) => Assert.Equal(expected, Address.Url(typed)?.AbsoluteUri);

    [Fact]
    public void FileUrlMayContainSpaces() =>
        Assert.Equal("file", Address.Url("file:///C:/My Documents/a.html")?.Scheme);

    [Fact]
    public void ZeroIsLocalhost() =>
        Assert.Equal("http://localhost:5173/", Address.Reachable(new Uri("http://0.0.0.0:5173/")).AbsoluteUri);

    [Theory]
    [InlineData("https://example.com/", "example.com")]
    [InlineData("http://example.com/", "http://example.com/")]
    [InlineData("https://example.com/a", "example.com/a")]
    [InlineData("http://localhost:3000/", "localhost:3000")]
    public void Editable(string page, string expected) => Assert.Equal(expected, Address.Editable(new Uri(page)));

    [Theory]
    [InlineData("www.bbc.co.uk", "bbc.co.uk")]
    [InlineData("a.b.example.com", "example.com")]
    [InlineData("me.github.io", "me.github.io")]
    [InlineData("example.com", "example.com")]
    public void Registrable(string host, string expected) => Assert.Equal(expected, Address.Registrable(host));

    [Fact]
    public void Pretty() => Assert.Equal("example.com/docs", Address.Pretty(new Uri("https://www.example.com/docs")));

    [Fact]
    public void PrettyDecodesThePath() =>
        Assert.Equal("example.com/a b", Address.Pretty(new Uri("https://www.example.com/a%20b")));
}

public class EngineTests
{
    [Fact]
    public void Searches() =>
        Assert.Equal("https://duckduckgo.com/?q=caf%C3%A9%20%26%20cr%C3%A8me",
            Engine.Url("café & crème", Engine.Template("duckduckgo", ""))!.AbsoluteUri);

    [Fact]
    public void CustomFromAnotherBrowser()
    {
        Assert.Equal("https://search.example/?q=%s", Engine.Template("custom", "search.example/?q={searchTerms}"));
        Assert.Equal("search.example", Engine.Name("custom", "search.example/?q={searchTerms}"));
    }

    [Fact]
    public void BadCustomFallsBack()
    {
        var fallback = Engine.Template("google", "");
        Assert.Equal(fallback, Engine.Template("custom", "no placeholder"));
        Assert.Equal("https://www.google.com/search?q=github", Engine.Url("github", fallback)!.AbsoluteUri);
    }
}

public class ShieldTests
{
    static readonly Uri Page = new("https://news.example.com/story");

    [Theory]
    [InlineData("https://securepubads.g.doubleclick.net/tag/js/gpt.js", true)]
    [InlineData("https://www.google-analytics.com/analytics.js", true)]
    [InlineData("https://cdn.example.com/app.js", false)]
    [InlineData("https://notdoubleclick.net/x.js", false)]
    public void ThirdParties(string url, bool blocked) => Assert.Equal(blocked, Shield.ShouldBlock(new Uri(url), Page));

    [Fact]
    public void FirstPartyIsTheSite() =>
        Assert.False(Shield.ShouldBlock(new Uri("https://www.taboola.com/about.js"), new Uri("https://taboola.com/")));

    [Fact]
    public void CosmeticScriptListsPaused()
    {
        var script = Shield.CosmeticScript(new[] { "example.org" });
        Assert.Contains("\"example.org\"", script);
        Assert.Contains("off.indexOf(h)>=0", script);
    }

    [Fact]
    public void CosmeticScriptKeepsOneRulePerSelector()
    {
        var script = Shield.CosmeticScript(Array.Empty<string>());
        Assert.Contains(".adsbygoogle{display:none !important}", script);
        Assert.Contains("ins.adsbygoogle{display:none !important}", script);
        Assert.DoesNotContain(".adsbygoogle, ins.adsbygoogle", script);
    }
}



public class CleanLinkTests
{
    [Theory]
    [InlineData("https://e.com/a?utm_source=x&id=3&fbclid=y#top", "https://e.com/a?id=3#top")]
    [InlineData("https://e.com/a?utm_source=x", "https://e.com/a")]
    [InlineData("https://youtu.be/abc?si=who", "https://youtu.be/abc")]
    [InlineData("https://e.com/a?si=keep", "https://e.com/a?si=keep")]
    [InlineData("https://b.s3.amazonaws.com/f?utm_source=x&X-Amz-Signature=1", "https://b.s3.amazonaws.com/f?utm_source=x&X-Amz-Signature=1")]
    [InlineData("https://e.com:8443/a?gclid=1", "https://e.com:8443/a")]
    public void Cleans(string from, string to) => Assert.Equal(to, CleanLink.Clean(from));
}

public class CrxTests
{
    [Theory]
    [InlineData("https://chromewebstore.google.com/detail/ublock-origin-lite/ddkjiahejlhfcafbddmgiahcphecmpfh", "ddkjiahejlhfcafbddmgiahcphecmpfh")]
    [InlineData("https://chrome.google.com/webstore/detail/x/cjpalhdlnbpafiamejdnhcphjbkeiagm?hl=en", "cjpalhdlnbpafiamejdnhcphjbkeiagm")]
    [InlineData("ddkjiahejlhfcafbddmgiahcphecmpfh", "ddkjiahejlhfcafbddmgiahcphecmpfh")]
    [InlineData("https://example.com/ddkjiahejlhfcafbddmgiahcphecmpfh", null)]
    public void Ids(string text, string? id) => Assert.Equal(id, Crx.IdFrom(text));

    static byte[] Zip()
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open()))
                w.Write("{\"manifest_version\":3,\"name\":\"T\",\"action\":{\"default_popup\":\"/popup.html\"}}");
            z.CreateEntry("_metadata/verified_contents.json").Open().Dispose();
            z.CreateEntry("../escape.txt").Open().Dispose();
        }
        return ms.ToArray();
    }

    [Fact]
    public void UnpacksCrx3()
    {
        var zip = Zip();
        var header = new byte[] { 1, 2, 3, 4, 5 };
        var crx = new List<byte>();
        crx.AddRange("Cr24"u8.ToArray());
        crx.AddRange(BitConverter.GetBytes(3u));
        crx.AddRange(BitConverter.GetBytes((uint)header.Length));
        crx.AddRange(header);
        crx.AddRange(zip);
        var root = Path.Combine(Path.GetTempPath(), "crxtest-" + Guid.NewGuid().ToString("N"));
        var dir = Path.Combine(root, "ext");
        try
        {
            Crx.Unpack(crx.ToArray(), dir);
            Assert.True(File.Exists(Path.Combine(dir, "manifest.json")));
            Assert.False(Directory.Exists(Path.Combine(dir, "_metadata")));
            Assert.False(File.Exists(Path.Combine(root, "escape.txt")));
            Assert.Equal("popup.html", Crx.Popup(dir));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Crx2Offset()
    {
        var data = new byte[16 + 3 + 4];
        "Cr24"u8.CopyTo(data);
        BitConverter.GetBytes(2u).CopyTo(data, 4);
        BitConverter.GetBytes(3u).CopyTo(data, 8);
        BitConverter.GetBytes(4u).CopyTo(data, 12);
        Assert.Equal(23, Crx.ZipOffset(data));
    }

    [Fact]
    public void RejectsOther() => Assert.Throws<InvalidDataException>(() => Crx.ZipOffset(new byte[20]));
}

public class HistoryTests
{
    static readonly DateTime Now = new(2026, 11, 1);

    [Fact]
    public void FinishesHosts()
    {
        var h = new History();
        h.Record("https://github.com/x/y", "Y", Now);
        h.Record("https://www.gitlab.com/", "GL", Now.AddDays(-30));
        Assert.Equal("hub.com", h.Complete("git", Now));
        Assert.Equal("x", h.Complete("github.com/", Now));
        Assert.Null(h.Complete("zzz", Now));
    }

    [Fact]
    public void SuggestsByTitleToo()
    {
        var h = new History();
        h.Record("https://a.example/", "Quarterly report", Now);
        Assert.Single(h.Suggest("quarterly", 5, Now));
    }

    [Fact]
    public void Caps()
    {
        var h = new History();
        for (int i = 0; i < History.Cap + 5; i++) h.Visits.Add(new Visit { Url = "https://e.com/" + i });
        h.Record("https://e.com/new", "", Now);
        Assert.Equal(History.Cap, h.Visits.Count);
    }
}

public class HiddenTests
{
    [Fact]
    public void PerSite()
    {
        var hidden = new Hidden();
        hidden.Add("www.example.com", new Veil { Selector = "#banner", Label = "Banner" });
        hidden.Add("example.com", new Veil { Selector = "#banner", Label = "Banner" });
        Assert.Single(hidden.On("example.com"));
        Assert.Contains("#banner", hidden.Script());
        hidden.Remove("example.com", "#banner");
        Assert.Empty(hidden.On("example.com"));
    }
}

public class FuzzyTests
{
    [Fact]
    public void Ranks()
    {
        Assert.True(Fuzzy.Score("gml", "Gmail - Inbox") > 0);
        Assert.Equal(0, Fuzzy.Score("xyz", "Gmail"));
        Assert.True(Fuzzy.Score("in", "Inbox") > Fuzzy.Score("in", "Gmail - Inbox"));
    }
}

public class StoreTests
{
    [Fact]
    public void RoundTrips()
    {
        var old = Store.Folder;
        Store.Folder = Path.Combine(Path.GetTempPath(), "storetest-" + Guid.NewGuid().ToString("N"));
        try
        {
            var s = new Session { Spaces = { new SavedSpace { Name = "Work", Tabs = { new SavedTab { Url = "https://e.com/" } } } } };
            Store.Save("session.json", s);
            var back = Store.Load<Session>("session.json");
            Assert.Equal("Work", back.Spaces[0].Name);
            Assert.Equal("https://e.com/", back.Spaces[0].Tabs[0].Url);
            File.WriteAllText(Path.Combine(Store.Folder, "settings.json"), "{ not json");
            Assert.True(Store.Load<Settings>("settings.json").ShieldOn);
        }
        finally { Directory.Delete(Store.Folder, true); Store.Folder = old; }
    }
}

public class ScriptTests
{
    [Fact]
    public void Embedded()
    {
        Assert.Contains("office-reader", Scripts.Reader("light"));
        Assert.Contains("requestPictureInPicture", Scripts.FloatCall("toggle"));
        Assert.Contains("postMessage", Scripts.Hider.Value);
    }
}
