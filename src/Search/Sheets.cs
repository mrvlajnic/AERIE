using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Search.Core;

namespace Search;

/// A panel that floats over the top of the page and goes away when you
/// click anywhere else or press Esc. Its own little window, so it can sit
/// above the page (which is a window of its own) and take the keyboard.
abstract class Sheet : Window
{
    protected readonly BrowserWindow Browser;
    readonly double width;
    bool closing;

    protected Sheet(BrowserWindow owner, double width)
    {
        Browser = owner;
        this.width = width;
        Owner = owner;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = width + 36;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        Deactivated += (_, _) => Dismiss();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
    }

    protected abstract UIElement Build();

    public void Present()
    {
        Content = Ui.Card(Build());
        var at = Browser.Anchor(width + 36);
        Left = at.X; Top = at.Y - 18;
        Show();
        Activate();
        Focused()?.Focus();
    }

    protected virtual IInputElement? Focused() => null;

    protected void Rebuild() => Content = Ui.Card(Build());

    public void Dismiss()
    {
        if (closing) return;
        closing = true;
        Dispatcher.BeginInvoke(() => { try { Close(); } catch { } Browser.Current?.Page?.Focus(); });
    }

    /// A row in a list: an icon, a title, a line under it; grey when chosen.
    protected static Border ListRow(UIElement icon, string title, string? sub, bool chosen, Action act, UIElement? trailing = null)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(icon);
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(Ui.Text(title, 13.5));
        if (!string.IsNullOrEmpty(sub)) words.Children.Add(Ui.Text(sub!, 11.5, Theme.Muted));
        Grid.SetColumn(words, 1);
        g.Children.Add(words);
        if (trailing != null) { Grid.SetColumn(trailing, 2); g.Children.Add(trailing); }
        var row = Ui.Button(g, act, pad: new Thickness(10, 7, 10, 7), rest: chosen ? Theme.Wash : Brushes.Transparent, radius: 7);
        return row;
    }

    protected static UIElement Caption(string text)
    {
        var t = Ui.Text(text.ToUpperInvariant(), 10.5, Theme.Muted, FontWeights.SemiBold);
        t.Margin = new Thickness(10, 4, 0, 6);
        return t;
    }
}

/// The one field: an address goes there, words get searched. It finishes
/// addresses from your own history, and sends nothing anywhere until Enter.
sealed class Omnibox : Sheet
{
    readonly bool newTab, priv;
    readonly TextBox box = Ui.Field("", 16);
    readonly StackPanel list = new();
    readonly List<(UIElement Icon, string Title, string? Sub, Action Go)> rows = new();
    int chosen;
    string typed = "";
    bool quiet;

    public Omnibox(BrowserWindow owner, bool newTab, bool priv) : base(owner, 600)
    {
        this.newTab = newTab; this.priv = priv;
        var here = owner.Current;
        if (!newTab && here != null && Uri.TryCreate(here.Url, UriKind.Absolute, out var u) && here.Url != "about:blank")
            box.Text = Address.Editable(u);
        typed = box.Text;
        box.TextChanged += (_, _) => Changed();
        box.PreviewKeyDown += Keys;
    }

    protected override IInputElement Focused() { box.SelectAll(); return box; }

    protected override UIElement Build()
    {
        var head = new DockPanel { Margin = new Thickness(14, 10, 14, 10) };
        var glyph = Ui.Icon(priv ? Ui.Glyph.Private : Ui.Glyph.Search, 14);
        glyph.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(glyph, Dock.Left);
        head.Children.Add(glyph);
        head.Children.Add(Ui.Placeheld(box, priv ? "Private tab — search or enter address" : "Search or enter address"));
        var panel = new StackPanel();
        panel.Children.Add(head);
        list.Margin = new Thickness(6, 0, 6, 6);
        panel.Children.Add(list);
        Fill();
        return panel;
    }

