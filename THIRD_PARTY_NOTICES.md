# Third-party notices

WinApk Launcher's own code is licensed under the GNU Affero General Public License v3.0
([LICENSE](LICENSE)).

A WinApk Launcher release (the installer and the portable zip) also contains the components
below. Each is used under its own license. The Android apps you run with it are not part of
WinApk Launcher and are not covered here.

## Win.apk runtime (`runtime\`)

License: **GNU AGPL-3.0** (`runtime\LICENSE.txt`).

The runtime is built from source by this repository's release workflow, from the fork
[5566brs/win.apk](https://github.com/5566brs/win.apk), which carries extra fixes. Each release's
notes, and `runtime\SOURCE.txt` in the installed files, give the exact repository and commit,
with a link to the source tree at that commit.

- **Win.apk** by israel2176: <https://github.com/israel2176/win.apk>, an unofficial port of
  Mac.apk to Windows x64. AGPL-3.0.
- **Mac.apk** by Kaleb Simpson / Voltare (kksimp): <https://github.com/kksimp/Mac.apk>.
  AGPL-3.0; its author also offers it under a commercial license. The "Mac.apk" name and logo
  belong to that project (see its `TRADEMARKS.md`). WinApk Launcher is a separate project and
  is not endorsed by the Mac.apk or Win.apk projects.
- Parts of the runtime's Java framework (`lib\android-framework.jar`) are derived from the
  **Android Open Source Project**, Apache-2.0.

The runtime folder in turn bundles:

| Component | Where | License |
|---|---|---|
| Eclipse Temurin JRE 21 (OpenJDK), from [Eclipse Adoptium](https://adoptium.net/) | `runtime\jre\` | GPL-2.0 with the Classpath Exception; the full notices are in `runtime\jre\legal\` and `runtime\jre\NOTICE` |
| ANGLE (`libEGL.dll`, `libGLESv2.dll`), Chromium's build as shipped in Electron v33.4.11 | `runtime\bin\` | BSD-3-Clause |
| SwiftShader (`vk_swiftshader.dll`) and the Vulkan loader (`vulkan-1.dll`), from the same Electron build | `runtime\bin\` | Apache-2.0 |
| `d3dcompiler_47.dll`, Microsoft's Direct3D shader compiler as redistributed with Chromium/Electron | `runtime\bin\` | Microsoft redistributable |
| Android platform resources (`framework-res.apk`), compiled from AOSP `frameworks/base/core/res` (android-16.0.0_r2) | `runtime\bin\tools\` | Apache-2.0 |
| [Bouncy Castle](https://www.bouncycastle.org/) `bcprov-jdk18on-1.79.jar` | `runtime\lib\` | Bouncy Castle License (MIT-style) |
| [SQLite](https://sqlite.org/) 3.54.1, linked into `winapk-runtime.exe` | `runtime\bin\` | Public domain |
| [libwebp](https://chromium.googlesource.com/webm/libwebp) 1.5.0 decoder, linked into `winapk-runtime.exe` | `runtime\bin\` | BSD-3-Clause |
| [stb_vorbis](https://github.com/nothings/stb), linked into `winapk-runtime.exe` | `runtime\bin\` | Public domain or MIT |
| LLVM libc++ / compiler-rt and the mingw-w64 runtime, linked statically into the native executables (MSYS2 CLANG64 toolchain) | `runtime\bin\` | Apache-2.0 with LLVM exception; mingw-w64 runtime: ZPL-2.1 and public domain |
| Fonts packed into `android-framework.jar`: Roboto, Roboto Condensed, Droid Sans Mono, Coming Soon | `runtime\lib\` | Apache-2.0 |
| Fonts packed into `android-framework.jar`: Noto Serif, Noto Sans Hebrew, Noto Sans Symbols, Source Sans Pro, Carrois Gothic SC, Cutive Mono, Dancing Script | `runtime\lib\` | SIL Open Font License 1.1 |

The license texts of the Chromium components (ANGLE, SwiftShader, the Vulkan loader and what
they include) are in `runtime\bin\licenses\LICENSES.chromium.html`.

The full license texts: Apache-2.0 <https://www.apache.org/licenses/LICENSE-2.0>,
BSD-3-Clause <https://opensource.org/license/bsd-3-clause>, SIL OFL 1.1
<https://openfontlicense.org/>, GPL-2.0 with the Classpath Exception
<https://openjdk.org/legal/gplv2+ce.html>, Bouncy Castle <https://www.bouncycastle.org/licence.html>.

## .NET runtime (next to `WinApkLauncher.exe`)

WinApk Launcher is published self-contained, so the .NET 10 runtime, Windows Forms and WPF ship
with it. License: **MIT**, Copyright (c) .NET Foundation and Contributors. They include
third-party components listed in
[dotnet/runtime THIRD-PARTY-NOTICES](https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT),
[dotnet/winforms THIRD-PARTY-NOTICES](https://github.com/dotnet/winforms/blob/main/THIRD-PARTY-NOTICES.TXT) and
[dotnet/wpf THIRD-PARTY-NOTICES](https://github.com/dotnet/wpf/blob/main/THIRD-PARTY-NOTICES.TXT).
The WPF native helpers (`*_cor3.dll`) are Microsoft redistributables that ship as part of .NET.

## Names

Android is a trademark of Google LLC. WhatsApp is a trademark of WhatsApp LLC (Meta). WinApk
Launcher is not affiliated with, endorsed by or sponsored by Google, WhatsApp or Meta.
