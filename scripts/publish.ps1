# Publish self-contained builds (target machine needs NO .NET / Windows App SDK runtime)
# Usage: .\publish.ps1 [-Runtime win-x64|win-x86|win-arm64] [-SkipApp] [-SkipCli] [-Version 1.2.3]
#
# -Version is optional: it stamps the assembly version (CI passes the tag, so the
# built exe reports the released version instead of the project default).

param(
    [ValidateSet('win-x64','win-x86','win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipApp,
    [switch]$SkipCli,
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 交给 dotnet 的版本参数（不指定就沿用项目里的默认值）
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

Write-Host "== Publish M3U8 Downloader ($Runtime, self-contained) ==" -ForegroundColor Cyan
if ($Version) { Write-Host "   version: $Version" -ForegroundColor Cyan }

if (-not $SkipCli) {
    Write-Host ""
    Write-Host "[1/2] CLI" -ForegroundColor Yellow
    $cliOut = Join-Path $root "publish\cli-$Runtime"
    dotnet publish "src\M3U8Downloader.Cli\M3U8Downloader.Cli.csproj" -c Release -r $Runtime -o $cliOut @versionArgs
    if ($LASTEXITCODE -ne 0) { exit 1 }
    Write-Host ("  -> " + (Join-Path $cliOut 'm3u8dl.exe')) -ForegroundColor Green
}

if (-not $SkipApp) {
    Write-Host ""
    Write-Host "[2/2] WinUI app" -ForegroundColor Yellow
    $appOut = Join-Path $root "publish\app-$Runtime"

    # A running copy locks the DLLs in the publish folder, which surfaces as a
    # confusing "MSB3021/MSB3027: file is being used by another process" after
    # 10 retries. Fail fast with an actionable message instead.
    # NOTE: keep this file ASCII-only. Windows PowerShell 5.1 reads BOM-less
    # scripts as ANSI, so non-ASCII text here would corrupt the parser.
    $running = @(Get-Process -Name 'M3U8Downloader' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Host ""
        Write-Host "ERROR: M3U8Downloader is running (PID $($running.Id -join ', '))." -ForegroundColor Red
        Write-Host "       It locks files in '$appOut', so publishing cannot overwrite them." -ForegroundColor Red
        Write-Host "       Close the app first, or run:" -ForegroundColor Red
        Write-Host "         Get-Process M3U8Downloader | Stop-Process -Force" -ForegroundColor Yellow
        exit 1
    }

    $platform = switch ($Runtime) { 'win-x64' { 'x64' } 'win-x86' { 'x86' } 'win-arm64' { 'ARM64' } }
    dotnet publish "src\M3U8Downloader.App\M3U8Downloader.App.csproj" -c Release -r $Runtime -p:Platform=$platform -o $appOut @versionArgs
    if ($LASTEXITCODE -ne 0) { exit 1 }

    # Verify the private runtime was actually bundled, otherwise the target
    # machine will fail with "required components of the Windows App Runtime are missing".
    $required = @('M3U8Downloader.exe', 'hostpolicy.dll', 'coreclr.dll', 'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll')
    $missing = @()
    foreach ($f in $required) {
        if (-not (Test-Path (Join-Path $appOut $f))) { $missing += $f }
    }

    $totalMb = [math]::Round(((Get-ChildItem $appOut -File | Measure-Object Length -Sum).Sum / 1MB), 1)
    Write-Host ("  -> " + (Join-Path $appOut 'M3U8Downloader.exe') + "  ($totalMb MB)") -ForegroundColor Green

    if ($missing.Count -gt 0) {
        Write-Host ""
        Write-Host "WARNING: missing required files; the app may fail to start:" -ForegroundColor Red
        $missing | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "  runtime completeness check passed (runs without any install)" -ForegroundColor Green
}

Write-Host ""
Write-Host "Publish finished." -ForegroundColor Cyan
