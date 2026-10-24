# Public release roadmap

The user intends to release the application. Both display names remain under evaluation. Keep the current internal identity stable; selecting one final display name should be a cosmetic decision.

Before calling this ready for distribution:

1. Complete the agreed product features still missing: Windows sign-in startup registration, usable per-app notification permissions/settings, download settings, and a clear first-run experience.
2. Reproduce real Gmail A/B, Calendar A/B and SoundCloud sign-in, sign-out, account switching, popup cancellation and provider/passkey flows. Fixtures do not establish universal embedded-provider compatibility. Resolve any blocking flows without extracting cookies or tokens.
3. Exercise sleep/resume, offline/reconnect, fullscreen, Windows scaling/display changes, hotkey conflicts, Explorer restart, crash recovery, long sessions and app removal. Measure laptop memory, startup, idle CPU and loaded switching.
4. Decide supported Windows/runtime versions; add installer/uninstaller, startup integration, version metadata, signing, update/rollback strategy and user-data migration. Define what uninstall preserves. Keep development tools, fixture profiles and personal credentials out of packages.
5. Review dependency security and third-party notices, select a project license, establish a repository/release process, and document permissions/data handling. No telemetry or credential collection has been requested.

Do not publish merely because a local prototype passes its tests. Work through a concrete release candidate and obtain the user's distribution authorization.