    void Changed()
    {
        if (quiet) return;
        var text = box.Text;
        // Finishing the address: only while typing forward at the end.
        bool forward = text.Length > typed.Length && text.StartsWith(typed, StringComparison.OrdinalIgnoreCase) && box.CaretIndex == text.Length;
        typed = text;
        if (forward && !priv)
        {
            var rest = Browser.History.Complete(text, DateTime.Now);
            if (!string.IsNullOrEmpty(rest))
            {
                quiet = true;
                box.Text = text + rest;
                box.Select(text.Length, rest.Length);
                quiet = false;
            }
        }
        chosen = 0;
        Fill();
    }

    void Fill()
    {
        rows.Clear();
        var text = box.Text.Trim();
        var bare = typed.Trim();
        if (text.Length > 0)
        {
            var engine = Engine.Name(Browser.Settings.Engine, Browser.Settings.CustomEngine);
            var asAddress = Address.File(text) ?? Address.Url(text);
            if (asAddress != null)
                rows.Add((Ui.Icon(Ui.Glyph.Globe, 13), text, "Go to this address", () => Commit(text)));
            var words = asAddress != null && bare.Length > 0 && bare != text ? bare : text;
            rows.Add((Ui.Icon(Ui.Glyph.Search, 13), words, "Search " + engine, () => Commit(Engine.Url(words, Browser.SearchTemplate)!.AbsoluteUri)));

            if (!priv)
            {
                var seen = new HashSet<string>();
                foreach (var t in Browser.Spaces.SelectMany(s => s.All).Where(t => t != Browser.Current)
                             .Select(t => (t, score: Fuzzy.Score(bare, t.Display + " " + t.Url))).Where(x => x.score > 20)
                             .OrderByDescending(x => x.score).Take(3).Select(x => x.t))
                {
                    var tab = t;
                    seen.Add(t.Url);
                    rows.Add((Ui.Icon(Ui.Glyph.Right, 11), t.Display, "Switch to tab" + (t.Space != Browser.Space ? " · " + t.Space.Name : ""),
                        () => { Dismiss(); _ = Browser.Select(tab); }));
                }
                foreach (var v in Browser.History.Suggest(bare.Length > 0 ? bare : text, 6, DateTime.Now))
                {
                    if (!seen.Add(v.Url)) continue;
                    var url = v.Url;
                    rows.Add((Ui.Icon(Ui.Glyph.History, 12), string.IsNullOrWhiteSpace(v.Title) ? v.Url : v.Title, v.Url, () => Commit(url)));
                }
            }
        }
        list.Children.Clear();
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            int index = i;
            var row = ListRow(r.Icon, r.Title, r.Sub, i == chosen, () => { chosen = index; rows[index].Go(); });
            list.Children.Add(row);
        }
    }

    void Commit(string urlOrText)
    {
        Dismiss();
        var url = urlOrText.Contains("://") || urlOrText.StartsWith("about:") ? urlOrText : Browser.Resolve(urlOrText);
        if (priv) Browser.Open(url, priv: true);
        else if (newTab || Browser.Current == null) Browser.Open(url);
        else Browser.Navigate(Browser.Current, url);
    }

    void Keys(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: if (rows.Count > 0) { chosen = (chosen + 1) % rows.Count; Fill(); } e.Handled = true; break;
            case Key.Up: if (rows.Count > 0) { chosen = (chosen - 1 + rows.Count) % rows.Count; Fill(); } e.Handled = true; break;
            case Key.Enter:
                e.Handled = true;
                if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && !box.Text.Contains('.'))
                { Commit("https://www." + box.Text.Trim() + ".com/"); break; }   // Ctrl+Enter: www.…com
                if (rows.Count > 0) rows[Math.Clamp(chosen, 0, rows.Count - 1)].Go();
                break;
            case Key.Tab:
                if (box.SelectionLength > 0) { box.CaretIndex = box.Text.Length; typed = box.Text; e.Handled = true; }
                break;
            case Key.Back: case Key.Delete:
                if (box.SelectionLength > 0 && box.SelectionStart + box.SelectionLength == box.Text.Length && box.SelectionStart == typed.Length)
                {
                    // Take away the finished part only.
                    quiet = true; box.Text = typed; box.CaretIndex = typed.Length; quiet = false;
                    Fill(); e.Handled = true;
                }
                break;
        }
    }
}

