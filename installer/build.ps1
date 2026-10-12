# Self-contained publish + Inno Setup compile -> installer\out\BS1Cooler-Setup-<ver>.exe
# -Version overrides the csproj version (CI passes the tag's version).
param([string]$Version)
$ErrorActionPreference = 'Stop'
Remove-Item "$PSScriptRoot\app" -Recurse -Force -ErrorAction SilentlyContinue
[string[]]$ver = if ($Version) { "-p:Version=$Version" } else { @() }
dotnet publish "$PSScriptRoot\..\cooler" -c Release -r win-x64 --self-contained true -o "$PSScriptRoot\app" @ver
if ($LASTEXITCODE) { throw 'publish failed' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found' }
& $iscc /Q "$PSScriptRoot\Bs1Cooler.iss"
if ($LASTEXITCODE) { throw 'ISCC failed' }
