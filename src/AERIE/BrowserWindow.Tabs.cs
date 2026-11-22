using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using AERIE.Core;

namespace AERIE;

sealed partial class BrowserWindow
{
    readonly Stack<(SavedTab Tab, Space Space, int Index)> closed = new();
    /// Links handed over before the engine finished starting.
    readonly List<string[]> earlyArrivals = new();
    public IEngine Engine { get; private set; } = null!;
    public Bench? Bench { get; private set; }

    public Tab? Current => Space?.Selected;

    // Selecting, waking, sleeping -------------------------------------------

    public async Task Select(Tab t, bool force = false)
    {
        if (t.Space != Space) SwitchSpace(t.Space, wake: false);
        var prev = Space.Selected;
        if (prev == t && t.Awake && !force) { if (!Sheet.AnyOpen) t.Page!.Focus(); return; }
        if (prev != null && prev != t && prev.Awake)
        {
            prev.Seen = DateTime.Now;
            if (Settings.FloatOnLeave && prev.Audible) await Float(prev, "leave");
        }
        Space.Selected = t;
        t.Seen = DateTime.Now;
        if (!t.Awake) Wake(t);
        Engine.Extensions.Tabs("activated", t);
        ShowSelected();
        Render();
        SaveSoon();
        if (!Sheet.AnyOpen) t.Page?.Focus();
    }

    Tab? shownTab;

