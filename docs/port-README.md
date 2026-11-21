# Search for Windows

A Windows port of [Search](https://searchbrowser.app), the small, fast, quiet browser for the Mac by
[Office Commun](https://officecommun.com) ([source](https://github.com/driceroland/Search), MIT).
This is an unofficial port: Office Commun does not make or support it.

A row of tabs, down the left or across the top, and the page. No toolbar, no start page,
no account, no telemetry.

## Two engines

**WebView2** is Microsoft Edge's Chromium engine. Every Windows 10 and 11 PC already has it, so Search is a single ~1.5 MB exe. **This is the default, and the one that uses the least memory.**

**WebKit** is the engine Search uses on the Mac, through WebKit's own Windows port (WinCairo, as built by Microsoft's Playwright project). It's in the *WebKit build*: a zip with Search.exe and WebKit's files beside it (~65 MB zipped). Choose it in Settings › Memory.

### What the measurements say

CI runs the same six sites in both engines on the same Windows machine. It adds up the private memory of every process the browser starts:

| Private MB (processes) | Empty window | 6 tabs open | Background tabs frozen | Background tabs asleep |
|---|---:|---:|---:|---:|
| WebView2 | 46 (1) | 680–707 (13–14) | 580–621 (13) | 266–295 (7) |
| WebKit, GPU compositing on | 310 (1) | 3,493 (17) | 3,553 (17) | 1,656 (7) |

The WebView2 figures span several runs. WebKit completed the benchmark once.

**This WebKit build uses about five times WebView2's memory with the same tabs open,** and freezing background tabs doesn't bring it down. Pages loaded about as fast in both.

**WebKit on Windows is an experiment, not a recommendation:**
- **Security:** it's built for testing, not shipped in any browser. It gets security fixes only when Playwright rebuilds it, and its Windows sandboxing is weaker than Edge's.
- **Stability:** it still crashes intermittently when a second tab opens. The cause is being traced in CI.
- **No content blocker:** this build can't compile content rules at all, so network-level ad blocking and extensions' `declarativeNetRequest` rules can't work on it. The ad blocker falls back to hiding empty ad slots on the page.
- **Missing for now:** downloads, find on page, fullscreen video and picture-in-picture.

## Download

Get the builds from the **Actions** tab (artifact `Search-windows`), or from Releases once a `v*` tag is pushed:

| File | Size | Needs |
|---|---|---|
| `Search-win-x64.exe` / `Search-win-arm64.exe` | ~1.5 MB | [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows offers to install it the first time) |
| `Search-win-x64-standalone.exe` / `…arm64-standalone.exe` | ~60 MB | nothing |
| `Search-WebKit-win-x64.zip` | ~65 MB | .NET 8 Desktop Runtime; unzip and run Search.exe (WebKit engine, x64 only) |

Both need WebView2, which Windows 10 and 11 already have. If it's missing, Search links to the installer.

## Memory

Search is built to use as little RAM as it can, whichever engine draws the pages.

**Both engines:**
- **Restored tabs cost nothing.** Tabs from your last session come back as addresses only until you click one.
- **Background tabs are frozen after 20 seconds.** A tab you leave is told to use as little memory as it can, then frozen. With WebView2 that's `TrySuspendAsync`. With WebKit the page is taken out of the window and its timers are throttled.
- **Idle tabs sleep after 10 minutes.** The page is closed and the tab is just an address until you click it.
- **Tabs playing sound are left alone.**
- **Minimising the window** freezes every page and compacts the app's own memory.

**WebView2 only:**
- **Same-site tabs share a process** (`--process-per-site`).
- **Chromium's low-memory mode** (`--enable-low-end-device-mode`) is on by default. Turn it off in Settings › Memory if gradients look banded.

**WebKit only:**
- **No idle processes:** no prewarmed or cached spare processes.
- **Small caches:** the smallest cache model, and no back-forward cache in lean mode.
- **No GPU compositing in lean mode:** pages are drawn in software instead.
- **Optional shared process (experimental, currently crashes):** *all tabs of a space share one page process*. One crash takes every tab down, and sites aren't kept apart.

**Measured, not guessed.** CI runs `Search.exe --bench` on a Windows machine for each engine setup. Each run opens the same six sites and adds up the memory of every process the browser started. The results are in the job summary of the latest *Build* run, with a per-process breakdown in the log; the table above comes from those runs.

## What carries over

| Search for Mac | Windows | Shortcut |
|---|---|---|
| One field for addresses and searches, finished from your own history | ✓ | Ctrl+L, Ctrl+T, F6, Alt+D |
| Tabs down the side or across the top | ✓ | Ctrl+Shift+S |
| Fold the sidebar away; the left edge brings it back | ✓ | Ctrl+S |
| Pins: a letter or favicon, kept across restarts; closing one puts it to sleep | ✓ | Ctrl+D pins / unpins |
| Spaces, each with its own icon; "signed in" or "fresh" (own cookies and sign-ins) | ✓ (fresh spaces use their own WebView2 profile) | Ctrl+Alt+← / →, two-finger swipe over the tabs |
| Search open tabs | ✓ | Ctrl+K |
| Reading mode (the same extraction script as the Mac app) | ✓ | Ctrl+Shift+R |
| Floating video above every app | ✓ (Chromium picture-in-picture) | Ctrl+Shift+P |
| Ad blocker that runs before the page | ✓ (network-level request blocking plus the same short cosmetic list) | per site from the lock icon |
| Hide anything, for good, on that site | ✓ | Ctrl+Shift+H; review with Ctrl+Shift+U |
| Private tabs | ✓ (InPrivate) | Ctrl+Shift+N |
| Reopen closed tab | ✓ | Ctrl+Shift+T |
| Copy clean link (tracking parameters removed) | ✓ | Ctrl+Alt+C |
| History panel | ✓ | Ctrl+H |
| Web Inspector and console | ✓ (Edge DevTools) | F12, Ctrl+Shift+I, Ctrl+Shift+J |
| Chrome extensions from the Chrome Web Store | ✓ see below | |
| Passwords | ✓ saved by the engine, encrypted with your Windows account (not Windows Hello-gated) | |
| Light, dark, or as the system is | ✓ | |
| Default browser | ✓ Settings › This PC | |
| Rename, duplicate, mute, drag to reorder, middle-click to close | ✓ | |
| Find, print, zoom | the engine's own | Ctrl+F, Ctrl+P, Ctrl+ + / − / 0 |

**Not ported yet:** Split View, Import from other browsers, Sync, bookmarks bar, automatic updates,
the AI features, passkeys via Windows Hello, Translate, site styles, and the many smaller
features in the Mac app's 117k lines. Extension popups open in a small window rather than
anchored to a toolbar button.

## Chrome extensions

Open an extension's page in the Chrome Web Store, then use the puzzle button › *Add This Extension*. Or paste the link in Settings › Extensions, or load an unpacked folder.

- **On WebView2,** extensions run in Edge's own extension support.
- **On WebKit,** Search is the extension engine. The Mac app runs Chrome extensions on Safari's extension engine, but WebKit for Windows has none, so Search implements Chrome's extension API itself:
  - **Its own origin:** each extension's files are served on a private loopback port (`http://127.0.0.1:port/`), standing in for `chrome-extension://id/`.
  - **Background:** a hidden page stands in for the background service worker (`importScripts` works).
  - **Content scripts** run in every page they match, with their CSS, at their `run_at` time.
  - **APIs:** `runtime` (messages, ports, `onInstalled`), `storage` (local, sync, session, `onChanged`), `tabs`, `scripting`, `i18n`, `alarms`, `action` (badge, popup), `webNavigation`, `notifications`, `permissions`.
  - **Network rules:** `declarativeNetRequest` static, dynamic and session rules are compiled to WebKit's content blocker, as Safari does. Redirect and header-changing rules are skipped.
  - **Not yet:**
    - `webRequest` blocking, which WebKit's API can't intercept. So classic uBlock Origin won't work; uBlock Origin Lite will.
    - Showing context menus.
    - `identity`, `offscreen` beyond a stub, and native messaging.
  - **Known gap:** content scripts share the page's JavaScript world rather than an isolated one. The page can't reach `chrome.*`, but unlike in Chrome it can see the DOM changes and prototypes a content script touches.

## Where things are kept

Everything is in `%LOCALAPPDATA%\Search`: `settings.json`, `session.json`, `history.json`,
`hidden.json`, `extensions.json`, and the engine's data in `Web\`. Nothing leaves the PC except
the pages you ask for and their icons.

## Building

```
dotnet test tests/Search.Core.Tests
dotnet publish src/Search -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

The window is built in C# without XAML and references WPF directly, so it builds from Windows,
macOS or Linux (`EnableWindowsTargeting`). It only runs on Windows.

- `src/Search.Core`: platform-free logic (address parsing, search engines, the ad blocker,
  clean links, history, hidden elements, Chrome Web Store packages) plus the injected scripts. Unit tested.
- `src/Search`: the WPF window, panels, settings, and `Engines/` (the WebView2 engine, the WebKit engine through WebKit's C API, and the extension runtime host).

`dotnet build` works anywhere. Running the WebKit engine needs WebKit's files beside the exe. CI downloads them; see `.github/workflows/build.yml`.
