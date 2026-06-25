# Builds a self-contained Release publish of DiskMap.App and compiles the Inno Setup installer.
# Usage: pwsh installer/build.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "src\DiskMap.App\bin\Release\net10.0-windows\win-x64\publish"

Write-Host "Publishing DiskMap.App (self-contained, win-x64, Release)..." -ForegroundColor Cyan
dotnet publish (Join-Path $root "src\DiskMap.App\DiskMap.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = Get-ChildItem -Path "$env:LOCALAPPDATA\Programs", "C:\Program Files (x86)", "C:\Program Files" `
    -Filter "ISCC.exe" -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty FullName
if (-not $iscc) { throw "ISCC.exe (Inno Setup compiler) not found. Install via: winget install JRSoftware.InnoSetup" }

Write-Host "Compiling installer with $iscc..." -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot "DiskMap.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC compile failed" }

Write-Host "Done. Installer in installer\Output\" -ForegroundColor Green