    /// Exactly one page shows: the space's selected tab. Only the page that
    /// changes is touched — the rest keep their state.
    void ShowSelected()
    {
        var sel = Space?.Selected;
        if (shownTab != sel && shownTab?.Page is { } previous)
        {
            previous.SetShown(false);
            shownTab.Left = DateTime.Now;
        }
        if (sel?.Page is { } page)
        {
            page.SetShown(true);
            sel.Frozen = false;
        }
        shownTab = sel?.Page != null ? sel : null;
        empty.Visibility = sel == null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// The page made and put in the window. It navigates on its own once ready.
    void Wake(Tab t, IPage? adopted = null)
    {
        if (t.Awake) return;
        var page = adopted ?? Engine.Create(new PageOwner(t.Space.Profile, t.Private, t));
        t.Page = page;
        t.Frozen = false;
        page.SetShown(t == Space.Selected);
        Pages.Children.Add(page.Element);
        Wire(t, page);
        if (adopted == null && !string.IsNullOrEmpty(t.Url) && t.Url != "about:blank") page.Navigate(t.Url);
    }

    public void Sleep(Tab t)
    {
        if (t.Page is not { } page) return;
        if (shownTab == t) shownTab = null;
        t.Page = null;
        Pages.Children.Remove(page.Element);
        page.Dispose();
        t.Loading = t.Audible = t.Reading = false;
        if (t.Pinned) t.Url = t.Home!;
        Render();
    }

    /// Memory, in three steps. A tab you leave is told to use as little as it
    /// can at once; after FreezeSeconds its page is frozen; after SleepMinutes
    /// the page is closed and the tab is only an address again. A tab
    /// playing sound is left alone.
    void SleepIdle()
    {
        var now = DateTime.Now;
        foreach (var t in Spaces.SelectMany(s => s.All).ToList())
        {
            if (!t.Awake || t == Current || t.Audible) continue;
            var away = now - (t.Left > t.Seen ? t.Left : t.Seen);
            if (Settings.SleepMinutes > 0 && away > TimeSpan.FromMinutes(Settings.SleepMinutes)) { Sleep(t); continue; }
            // Once per rest: Freeze is expensive on WebKit (context GC + caches).
            if (Settings.FreezeSeconds > 0 && away > TimeSpan.FromSeconds(Settings.FreezeSeconds) && !t.Frozen)
            {
                t.Frozen = true;
                _ = t.Page!.Freeze();
            }
        }
        EnforceAwakeCap();
    }

    /// One WebView2 controller per awake tab is the architecture; this bounds
    /// how many stay awake, sleeping the least recently used background pages.
    void EnforceAwakeCap()
    {
        int cap = Settings.MaxAwakeTabs;
        if (cap <= 0) return;
        var awake = Spaces.SelectMany(s => s.All).Where(t => t.Awake).ToList();
        int over = awake.Count - cap;
        if (over <= 0) return;
        foreach (var t in awake.Where(t => t != Current && !t.Audible)
                     .OrderBy(t => t.Pinned).ThenBy(t => t.Seen))
        {
            if (over <= 0) break;
            Sleep(t);
            over--;
        }
    }

    /// Called from the low-memory watcher: give back several background tabs.
    void RelieveMemory()
    {
        if (Space == null) return;
        var sleep = Spaces.SelectMany(s => s.All)
            .Where(t => t.Awake && t != Current && !t.Audible)
            .OrderBy(t => t.Pinned).ThenBy(t => t.Seen)
            .Take(3).ToList();
        foreach (var t in sleep) Sleep(t);
        if (sleep.Count > 0) Toast("Memory is low — sleeping background tabs");
    }

    /// Minimised: every page is frozen and this process gives back what it can.
    async Task FreezeAll()
    {
        foreach (var t in Spaces.SelectMany(s => s.All).ToList())
            if (t.Page is { } p && !t.Audible && t != Current) await p.Freeze();
        // Hand memory back without freezing the UI thread mid-minimise.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false, compacting: false);
    }

    // The page's own events ---------------------------------------------------

    void Wire(Tab t, IPage page)
    {
        page.Changed += () =>
        {
            if (t.Page != page) return;
            if (page.Url.Length > 0) t.Url = page.Url;
            if (page.Title.Length > 0 && page.Title != t.Title)
            {
                t.Title = page.Title;
                if (!t.Private) History.Retitle(t.Url, t.Title);
            }
            t.Loading = page.Loading;
            t.Audible = page.Audible;
            t.Muted = page.Muted;
            Render();
        };
        page.IconChanged += icon => { t.Icon = icon; Render(); };
        page.Finished += (url, title, ok) =>
        {
            if (ok && !t.Private) { History.Record(url, title, DateTime.Now); historyDirty = true; SaveSoon(); }
            if (ok && !t.Private && Uri.TryCreate(url, UriKind.Absolute, out var done) && done.Host.Length > 0)
            {
                // A link can cross hosts: the script the document started with
                // was for the old one, so apply this host's selectors now and
                // refresh the script for the next document.
                t.Page?.RefreshScripts();
                _ = t.Page?.Eval(Hidden.Apply(done.Host));
            }
            Bench?.Loaded(t);
        };
        page.Opened += (child, url, background) => OpenedBy(t, child, url, background);
        page.CloseRequested += () => Close(t);
        page.Message += json => Message(t, json);
        page.FullScreen += on => SetFullScreen(on);
    }

    /// A link that wants a new window: a tab right after its own. Ctrl held: in the background.
    void OpenedBy(Tab from, IPage child, string url, bool background)
    {
        var t = new Tab(from.Space, url) { Private = from.Private };
        int at = from.Pinned ? 0 : from.Space.Tabs.IndexOf(from) + 1;
        from.Space.Tabs.Insert(Math.Clamp(at, 0, from.Space.Tabs.Count), t);
        Wake(t, child);
        Engine.Extensions.Tabs("created", t);
        if (!background) _ = Select(t); else Render();
        SaveSoon();
    }

    void Message(Tab t, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.ValueKind == JsonValueKind.String ? JsonDocument.Parse(doc.RootElement.GetString()!).RootElement : doc.RootElement;
            var kind = root.GetProperty("kind").GetString();
            if (kind == "hide" && t.Host.Length > 0)
            {
                Hidden.Add(t.Host, new Veil
                {
                    Selector = root.GetProperty("selector").GetString() ?? "",
                    Label = root.GetProperty("label").GetString() ?? "",
                });
                Store.Save("hidden.json", Hidden);
                _ = RefreshHidden();
            }
            else if (kind == "hiding" && root.GetProperty("on").GetBoolean() && t == Current)
                Toast("Click something to hide it on " + Address.WithoutWww(t.Host));
            else if (kind == "key")
            {
                // A shortcut pressed inside the page: WebView2's WPF control has
                // no accelerator event, so the page posts a whitelist instead.
                var name = root.GetProperty("key").GetString() ?? "";
                bool ctrl = root.TryGetProperty("ctrl", out var c) && c.GetBoolean();
                bool shift = root.TryGetProperty("shift", out var s) && s.GetBoolean();
                bool alt = root.TryGetProperty("alt", out var a) && a.GetBoolean();
                if (KeyFor(name) is { } key) Run(key, ctrl, shift, alt);
            }
        }
        catch { }
    }

