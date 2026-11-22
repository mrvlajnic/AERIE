using System.Reflection;

namespace AERIE.Core;

/// The JavaScript and CSS that ship inside the app.
public static class Scripts
{
    static string Read(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AERIE.Core.Scripts." + name)
            ?? throw new FileNotFoundException(name);
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    static readonly Lazy<string> reader = new(() => Read("reader.js"));
    static readonly Lazy<string> readerSheet = new(() => Read("reader.css"));
    public static readonly Lazy<string> Hider = new(() => Read("hider.js"));
    public static readonly Lazy<string> Float = new(() => Read("float.js"));
    public static readonly Lazy<string> Transport = new(() => Read("transport.js"));
    public static readonly Lazy<string> ExtensionApi = new(() => Read("ext-api.js"));

    /// Reading mode for the page, in `look` (light, dark or system).
    public static string Reader(string look) =>
        "(function(sheet, readerLook){\n" + reader.Value + "\n})(" + Shield.Json(readerSheet.Value) + "," + Shield.Json(look) + ");";

    /// The floating-video script, run with a user gesture so Chromium allows it.
    public static string FloatCall(string mode) => Float.Value.TrimEnd().TrimEnd(';') + "(" + Shield.Json(mode) + ")";

    /// Peeks one hidden thing back, outlined, or puts every one back to hidden.
    public static string Peek(string? selector) => selector is null
        ? "document.querySelectorAll('[data-office-peek]').forEach(function(e){e.style.removeProperty('display');e.style.removeProperty('outline');e.removeAttribute('data-office-peek')});"
        : "(function(){var e=document.querySelector(" + Shield.Json(selector) + ");if(!e)return;e.setAttribute('data-office-peek','');" +
          "e.style.setProperty('display','revert','important');e.style.setProperty('outline','2px solid #f59e0b','important');e.scrollIntoView({block:'center'});})();";
}
