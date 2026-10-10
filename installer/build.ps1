# Self-contained publish + Inno Setup compile -> installer\out\BS1Cooler-Setup-<ver>.exe
$ErrorActionPreference = 'Stop'
Remove-Item "$PSScriptRoot\app" -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot\..\cooler" -c Release -r win-x64 --self-contained true -o "$PSScriptRoot\app"
if ($LASTEXITCODE) { throw 'publish failed' }
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" /Q "$PSScriptRoot\Bs1Cooler.iss"
if ($LASTEXITCODE) { throw 'ISCC failed' }
