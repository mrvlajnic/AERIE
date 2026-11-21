namespace Search.Core;

/// The ad blocker. On Windows it sits in WebView2's request interception:
/// a third-party request to a host whose only job is to watch or to sell is
/// answered with nothing before it leaves the machine. The lists are
/// Search's own (Shield.swift), deliberately short.
public static class Shield
{
    public static readonly string[] Unwanted =
    {
        "doubleclick.net", "googlesyndication.com", "googleadservices.com",
        "googletagservices.com", "google-analytics.com", "googletagmanager.com",
        "adservice.google.com", "amazon-adsystem.com", "adnxs.com", "adsrvr.org",
        "criteo.com", "criteo.net", "taboola.com", "outbrain.com",
        "rubiconproject.com", "pubmatic.com", "openx.net", "casalemedia.com",
        "smartadserver.com", "sharethrough.com", "indexww.com", "bidswitch.net",
        "33across.com", "teads.tv", "moatads.com", "adroll.com",
        "scorecardresearch.com", "quantserve.com", "chartbeat.com",
        "hotjar.com", "mouseflow.com", "fullstory.com", "clarity.ms",
        "mixpanel.com", "amplitude.com", "segment.com", "segment.io",
        "branch.io", "appsflyer.com", "adjust.com", "analytics.tiktok.com",
        "connect.facebook.net", "ads-twitter.com", "analytics.twitter.com",
    };

    /// Slots that are reliably an advertisement and nothing else.
    public static readonly string[] Slots =
    {
        ".adsbygoogle", "ins.adsbygoogle", "[id^=\"google_ads_\"]",
        "[id^=\"div-gpt-ad\"]", "[id^=\"taboola-\"]", "#taboola-below-article",
        "iframe[src*=\"doubleclick.net\"]", "iframe[src*=\"googlesyndication\"]",
        "iframe[src*=\"amazon-adsystem\"]",
        ".ad-slot", ".ad-slot-container", ".top-banner-ad-container",
        ".ad-leaderboard", ".ad-billboard", ".ad-giga", ".ad-mpu", ".ad-mrec",
        ".ad-unit", ".adunit", ".adslot", ".dfp-ad", ".gpt-ad", ".w_ad",
    };

    public static readonly (string[] Sites, string Selector)[] SlotsBySite =
    {
        (new[] { "as.com", "elpais.com" }, ".ad"),
        (new[] { "theguardian.com" }, ".top-fronts-banner-ad-container"),
        (new[] { "independent.co.uk", "the-independent.com" }, "#billboard-wrapper"),
        (new[] { "cnn.com" }, ".ad-slot-header__wrapper"),
    };

    static readonly HashSet<string> Exact = new(Unwanted, StringComparer.OrdinalIgnoreCase);

    public static bool IsUnwantedHost(string host)
    {
        host = host.ToLowerInvariant().TrimEnd('.');
        while (true)
        {
            if (Exact.Contains(host)) return true;
            int dot = host.IndexOf('.');
            if (dot < 0) return false;
            host = host[(dot + 1)..];
        }
    }

    /// Blocked when the request is to an unwanted host and is not the
    /// page's own: a site's own scripts are the site.
    public static bool ShouldBlock(Uri request, Uri? page)
    {
        if (request.Scheme != "http" && request.Scheme != "https") return false;
        if (!IsUnwantedHost(request.Host)) return false;
        if (page is null || string.IsNullOrEmpty(page.Host)) return true;
        return Address.Registrable(request.Host) != Address.Registrable(page.Host);
    }

    /// The script that hides the empty ad boxes, added to every document
    /// before it runs. `paused` lists the hosts it is off for.
    public static string CosmeticScript(IEnumerable<string> paused)
    {
        var bySite = string.Join(",", SlotsBySite.Select(s =>
            $"[{Json(string.Join(",", s.Sites))},{Json(s.Selector)}]"));
        var off = string.Join(",", paused.Select(Json));
        // One rule per selector: a selector Chromium no longer understands
        // drops only itself, not the whole cosmetic list.
        var slotCss = string.Join("\n", Slots.Select(s => s + "{display:none !important}"));
        return
            "(function(){try{" + Put + "var h=location.hostname.replace(/^www\\./,'');" +
            $"var off=[{off}];if(off.indexOf(h)>=0||off.indexOf(location.hostname)>=0)return;" +
            $"var css={Json(slotCss)};" +
            $"var by=[{bySite}];" +
            "by.forEach(function(e){if(e[0].split(',').some(function(d){return h===d||h.endsWith('.'+d)}))css+='\\n'+e[1]+'{display:none !important}'});" +
            "var s=document.createElement('style');s.id='office-shield';s.textContent=css;put(s);" +
            "}catch(e){}})();";
    }

    /// Puts a style in the page as early as the page exists: scripts added
    /// at document creation run before there is even an <html> element.
    public const string Put = "function put(s){var r=document.head||document.documentElement;if(r){r.appendChild(s);}else{new MutationObserver(function(m,o){if(document.documentElement){o.disconnect();document.documentElement.appendChild(s);}}).observe(document,{childList:true});}document.addEventListener('DOMContentLoaded',function(){if(!s.isConnected)(document.head||document.documentElement).appendChild(s)});}";

    public static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);
}
