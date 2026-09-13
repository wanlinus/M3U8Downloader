# 构建全部项目（Debug）
# 用法: .\build.ps1 [-Configuration Debug|Release]

param(
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== 构建 M3U8 下载器 ($Configuration) ==" -ForegroundColor Cyan

Write-Host "`n[1/2] 核心库 + 命令行" -ForegroundColor Yellow
dotnet build "src\M3U8Downloader.Cli\M3U8Downloader.Cli.csproj" -c $Configuration
if ($LASTEXITCODE -ne 0) { Write-Host "核心库构建失败" -ForegroundColor Red; exit 1 }

Write-Host "`n[2/2] WinUI 图形界面" -ForegroundColor Yellow
dotnet build "src\M3U8Downloader.App\M3U8Downloader.App.csproj" -c $Configuration -p:Platform=x64 -r win-x64
if ($LASTEXITCODE -ne 0) { Write-Host "界面构建失败" -ForegroundColor Red; exit 1 }

$appExe = Join-Path $root "src\M3U8Downloader.App\bin\x64\$Configuration\net10.0-windows10.0.19041.0\win-x64\M3U8Downloader.exe"
$cliExe = Join-Path $root "src\M3U8Downloader.Cli\bin\$Configuration\net10.0\m3u8dl.exe"

Write-Host "`n构建完成：" -ForegroundColor Green
Write-Host "  界面: $appExe"
Write-Host "  命令行: $cliExe"
