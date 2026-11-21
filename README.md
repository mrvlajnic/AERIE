# AERIE

**Status: early development — a working prototype, not a daily driver.**

AERIE is a Windows-first browser built on Microsoft WebView2. It starts from
**Search**, the open-source browser for macOS by
[Office Commun](https://officecommun.com): the same philosophy — tabs that stay
out of the way, one field, no account, no telemetry — reimplemented for Windows
in C#/.NET.

This repository currently holds the inherited **Search for Windows** prototype.
The AERIE name is new; the application still builds as `Search.exe`, with the
original namespaces and file layout, while the identity and architecture are
being established.

![AERIE code audit treemap](docs/windows-port-audit.svg)

*Audit of the current prototype — 63 findings: **38 fixed (60%)**, no blockers,
3 major, 18 minor. Section percentages show how much of each area is fixed.
Tiles are coloured by severity and sized by impact; the full map is
[docs/windows-port-audit.svg](docs/windows-port-audit.svg).*

## What works today

- One field for addresses and searches, with completion from local history
- Tabs down the side or across the top, pins, spaces, session restore
- Reading mode, element hiding, network-level ad blocking
- History, settings, private tabs, spaces with their own sign-ins
- Chrome extensions through WebView2's extension support
- An optional experimental WebKit engine (Playwright's WinCairo build)

## Known issues

The audit found 63 issues; **38 are fixed (60%) and no blockers remain.**

The largest remaining items:

- **Memory** — one WebView2 per tab, and a process set per fresh space.
- **Store** — a corrupt file is silently replaced instead of quarantined.
- **WebKit** — find, print, picture-in-picture and passwords remain stubs.
- **UI tests** — CI still has none.
- **Packaging** — the WebKit bundle lacks the LGPL license texts.

Fixed in this batch, each verified against the running build:

- **Typing** — `InvariantGlobalization` made every WPF text box read-only.
- **Page-focused shortcuts** — Ctrl+L/T/W/K/R, Ctrl+Tab, F5, F6 and Alt+arrows
  now reach the window through an injected key bridge.
- **Top-tabs layout** — it had no address field at all; it does now.
- **Private tabs** — the omnibox no longer shows history or completions there.
- **Focus** — selecting a tab or dismissing a sheet no longer steals focus from
  an open panel.
- **Omnibox** — no longer dismisses on the activation bounce while opening,
  follows the browser window when it moves, and retries focus if Windows refuses
  activation.
- **Small input fixes** — the placeholder no longer covers a filled URL, typing
  in the empty state opens the field, Tab walks the suggestions, a bad custom
  engine no longer crashes, and horizontal wheel scrolling over the tabs works.
- **WebKit** — callbacks are marshalled to the UI thread and the eval map is
  concurrent.
- **Performance** — background tabs are frozen once per rest instead of every
  10 seconds (the WebKit GC storm), and minimise no longer runs a blocking GC.
- **Rendering** — the chrome is rebuilt only when its structure changes; titles,
  icons, audio and the address update in place, and a tab switch touches only
  the two pages involved.
- **History** — snapshots (2,000-entry cap) are serialized on a worker and
  flushed on exit, so a large history no longer blocks the UI thread.
- **Orphans** — a kill-on-close job object ties WebView2/WebKit children to the
  app, and the single-instance mutex and pipe are keyed by data folder.
- **Shield** — a compact public-suffix list replaces the two-letter heuristic.
- **Crashes** — two silent retries a minute, then a crash page with Ctrl+R;
  WebKit gets the same treatment instead of an endless reload.
- **Permissions** — camera, microphone, location and notifications ask once per
  site and are remembered; private tabs ask every time, and Settings can forget.
- **Downloads** — a Save dialog per download and a completion toast.
- **Space deletion** — a fresh space's profile and cookies are deleted with it.
- **History** — URL identity dedupes fragments and strips credentials; ranking
  is match quality × log(2 + frecency); one letter finishes a front door; a
  typo within one edit still finds the page.
- **Lifecycle** — page-init failures are visible, links arriving during startup
  are queued, and DevTools are off in release unless enabled in settings.
- **Accessibility** — every button, tab, pin and toggle is now Tab-reachable,
  activates with Enter or Space, shows a focus glow, and exposes a real UIA
  Button with an Invoke pattern (checked through the accessibility tree).
- **CRX3** — the signature and extension id are verified over the signed
  header and archive before anything is unpacked; tampered, wrong-id, CRX2 and
  zip-traversal packages are rejected (96 tests).
- **WebKit extensions** — the page-world runtime is disabled: any page could
  act as an installed extension. The menu and settings explain it; WebView2
  remains the supported extension path.
- **Core parser** — `search:` is no longer treated as a URL, any IPv6 literal
  parses, file URLs may contain spaces, and titles decode their paths; a parser
  regression corpus brings the suite to 96 tests.
- **Cosmetic blocking** — the ad-slot CSS is emitted one rule per selector, so
  one selector the engine stops understanding cannot disable the rest.
- **CI** — the benchmark job now fails when the WebView2 run crashes or writes
  no report, and the WebKit archive is checked for `WebKit2.dll`.

The remaining blockers need a decision (CRX3 verification, and whether to keep,
fix or disable the WebKit extension host), so they are deliberately left for
the next discussion.

## Building

Requirements: Windows 10/11, the .NET 8 SDK or newer, and the WebView2
Evergreen Runtime (present on Windows 10 and 11 by default).

```
dotnet build Search.sln
dotnet test Search.sln
dotnet publish src/Search -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o out
```

Run `out\Search.exe`. The framework-dependent build needs the .NET 8 Desktop
Runtime; a self-contained build is available with `--self-contained true`.

## A note on the names

The binary, namespaces, settings folder (`%LOCALAPPDATA%\Search`) and file names
still say "Search". That is deliberate for now: the audit, tests and behaviour
are unchanged by branding, and the rename comes with the product identity.

## Attribution

Search for Mac is by [Office Commun](https://officecommun.com), MIT-licensed —
see [LICENSE](LICENSE) and [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
AERIE is an independent, unofficial Windows implementation; it is not affiliated
with or endorsed by Office Commun. "Search" and its icon remain Office Commun's.

The inherited port's own notes — feature table, memory measurements, WebKit
caveats — are kept in [docs/port-README.md](docs/port-README.md).
