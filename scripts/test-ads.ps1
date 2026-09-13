# 广告分片识别验证：对指定 m3u8 地址做分析（可选择同时下载）
#
# 默认使用项目开发中复现问题的那类「动态插播广告」源做验证。
# 也可以传入自己的地址。
#
# 用法:
#   .\test-ads.ps1
#   .\test-ads.ps1 -Url "https://example.com/index.m3u8"
#   .\test-ads.ps1 -Url "..." -Download

param(
    [string]$Url = 'https://vod1.maowushi.com/20260906/K2KZSZlA/index.m3u8',
    [string]$Referer = '',
    [string]$Origin = '',
    [switch]$Download,
    [int]$Concurrency = 16
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# 优先用发布版，找不到则用 Debug 构建
$cli = Join-Path $root 'publish\cli-win-x64\m3u8dl.exe'
if (-not (Test-Path $cli)) {
    $cli = Join-Path $root 'src\M3U8Downloader.Cli\bin\Debug\net10.0\m3u8dl.exe'
}
if (-not (Test-Path $cli)) {
    Write-Host "未找到 m3u8dl.exe，请先运行 scripts\build.ps1 或 scripts\publish.ps1" -ForegroundColor Red
    exit 1
}

Write-Host "使用: $cli`n" -ForegroundColor DarkGray

$args = @($Url, '--dry-run')
if ($Referer) { $args += @('--referer', $Referer) }
if ($Origin) { $args += @('--origin', $Origin) }

if ($Download) {
    # 去掉 --dry-run 改为实际下载
    $args = @($Url, '-c', "$Concurrency", '-o', (Join-Path $root 'test-output'), '-n', 'test')
    if ($Referer) { $args += @('--referer', $Referer) }
    if ($Origin) { $args += @('--origin', $Origin) }
}

& $cli @args
exit $LASTEXITCODE
