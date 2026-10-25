# Changelog

All notable changes to WinApk Launcher are listed in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project
uses [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- On a small or scaled screen, the bottom of an app's window, with buttons like **Agree and
  continue**, no longer hides behind the taskbar. The runtime fits the whole window, title bar
  included, into the screen's work area. It does that by lowering the density the app draws at, so
  the app keeps its layout (360 x 800 dp stays 360 x 800 dp) and stays sharp. The window is fitted
  again when it moves to another monitor or the taskbar changes. This needs the runtime from
  `5566brs/win.apk` `bundle/v0.1.3`.
- Apps no longer fail when a folder name has letters that the Windows setting **Language for
  non-Unicode programs** can't show, such as a Windows user name in Hebrew on an English system.
  Before, installing the app stopped with "could not find java.dll" or "Illegal char <?>". The
  runtime from `bundle/v0.1.3` takes such paths by itself on Windows 10 version 1903 and later.
  On older Windows, or with an older runtime, the launcher moves each such path to a
  plain-letter folder under `C:\WinApk Launcher` (the data root, the temp folder, a copy of the
  APK, and a junction to the runtime's folder) and shows a one-line notice. Paths that the
  setting can show stay as they are. `--dry-run` shows what would move.
- When `%LOCALAPPDATA%` has such a letter, the installer installs to `C:\WinApk Launcher`
  instead of `%LOCALAPPDATA%\Programs\WinApk Launcher`, still for the current user only and
  without administrator rights.

### Changed

- A profile with the default phone size, 360 x 800, no longer sets `WINAPK_PHONE_SIZE`; the
  runtime uses that size by itself. `--dry-run` shows the phone size and whether it is sent.

## [0.1.2] - 2026-10-24

### Fixed

- A fresh install of WhatsApp no longer stays on **Starting…** forever. The bundled runtime's code
  converter used to replace one oversize, data-only static initializer in WhatsApp with a method
  that throws, and the app then failed in `Application.onCreate` with no window. The converter
  now computes that initializer's data at conversion time and keeps the class working. The fix
  is general, not specific to WhatsApp, and is proposed upstream as kksimp/dex2jvm#1. The runtime
  is now built from `5566brs/win.apk` `bundle/v0.1.2`.

### Known issues

- On WhatsApp's very first launch, it may show its own "corrupt installation" screen. It
  unpacks its native libraries during that first run, and the runtime picks them up only on the
  next start. Close WhatsApp and launch it again.


### Added

- A Hebrew user interface. In Hebrew, the windows read right to left: the toolbar, the tiles,
  the list and the dialogs are mirrored, while paths, addresses, ports and log lines stay left to
  right.
- A language setting: **…** > **Language / שפה** > **Automatic**, **English** or **עברית**.
  **Automatic**, the default, follows the Windows display language. The window switches at once,
  and `profiles.json` keeps the choice in a new `Language` field. `--lang` sets the language for
  one run.
- The installer asks for its language, English or Hebrew, and preselects the Windows display
  language.
- The window title shows the version, for example "WinApk Launcher 0.1.1". The startup window
  shows it too, in small text.
- The startup window shows the newest line of the run log, and its **Open log** link opens the
  run log at any time, not only after a failed start.
- When neither the run log nor the CPU time of the app's runtime has changed for 3 minutes, the
  startup window says that the app might be stuck and offers **Open log** and **Stop**. **Stop**
  asks first. When only the log is quiet and the CPU is busy, as in a long code conversion, it
  says **Still working (CPU busy)…**.
- Run logs have a size limit, 1 MB by default. A hidden log keeper process writes the runtime's
  output: the first 256 KB of the run, then a line "[… N MB of log omitted …]", then the newest
  output. The log stays readable while the app runs, and the keeper keeps it going after the
  launcher exits. If the keeper can't start, the runtime writes the log directly, as before.
- Old run logs are deleted when an app is launched: each app keeps its newest 10 run logs, and
  the logs folder stays under 50 MB, oldest logs first. The log of an app that is running is
  never deleted.
- The proxy adapter's `proxy.log` has the same size limit. When it's full, it becomes
  `proxy.log.1`, replacing the older one, and a new `proxy.log` starts.
- A **Logs…** dialog in the toolbar's **…** menu sets the limits (`MaxRunLogMB`, `KeepRunLogs`
  and `MaxLogsFolderMB` in `profiles.json`), shows the logs folder's size, and has a **Delete
  old logs** button, which keeps only the newest run log of each app.

### Changed

- The delete confirmation and the "runtime not found" question are now launcher dialogs, so
  their buttons follow the launcher's language.
- Quieter logs. Apps no longer log a line for every class they load (`MACAPK_QUIET_CLASSLOAD=1`),
  which was most of a run log, unless **Detailed class-loading log** is selected in the Edit
  dialog. Existing profiles keep their log level, and a new profile starts at log level 4 (Info)
  instead of all.

## [0.1.1] - 2026-10-23

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

[Unreleased]: https://github.com/5566brs/winapk-launcher/compare/v0.1.1...HEAD
[0.1.1]: https://github.com/5566brs/winapk-launcher/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/5566brs/winapk-launcher/releases/tag/v0.1.0
