# Cold-start handoff

Read this file and `README.md` before editing. This is an existing Windows desktop application, not a website. The user intends to release it publicly eventually. The source is in a public GitHub repository, so never commit personal data, credentials, browser profiles or absolute paths from this machine. The displayed name alternates between **The Usuals** and **Pane in the Apps** on purpose while the user chooses a final name.

## Working rules

- Use Sonnet or Luna subagents for suitable implementation work, with explicit file ownership. Coordinate and review their work; do not delegate away the final judgment.
- The user expects bug checking and hidden-assumption review before and after a build. Inspect the actual WPF UI, not a replacement HTML mockup. Use bounded visual passes.
- Do not open localhost pages showing plans or proposals. Local fixture servers inside automated browser tests are test infrastructure, not presentation pages.
- Keep UI compact, dark, and restrained. The user rejected oversized chrome and dated controls. Use “dark mode” and “light mode,” never “Theme” as interface copy. Official app icons are grayscale at rest, original color on hover. Mode, external-browser, and appearance actions use recognizable icons with tooltips.
- For structural code exploration use the installed `codebase-memory` skill if available. If graph tools are unavailable, use bounded source/`rg` inspection and say so; never invent graph coverage.
- Complete authorized reversible work without repeated permission questions. Do not publish releases, message others, or touch unrelated software without authorization.

## Technical map

- .NET 10, C#, WPF plus a WinForms tray icon, Microsoft WebView2 `1.0.4314.35`. Builds use the system-wide .NET 10 SDK.
- `Program.cs`: application startup, host lifecycle, WebView setup, placement, toolbar dispatch, zoom/identity and fixture runners.
- `DockUi.cs`: dock controls, shared button/menu templates, add/reorder UI, custom favicon/image handling.
- `PanelWindow.cs`: compact toolbar, responsive overflow, retained page surfaces, native window hook and frame behavior.
- `Core.cs`: persisted settings, launchers/catalog, URL rules, sizing and visible-state generations.
- `AuthenticationHost.cs`, `AuthSurface.cs`, `NavigationSession.cs`, `AppReturnPolicy.cs`: popup lifecycle, accepted redirects, app return, explicit external-browser routing and unload ownership.
- `BrowserIdentity.cs`: user agent/client hints/mobile metrics.
- `AppBranding.cs` / `AppBrandingHost.cs`: pure name selection and six-hour display rotation. `BrandingMode` defaults to `Rotate`; `TheUsuals` and `PaneInTheApps` are fixed-name modes for the eventual decision. `LastBrandIndex` persists the sequence. There is no name-selection settings UI yet; do not imply one exists.
- `Shell/`: Windows AppBar reservation, DPI, hotkey and focus behavior.
- `UiRegression.cs`, `UnloadRegression.cs`: isolated integration checks, currently compiled into the app and run through hidden command-line switches. `tests/PolicyProbe` links pure source files directly; preserve the relative `src/` and `tests/` structure.

## Invariants learned through real failures

1. **Stable identity:** keep assembly/namespace, singleton identity, `%LOCALAPPDATA%\SidebarPrototype`, and named account profiles stable when changing branding. Cosmetic names must not fragment logins or settings. `AccountA` is shared by Gmail A/Calendar A; `AccountB` by B; SoundCloud and added websites are separate. Renaming the exe or data folder needs a one-time migration and must happen before public release.
2. **No switch-driven layout churn:** apply saved width/mode when opening a closed panel. While open, app switching keeps the current width/mode. Only deliberate placement/resize/open/close/hide events should alter AppBar reservation.
3. **Native frame:** both dock and panel receive `WS_EX_TOOLWINDOW` before their first reservation/show. Removing this reproduced large DPI/work-area jumps. The panel uses zero caption/glass framing and a right-edge resize target. Install the HWND hook in production as well as tests: `WM_EXITSIZEMOVE` commits width. Keep native geometry checks, including dock samples while the panel is closed.
4. **Unload ownership:** closing an app invalidates its epoch and disposes all of its current, retired and owned popup views. Late asynchronous initialization/popup/promotion cannot resurrect it, retain visuals, report a stale error, or crash an async event boundary. Other pages sharing the profile must survive. Preserve profile storage.
5. **Authentication:** owned sign-in surfaces live inside the panel. Keep accepted HTTPS redirects/POST callbacks in their profile. Successful app return can promote the existing surface without reloading. A scoped `window.close` bridge prevents closing the host. Do not export cookies/tokens or bypass provider authentication. Windows/provider passkey UX can behave differently from controlled fixtures.
6. **Buttons and menus:** local background properties previously defeated hover triggers; target the named template border. A full ContextMenu template is needed to remove the default white gutter. Don't duplicate visible commands in overflow. Wider panels promote hidden actions.
7. **Added apps:** validate URLs and persisted entries individually; one malformed custom app must not reset unrelated preferences. Stable unique profile IDs survive reorder/restart. Drag needs a threshold, actual drop handling and click suppression. Async favicon loading is bounded and falls back safely. A chosen icon needs both grayscale and original-color layers.

## Build, verification, and delivery

Use `scripts/Build.ps1` and `scripts/Test.ps1`; paths are relative to the project. Generated `bin/obj` and `build/` can be regenerated; test results land in `build/test-results` and are not committed.

Publish to `build/publish`, verify the candidate, then copy it to `app/` (the user's running copy, git-ignored) only after the existing app has quit. Never overwrite a running version. Clearly distinguish executed tests, source review, and live service behavior. A test's status must actually gate all claimed checks; a dictionary entry or null controller alone is not proof of disposal. Live Google/provider credentials and OS passkey flows have not been automated, and the DevTools renderer-crash injection check was intermittent; do not describe it as proven. Public release is a goal, not a completed state or permission to publish. Next priorities are in `docs/RELEASE.md`.

## Data safety

Never delete, move, inspect for credentials, or commit `%LOCALAPPDATA%\SidebarPrototype\browser-data`. Test data belongs under a unique `build/test-runs` directory. Other applications in `D:\Software` are unrelated.