/// Ctrl+K: every open tab by name.
sealed class Switcher : Sheet
{
    readonly TextBox box = Ui.Field("", 15);
    readonly StackPanel list = new();
    List<Tab> found = new();
    int chosen;

    public Switcher(BrowserWindow owner) : base(owner, 520)
    {
        box.TextChanged += (_, _) => { chosen = 0; Fill(); };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Down) { chosen = Math.Min(chosen + 1, found.Count - 1); Fill(); e.Handled = true; }
            else if (e.Key == Key.Up) { chosen = Math.Max(chosen - 1, 0); Fill(); e.Handled = true; }
            else if (e.Key == Key.Enter && found.Count > 0) { Go(found[chosen]); e.Handled = true; }
            else if (e.Key == Key.K && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Dismiss(); e.Handled = true; }
        };
    }

    protected override IInputElement Focused() => box;

    protected override UIElement Build()
    {
        var head = new DockPanel { Margin = new Thickness(14, 10, 14, 8) };
        head.Children.Add(Ui.Placeheld(box, "Search open tabs"));
        var p = new StackPanel();
        p.Children.Add(head);
        list.Margin = new Thickness(6, 0, 6, 6);
        p.Children.Add(new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Fill();
        return p;
    }

    void Fill()
    {
        var q = box.Text;
        found = Browser.Spaces.SelectMany(s => s.All)
            .Select(t => (t, s: Fuzzy.Score(q, t.Display + " " + Address.WithoutWww(t.Host))))
            .Where(x => x.s > 0)
            .OrderByDescending(x => q.Length == 0 ? 0 : x.s).ThenBy(x => x.t.Space == Browser.Space ? 0 : 1).ThenByDescending(x => x.t.Seen)
            .Select(x => x.t).Take(40).ToList();
        list.Children.Clear();
        for (int i = 0; i < found.Count; i++)
        {
            var t = found[i];
            var sub = Address.WithoutWww(t.Host) + (t.Space != Browser.Space ? " · " + t.Space.Name : "") + (t.Pinned ? " · pinned" : "");
            list.Children.Add(ListRow(Ui.Icon(t.Pinned ? Ui.Glyph.Pin : Ui.Glyph.Globe, 12), t.Display, sub, i == chosen, () => Go(t)));
        }
        if (found.Count == 0) list.Children.Add(Nothing("No tab by that name"));
    }

    void Go(Tab t) { Dismiss(); _ = Browser.Select(t); }

    internal static UIElement Nothing(string s) { var t = Ui.Text(s, 13, Theme.Muted); t.Margin = new Thickness(12, 8, 12, 12); return t; }
}

/// Ctrl+H: where you've been, searchable.
sealed class HistoryPanel : Sheet
{
    readonly TextBox box = Ui.Field("", 14);
    readonly StackPanel list = new();

    public HistoryPanel(BrowserWindow owner) : base(owner, 560) { box.TextChanged += (_, _) => Fill(); }
    protected override IInputElement Focused() => box;

