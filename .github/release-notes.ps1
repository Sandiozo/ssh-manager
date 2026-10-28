# Release notes for one version: its section of CHANGELOG.md plus download instructions, the
# installer's SHA-256 and (-Signed) how to check its signature. Used by the release workflow; prints
# the notes to stdout.
param([Parameter(Mandatory)][string]$Version, [string]$Sha256 = "", [switch]$Signed)
$Version = $Version.TrimStart('v')
$root = Split-Path -Parent $PSScriptRoot
$text = Get-Content (Join-Path $root 'CHANGELOG.md') -Raw -Encoding utf8
$m = [regex]::Match($text, "(?ms)^## \[$([regex]::Escape($Version))\][^\n]*\n(.*?)(?=^## \[|^\[\d)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "See the [changelog](https://github.com/Sandiozo/ssh-manager/blob/main/CHANGELOG.md)." }
$notes += "`n`n---`n`n**Install:** download ``SSHManager-Setup.exe`` below and run it on every PC you want to use. " +
          "PCs that already have SSH Manager update themselves."
if ($Sha256) { $notes += "`n`nSHA-256 of ``SSHManager-Setup.exe``: ``$Sha256``" }
if ($Signed) {
    $notes += "`n`n``SSHManager-Setup.exe.sig`` is its release signature (SSH Manager checks it before installing an update). " +
              "To check it yourself, save the lines ``sshmanager-release ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINA7i2sOU03vcOMdvoyMlz09XEufb34fqN8PprEfNd8r`` and " +
              "``sshmanager-release ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAICYUvd8e+M5q9ZZMhL76qYzmUk3EN6eWRecEpkikPZIc`` (the offline backup key) " +
              "as ``allowed_signers``, then run: ``cmd /c ""ssh-keygen -Y verify -f allowed_signers -I sshmanager-release -n sshmanager-release -s SSHManager-Setup.exe.sig < SSHManager-Setup.exe""``"
}
$notes
