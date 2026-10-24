# The Usuals / Pane in the Apps

A Windows sidebar for the web apps you open all day. A slim dock on the left edge of the desktop holds Gmail, Google Calendar, SoundCloud and any websites you add. Click an icon and the app opens in a panel beside your work or over it, still logged in and exactly where you left it.

Both names are candidates. The app alternates between them on purpose while the final name is chosen. This is a personal prototype; a public release is the goal but not yet the state. See `docs/RELEASE.md`.

## Using it

- Built-in apps: Gmail A, Calendar A, Gmail B, Calendar B and SoundCloud. Gmail and Calendar share a login within one account; A and B stay separate. Added websites each get their own login storage.
- One app is visible at a time. Hidden apps keep running. The panel's × only hides it; right-click an icon and choose **Close** to unload the app and stop its audio.
- Click **+** at the bottom of the dock to add a website with a name, address and icon. Drag icons to reorder them.
- The toolbar has back/forward, Open in browser, drawer or side-by-side placement, Desktop/Mobile, Always on Top, zoom (also Ctrl +, Ctrl −, Ctrl 0) and dark/light mode. More buttons appear as the panel widens. Drag the panel's right edge to resize it.
- The gear at the bottom holds the keyboard shortcut, Hide and Quit.

Settings and logins are stored in `%LOCALAPPDATA%\SidebarPrototype`, outside this folder.

## Build and run

Requires Windows, the .NET 10 SDK (`winget install Microsoft.DotNet.SDK.10`) and the WebView2 runtime.

```powershell
.\scripts\Build.ps1 -Publish
.\scripts\Test.ps1 -Mode All
```

`Build.ps1 -Publish` writes the app to `build\publish`. Copy it into `app\` after quitting the running copy, then launch it with `Start.cmd`. Test modes are `Quick`, `Ui`, `Unload`, `Native` and `All`. `Test.ps1` builds its own `TestHarness` copy with the test code included; the published app contains none of it. Tests use throwaway profiles under `build\test-runs`, never real logins. Native tests briefly reserve desktop space and restore it.

## Not done yet

Starting with Windows, per-app notification and download settings, and a first-run setup. Real Google and SoundCloud sign-in flows and Windows passkey dialogs have not been tested automatically.
