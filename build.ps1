# Builds WinApk Launcher.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Version 0.1.0]
#       -> publish\  (self-contained win-x64 folder: WinApkLauncher.exe + the .NET runtime)
#
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Version 0.1.0 -RuntimeDir <staged Win.apk folder>
#       -> also dist\WinApkLauncher-Setup-0.1.0.exe and dist\WinApkLauncher-0.1.0-portable.zip
#
# The release workflow (.github\workflows\release.yml) does the same on GitHub, with a runtime
# built from source.
param(
    [string]$Version = '0.0.0-dev',
    [string]$RuntimeDir,
    [string]$Output = (Join-Path $PSScriptRoot 'publish'),
    [string]$Dist = (Join-Path $PSScriptRoot 'dist')
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path $Output) { Remove-Item -Recurse -Force $Output }
dotnet publish "$PSScriptRoot\WinApkLauncher.csproj" -c Release -r win-x64 --self-contained true `
    -o $Output "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Write-Host "Published: $Output\WinApkLauncher.exe"

if (-not $RuntimeDir) { return }
$RuntimeDir = (Resolve-Path $RuntimeDir).Path
if (-not (Test-Path "$RuntimeDir\bin\winapk-runtime.exe")) { throw "no bin\winapk-runtime.exe in $RuntimeDir" }
New-Item -ItemType Directory -Force $Dist | Out-Null

# Installer (Inno Setup 6)
$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found' }
& $iscc "/DLauncherDir=$Output" "/DRuntimeDir=$RuntimeDir" "/DAppVersion=$Version" "/O$Dist" `
    "$PSScriptRoot\installer\WinApkLauncher.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)" }

# Portable zip: launcher + runtime + portable.txt
$name = "WinApkLauncher-$Version-portable"
$stage = Join-Path $Dist $name
if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$Output\*" $stage -Recurse
Copy-Item $RuntimeDir "$stage\runtime" -Recurse
Get-ChildItem "$stage\runtime" -Recurse -File -Include '*.orig', '*.prev-*', '*.bak' | Remove-Item -Force
Copy-Item "$PSScriptRoot\installer\portable.txt" $stage
Copy-Item "$PSScriptRoot\LICENSE" "$stage\LICENSE.txt"
Copy-Item "$PSScriptRoot\THIRD_PARTY_NOTICES.md", "$PSScriptRoot\README.md" $stage
$zip = Join-Path $Dist "$name.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path $stage -DestinationPath $zip
Remove-Item -Recurse -Force $stage
Write-Host "Installer and portable zip in $Dist"
