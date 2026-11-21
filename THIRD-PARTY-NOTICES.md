# Third-party notices

## WebKit (the WebKit build only)

The WebKit build of Search ships WebKit's Windows port (WinCairo), as built by
Microsoft's Playwright project, unmodified, beside Search.exe. WebKit is
licensed under the GNU LGPL 2.1 (WebCore) and BSD-style licenses
(JavaScriptCore, WebKit). It is dynamically linked; you may replace those DLLs
with your own build of the same revision.

- WebKit source: https://github.com/WebKit/WebKit
- Playwright's WebKit build and patches: https://github.com/microsoft/playwright/tree/main/browser_patches/webkit
- The build used is the `webkit-win64.zip` of Playwright WebKit revision named in `.github/workflows/build.yml`, pinned there by SHA-256.
- The LGPL-2.1 and BSD-2-Clause texts are kept in `THIRD-PARTY-LICENSES/` in
  this repository and shipped in the WebKit download beside the browser.

The build also carries the libraries WebKit depends on (ICU, cairo, curl,
libxml2, libpng, libjpeg-turbo, libwebp, harfbuzz, OpenSSL/LibreSSL, sqlite,
nghttp2/3, brotli, and the Microsoft Visual C++ runtime), each under its own
license, as distributed in that archive.

## Search for Mac

Search for Windows is a port of Search by Office Commun (MIT); the reading
mode extraction script is theirs, unchanged.
