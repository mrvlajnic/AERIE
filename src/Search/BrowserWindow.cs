using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Search.Core;

namespace Search;

/// The one window: a column of tabs down the left, or a row across the top,
/// and the page. No toolbar, no start page.
sealed partial class BrowserWindow : Window
{
    public readonly Settings Settings = Store.Load<Settings>("settings.json");
    public readonly History History = Store.Load<History>("history.json");
    public readonly Hidden Hidden = Store.Load<Hidden>("hidden.json");
    public readonly List<Space> Spaces = new();
    public Space Space = null!;
    public bool Started { get; private set; }

    readonly string[] startArgs;
    readonly Grid root = new();
    readonly ColumnDefinition sideCol = new(), edgeCol = new(), mainCol = new() { Width = new GridLength(1, GridUnitType.Star) };
    readonly RowDefinition stripRow = new() { Height = GridLength.Auto }, mainRow = new() { Height = new GridLength(1, GridUnitType.Star) };
    readonly Grid side = new();
    readonly Border strip = new() { Height = 42 };
    readonly Border edge = new() { Background = Brushes.Transparent };
    readonly Border card = new();
    public readonly Grid Pages = new();
    readonly StackPanel empty = new() { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    readonly Popup toast = new() { AllowsTransparency = true, Placement = PlacementMode.Relative, StaysOpen = true };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(2.4) };
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer sleepTimer = new() { Interval = TimeSpan.FromSeconds(10) };
    bool renderQueued, historyDirty, peeking, fullScreen;
    WindowState stateBeforeFull;
    double swipe;
    DateTime lastSwipe;

