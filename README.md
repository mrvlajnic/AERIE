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

*Audit of the current prototype — 63 findings: 18 fixed, 2 blockers, 16 major,
23 minor. Tiles are coloured by severity and sized by impact; the full map is
[docs/windows-port-audit.svg](docs/windows-port-audit.svg).*

## What works today

- One field for addresses and searches, with completion from local history
- Tabs down the side or across the top, pins, spaces, session restore
- Reading mode, element hiding, network-level ad blocking
- History, settings, private tabs, spaces with their own sign-ins
- Chrome extensions through WebView2's extension support
- An optional experimental WebKit engine (Playwright's WinCairo build)

## Known blockers

The audit found 63 issues; this batch fixes 18 of them. **Two blockers remain:**

1. **CRX3 signatures are never verified** — an altered extension package would
   be accepted.
2. **WebKit: any page can impersonate an extension** — its runtime trusts a
   client-supplied extension id.

Fixed in this batch, each verified against the running build:

- **Typing** — `InvariantGlobalization` made every WPF text box read-only.
- **Page-focused shortcuts** — Ctrl+L/T/W/K/R, Ctrl+Tab, F5, F6 and Alt+arrows
  now reach the window through an injected key bridge.
- **Top-tabs layout** — it had no address field at all; it does now.
- **Private tabs** — the omnibox no longer shows history or completions there.
- **Focus** — selecting a tab or dismissing a sheet no longer steals focus from
  an open panel.
- **Small input fixes** — the placeholder no longer covers a filled URL, typing
  in the empty state opens the field, Tab walks the suggestions, a bad custom
  engine no longer crashes, and horizontal wheel scrolling over the tabs works.
- **WebKit** — callbacks are marshalled to the UI thread and the eval map is
  concurrent.
- **Performance** — background tabs are frozen once per rest instead of every
  10 seconds (the WebKit GC storm), and minimise no longer runs a blocking GC.
- **Lifecycle** — page-init failures are visible, links arriving during startup
  are queued, and DevTools are off in release unless enabled in settings.
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