    protected override UIElement Build()
    {
        var p = new StackPanel();
        var head = new DockPanel { Margin = new Thickness(14, 10, 14, 8) };
        var clear = Ui.Pill("Clear History…", () =>
        {
            Dismiss();
            if (MessageBox.Show(Browser, "Forget every page you have visited?", "Search", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            Browser.History.Visits.Clear();
            Store.Save("history.json", Browser.History);
        });
        DockPanel.SetDock(clear, Dock.Right);
        head.Children.Add(clear);
        head.Children.Add(Ui.Placeheld(box, "Search history"));
        p.Children.Add(head);
        list.Margin = new Thickness(6, 0, 6, 6);
        p.Children.Add(new ScrollViewer { Content = list, MaxHeight = 480, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Fill();
        return p;
    }

    void Fill()
    {
        list.Children.Clear();
        var q = box.Text.Trim();
        IEnumerable<Visit> visits = Enumerable.Reverse(Browser.History.Visits);
        if (q.Length > 0) visits = visits.Where(v => v.Title.Contains(q, StringComparison.OrdinalIgnoreCase) || v.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
        string? day = null;
        foreach (var v in visits.Take(250))
        {
            var d = v.Last.Date == DateTime.Today ? "Today" : v.Last.Date == DateTime.Today.AddDays(-1) ? "Yesterday" : v.Last.ToString("dddd d MMMM");
            if (d != day) { list.Children.Add(Caption(d)); day = d; }
            var visit = v;
            var forget = Ui.Button(Ui.Icon(Ui.Glyph.Close, 9), () =>
            {
                Browser.History.Forget(x => x == visit);
                Store.Save("history.json", Browser.History);
                Fill();
            }, "Forget", new Thickness(5));
            list.Children.Add(ListRow(Ui.Text(v.Last.ToString("HH:mm"), 11, Theme.Muted), string.IsNullOrWhiteSpace(v.Title) ? v.Url : v.Title, v.Url, false,
                () => { Dismiss(); Browser.Open(visit.Url); }, forget));
        }
        if (list.Children.Count == 0) list.Children.Add(Switcher.Nothing(q.Length > 0 ? "Nothing by that name" : "Nothing yet"));
    }
}

/// Ctrl+Shift+U: what has been taken off this site. Resting on a line puts
/// that thing back, outlined, so you choose by looking rather than reading
/// a selector.
sealed class HiddenPanel : Sheet
{
    readonly string host;
    public HiddenPanel(BrowserWindow owner, string host) : base(owner, 420)
    {
        this.host = host;
        MouseLeave += (_, _) => _ = Browser.Peek(null);
        Closed += (_, _) => _ = Browser.Peek(null);
    }

    protected override UIElement Build()
    {
        var p = new StackPanel { Margin = new Thickness(8) };
        var title = Ui.Text(Address.WithoutWww(host), 14, weight: FontWeights.SemiBold);
        title.Margin = new Thickness(10, 4, 10, 6);
        p.Children.Add(title);
        var veils = Browser.Hidden.On(host);
        if (veils.Count == 0) p.Children.Add(Switcher.Nothing("Nothing is hidden here."));
        else
        {
            p.Children.Add(Caption("Hidden on this site — rest on a line to see it"));
            foreach (var v in veils.ToList())
            {
                var veil = v;
                var restore = Ui.Pill("Restore", async () => { await Browser.Restore(host, veil.Selector); Rebuild(); });
                var row = ListRow(Ui.Icon(Ui.Glyph.Hide, 12), v.Label, v.Selector, false, () => { }, restore);
                row.MouseEnter += (_, _) => _ = Browser.Peek(veil.Selector);
                row.MouseLeave += (_, _) => _ = Browser.Peek(null);
                p.Children.Add(row);
            }
        }
        var foot = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 10, 8, 4) };
        foot.Children.Add(Ui.Pill("Hide something…", () => { Dismiss(); _ = Browser.ToggleHiding(); }, filled: true));
        if (veils.Count > 0)
        {
            var all = Ui.Pill("Restore all", async () => { await Browser.Restore(host, null); Rebuild(); });
            all.Margin = new Thickness(8, 0, 0, 0);
            foot.Children.Add(all);
        }
        p.Children.Add(foot);
        return p;
    }
}

/// The lock beside the address: who you're talking to and the switches
/// that belong to this one site.
sealed class SiteCard : Sheet
{
    readonly Tab tab;
    public SiteCard(BrowserWindow owner, Tab tab) : base(owner, 360) { this.tab = tab; }

    protected override UIElement Build()
    {
        var p = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        Uri.TryCreate(tab.Url, UriKind.Absolute, out var u);
        var host = u?.Host ?? "";
        p.Children.Add(Ui.Text(host.Length > 0 ? Address.WithoutWww(host) : tab.Display, 15, weight: FontWeights.SemiBold));
        bool secure = u?.Scheme == "https";
        var line = Ui.Text(tab.Private ? "Private tab · nothing is remembered" : secure ? "Connection is encrypted" : "Connection is not encrypted — others on the network can read it",
            12, secure || tab.Private ? Theme.Safe : Theme.Unsafe);
        line.TextWrapping = TextWrapping.Wrap;
        line.Margin = new Thickness(0, 4, 0, 12);
        p.Children.Add(line);
        if (host.Length > 0)
        {
            bool blocking = Browser.Settings.ShieldOn && !Browser.Paused(host);
            p.Children.Add(Toggle("Block ads and trackers here", blocking, Browser.Settings.ShieldOn, async on =>
            {
                var key = Hidden.Key(host);
                Browser.Settings.ShieldPaused.Remove(key);
                Browser.Settings.ShieldPaused.Remove(host);
                if (!on) Browser.Settings.ShieldPaused.Add(key);
                Browser.SaveSettings();
                await Browser.RefreshShield();
                Browser.Reload();
            }));
            var n = Browser.Hidden.On(host).Count;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            row.Children.Add(Ui.Pill(n == 0 ? "Hide something…" : $"Hidden here ({n})…", () =>
            {
                Dismiss();
                if (n == 0) _ = Browser.ToggleHiding(); else new HiddenPanel(Browser, host).Present();
            }));
            var copy = Ui.Pill("Copy Clean Link", () => { Browser.CopyCleanLink(); Dismiss(); });
            copy.Margin = new Thickness(8, 0, 0, 0);
            row.Children.Add(copy);
            p.Children.Add(row);
        }
        return p;
    }

    internal static UIElement Toggle(string label, bool on, bool enabled, Action<bool> changed)
    {
        var d = new DockPanel { Margin = new Thickness(0, 4, 0, 4), Opacity = enabled ? 1 : 0.5 };
        var knob = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(8), Background = Brushes.White, HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(2) };
        var track = new Border { Width = 36, Height = 20, CornerRadius = new CornerRadius(10), Background = on ? Theme.Ink : Theme.Faint, Child = knob, Cursor = Cursors.Arrow };
        if (enabled)
            track.MouseLeftButtonUp += (_, _) =>
            {
                on = !on;
                track.Background = on ? Theme.Ink : Theme.Faint;
                knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
                changed(on);
            };
        DockPanel.SetDock(track, Dock.Right);
        d.Children.Add(track);
        var t = Ui.Text(label, 13);
        t.TextWrapping = TextWrapping.Wrap;
        d.Children.Add(t);
        return d;
    }
}

/// The puzzle button: installed extensions, each opening its popup.
sealed class ExtensionsMenu : Sheet
{
    public ExtensionsMenu(BrowserWindow owner) : base(owner, 340) { }

    protected override UIElement Build()
    {
        var p = new StackPanel { Margin = new Thickness(8) };
        p.Children.Add(Caption("Extensions"));
        var here = Browser.Current?.Url ?? "";
        if (Crx.IdFrom(here) != null && !here.EndsWith("chromewebstore.google.com/"))
        {
            var add = Ui.Pill("Add This Extension to Search", async () =>
            {
                try { var name = await Browser.Engine.Extensions.InstallFromStore(here); Browser.Toast("Added " + name); }
                catch (Exception e) { Browser.Toast(e.Message); }
                Dismiss();
            }, filled: true);
            add.Margin = new Thickness(8, 0, 8, 8);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            p.Children.Add(add);
        }
        var all = Browser.Engine.Extensions.All;
        if (all.Count == 0)
            p.Children.Add(Switcher.Nothing("None yet. Open an extension's page in the Chrome Web Store and come back here, or add one in Settings."));
        foreach (var ext in all)
        {
            var e = ext;
            p.Children.Add(ListRow(Ui.Icon(Ui.Glyph.Puzzle, 12), e.Name, e.HasPopup ? null : "No popup — opens its options", false,
                () => { Dismiss(); Browser.Engine.Extensions.OpenPopup(Browser, e); }));
        }
        var manage = Ui.Pill("Manage in Settings…", () => { Dismiss(); Browser.Dispatcher.BeginInvoke(() => Browser.RaiseSettings()); });
        manage.Margin = new Thickness(8, 8, 8, 4);
        manage.HorizontalAlignment = HorizontalAlignment.Left;
        p.Children.Add(manage);
        return p;
    }
}