    static Key? KeyFor(string name) => name.ToLowerInvariant() switch
    {
        "t" => Key.T, "n" => Key.N, "l" => Key.L, "w" => Key.W, "k" => Key.K,
        "s" => Key.S, "r" => Key.R, "d" => Key.D, "h" => Key.H, "y" => Key.Y,
        "c" => Key.C, "i" => Key.I, "p" => Key.P, "u" => Key.U, "f" => Key.F,
        "," => Key.OemComma, "[" => Key.OemOpenBrackets, "]" => Key.OemCloseBrackets,
        "1" => Key.D1, "2" => Key.D2, "3" => Key.D3, "4" => Key.D4, "5" => Key.D5,
        "6" => Key.D6, "7" => Key.D7, "8" => Key.D8, "9" => Key.D9,
        "tab" => Key.Tab, "pagedown" => Key.PageDown, "pageup" => Key.PageUp,
        "arrowleft" => Key.Left, "arrowright" => Key.Right, "home" => Key.Home,
        "f5" => Key.F5, "f6" => Key.F6, "f11" => Key.F11,
        _ => null,
    };

    /// The scripts every page runs before its document: what you have hidden.
    IEnumerable<string> DocumentScripts(string host) { yield return Hidden.Script(host); }

    public async Task RefreshHidden()
    {
        foreach (var t in Spaces.SelectMany(s => s.All)) t.Page?.RefreshScripts();
        if (Current?.Page is { } here && Current.Host.Length > 0) await here.Eval(Hidden.Apply(Current.Host));
    }

    public Task RefreshShield() => Engine.ApplyShield(Settings.ShieldOn, Settings.ShieldPaused);

    public bool Paused(string host) =>
        Settings.ShieldPaused.Contains(Hidden.Key(host)) || Settings.ShieldPaused.Contains(host);

    // Going places --------------------------------------------------------------

    public string SearchTemplate => Core.Engine.Template(Settings.Engine, Settings.CustomEngine);

    /// What was typed, as an address or as a search.
    public string Resolve(string text)
    {
        var u = Address.File(text) ?? Address.Url(text);
        if (u != null) return Address.Reachable(u).AbsoluteUri;
        return Core.Engine.Url(text, SearchTemplate)?.AbsoluteUri ?? "about:blank";
    }

