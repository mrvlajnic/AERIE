using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AERIE.Core;

namespace AERIE;

/// A small window in the app's own colours.
class Plain : Window
{
    public Plain(Window owner, string title, double width)
    {
        Owner = owner;
        Title = title;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Background = Theme.Ground;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        SourceInitialized += (_, _) => Native.PaintFrame(this);
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    protected static Border Boxed(TextBox box) => new()
    {
        Child = box, BorderBrush = Theme.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 6, 0, 0),
    };
}

static class Prompt
{
    public static string? Ask(Window owner, string question, string initial = "")
    {
        string? answer = null;
        var w = new Plain(owner, "AERIE", 380);
        var box = Ui.Field("", 14);
        box.Text = initial;
        var p = new StackPanel { Margin = new Thickness(18) };
        p.Children.Add(Ui.Text(question, 13.5));
        p.Children.Add(new Border
        {
            Child = box, BorderBrush = Theme.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 10, 0, 14),
        });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = Ui.Pill("Cancel", () => w.Close());
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        buttons.Children.Add(Ui.Pill("OK", () => { answer = box.Text; w.Close(); }, filled: true));
        p.Children.Add(buttons);
        box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { answer = box.Text; w.Close(); } };
        w.Content = p;
        w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        w.ShowDialog();
        return answer;
    }
}

/// A new space: a name, a mark, and whether it shares your sign-ins.
static class NewSpaceDialog
{
    public static Space? Ask(BrowserWindow owner)
    {
        Space? made = null;
        var w = new Plain(owner, "New Space", 420);
        var name = Ui.Field("", 14);
        var icon = Ui.Field("", 14);
        icon.MaxLength = 4;
        bool fresh = false;
        var p = new StackPanel { Margin = new Thickness(20) };
        p.Children.Add(Ui.Text("Name", 12, Theme.Muted));
        p.Children.Add(new Border { Child = Ui.Placeheld(name, "Work, Home, Side project…"), BorderBrush = Theme.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 6, 0, 12) });
        p.Children.Add(Ui.Text("Icon — an emoji or a letter (optional)", 12, Theme.Muted));
        p.Children.Add(new Border { Child = icon, BorderBrush = Theme.Faint, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 6, 0, 14), Width = 80, HorizontalAlignment = HorizontalAlignment.Left });

        var choice = new StackPanel();
        void Draw()
        {
            choice.Children.Clear();
            choice.Children.Add(Option("Signed in", "Shares cookies and sign-ins with your other signed-in spaces.", !fresh, () => { fresh = false; Draw(); }));
            choice.Children.Add(Option("Fresh", "Starts signed out, with cookies and sign-ins of its own.", fresh, () => { fresh = true; Draw(); }));
        }
        Draw();
        p.Children.Add(choice);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = Ui.Pill("Cancel", () => w.Close());
        cancel.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(cancel);
        void Make()
        {
            var n = name.Text.Trim();
            made = new Space { Name = n.Length > 0 ? n : "Space " + (owner.Spaces.Count + 1), Icon = icon.Text.Trim(), Fresh = fresh };
            w.Close();
        }
        buttons.Children.Add(Ui.Pill("Create Space", Make, filled: true));
        p.Children.Add(buttons);
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) Make(); };
        w.Content = p;
        w.Loaded += (_, _) => name.Focus();
        w.ShowDialog();
        return made;
    }

    static UIElement Option(string title, string sub, bool on, Action pick)
    {
        var dot = new Border
        {
            Width = 14, Height = 14, CornerRadius = new CornerRadius(7), BorderBrush = on ? Theme.Ink : Theme.Faint,
            BorderThickness = new Thickness(on ? 4.5 : 1.5), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 3, 10, 0),
        };
        var words = new StackPanel();
        words.Children.Add(Ui.Text(title, 13.5, weight: FontWeights.SemiBold));
        var s = Ui.Text(sub, 12, Theme.Muted);
        s.TextWrapping = TextWrapping.Wrap;
        words.Children.Add(s);
        var d = new DockPanel();
        DockPanel.SetDock(dot, Dock.Left);
        d.Children.Add(dot);
        d.Children.Add(words);
        return Ui.Button(d, pick, pad: new Thickness(10, 8, 10, 8), rest: on ? Theme.Wash : Brushes.Transparent, radius: 8);
    }
}

/// Settings: a page of choices, each taking effect as it is made.
sealed class SettingsWindow : Plain
{
    readonly BrowserWindow browser;
    readonly StackPanel page = new() { Margin = new Thickness(24, 16, 24, 24) };
    Settings S => browser.Settings;

