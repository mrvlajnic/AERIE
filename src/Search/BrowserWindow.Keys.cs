using System.Windows.Input;

namespace Search;

/// The keyboard: Search's Mac shortcuts with ⌘ as Ctrl and ⌥ as Alt, plus
/// the Windows habits (F5, F6, Alt+D, Ctrl+Tab, Ctrl+H). Keys pressed inside
/// a page reach here too: WebView2 hands its accelerator keys to the window.
/// Find (Ctrl+F), Print (Ctrl+P), Zoom (Ctrl +/-/0) and the developer
/// tools (F12, Ctrl+Shift+I, Ctrl+Shift+J) are the engine's own.
sealed partial class BrowserWindow
{
    void OnKey(object sender, KeyEventArgs e)
    {
        if (!Started) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var m = Keyboard.Modifiers;
        // Asked of Windows too: keys pressed inside the page are typed into
        // another process, and WPF's own idea of them can lag.
        bool ctrl = m.HasFlag(ModifierKeys.Control) || Native.CtrlHeld;
        bool shift = m.HasFlag(ModifierKeys.Shift) || Native.ShiftHeld;
        bool alt = m.HasFlag(ModifierKeys.Alt) || Native.AltHeld;
        if (Run(key, ctrl, shift, alt)) e.Handled = true;
    }

    bool Run(Key key, bool ctrl, bool shift, bool alt)
    {
        if (ctrl && !shift && !alt)
        {
            switch (key)
            {
                case Key.T: case Key.N: OpenOmnibox(newTab: true); return true;
                case Key.L: OpenOmnibox(newTab: Current == null, priv: Current?.Private == true); return true;
                case Key.W: case Key.F4: if (Current != null) Close(Current); return true;
                case Key.K: ShowSwitcher(); return true;
                case Key.S: ToggleFold(); return true;
                case Key.R: Reload(); return true;
                case Key.D: if (Current != null) TogglePin(Current); return true;
                case Key.H: case Key.Y: ShowHistory(); return true;
                case Key.F: FindOnPage(); return true;
                case Key.OemComma: ShowSettings(); return true;
                case Key.Tab: case Key.PageDown: Step(1); return true;
                case Key.PageUp: Step(-1); return true;
                case Key.OemOpenBrackets: Back(); return true;
                case Key.OemCloseBrackets: Forward(); return true;
                case >= Key.D1 and <= Key.D9: SelectNumber(key - Key.D0); return true;
                case >= Key.NumPad1 and <= Key.NumPad9: SelectNumber(key - Key.NumPad0); return true;
            }
        }
        if (ctrl && shift && !alt)
        {
            switch (key)
            {
                case Key.T: Reopen(); return true;
                case Key.N: OpenPrivate(); return true;
                case Key.R: _ = ToggleReader(); return true;
                case Key.P: if (Current != null) _ = Float(Current, "toggle"); return true;
                case Key.H: _ = ToggleHiding(); return true;
                case Key.U: ShowHidden(); return true;
                case Key.S: ToggleSide(); return true;
                case Key.Tab: case Key.OemOpenBrackets: Step(-1); return true;
                case Key.OemCloseBrackets: Step(1); return true;
            }
        }
        if (ctrl && alt && !shift)
        {
            switch (key)
            {
                case Key.Left: StepSpace(-1); return true;
                case Key.Right: StepSpace(1, offerNew: true); return true;
                case Key.C: CopyCleanLink(); return true;
                case Key.I: OpenDevTools(); return true;
            }
        }
        if (alt && !ctrl && !shift)
        {
            switch (key)
            {
                case Key.Left: Back(); return true;
                case Key.Right: Forward(); return true;
                case Key.D: OpenOmnibox(newTab: Current == null, priv: Current?.Private == true); return true;
                case Key.Home: if (Current?.Pinned == true) Navigate(Current, Current.Home!); return true;
            }
        }
        if (!ctrl && !alt && !shift)
        {
            switch (key)
            {
                case Key.F5: Reload(); return true;
                case Key.F6: OpenOmnibox(newTab: Current == null); return true;
                case Key.F11: SetFullScreen(!fullScreen); return true;
                case Key.BrowserBack: Back(); return true;
                case Key.BrowserForward: Forward(); return true;
            }
        }
        return false;
    }

    public void ToggleFold()
    {
        Settings.SideFolded = !Settings.SideFolded;
        peeking = false;
        SaveSettings();
        Render();
    }

    public void ToggleSide()
    {
        Settings.TabsOnSide = !Settings.TabsOnSide;
        SaveSettings();
        Render();
    }

    /// Settings changed in the Settings window: applied to everything open.
    public async void ApplySettings(bool shieldChanged = false)
    {
        if (Theme.Look != Settings.Look) Theme.Apply(Settings.Look);
        if (shieldChanged) await RefreshShield();
        SaveSettings();
        Render();
    }

    // Panels -------------------------------------------------------------------

    public void OpenOmnibox(bool newTab, bool priv = false, string? seed = null) => new Omnibox(this, newTab, priv, seed).Present();

    string? find;
    void FindOnPage()
    {
        if (Current?.Page is not { } page) return;
        var text = Prompt.Ask(this, "Find on page", find ?? "");
        if (string.IsNullOrWhiteSpace(text)) return;
        find = text;
        page.Find(text, false);
    }
    void ShowSwitcher() => new Switcher(this).Present();
    void ShowHistory() => new HistoryPanel(this).Present();
    void ShowHidden() { if (Current?.Host.Length > 0) new HiddenPanel(this, Current.Host).Present(); }
    void ShowSiteCard() { if (Current != null) new SiteCard(this, Current).Present(); }
    void ShowExtensions() => new ExtensionsMenu(this).Present();

    public void RaiseSettings() => ShowSettings();

    SettingsWindow? settingsWindow;
    void ShowSettings()
    {
        if (settingsWindow is { IsLoaded: true }) { settingsWindow.Activate(); return; }
        settingsWindow = new SettingsWindow(this);
        settingsWindow.Show();
    }
}
