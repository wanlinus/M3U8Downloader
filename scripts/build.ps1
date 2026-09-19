# Build all projects (Debug)
# Usage: .\build.ps1 [-Configuration Debug|Release]
#
# NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less scripts
# as ANSI, so any non-ASCII (e.g. Chinese) text here corrupts the parser -- it
# once swallowed a closing quote and the whole script failed to parse with
# "The string is missing the terminator". Same rule as publish.ps1.

param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Build M3U8 Downloader ($Configuration) ==" -ForegroundColor Cyan

Write-Host "`n[1/3] Core library" -ForegroundColor Yellow
dotnet build "src\M3U8Downloader.Core\M3U8Downloader.Core.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host "Core build FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n[2/3] Self-test" -ForegroundColor Yellow
dotnet build "tests\M3U8Downloader.SelfTest\M3U8Downloader.SelfTest.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host "Self-test build FAILED" -ForegroundColor Red; exit 1 }

Write-Host "`n[3/3] WinUI app" -ForegroundColor Yellow
dotnet build "src\M3U8Downloader.App\M3U8Downloader.App.csproj" -c $Configuration -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { Write-Host "App build FAILED" -ForegroundColor Red; exit 1 }

$appExe = Join-Path $root "src\M3U8Downloader.App\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\M3U8Downloader.exe"

Write-Host "`nBuild finished." -ForegroundColor Green
Write-Host "  app: $appExe"
