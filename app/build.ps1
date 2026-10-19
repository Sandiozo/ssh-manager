# Builds the desktop app into app\dist\SSHManager (+ SSHManager.zip for a release) and the HiddenRun
# helper into bin\. Needs the .NET 8 SDK or newer.
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dist = "$root\dist\SSHManager"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
dotnet publish "$root\SSHManager\SSHManager.csproj" -c Release -o $dist --nologo
dotnet publish "$root\HiddenRun\HiddenRun.csproj" -c Release -o "$root\..\bin" --nologo
Remove-Item "$dist\*.pdb", "$dist\*.xml" -ErrorAction SilentlyContinue
Copy-Item "$root\installer\*" $dist -Force
Compress-Archive -Path "$dist\*" -DestinationPath "$root\dist\SSHManager.zip" -Force
"Built: $root\dist\SSHManager.zip"
