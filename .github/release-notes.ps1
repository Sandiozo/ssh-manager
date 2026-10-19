# Release notes for one version: its section of CHANGELOG.md plus download instructions and the
# installer's SHA-256. Used by the release workflow; prints the notes to stdout.
param([Parameter(Mandatory)][string]$Version, [string]$Sha256 = "")
$Version = $Version.TrimStart('v')
$root = Split-Path -Parent $PSScriptRoot
$text = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding utf8
$m = [regex]::Match($text, "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|^\[\d)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "See the [changelog](https://github.com/Sandiozo/ssh-manager/blob/main/CHANGELOG.md)." }
$notes += "`n`n---`n`n**Install:** download ``SSHManager-Setup.exe`` below and run it on every PC you want to use. " +
          "PCs that already have SSH Manager update themselves."
if ($Sha256) { $notes += "`n`nSHA-256 of ``SSHManager-Setup.exe``: ``$Sha256``" }
$notes