    public SettingsWindow(BrowserWindow owner) : base(owner, "Settings", 560)
    {
        browser = owner;
        SizeToContent = SizeToContent.Manual;
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 80);
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        Content = new ScrollViewer { Content = page, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Theme.Changed += Draw;
        Closed += (_, _) => Theme.Changed -= Draw;
        Draw();
    }

    void Draw()
    {
        Background = Theme.Ground;
        if (IsLoaded) Native.PaintFrame(this);
        page.Children.Clear();

        Section("Tabs");
        Choice("Tabs sit", new[] { ("side", "Down the side"), ("top", "Across the top") }, S.TabsOnSide ? "side" : "top",
            v => { S.TabsOnSide = v == "side"; browser.ApplySettings(); });
        Add(SiteCard.Toggle("Video floats when you leave its tab", S.FloatOnLeave, true, on => { S.FloatOnLeave = on; browser.ApplySettings(); }));
        Add(SiteCard.Toggle("Swipe sideways over the tabs to change space", S.SpaceSwipe, true, on => { S.SpaceSwipe = on; browser.ApplySettings(); }));

        Section("Memory");
        Note("A tab you leave is frozen after a few seconds, and closed down to just its address after a while. Clicking it brings it back. Tabs playing sound are left alone.");
        Choice("Freeze background tabs after", new[] { ("0", "Never"), ("5", "5 s"), ("20", "20 s"), ("60", "1 min"), ("300", "5 min") },
            S.FreezeSeconds.ToString(), v => { S.FreezeSeconds = int.Parse(v); browser.ApplySettings(); });
        Choice("Unused tabs sleep after", new[] { ("0", "Never"), ("5", "5 min"), ("10", "10 min"), ("30", "30 min"), ("60", "1 hour"), ("240", "4 hours") },
            S.SleepMinutes.ToString(), v => { S.SleepMinutes = int.Parse(v); browser.ApplySettings(); });
        Add(SiteCard.Toggle("Lean engine — smaller caches, as on a low-memory PC (after a restart)", S.Lean, true, on => { S.Lean = on; browser.ApplySettings(); }));
        Note("Lean mode caps the disk cache and the JavaScript heap. Heavy pages may be slower, but pages look the same.");
        Add(SiteCard.Toggle("Share one process between pages of the same site — less memory, weaker isolation (after a restart)", S.ProcessPerSite, true, on => { S.ProcessPerSite = on; browser.ApplySettings(); }));

        if (WebKit.Available)
        {
            Choice("Engine (after a restart)", new[] { ("webkit", "WebKit"), ("webview2", "WebView2 (Edge)") },
                Engines.Default(S), v => { S.EngineName = v; browser.SaveSettings(); });
            Add(SiteCard.Toggle("WebKit: all tabs of a space share one page process (least memory; a crash takes them all, and sites aren't kept apart)", S.SharedProcess, true, on => { S.SharedProcess = on; browser.SaveSettings(); }));
        }

        Section("Appearance");
        Choice(null, new[] { ("system", "As Windows is"), ("light", "Light"), ("dark", "Dark") }, S.Look,
            v => { S.Look = v; browser.ApplySettings(); });

        Section("AERIE");
        Choice("Words typed in the field go to", Engine.All.Select(e => (e.Id, e.Title)).ToArray(), S.Engine,
            v => { S.Engine = v; browser.ApplySettings(); Draw(); });
        if (S.Engine == "custom")
        {
            var box = Ui.Field("", 13);
            box.Text = S.CustomEngine;
            box.TextChanged += (_, _) => { S.CustomEngine = box.Text; browser.SaveSettings(); };
            Add(Boxed(box));
            Note("The address with %s where the words go, e.g. https://example.com/search?q=%s");
        }

        Section("Privacy");
        Add(SiteCard.Toggle("Block ads and trackers before they load", S.ShieldOn, true, on => { S.ShieldOn = on; browser.ApplySettings(shieldChanged: true); Draw(); }));
        if (S.ShieldPaused.Count > 0)
        {
            Note("Off on these sites:");
            foreach (var host in S.ShieldPaused.ToList())
            {
                var h = host;
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
                var remove = Ui.Pill("Block here again", () => { S.ShieldPaused.Remove(h); browser.ApplySettings(shieldChanged: true); Draw(); });
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(Ui.Text(h, 13));
                Add(row);
            }
        }
        Add(SiteCard.Toggle("Offer to save passwords", S.SavePasswords, true, on => { S.SavePasswords = on; browser.ApplySettings(); }));
        Note("Passwords are kept by the engine, encrypted with your Windows account, on this PC only.");
        var hiddenCount = browser.Hidden.Sites.Sum(s => s.Value.Count);
        Buttons(
            ("Clear Browsing Data…", async () =>
            {
                if (MessageBox.Show(this, "Clear cookies, cache, history and saved site data for every space?", "AERIE", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
                await browser.ClearBrowsingData();
                browser.Toast("Cleared");
            }),
            ($"Forget Hidden Elements ({hiddenCount})", hiddenCount == 0 ? null : async () =>
            {
                browser.Hidden.Sites.Clear();
                Store.Save("hidden.json", browser.Hidden);
                await browser.RefreshHidden();
                Draw();
            }),
            ($"Forget Site Permissions ({S.SitePermissions.Count})", S.SitePermissions.Count == 0 ? null : () =>
            {
                S.SitePermissions.Clear();
                browser.SaveSettings();
                browser.Toast("Site permissions forgotten");
                Draw();
            }));

        Section("Extensions");
        var extensionHost = browser.Engine.Extensions;
        Note(extensionHost.About);
        foreach (var ext in extensionHost.All.ToList())
        {
            var e = ext;
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
            var remove = Ui.Pill("Remove", async () => { await extensionHost.Remove(e); Draw(); });
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var words = new StackPanel();
            words.Children.Add(Ui.Text(e.Name, 13.5));
            words.Children.Add(Ui.Text(e.StoreId != null ? "From the Chrome Web Store" : e.Folder, 11.5, Theme.Muted));
            row.Children.Add(words);
            Add(row);
        }
        if (extensionHost.Enabled)
        {
            var link = Ui.Field("", 13);
            var linkRow = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
            var addButton = Ui.Pill("Add", async () =>
            {
                try { var n = await extensionHost.InstallFromStore(link.Text); browser.Toast("Added " + n); Draw(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "AERIE"); }
            }, filled: true);
            addButton.Margin = new Thickness(8, 6, 0, 0);
            DockPanel.SetDock(addButton, Dock.Right);
            linkRow.Children.Add(addButton);
            linkRow.Children.Add(new Border
            {
                Child = Ui.Placeheld(link, "https://chromewebstore.google.com/detail/…"), BorderBrush = Theme.Faint, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(0, 6, 0, 0),
            });
            Add(linkRow);
            Buttons(("Load Unpacked…", async () =>
            {
                var pick = new Microsoft.Win32.OpenFolderDialog { Title = "The extension's folder (with manifest.json)" };
                if (pick.ShowDialog(this) != true) return;
                try { var n = await extensionHost.AddFolder(pick.FolderName); browser.Toast("Added " + n); Draw(); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "AERIE"); }
            }));
        }

        Section("This PC");
        bool isDefault = DefaultBrowser.IsDefault();
        Note(isDefault ? "AERIE is your default browser." : "Links you click in other apps open in another browser.");
        Buttons(
            (isDefault ? "Default Apps Settings…" : "Make AERIE the Default Browser…", () => { DefaultBrowser.Register(); DefaultBrowser.OpenSettings(); }),
            ("Open Data Folder", () => Process.Start(new ProcessStartInfo(Store.Folder) { UseShellExecute = true })));

        Section("About");
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        Note($"AERIE for Windows {version?.ToString(3)} · engine: {browser.Engine.Name} {browser.Engine.Version ?? ""}");
        Note("A port of Search for Mac by Office Commun (MIT). Nothing is sent anywhere except the pages you ask for.");
    }

    void Add(UIElement e) => page.Children.Add(e);

    void Section(string title)
    {
        var t = Ui.Text(title, 15, weight: FontWeights.SemiBold);
        t.Margin = new Thickness(0, page.Children.Count == 0 ? 0 : 22, 0, 8);
        Add(t);
    }

    void Note(string text)
    {
        var t = Ui.Text(text, 12, Theme.Muted);
        t.TextWrapping = TextWrapping.Wrap;
        t.TextTrimming = TextTrimming.None;
        t.Margin = new Thickness(0, 4, 0, 4);
        Add(t);
    }

    void Choice(string? label, (string Value, string Title)[] options, string current, Action<string> pick)
    {
        if (label != null) { var l = Ui.Text(label, 13); l.Margin = new Thickness(0, 6, 0, 4); Add(l); }
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
        foreach (var (value, title) in options)
        {
            var v = value;
            var pill = Ui.Pill(title, () => { pick(v); Draw(); }, filled: v == current);
            pill.Margin = new Thickness(0, 0, 6, 6);
            wrap.Children.Add(pill);
        }
        Add(wrap);
    }

    void Buttons(params (string Label, Action? Act)[] items)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var (label, act) in items)
        {
            if (act == null) continue;
            var b = Ui.Pill(label, act);
            b.Margin = new Thickness(0, 0, 8, 6);
            wrap.Children.Add(b);
        }
        Add(wrap);
    }
}
