# Publish self-contained builds (target machine needs NO .NET / Windows App SDK runtime)
# Usage: .\publish.ps1 [-Runtime win-x64|win-x86|win-arm64] [-SkipApp] [-Version 1.2.3]
#
# -Version is optional: it stamps the assembly version (CI passes the tag, so the
# built exe reports the released version instead of the project default).

param(
    [ValidateSet('win-x64','win-x86','win-arm64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipApp,
    [string]$Version = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# Version argument passed to dotnet (empty = keep the project default)
$versionArgs = @()
if ($Version) { $versionArgs = @("-p:Version=$Version") }

Write-Host "== Publish M3U8 Downloader ($Runtime, self-contained) ==" -ForegroundColor Cyan
if ($Version) { Write-Host "   version: $Version" -ForegroundColor Cyan }

if (-not $SkipApp) {
    Write-Host ""
    Write-Host "[1/1] WinUI app" -ForegroundColor Yellow
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

    # ---- Trim what a plain WinUI app never uses ----
    #
    # Microsoft.WindowsAppSDK is a *metapackage*: besides WinUI it also drags in the
    # AI / ML / Search / Widgets / Workloads components. That is ~51 MB of the publish
    # output (onnxruntime.dll alone is 21 MB, DirectML.dll 18 MB), and this project's
    # code references none of those APIs. Verified by trimming a copy and launching it:
    # the window comes up normally, which also proves the SQLite native library and the
    # XAML resource index survived the trim.
    #
    # If the app ever starts using one of these APIs, the matching name must be removed
    # from this pattern -- otherwise the app will fail at runtime, not at build time.
    $unusedPattern = '^(onnxruntime\.dll|DirectML\.dll|PerceptiveStreaming\.dll|NPUDetect\.dll|workloads\.json|workloads\..*\.json|Microsoft\.(Windows\.AI|Windows\.Internal\.AI|Windows\.ImageCreationInternal|Windows\.Internal\.ImageCreation|Windows\.Internal\.Vision|Windows\.Internal\.SemanticSearch|Windows\.Internal\.ContentModeration|Windows\.SemanticSearch|Windows\.Vision|Windows\.Search|Windows\.Widgets|Windows\.Workloads|Windows\.Private\.Workloads|ML\.OnnxRuntime|AI\.MachineLearning|Graphics\.Imaging|Graphics\.Internal\.Imaging|Graphics\.ImagingInternal))'
    $unused = @(Get-ChildItem $appOut -File | Where-Object { $_.Name -match $unusedPattern })
    $unusedMb = [math]::Round((($unused | Measure-Object Length -Sum).Sum) / 1MB, 1)
    $unused | Remove-Item -Force

    # WinUI ships ~85 satellite resource folders; we only ever show a Chinese or English
    # UI, so the rest are dead weight (and 82 extra folders in the user's face).
    $keepLanguages = @('zh-CN', 'zh-TW', 'en-us')
    $langDirs = @(Get-ChildItem $appOut -Directory |
        Where-Object { $_.Name -match '^[a-z]{2,3}(-[A-Za-z]{2,4})*$' -and $keepLanguages -notcontains $_.Name })
    $langDirs | Remove-Item -Recurse -Force

    Write-Host ("  trimmed {0} unused files ({1} MB) and {2} language folders" -f $unused.Count, $unusedMb, $langDirs.Count) -ForegroundColor DarkGray

    # Verify the private runtime was actually bundled, otherwise the target
    # machine will fail with "required components of the Windows App Runtime are missing".
    # e_sqlite3.dll is the native SQLite library behind the unified database
    # (settings / task list / download history). Missing it is nastier than the
    # others: the app starts fine and only blows up later, the first time storage
    # is touched -- so it belongs in this completeness check.
    # M3U8Downloader.pri is the XAML resource index, and it is easy to lose silently:
    # the build always produces it, but it only lands in the *publish* output when
    # EnableMsixTooling is true (see docs/pitfalls.md #34). A publish that misses it
    # still reports success and only blows up when the app is launched.
    $required = @(
        'M3U8Downloader.exe', 'hostpolicy.dll', 'coreclr.dll',
        'Microsoft.WindowsAppRuntime.dll', 'Microsoft.ui.xaml.dll',
        'M3U8Downloader.pri',
        'e_sqlite3.dll', 'Microsoft.Data.Sqlite.dll'
    )
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
