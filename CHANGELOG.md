# Changelog

All notable changes to WinApk Launcher are listed in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1] - Unreleased

### Fixed

- Launching an app that is already running, or still starting, no longer starts a second copy
  that fails with "exited with code 3 (crashed launch)". If the app's window is open, the launcher
  brings it to the front. If the app is still starting, the launcher brings its startup window to
  the front and starts nothing. This applies to **Launch**, double-click, Enter, desktop
  shortcuts and `--launch`.
- When an app does exit with code 3, the launcher no longer reports a crash. It says that the app
  is already running, or still starting in the background, and offers **Wait**, **Show it** and
  **Stop it**. **Stop it** asks first, because the app loses anything it hasn't saved.
- The run log and the crash message no longer point to the app's `logs` folder in its data root
  before that folder exists.
- Command-line output now reaches a file or a pipe when you redirect it in cmd.exe, for example
  `WinApkLauncher.exe --version > version.txt`. It used to go to the console, and the file stayed
  empty.
- `SHA256SUMS.txt` in a release now has LF line endings, so `sha256sum -c SHA256SUMS.txt` works
  on Linux. The release workflow checks this file, and the hashes in the release notes, before it
  publishes.

### Added

- A startup window for every launch, from the launcher, a desktop shortcut or `--launch`. It
  shows the app's icon and name, **Starting…** with a progress bar, the time so far and the
  current step: **Installing the app…**, **Converting the app's code — first launch only, large
  apps take a few minutes…** or **Starting…**. It closes when the app's window opens. If the app
  exits first, it says so and offers **Open log** and **Open logs folder**. **Hide** closes only
  this window. The first launch of a large app, such as WhatsApp, can take several minutes.
- Startup progress in the launcher: the app's tile shows **Starting…** with a small spinner, and
  the status bar shows the current step.
- `--launch <name or id> --no-splash` launches without the startup window.
- An **Open logs folder** button in the crash message, which opens Explorer with that run log
  selected, and an **Open logs folder** command in the toolbar's **…** menu.
- **Run log** opens the app's newest log even after you restart the launcher.
- `--status <name or id>`, which prints whether an app is running or still starting, and
  `--self-test`, which runs the launcher's built-in checks.
- This changelog, which the release notes now start with.

## [0.1.0] - 2026-10-23

The first release.

### Added

- Tiles and list views, with each app's icon taken from its package.
- Per-app profiles, each with its own data root, phone size, Android API level, display density,
  JVM options and log level.
- A per-app network proxy: SOCKS5, SOCKS4a or HTTP CONNECT, with an optional username and a
  password that is stored encrypted, and a **Test** button. The proxy covers the app's Java
  traffic and the runtime's Google push client.
- Desktop shortcuts that launch one app.
- Command-line options `--launch`, `--dry-run`, `--test-proxy` and `--list`.
- Portable mode: with `portable.txt` next to `WinApkLauncher.exe`, the launcher keeps everything
  in its own folder.
- An all-in-one, per-user installer that includes the Win.apk runtime, built from
  [5566brs/win.apk](https://github.com/5566brs/win.apk) `bundle/v0.1.0`.
- Bundled runtime: Google push goes through the app's proxy.
- Bundled runtime: 29 UI fixes, found with two notes apps.

[0.1.1]: https://github.com/5566brs/winapk-launcher/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/5566brs/winapk-launcher/releases/tag/v0.1.0
