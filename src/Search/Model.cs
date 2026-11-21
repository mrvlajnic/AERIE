using System.Windows.Media;
using Search.Core;

namespace Search;

/// One tab: what it shows and, while it is awake, the page itself. A tab
/// that has slept, or came back from the last session, is just an address
/// and a title until it is clicked.
sealed class Tab
{
    public Tab(Space space, string url) { Space = space; Url = url; }

    public Space Space { get; set; }
    public string Url { get; set; }
    public string Title { get; set; } = "";
    public string? Name { get; set; }
    public string? Home { get; set; }          // a pin's own address
    public bool Private { get; init; }
    public bool Pinned => Home != null;
    public ImageSource? Icon { get; set; }
    public bool Loading { get; set; }
    public bool Reading { get; set; }
    public bool Audible { get; set; }
    public bool Muted { get; set; }
    public DateTime Seen { get; set; } = DateTime.Now;
    /// When it was last hidden: frozen a little after.
    public DateTime Left { get; set; } = DateTime.Now;
    /// Already sent to Freeze(); set once per rest so the sleep timer does not
    /// repeat a context-wide GC and cache purge every tick.
    public bool Frozen { get; set; }
    public IPage? Page { get; set; }
    public bool Awake => Page != null;

    public string Host => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.Host : "";

    public string Display
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name)) return Name!;
            if (!string.IsNullOrWhiteSpace(Title)) return Title;
            if (Url.Length == 0 || Url == "about:blank") return "New Tab";
            return Uri.TryCreate(Url, UriKind.Absolute, out var u) ? Address.Pretty(u) : Url;
        }
    }

    public string Letter
    {
        get
        {
            var h = Address.WithoutWww(Host);
            var s = h.Length > 0 ? h : Display;
            return s.Length > 0 ? char.ToUpperInvariant(s[0]).ToString() : "·";
        }
    }

    public SavedTab Save() => new()
    {
        Url = Pinned && !Awake ? Home! : Url, Title = Title, Name = Name, Home = Home,
    };
}

/// A set of tabs of its own: work, home. A fresh space keeps its own
/// cookies and sign-ins, in a profile of its own.
sealed class Space
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "Home";
    public string Icon { get; set; } = "";
    public bool Fresh { get; init; }
    public List<Tab> Pins { get; } = new();
    public List<Tab> Tabs { get; } = new();
    public Tab? Selected { get; set; }

    public string Profile => Fresh ? "space-" + Id : "Default";
    public IEnumerable<Tab> All => Pins.Concat(Tabs);
    public string Mark => Icon.Length > 0 ? Icon : (Name.Length > 0 ? char.ToUpperInvariant(Name[0]).ToString() : "·");

    public SavedSpace Save()
    {
        var pins = Pins.Select(t => t.Save()).ToList();
        var tabs = Tabs.Where(t => !t.Private).Select(t => t.Save()).ToList();
        int sel = -1;
        if (Selected != null)
        {
            sel = Pins.IndexOf(Selected);
            if (sel < 0)
            {
                var i = Tabs.Where(t => !t.Private).ToList().IndexOf(Selected);
                if (i >= 0) sel = Pins.Count + i;
            }
        }
        return new SavedSpace { Id = Id, Name = Name, Icon = Icon, Fresh = Fresh, Pins = pins, Tabs = tabs, Selected = sel };
    }

    public static Space From(SavedSpace s)
    {
        var space = new Space { Id = s.Id, Name = s.Name, Icon = s.Icon, Fresh = s.Fresh };
        foreach (var p in s.Pins)
            space.Pins.Add(new Tab(space, p.Home ?? p.Url) { Title = p.Title ?? "", Name = p.Name, Home = p.Home ?? p.Url });
        foreach (var t in s.Tabs)
            space.Tabs.Add(new Tab(space, t.Url) { Title = t.Title ?? "", Name = t.Name });
        var all = space.All.ToList();
        if (s.Selected >= 0 && s.Selected < all.Count) space.Selected = all[s.Selected];
        return space;
    }
}
