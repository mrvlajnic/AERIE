namespace Search.Core;

public sealed class Veil
{
    public string Selector { get; set; } = "";
    public string Label { get; set; } = "";
}

/// What has been taken off each site, by host, and the script that keeps it
/// gone before the page draws a frame.
public sealed class Hidden
{
    public Dictionary<string, List<Veil>> Sites { get; set; } = new();

    public static string Key(string host) => Address.WithoutWww(host.ToLowerInvariant());

    public IReadOnlyList<Veil> On(string host) =>
        Sites.TryGetValue(Key(host), out var l) ? l : Array.Empty<Veil>();

    public void Add(string host, Veil veil)
    {
        var key = Key(host);
        if (!Sites.TryGetValue(key, out var list)) Sites[key] = list = new();
        if (!list.Any(v => v.Selector == veil.Selector)) list.Add(veil);
    }

    public void Remove(string host, string selector)
    {
        var key = Key(host);
        if (!Sites.TryGetValue(key, out var list)) return;
        list.RemoveAll(v => v.Selector == selector);
        if (list.Count == 0) Sites.Remove(key);
    }

    public void Clear(string host) => Sites.Remove(Key(host));

    /// Added to every document at creation: hides this site's selectors.
    public string Script()
    {
        var map = Sites.ToDictionary(k => k.Key, k => k.Value.Select(v => v.Selector).ToArray());
        var json = System.Text.Json.JsonSerializer.Serialize(map);
        return "(function(){try{" + Shield.Put + "var m=" + json + ";var h=location.hostname.toLowerCase().replace(/^www\\./,'');" +
               "var l=m[h];if(!l||!l.length)return;var s=document.getElementById('office-hidden')||document.createElement('style');" +
               "s.id='office-hidden';s.textContent=l.map(function(x){return x+'{display:none !important}'}).join('\\n');put(s);" +
               "}catch(e){}})();";
    }

    /// Applied at once to a page already open.
    public string Apply(string host)
    {
        var css = string.Join("\n", On(host).Select(v => v.Selector + "{display:none !important}"));
        return "(function(){var s=document.getElementById('office-hidden');if(!s){s=document.createElement('style');s.id='office-hidden';" +
               "(document.head||document.documentElement).appendChild(s);}s.textContent=" + Shield.Json(css) + ";})();";
    }
}
