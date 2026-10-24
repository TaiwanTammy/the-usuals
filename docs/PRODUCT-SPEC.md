Original approved target specification. For the implemented state, later UI decisions, and remaining work, read the root README.md and CLAUDE.md.

# Personal Windows Sidebar — Product Specification

Approved product specification · 24 October 2026

## Purpose

A persistent dock on the left side of your laptop gives you quick access to Gmail, Google Calendar, SoundCloud, and websites you add later. Open an app beside your work or over it, then return to the same running page without a reload caused by closing the panel.

This is a personal Windows utility. Responsiveness, low background resource use, and reliable account handling matter more than cross-platform support.

## Confirmed product decisions

| Area | Required behavior |
|---|---|
| Dock | Left edge; reserves desktop space while visible |
| Dock width | Starts at 45 logical pixels; adjustable; respects Windows display scaling |
| Panel width | Starts at about one-third of the screen width; adjustable |
| Activation | Click an icon to open; click the active icon again to close |
| Visible apps | One at a time; other opened apps retain their running pages |
| Modes | Overlay and split view; remembered separately for each app icon |
| Always on top | User toggle; enabled initially |
| Startup | Starts with Windows; apps load only when clicked |
| Close | Hides the utility to the system tray and releases reserved space |
| Quit | Fully exits and releases reserved space |
| Keyboard | Escape closes either panel mode; configurable global shortcut |
| Appearance | Always-dark dock and panel frame; grayscale dock icons |
| Website theme | Prefer dark where supported; easy light/dark switch for content |
| Zoom | Ctrl +, Ctrl −, Ctrl 0; saved separately per app icon |
| Navigation | Back and forward controls |
| Notifications | Off by default; permission per app |
| Files | Normal uploads, downloads, attachments, and download-location setting |
| Restart | Restore settings, login sessions where valid, and last page where practical; load on click |
| Full screen | Full-screen apps may cover the dock; shortcut can request access again |

## Initial apps and accounts

Five icons: Gmail for Account A, Calendar for Account A, Gmail for Account B, Calendar for Account B, and SoundCloud.

Use distinct account labels so grayscale icons remain easy to distinguish. Proposed presentation: grayscale service icon with a short account label, tooltip, and a clear active indicator. Account labels are chosen locally; the utility does not need your credentials in its settings.

Proposed account model: two isolated browser profiles. Gmail and Calendar for Account A share that profile's login storage; Account B uses a separate profile. SoundCloud uses its own profile. Each icon still has its own persistent page instance. Shared cookies do not guarantee that a website will select the intended account: this must pass the compatibility test.

Adding a website requires a URL, name, icon, and profile selection. Reuse an existing profile or create a new isolated one. Support editing, removing, and dragging icons to reorder. Removing an icon does not silently erase an account profile; clearing stored site data is a separate explicit action.

## Dock and panel behavior

The dock occupies the left edge of the laptop display, respecting the Windows taskbar. Maximized windows leave room for its width. This reserved space is separate from always-on-top behavior: turning off always on top allows other windows to cover the dock, but keeps its reservation until the dock is hidden.

Only the laptop display receives a dock in version one. If that display becomes unavailable, move to the current primary display and clamp the utility to its usable bounds. Duplicated docks and advanced monitor configuration are outside version one.

Proposed virtual-desktop behavior: keep the same dock accessible when switching Windows virtual desktops, with one shared set of running pages. Validate Windows support during the shell prototype; desktop-space reservation and appearing on every virtual desktop are separate capabilities. If this requires fragile unsupported integration, bring the limitation back for review rather than silently promising it.

### Overlay mode

The panel opens directly to the right of the dock, over the active application. It does not reserve additional desktop space. Clicking another application dismisses the panel and lets the click reach its normal target. Escape or clicking the active icon also closes it.

Clicks on the dock, panel controls, authentication popups, file pickers, or other dialogs belonging to the utility must not trigger accidental dismissal. Switching directly to another dock icon replaces the visible app without a close-and-reopen flash.

### Split view

The panel reserves additional desktop space to the right of the dock, allowing maximized applications to fit beside it. It stays open when you click another application so you can copy and paste between the two. Escape, the active icon, or a visible close control closes it and releases the extra space.

Use Windows desktop-space reservation if the prototype confirms reliable behavior. Do not move every application window manually. Ordinary floating windows are not guaranteed to rearrange themselves; the core requirement is maximized windows fitting beside the panel.

### Switching and resizing

Each icon remembers its opening mode, panel width, and zoom. Opening a closed panel applies the selected app's mode and width. Switching icons while the panel is open preserves its current mode and width so the desktop does not reshuffle. A visible mode button explicitly switches the open panel between overlay and split view and saves that preference for the current app. Starting default: overlay for newly added apps.