    public void Go(string text, bool newTab)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var url = Resolve(text);
        if (newTab || Current == null) Open(url);
        else Navigate(Current, url);
    }

    public void Navigate(Tab t, string url)
    {
        t.Url = url;
        t.Reading = false;
        if (t.Page is { } page) page.Navigate(url);
        else if (t == Current) Wake(t);
        Render();
    }

    public Tab Open(string url, bool select = true, bool priv = false, Space? space = null, int? index = null, bool external = false)
    {
        if (external)
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return Current!;
        }
        space ??= Space;
        var t = new Tab(space, url) { Private = priv };
        int at = index ?? space.Tabs.Count;
        space.Tabs.Insert(Math.Clamp(at, 0, space.Tabs.Count), t);
        Engine.Extensions.Tabs("created", t);
        if (select) _ = Select(t); else Render();
        SaveSoon();
        return t;
    }

    public void OpenPrivate() => OpenOmnibox(newTab: true, priv: true);

    /// Addresses handed over by Windows: a link clicked in another app.
    public void Arrived(string[] lines, bool activate = true)
    {
        if (!Started) { lock (earlyArrivals) earlyArrivals.Add(lines); return; }
        Tab? last = null;
        foreach (var raw in lines)
        {
            var arg = raw.Trim().Trim('"');
            if (arg.Length == 0 || arg.StartsWith("--")) continue;
            string url = Uri.TryCreate(arg, UriKind.Absolute, out var u) && (u.IsFile || u.Scheme.StartsWith("http"))
                ? u.AbsoluteUri : Resolve(arg);
            last = Open(url, select: false);
        }
        if (last != null) _ = Select(last);
        if (activate) Native.BringForward(this);
    }

    public void Close(Tab t)
    {
        var space = t.Space;
        bool wasSelected = space.Selected == t;
        if (t.Pinned)
        {
            Sleep(t);
            if (wasSelected) SelectAfterClosing(space, null, -1);
            Render(); SaveSoon();
            return;
        }
        int index = space.Tabs.IndexOf(t);
        if (index < 0) return;
        if (!t.Private && t.Url.Length > 0) closed.Push((t.Save(), space, index));
        space.Tabs.Remove(t);
        Sleep(t);
        Engine.Extensions.Tabs("removed", t);
        if (wasSelected) SelectAfterClosing(space, t, index);
        Render(); SaveSoon();
    }

    void SelectAfterClosing(Space space, Tab? gone, int index)
    {
        Tab? next = null;
        if (space.Tabs.Count > 0 && index >= 0) next = space.Tabs[Math.Min(index, space.Tabs.Count - 1)];
        next ??= space.All.Where(x => x != gone && x != space.Selected).OrderByDescending(x => x.Awake).ThenByDescending(x => x.Seen).FirstOrDefault();
        space.Selected = null;
        if (next != null && space == Space) _ = Select(next);
        else if (next != null) space.Selected = next;
        ShowSelected();
    }

    public void Reopen()
    {
        while (closed.Count > 0)
        {
            var (saved, space, index) = closed.Pop();
            if (!Spaces.Contains(space)) continue;
            var t = Open(saved.Url, select: false, space: space, index: index);
            t.Title = saved.Title ?? ""; t.Name = saved.Name;
            _ = Select(t);
            return;
        }
    }

    public void TogglePin(Tab t)
    {
        var s = t.Space;
        if (t.Pinned) { s.Pins.Remove(t); t.Home = null; s.Tabs.Insert(0, t); }
        else if (!t.Private) { s.Tabs.Remove(t); t.Home = t.Url; s.Pins.Add(t); }
        Render(); SaveSoon();
    }

    public void Rename(Tab t)
    {
        var name = Prompt.Ask(this, "Name this tab", t.Name ?? t.Display);
        if (name == null) return;
        t.Name = string.IsNullOrWhiteSpace(name) || name == t.Title ? null : name.Trim();
        Render(); SaveSoon();
    }

    void Mute(Tab t) { if (t.Page is { } p) p.Muted = !p.Muted; }

    public void Step(int by)
    {
        var all = Space.All.ToList();
        if (all.Count == 0) return;
        int i = Current == null ? -1 : all.IndexOf(Current);
        _ = Select(all[((i + by) % all.Count + all.Count) % all.Count]);
    }

    public void SelectNumber(int n)
    {
        var all = Space.All.ToList();
        if (all.Count == 0) return;
        _ = Select(n >= 9 ? all[^1] : all[Math.Min(n, all.Count) - 1]);
    }

    public void Back() => Current?.Page?.Back();
    public void Forward() => Current?.Page?.Forward();

    public void Reload()
    {
        if (Current is not { } t) return;
        if (t.Page is { } p) { t.Reading = false; p.Reload(); }
        else Wake(t);
    }

    // Spaces -------------------------------------------------------------------

    public void SwitchSpace(Space s, bool wake = true)
    {
        if (s == Space) return;
        Space = s;
        if (s.Selected == null && s.All.Any()) s.Selected = s.All.First();
        ShowSelected();
        Render();
        SaveSoon();
        if (wake && s.Selected is { } sel) _ = Select(sel, force: true);
    }

    public void StepSpace(int by, bool offerNew = false)
    {
        int i = Spaces.IndexOf(Space) + by;
        if (i >= Spaces.Count && offerNew) { NewSpace(); return; }
        if (i < 0 || i >= Spaces.Count) return;
        SwitchSpace(Spaces[i]);
        Toast(Space.Name);
    }

    public void NewSpace()
    {
        var made = NewSpaceDialog.Ask(this);
        if (made == null) return;
        Spaces.Add(made);
        SwitchSpace(made);
        OpenOmnibox(newTab: true);
    }

    void DeleteSpace(Space s)
    {
        if (Spaces.Count < 2) return;
        var n = s.All.Count();
        if (n > 0 && MessageBox.Show(this, $"Delete “{s.Name}” and close its {n} tab{(n == 1 ? "" : "s")}?", "AERIE",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        foreach (var t in s.All.ToList()) Sleep(t);
        if (s.Fresh) Engine.DeleteProfile(s.Profile);   // its sign-ins go with it
        int i = Spaces.IndexOf(s);
        Spaces.Remove(s);
        if (Space == s) { Space = null!; SwitchSpace(Spaces[Math.Max(0, i - 1)]); }
        Render(); SaveSoon();
    }

    void MoveToSpace(Tab t, Space to)
    {
        var from = t.Space;
        bool wasSelected = from.Selected == t;
        from.Tabs.Remove(t);
        if (from.Pins.Remove(t)) t.Home = null;
        if (from.Profile != to.Profile) Sleep(t);   // its sign-ins belong to the other space
        t.Space = to;
        to.Tabs.Add(t);
        if (wasSelected) SelectAfterClosing(from, t, -1);
        ShowSelected(); Render(); SaveSoon();
        Toast("Moved to " + to.Name);
    }

    // The page's own tools ---------------------------------------------------------

    public async Task Float(Tab t, string mode)
    {
        if (t.Page is not { } page) return;
        var value = await page.EvalWithGesture(Scripts.FloatCall(mode));
        if (mode != "toggle") return;
        if (value == null || value.Contains("none")) Toast("No video on this page");
        else if (value.Contains("refused")) Toast("This video can't float");
    }

    public async Task ToggleReader()
    {
        if (Current is not { } t || t.Page is not { } page) return;
        if (t.Reading) { t.Reading = false; page.Reload(); return; }
        var look = Theme.Look == "system" ? "system" : Theme.Look;
        var result = await page.Eval(Scripts.Reader(look));
        if (result?.Contains("read") == true) t.Reading = true;
        else Toast("Nothing to read on this page");
    }

    public async Task ToggleHiding()
    {
        if (Current?.Page is not { } page || Current.Host.Length == 0) return;
        await page.Eval(Scripts.Hider.Value);
        page.Focus();
    }

    public async Task Peek(string? selector)
    {
        if (Current?.Page is { } page) await page.Eval(Scripts.Peek(selector));
    }

    public async Task Restore(string host, string? selector)
    {
        if (selector == null) Hidden.Clear(host); else Hidden.Remove(host, selector);
        Store.Save("hidden.json", Hidden);
        await Peek(null);
        await RefreshHidden();
    }

    public void CopyCleanLink()
    {
        if (Current is not { } t) return;
        Clipboard.SetText(CleanLink.Clean(t.Url));
        Toast("Copied");
    }

    public void OpenDevTools() => Current?.Page?.OpenDevTools();

    public async Task ClearBrowsingData()
    {
        await Engine.ClearData();
        History.Visits.Clear();
        Store.Save("history.json", History);
    }
}