    public BrowserWindow(string[] args)
    {
        startArgs = args;
        Theme.Apply(Settings.Look);
        Title = "Search";
        Width = Settings.Width; Height = Settings.Height;
        MinWidth = 480; MinHeight = 320;
        if (Settings.Left is double l && Settings.Top is double t && OnScreen(l, t))
        { Left = l; Top = t; WindowStartupLocation = WindowStartupLocation.Manual; }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (Settings.Maximized) WindowState = WindowState.Maximized;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        root.ColumnDefinitions.Add(sideCol);
        root.ColumnDefinitions.Add(edgeCol);
        root.ColumnDefinitions.Add(mainCol);
        root.RowDefinitions.Add(stripRow);
        root.RowDefinitions.Add(mainRow);

        Grid.SetRowSpan(side, 2);
        Grid.SetColumnSpan(strip, 3);
        Grid.SetColumn(edge, 1); Grid.SetRowSpan(edge, 2);
        Grid.SetColumn(card, 2); Grid.SetRow(card, 1);
        card.Child = Pages;
        empty.Cursor = Cursors.IBeam;
        empty.Background = Brushes.Transparent;
        empty.MouseLeftButtonUp += (_, _) => { if (Started) OpenOmnibox(newTab: true); };
        Pages.Children.Add(empty);
        root.Children.Add(side);
        root.Children.Add(strip);
        root.Children.Add(edge);
        root.Children.Add(card);
        Content = root;

        edge.MouseEnter += (_, _) => { if (Settings.SideFolded) { peeking = true; Layout(); } };
        side.MouseLeave += (_, _) => { if (peeking) { peeking = false; Layout(); } };
        strip.MouseLeave += (_, _) => { if (peeking) { peeking = false; Layout(); } };

        toast.PlacementTarget = card;
        toastTimer.Tick += (_, _) => { toast.IsOpen = false; toastTimer.Stop(); };
        saveTimer.Tick += (_, _) => { saveTimer.Stop(); SaveSession(); };
        sleepTimer.Tick += (_, _) => SleepIdle();
        sleepTimer.Start();

        Theme.Changed += OnTheme;
        SourceInitialized += (_, _) =>
        {
            Native.PaintFrame(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(Hook);
        };
        Loaded += async (_, _) => await Start();
        Closing += (_, _) => SaveAll();
        PreviewKeyDown += OnKey;
        PreviewTextInput += OnTypedCharacter;
        MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) Back();
            else if (e.ChangedButton == MouseButton.XButton2) Forward();
        };
        SizeChanged += (_, _) => { if (!Settings.TabsOnSide) Render(); };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) _ = FreezeAll();
            else Current?.Page?.SetShown(true);
        };
        OnTheme();
    }

    static bool OnScreen(double left, double top) =>
        left >= SystemParameters.VirtualScreenLeft - 50 && top >= SystemParameters.VirtualScreenTop - 10 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100;

    async Task Start()
    {
        WebKitEngine.ClearIfAsked();
        Engine = Engines.Make(Engines.Default(Settings));
        Trace.Log("engine: " + Engine.Name);
        Engine.DocumentScripts = DocumentScripts;
        try { await Engine.Start(this); }
        catch (Exception e) when (Engine is WebView2Engine && (e is Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException || e is System.IO.FileNotFoundException))
        {
            var go = MessageBox.Show(this,
                "Search draws pages with Microsoft Edge WebView2, which isn't installed on this PC.\n\nOpen the download page?",
                "Search", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (go == MessageBoxResult.Yes) Open("https://go.microsoft.com/fwlink/p/?LinkId=2124703", external: true);
            Close();
            return;
        }

        var saved = Store.Load<Session>("session.json");
        foreach (var s in saved.Spaces) Spaces.Add(Space.From(s));
        if (Spaces.Count == 0) Spaces.Add(new Space { Name = "Home" });
        Space = Spaces[Math.Clamp(saved.Current, 0, Spaces.Count - 1)];
        Started = true;
        string[][] queued;
        lock (earlyArrivals) { queued = earlyArrivals.ToArray(); earlyArrivals.Clear(); }
        foreach (var lines in queued) Arrived(lines, activate: false);
        Arrived(startArgs, activate: false);
        if (Space.Selected == null && Space.All.Any()) Space.Selected = Space.All.First();
        if (Space.Selected is { } sel) await Select(sel, force: true);
        Render();
        Bench = Bench.FromArgs(this);
        if (Bench != null) { _ = Bench.Run(); return; }
        if (!Space.All.Any()) OpenOmnibox(newTab: true);
    }

    // Layout -------------------------------------------------------------

    void OnTheme()
    {
        Background = Theme.Frame;
        Pages.Background = Theme.Ground;
        if (IsLoaded) Native.PaintFrame(this);
        Engine?.Recolour();
        BuildEmpty();
        Render();
    }

    void BuildEmpty()
    {
        empty.Children.Clear();
        var hint = Ui.Text("Type an address or a few words", 15, Theme.Muted);
        hint.HorizontalAlignment = HorizontalAlignment.Center;
        var keys = Ui.Text("Ctrl+T", 12, Theme.Faint);
        keys.HorizontalAlignment = HorizontalAlignment.Center;
        keys.Margin = new Thickness(0, 6, 0, 0);
        empty.Children.Add(hint);
        empty.Children.Add(keys);
    }

    void Layout()
    {
        bool onSide = Settings.TabsOnSide;
        bool folded = Settings.SideFolded && !peeking;
        if (fullScreen)
        {
            sideCol.Width = new GridLength(0); edgeCol.Width = new GridLength(0);
            stripRow.Height = new GridLength(0);
            side.Visibility = strip.Visibility = edge.Visibility = Visibility.Collapsed;
            card.Margin = new Thickness(0);
            return;
        }
        side.Visibility = onSide && !folded ? Visibility.Visible : Visibility.Collapsed;
        strip.Visibility = !onSide && !folded ? Visibility.Visible : Visibility.Collapsed;
        sideCol.Width = new GridLength(onSide && !folded ? Settings.SideWidth : 0);
        stripRow.Height = !onSide && !folded ? GridLength.Auto : new GridLength(onSide ? 0 : (Settings.SideFolded ? 5 : 0));
        edge.Visibility = Settings.SideFolded ? Visibility.Visible : Visibility.Collapsed;
        if (onSide)
        {
            Grid.SetColumn(edge, 1); Grid.SetRow(edge, 0); Grid.SetRowSpan(edge, 2); Grid.SetColumnSpan(edge, 1);
            edgeCol.Width = new GridLength(Settings.SideFolded && !peeking ? 6 : 0);
            edge.Height = double.NaN;
            card.Margin = new Thickness(0, 6, 6, 6);
        }
        else
        {
            edgeCol.Width = new GridLength(0);
            Grid.SetColumn(edge, 0); Grid.SetRow(edge, 0); Grid.SetRowSpan(edge, 1); Grid.SetColumnSpan(edge, 3);
            edge.Height = 5;
            card.Margin = new Thickness(0);
        }
    }

    /// Changes are drawn once per turn of the dispatcher, however many came in.
    public void Render()
    {
        if (renderQueued) return;
        renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { renderQueued = false; RenderNow(); });
    }

    void RenderNow()
    {
        Layout();
        if (Space == null) return;
        if (Settings.TabsOnSide) BuildSide(); else BuildStrip();
        var sel = Space.Selected;
        Title = sel == null ? "Search" : sel.Display + " — Search";
        empty.Visibility = sel == null ? Visibility.Visible : Visibility.Collapsed;
    }

    // The column ---------------------------------------------------------

    void BuildSide()
    {
        side.Children.Clear();
        side.Background = Theme.Frame;
        var dock = new DockPanel { Margin = new Thickness(8, 6, 6, 8), LastChildFill = true };

        // The field: where you are, and the way to somewhere else.
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var nav = new StackPanel { Orientation = Orientation.Horizontal };
        nav.Children.Add(Ui.IconButton(Ui.Glyph.Back, Back, "Back (Alt+Left)", 11, Theme.SideHover));
        nav.Children.Add(Ui.IconButton(Ui.Glyph.Forward, Forward, "Forward (Alt+Right)", 11, Theme.SideHover));
        nav.Children.Add(Ui.IconButton(Ui.Glyph.Reload, Reload, "Reload (Ctrl+R)", 11, Theme.SideHover));
        DockPanel.SetDock(nav, Dock.Right);
        head.Children.Add(nav);
        head.Children.Add(AddressChip());
        DockPanel.SetDock(head, Dock.Top);
        dock.Children.Add(head);

        if (Space.Pins.Count > 0)
        {
            var pins = new UniformGrid { Columns = Math.Max(3, (int)((Settings.SideWidth - 14) / 54)), Margin = new Thickness(0, 0, 0, 6) };
            foreach (var p in Space.Pins) pins.Children.Add(PinTile(p, 38));
            DockPanel.SetDock(pins, Dock.Top);
            AllowPinDrop(pins);
            dock.Children.Add(pins);
        }

        var foot = Footer();
        DockPanel.SetDock(foot, Dock.Bottom);
        dock.Children.Add(foot);

        var list = new StackPanel();
        foreach (var t in Space.Tabs) list.Children.Add(TabRow(t));
        var add = Ui.Button(Row(Ui.Icon(Ui.Glyph.Add, 11), Ui.Text("New Tab", 13, Theme.Muted)),
            () => OpenOmnibox(newTab: true), "Ctrl+T", new Thickness(8, 7, 8, 7), hover: Theme.SideHover);
        list.Children.Add(add);
        var scroll = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        AllowTabDrop(list, null);
        dock.Children.Add(scroll);

        side.Children.Add(dock);

        // The column's edge: drag it to make the column wider.
        var grip = new Border { Width = 5, Background = Brushes.Transparent, Cursor = Cursors.SizeWE, HorizontalAlignment = HorizontalAlignment.Right };
        grip.MouseLeftButtonDown += (_, e) => { grip.CaptureMouse(); e.Handled = true; };
        grip.MouseMove += (_, e) =>
        {
            if (!grip.IsMouseCaptured) return;
            Settings.SideWidth = Math.Clamp(e.GetPosition(root).X, 170, 420);
            sideCol.Width = new GridLength(Settings.SideWidth);
        };
        grip.MouseLeftButtonUp += (_, _) => { grip.ReleaseMouseCapture(); Render(); };
        side.Children.Add(grip);
    }

    static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items)
        {
            if (i is FrameworkElement f && p.Children.Count > 0) f.Margin = new Thickness(8, 0, 0, 0);
            p.Children.Add(i);
        }
        return p;
    }

    Border AddressChip()
    {
        var sel = Space.Selected;
        string text = "Search or enter address";
        string glyph = Ui.Glyph.Search;
        Brush tint = Theme.Muted;
        if (sel != null && Uri.TryCreate(sel.Url, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http"))
        {
            text = Address.WithoutWww(u.Host);
            glyph = u.Scheme == "https" ? Ui.Glyph.Lock : Ui.Glyph.Warning;
            tint = u.Scheme == "https" ? Theme.Muted : Theme.Unsafe;
        }
        else if (sel != null && sel.Url.Length > 0 && sel.Url != "about:blank") text = sel.Display;
        if (sel?.Private == true) { glyph = Ui.Glyph.Private; tint = Theme.Muted; }

        var lockButton = Ui.Button(Ui.Icon(glyph, 11, tint), () => { if (sel != null) ShowSiteCard(); }, "About this site", new Thickness(6, 4, 4, 4), hover: Theme.SideHover);
        var label = Ui.Text(text, 13, sel == null ? Theme.Muted : Theme.Ink);
        var d = new DockPanel();
        DockPanel.SetDock(lockButton, Dock.Left);
        d.Children.Add(lockButton);
        if (sel?.Loading == true)
        {
            var dot = Ui.Text("•", 13, Theme.Muted);
            DockPanel.SetDock(dot, Dock.Right);
            dot.Margin = new Thickness(4, 0, 4, 0);
            d.Children.Add(dot);
        }
        d.Children.Add(label);
        return Ui.Button(d, () => OpenOmnibox(newTab: sel == null, priv: sel?.Private == true), "Address and search (Ctrl+L)", new Thickness(2, 3, 6, 3), rest: Theme.SideTile, hover: Theme.SideHover, radius: 8);
    }

    UIElement Footer()
    {
        var foot = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(Ui.IconButton(Ui.Glyph.Settings, ShowSettings, "Settings (Ctrl+,)", 12, Theme.SideHover));
        left.Children.Add(Ui.IconButton(Ui.Glyph.Puzzle, ShowExtensions, "Extensions", 12, Theme.SideHover));
        left.Children.Add(Ui.IconButton(Ui.Glyph.History, ShowHistory, "History (Ctrl+H)", 12, Theme.SideHover));
        DockPanel.SetDock(left, Dock.Left);
        foot.Children.Add(left);

        var spaces = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var s in Spaces) spaces.Children.Add(SpaceMark(s));
        spaces.Children.Add(Ui.IconButton(Ui.Glyph.Add, NewSpace, "New space", 10, Theme.SideHover));
        foot.Children.Add(spaces);
        return foot;
    }

    UIElement SpaceMark(Space s)
    {
        bool here = s == Space;
        var mark = Ui.Text(s.Mark, 11.5, here ? Theme.Ink : Theme.Muted, here ? FontWeights.SemiBold : FontWeights.Normal);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        var b = Ui.Button(mark, () => SwitchSpace(s), s.Name + (s.Fresh ? " · its own sign-ins" : "") + "  (Ctrl+Alt+←/→)",
            new Thickness(0), rest: here ? Theme.SideWash : Brushes.Transparent, hover: Theme.SideHover, radius: 6);
        b.Width = 24; b.Height = 24; b.Margin = new Thickness(1, 0, 1, 0);
        b.ContextMenu = Ui.Menu(
            ("Rename Space…", () => { var n = Prompt.Ask(this, "Rename space", s.Name); if (!string.IsNullOrWhiteSpace(n)) { s.Name = n!; Render(); SaveSoon(); } }),
            ("Change Icon…", () => { var n = Prompt.Ask(this, "An emoji or a letter for this space", s.Icon); if (n != null) { s.Icon = n.Trim(); Render(); SaveSoon(); } }),
            ("-", null),
            ("Delete Space…", Spaces.Count > 1 ? () => DeleteSpace(s) : null));
        return b;
    }

    // The row across the top ------------------------------------------------

    void BuildStrip()
    {
        strip.Background = Theme.Frame;
        var grid = new Grid { Margin = new Thickness(6, 6, 6, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                 // pins
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                 // field
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // tabs
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                 // buttons

        var pins = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var p in Space.Pins) pins.Children.Add(PinTile(p, 30));
        AllowPinDrop(pins);
        grid.Children.Add(pins);

        // The field belongs in this layout too: without it the only ways to
        // search were the +, the selected tab, or shortcuts that do not reach
        // the window while a page has focus.
        var address = AddressChip();
        address.Width = 250;
        address.Margin = new Thickness(6, 0, 8, 0);
        Grid.SetColumn(address, 1);
        grid.Children.Add(address);

        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(Ui.IconButton(Ui.Glyph.Add, () => OpenOmnibox(newTab: true), "New Tab (Ctrl+T)", 11, Theme.SideHover));
        foreach (var s in Spaces) right.Children.Add(SpaceMark(s));
        right.Children.Add(Ui.IconButton(Ui.Glyph.Add, NewSpace, "New space", 9, Theme.SideHover));
        right.Children.Add(Ui.IconButton(Ui.Glyph.Puzzle, ShowExtensions, "Extensions", 12, Theme.SideHover));
        right.Children.Add(Ui.IconButton(Ui.Glyph.Settings, ShowSettings, "Settings (Ctrl+,)", 12, Theme.SideHover));
        Grid.SetColumn(right, 3);
        grid.Children.Add(right);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 4, 0) };
        double room = Math.Max(200, ActualWidth - 330 - Space.Pins.Count * 34 - (Spaces.Count + 3) * 30);
        double each = Space.Tabs.Count == 0 ? 0 : Math.Clamp(room / Space.Tabs.Count, 72, 230);
        foreach (var t in Space.Tabs)
        {
            var chip = TabChip(t);
            chip.Width = each;
            tabs.Children.Add(chip);
        }
        AllowTabDrop(tabs, null);
        var scroller = new ScrollViewer
        {
            Content = tabs, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false,
        };
        Grid.SetColumn(scroller, 2);
        grid.Children.Add(scroller);
        strip.Child = grid;
    }

    // Tabs, pins, and dragging them about ---------------------------------

    UIElement Favicon(Tab t, double size = 16)
    {
        if (t.Icon != null)
            return new Image { Source = t.Icon, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, SnapsToDevicePixels = true };
        if (t.Private) return Ui.Icon(Ui.Glyph.Private, size - 3);
        var letter = Ui.Text(t.Letter, size * 0.62, Theme.Ground, FontWeights.SemiBold);
        letter.HorizontalAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(4), Background = Theme.Muted,
            Child = letter, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    Border TabRow(Tab t)
    {
        bool live = t == Space.Selected;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        grid.Children.Add(Favicon(t));
        var title = Ui.Text(t.Display, 13, Theme.Ink);
        if (t.Private) title.FontStyle = FontStyles.Italic;
        title.Margin = new Thickness(9, 0, 4, 0);
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);

        if (t.Audible || t.Muted)
        {
            var sound = Ui.Button(Ui.Icon(t.Muted ? Ui.Glyph.Mute : Ui.Glyph.Speaker, 11), () => Mute(t), t.Muted ? "Unmute" : "Mute", new Thickness(3), hover: Theme.SideHover, radius: 4);
            Grid.SetColumn(sound, 2);
            grid.Children.Add(sound);
        }
        var close = Ui.Button(Ui.Icon(Ui.Glyph.Close, 9), () => Close(t), "Close (Ctrl+W)", new Thickness(4), hover: Theme.SidePinLive, radius: 4);
        close.Opacity = live ? 1 : 0;
        Grid.SetColumn(close, 3);
        grid.Children.Add(close);

        var row = new ClickableBorder
        {
            Child = grid, Padding = new Thickness(8, 6, 4, 6), CornerRadius = new CornerRadius(7),
            Background = live ? Theme.SideWash : Brushes.Transparent, ToolTip = t.Url, Margin = new Thickness(0, 0, 0, 1),
        };
        row.MouseEnter += (_, _) => { close.Opacity = 1; if (t != Space.Selected) row.Background = Theme.SideHover; };
        row.MouseLeave += (_, _) => { close.Opacity = t == Space.Selected ? 1 : 0; row.Background = t == Space.Selected ? Theme.SideWash : Brushes.Transparent; };
        Interactive(row, t);
        AllowTabDrop(row, t);
        return row;
    }

    Border TabChip(Tab t)
    {
        bool live = t == Space.Selected;
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(Favicon(t, 14));
        var title = Ui.Text(t.Display, 12.5, live ? Theme.Ink : Theme.Muted);
        title.Margin = new Thickness(7, 0, 2, 0);
        if (t.Private) title.FontStyle = FontStyles.Italic;
        Grid.SetColumn(title, 1);
        grid.Children.Add(title);
        var close = Ui.Button(Ui.Icon(Ui.Glyph.Close, 8), () => Close(t), "Close (Ctrl+W)", new Thickness(4), hover: Theme.SidePinLive, radius: 4);
        close.Opacity = live ? 1 : 0;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        var chip = new ClickableBorder
        {
            Child = grid, Padding = new Thickness(9, 5, 4, 5), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 3, 0),
            Background = live ? Theme.Ground : Brushes.Transparent, ToolTip = t.Display + "\n" + t.Url,
        };
        chip.MouseEnter += (_, _) => { close.Opacity = 1; if (t != Space.Selected) chip.Background = Theme.SideHover; };
        chip.MouseLeave += (_, _) => { close.Opacity = t == Space.Selected ? 1 : 0; chip.Background = t == Space.Selected ? Theme.Ground : Brushes.Transparent; };
        Interactive(chip, t);
        AllowTabDrop(chip, t);
        return chip;
    }

    Border PinTile(Tab t, double height)
    {
        bool live = t == Space.Selected;
        var tile = new ClickableBorder
        {
            Height = height, MinWidth = height, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 4, 4),
            Background = live ? Theme.SidePinLive : Theme.SideTile, ToolTip = t.Display, Child = Favicon(t, 16),
            Opacity = t.Awake || live ? 1 : 0.75,
        };
        if (!Settings.TabsOnSide) tile.Margin = new Thickness(0, 0, 4, 0);
        tile.MouseEnter += (_, _) => { if (t != Space.Selected) tile.Background = Theme.SideHover; };
        tile.MouseLeave += (_, _) => tile.Background = t == Space.Selected ? Theme.SidePinLive : Theme.SideTile;
        Interactive(tile, t);
        return tile;
    }

    Point dragFrom;
    Tab? pressed;

    /// Click to select, middle-click to close, double-click to rename,
    /// right-click for the rest, drag to move.
    void Interactive(Border b, Tab t)
    {
        void Go()
        {
            if (t == Space.Selected && !Settings.TabsOnSide && !t.Pinned) { OpenOmnibox(newTab: false); return; }
            _ = Select(t);
        }
        if (b is ClickableBorder clickable)
        {
            clickable.Activate = Go;
            Ui.Accessible(clickable, t.Display + (t.Pinned ? ", pinned" : "") + (t.Private ? ", private" : ""));
        }
        b.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && !t.Pinned) { Rename(t); e.Handled = true; return; }
            dragFrom = e.GetPosition(this); pressed = t;
        };
        b.MouseLeftButtonUp += (_, e) =>
        {
            if (pressed != t) return;
            pressed = null;
            Go();
        };
        b.MouseMove += (_, e) =>
        {
            if (pressed != t || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - dragFrom.X) + Math.Abs(p.Y - dragFrom.Y) < 6) return;
            pressed = null;
            DragDrop.DoDragDrop(b, new DataObject(typeof(Tab), t), DragDropEffects.Move);
        };
        b.MouseUp += (_, e) => { if (e.ChangedButton == MouseButton.Middle) Close(t); };
        b.ContextMenu = TabMenu(t);
    }

    ContextMenu TabMenu(Tab t)
    {
        var items = new List<(string, Action?)>
        {
            (t.Pinned ? "Unpin" : "Pin", t.Private ? null : () => TogglePin(t)),
            ("Rename…", () => Rename(t)),
            ("Duplicate", () => Open(t.Url, index: t.Pinned ? 0 : Space.Tabs.IndexOf(t) + 1)),
            ("Copy Address", () => Clipboard.SetText(t.Url)),
            ("Copy Clean Link", () => Clipboard.SetText(CleanLink.Clean(t.Url))),
            (t.Muted ? "Unmute" : "Mute", t.Awake ? () => Mute(t) : null),
        };
        if (t.Pinned) items.Add(("Back to Pinned Page", () => Navigate(t, t.Home!)));
        if (t.Awake && !t.Pinned) items.Add(("Sleep Now", () => { if (t == Space.Selected) return; Sleep(t); }));
        var menu = Ui.Menu(items.ToArray());
        if (Spaces.Count > 1 && !t.Private)
        {
            var move = new MenuItem { Header = "Move to Space" };
            foreach (var s in Spaces.Where(s => s != t.Space))
            {
                var target = s;
                var i = new MenuItem { Header = s.Name };
                i.Click += (_, _) => MoveToSpace(t, target);
                move.Items.Add(i);
            }
            menu.Items.Add(move);
        }
        menu.Items.Add(new Separator());
        var closeItem = new MenuItem { Header = t.Pinned ? "Sleep" : "Close", InputGestureText = "Ctrl+W" };
        closeItem.Click += (_, _) => Close(t);
        menu.Items.Add(closeItem);
        if (!t.Pinned)
        {
            var others = new MenuItem { Header = "Close Other Tabs" };
            others.Click += (_, _) => { foreach (var o in Space.Tabs.Where(x => x != t).ToList()) Close(o); };
            menu.Items.Add(others);
            var below = new MenuItem { Header = Settings.TabsOnSide ? "Close Tabs Below" : "Close Tabs to the Right" };
            below.Click += (_, _) => { foreach (var o in Space.Tabs.Skip(Space.Tabs.IndexOf(t) + 1).ToList()) Close(o); };
            menu.Items.Add(below);
        }
        return menu;
    }

    void AllowTabDrop(FrameworkElement target, Tab? before)
    {
        target.AllowDrop = true;
        target.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(Tab)) is not Tab t || t == before) return;
            e.Handled = true;
            var space = t.Space;
            if (t.Pinned) { space.Pins.Remove(t); t.Home = null; } else space.Tabs.Remove(t);
            if (space != Space) { MoveToSpace(t, Space); return; }
            int at = before == null || before.Pinned ? space.Tabs.Count : space.Tabs.IndexOf(before);
            space.Tabs.Insert(Math.Clamp(at, 0, space.Tabs.Count), t);
            Render(); SaveSoon();
        };
    }

    void AllowPinDrop(Panel target)
    {
        target.AllowDrop = true;
        target.Background ??= Brushes.Transparent;
        target.Drop += (_, e) =>
        {
            if (e.Data.GetData(typeof(Tab)) is not Tab t || t.Private) return;
            e.Handled = true;
            if (!t.Pinned) TogglePin(t);
        };
    }

    // Small things ---------------------------------------------------------

    public void Toast(string message)
    {
        var text = Ui.Text(message, 13, Brushes.White);
        toast.Child = new Border
        {
            Child = text, Background = new SolidColorBrush(Color.FromArgb(235, 23, 23, 23)),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 8, 14, 9),
        };
        toast.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        toast.HorizontalOffset = (card.ActualWidth - toast.Child.DesiredSize.Width) / 2;
        toast.VerticalOffset = card.ActualHeight - toast.Child.DesiredSize.Height - 24;
        toast.IsOpen = false; toast.IsOpen = true;
        toastTimer.Stop(); toastTimer.Start();
    }

    /// The top of the page area, centred, in screen coordinates (DIPs): where panels open.
    public Point Anchor(double width)
    {
        var target = card.IsVisible ? (FrameworkElement)card : root;
        var topLeft = target.PointToScreen(new Point(0, 0));
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null) topLeft = source.CompositionTarget.TransformFromDevice.Transform(topLeft);
        return new Point(topLeft.X + (target.ActualWidth - width) / 2, topLeft.Y + 40);
    }

    /// The empty state invites typing; with no sheet open those keys went
    /// nowhere. Open the field with the first character already in it.
    void OnTypedCharacter(object sender, TextCompositionEventArgs e)
    {
        if (!Started || Sheet.AnyOpen) return;
        if (Keyboard.FocusedElement is TextBoxBase) return;
        if (string.IsNullOrEmpty(e.Text) || e.Text.Any(char.IsControl)) return;
        e.Handled = true;
        OpenOmnibox(newTab: Current == null, priv: Current?.Private == true, seed: e.Text);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Two fingers sideways on a touchpad, over the tabs: the next space.
        if (msg == Native.WM_MOUSEHWHEEL && Settings.SpaceSwipe && Spaces.Count > 0 && (side.IsMouseOver || strip.IsMouseOver))
        {
            int delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if ((DateTime.Now - lastSwipe).TotalMilliseconds > 400) swipe = 0;
            lastSwipe = DateTime.Now;
            swipe += delta;
            if (Math.Abs(swipe) >= 360)
            {
                int step = swipe > 0 ? 1 : -1;
                swipe = -step * 100000; // one space per gesture
                Dispatcher.BeginInvoke(() => StepSpace(step, offerNew: true));
                handled = true;   // only swallow the wheel when a space changes
            }
        }
        return IntPtr.Zero;
    }

    void SetFullScreen(bool on)
    {
        if (on == fullScreen) return;
        fullScreen = on;
        if (on)
        {
            stateBeforeFull = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = stateBeforeFull;
        }
        Layout();
    }

    void SaveAll()
    {
        if (!Started) return;
        Settings.Maximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal && !fullScreen)
        { Settings.Width = Width; Settings.Height = Height; Settings.Left = Left; Settings.Top = Top; }
        else { Settings.Width = RestoreBounds.Width; Settings.Height = RestoreBounds.Height; Settings.Left = RestoreBounds.Left; Settings.Top = RestoreBounds.Top; }
        Store.Save("settings.json", Settings);
        SaveSession();
        Store.Save("history.json", History);
    }

    public void SaveSettings() => Store.Save("settings.json", Settings);

    public void SaveSoon() { saveTimer.Stop(); saveTimer.Start(); }

    void SaveSession()
    {
        if (!Started) return;
        Store.Save("session.json", new Session { Spaces = Spaces.Select(s => s.Save()).ToList(), Current = Spaces.IndexOf(Space) });
        if (historyDirty) { historyDirty = false; Store.Save("history.json", History); }
    }
}