Proposed sizing rule: initial content width is one-third of the display's usable width before the dock reservation. Drag the panel's right edge to resize; remember the final logical width. Clamp sizes after display or scaling changes so controls remain reachable and split view leaves useful room for another window. Exact minimums should be set during the laptop prototype.

Changing the dock width or split panel width updates the reservation. Commit split-view desktop resizing at the end of a drag if continuously resizing other applications feels slow or unstable.

### Focus and shortcuts

Opening an app gives its page keyboard focus. Reopening the same live page should preserve its insertion point and scroll position where the website allows. Clicking another application gives that application focus. Closing with the icon, Escape, or shortcut returns focus to the previously active application where practical.

Proposed global shortcut behavior: open and focus the last-used app if closed or hidden; close the panel if it is already focused. If no app has been used, show the dock. The shortcut is configurable and reports registration conflicts instead of failing silently.

Escape first dismisses a website-owned popup or active utility dialog when that control consumes Escape; otherwise it closes the panel. This avoids breaking ordinary website interactions.

The dock has a clear Show/Hide option in its tray menu. Hiding closes the visible panel, releases all reserved space, and keeps loaded apps alive, including SoundCloud playback. Showing restores the dock reservation. Quit ends those running pages and audio.

## Website content and browser controls

Use full websites in narrow, resizable views, preferring their own responsive layouts. Do not rebuild Gmail, Calendar, or SoundCloud. A narrow viewport does not guarantee that a service supplies a mobile or tablet layout. If one of the essential apps is unusable at the target width, review widening, zoom, or supported alternatives before committing to the build.

Provide a per-app Desktop/Mobile browser-identity button. Default to the native desktop identity; remember a requested mobile identity separately for each app. Match the request user agent, JavaScript user agent, and client-hint metadata. Changing identity deliberately reloads that app and the control must explain this. A website may ignore the request. Do not change identity to bypass a provider's sign-in restrictions.

The compact panel toolbar includes back, forward, reload, mode, content theme, zoom, open in browser, and close. Less-used actions can sit in a menu. Reload is explicit; switching, resizing, hiding, and changing mode must not recreate the page.

Links within the current app stay in the panel. Other websites open in the default browser. Use explicit app-domain rules for the initial services; do not assume every Google domain is the same app. Authentication redirects and popups are handled as login flows, not ordinary external links. The optional Open in browser action opens a URL; it cannot transfer unsaved page state or guarantee the same login session.

## Appearance

The dock and panel toolbar remain dark regardless of Windows theme. Icons remain grayscale by default, including the active icon; position, background, and labels communicate selection.

Website content requests dark mode by default. Provide an easy per-app light/dark preference. Websites with their own theme setting may require changing that setting inside the site. Do not promise forced dark rendering on every site; automatic color inversion is outside the initial scope.

Use a short, restrained opening/closing animation with no added wait before interaction. Respect Windows reduced-motion settings. Native web content may need host-window animation rather than a CSS transition; appearance must not dictate a fragile implementation.

## Persistence and laptop resource use

Navigate to an app's website on first use, then keep it until Quit. To reduce the first-click delay, prepare empty browser views at startup without navigating to websites. This consumes browser resources earlier; preparation must yield to interaction and must not take focus. Hiding, switching modes, switching icons, and tray hiding do not unload pages. Hidden SoundCloud continues playing. Do not automatically suspend a playing page.

Persist configuration locally: app list, profiles, order, sizes, modes, theme preferences, zoom, notification permissions, shortcut, startup preference, and practical last URLs. Browser profile storage retains valid login cookies. No cloud synchronization or custom password storage is needed.

After restart, show the dock with its panel closed and navigate to websites only on click. Empty browser views may be prepared in the background. Restored URLs must be safe, useful landing locations; avoid persisting authentication callbacks or one-time URLs. Websites remain responsible for saving their drafts and application data.

Sleep, network loss, crashes, website reloads, and expired authentication can interrupt live state or audio. On resume, preserve pages where possible and allow normal reconnection. A failed page offers a clear retry/reload action without automatically resetting every app or clearing logins.

Loaded websites will consume memory. The performance report must count the host and all browser subprocesses. Do not describe a small executable as proof of low runtime RAM use.

## Notifications, files, and permissions

Notifications are off by default. A site must have both utility permission and its own required permission before notifying. Initial scope is ordinary website notifications supported by the chosen runtime; do not build unread-count scraping or a custom mail polling service. Explain if notifications stop working when a site has not been loaded.

