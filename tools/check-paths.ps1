# Fails when a built file contains a local path (for example C:\Users\<name>\ or the checkout
# folder), in ASCII/UTF-8 or UTF-16. Used by the release workflow and by hand after a publish.
#
#   powershell -ExecutionPolicy Bypass -File tools\check-paths.ps1 publish\WinApkLauncher.exe publish\WinApkLauncher.dll
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Files,
    [string[]]$Patterns
)
$ErrorActionPreference = 'Stop'
if (-not $Patterns) { $Patterns = @('C:\Users\', (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))) }
$bad = 0
foreach ($f in $Files) {
    $bytes = [System.IO.File]::ReadAllBytes((Resolve-Path $f))
    $ascii = [System.Text.Encoding]::GetEncoding(28591).GetString($bytes)   # byte-for-byte
    $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes)
    $utf16odd = if ($bytes.Length -gt 1) { [System.Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1) } else { '' }
    foreach ($p in $Patterns | Where-Object { $_ }) {
        foreach ($text in @($ascii, $utf16, $utf16odd)) {
            $i = $text.IndexOf($p, [System.StringComparison]::OrdinalIgnoreCase)
            if ($i -ge 0) {
                $ctx = $text.Substring($i, [Math]::Min(80, $text.Length - $i)) -replace '[^\x20-\x7E]', '.'
                Write-Host "LEAK in ${f}: $ctx"
                $bad++
                break
            }
        }
    }
}
if ($bad) { Write-Host "$bad local path(s) found"; exit 1 }
Write-Host "no local paths in $($Files.Count) file(s)"
exit 0