Support normal file selection, attachment uploads, downloads, opening the download folder, and choosing a default download location. Proposed default: the Windows Downloads folder. Show download progress and errors through a compact download list. File pickers must remain usable without collapsing the overlay.

Keep remote websites isolated from privileged desktop commands. Grant camera, microphone, location, and other sensitive site permissions only when requested and explicitly allowed. Updates to the browser runtime and host must have a maintainable path.

## Technical direction — provisional

Use the Windows AppBar mechanism for desktop-space reservation, with a dock window and separately managed panel host. Keep each app's browser instance independent of whether its panel is visible. Recompute bounds when the taskbar, display scaling, display availability, or Explorer shell changes.

Start the compatibility prototype with a Windows-native host using WebView2; C# with WPF is a reasonable first candidate because the scope is Windows-only and requires direct window integration. This is a starting hypothesis, not a proven memory or speed advantage. Tauri remains an alternative if it makes the host easier to maintain without compromising these behaviors. Avoid choosing Electron solely to get Chromium: WebView2 already supplies a Chromium-based runtime on Windows.

The first implementation decision is whether the required websites can authenticate and operate reliably in the candidate runtime. External-browser OAuth for a custom integration does not automatically log an embedded full Gmail website in. Do not solve this by copying Chrome cookies or evading provider restrictions. If essential full websites fail, pause that architecture decision and assess a real browser-hosted approach against the same product requirements.

## Build sequence

1. **Compatibility prototype:** test both Google accounts, Gmail and Calendar profile sharing/isolation, compact layouts, SoundCloud playback while hidden, attachments, login popups, zoom, and theme behavior. Record actual outcomes before building the full UI.
2. **Windows shell prototype:** prove dock reservation, expanded split reservation, overlay dismissal, always-on-top toggle, focus, tray hiding, fullscreen return, and DPI changes on the laptop.
3. **Core utility:** implement the five icons, mode and size persistence, live page switching, keyboard controls, and local settings.
4. **Complete version one:** add custom sites, permissions, file handling, theme controls, startup registration, and recovery behavior; package a local installer with uninstall cleanup.

The user approved this product specification and subsequently authorized managing and building the compatibility prototype. The [prototype build card](prototype-build-card.md) defines the current implementation scope and review gates.

## Acceptance checks

- Both Google accounts open in the intended Gmail and Calendar instances without account crossover.
- A Gmail draft and scroll position survive repeated panel close/open, icon switching, resizing, and mode changes without a host-triggered reload.
- SoundCloud continues playing while another app is visible and while the utility is hidden to tray.
- A maximized Chrome window respects the dock reservation, fits beside a split panel, and regains available width when that panel closes.
- Clicking Chrome dismisses overlay mode but leaves split view open. Owned dialogs do not accidentally dismiss the panel.
- Opening a closed panel restores the chosen app's saved mode and width. Switching icons keeps the open panel's layout fixed and restores the target app's own zoom and browser identity. Ctrl +, Ctrl −, and Ctrl 0 work when web content has focus.
- Hide and Quit release reserved desktop space. Show restores it. Verify abnormal termination and Explorer restart do not leave stale reserved space.
- Full-screen content can cover the dock; the shortcut requests the dock/panel again where Windows permits. Secure desktop prompts and exclusive fullscreen are outside a universal overlay guarantee.
- A restart restores configuration and valid logins without loading every site automatically.
- Dock icons remain grayscale; shell remains dark; website theme requests and limitations are clear.
- Adding a site, choosing its profile, and reordering icons require no code changes.

Proposed responsiveness target: an already-loaded panel responds visually within 100 ms and finishes its animation in roughly 150–200 ms on this laptop. These are targets to measure, not existing results. Test with all five pages loaded, including active SoundCloud playback. Record total memory, idle CPU, resume behavior, and perceived responsiveness; set a numeric memory budget after measuring the actual workload.

## Supporting references

- [Microsoft: Application Desktop Toolbars](https://learn.microsoft.com/en-us/windows/win32/shell/application-desktop-toolbars) — edge docking, work-area reservation, position updates, and full-screen notifications.
- [Microsoft: WebView2 API overview](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/overview-features-apis) — browser features available to a native host.
- [Google: OAuth policies](https://developers.google.com/identity/protocols/oauth2/policies) — embedded authentication restrictions that require early validation.
- [Tauri: Process model](https://tauri.app/concept/process-model/) — native webview architecture and platform differences.

## Review notes

The interview decisions are captured above. Proposed defaults still open to adjustment: overlay as the initial mode, account-label presentation, shortcut toggle behavior, per-app panel widths, Windows Downloads as the default download folder, and the provisional native WebView2 host. The browser compatibility results and performance measurements remain unknown until the prototype.
