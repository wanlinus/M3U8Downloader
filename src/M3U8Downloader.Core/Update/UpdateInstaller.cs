using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using M3U8Downloader.Core.Net;

namespace M3U8Downloader.Core.Update;

/// <summary>下载进度</summary>
public sealed record UpdateDownloadProgress(long Received, long Total)
{
    public double Percent => Total > 0 ? Math.Min(100, Received * 100.0 / Total) : 0;

    public string Text => Total > 0
        ? $"{Received / 1024.0 / 1024.0:0.0} / {Total / 1024.0 / 1024.0:0.0} MB（{Percent:0}%）"
        : $"{Received / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>
/// 下载新版本并把它装上去。
///
/// 为什么要一个脱离主进程的脚本来收尾：程序自己是**跑在要替换的那批文件里**的 ——
/// Windows 不允许覆盖正在使用的 exe/dll，而 shim 又不能自己把自己换掉。
/// 业界通行做法都一样：主程序下载解压、拉一个短命进程、自己退出，
/// 由那个进程在"没人在用这些文件"之后动手。AutoUpdater.NET 用的就是这套
/// （它带一个 ZipExtractor 专门干这个）。
///
/// 这里用 PowerShell 而不是再编一个 exe：省一个项目、省一次发布配置，
/// 代价是首次运行时可能有杀软提示 —— 脚本内容可读、不做任何混淆，
/// 就是为了让它看起来像它本来的样子。
/// </summary>
public static class UpdateInstaller
{
    /// <summary>下载解压的工作目录（也是更新日志与备份的落脚点）</summary>
    public static string WorkDirectory =>
        Path.Combine(Path.GetTempPath(), "M3U8Downloader-update");

    public static string LogPath => Path.Combine(WorkDirectory, "update.log");

    /// <summary>
    /// 更新流程的诊断日志 —— 记的是**收尾脚本被拉起来之前**那一段。
    ///
    /// 单独一份、和脚本写的 update.log 分开：像"点了按钮没反应"这种情况，
    /// 问题几乎都出在脚本启动之前（对话框时序、守卫、下载失败）。
    /// 不留痕就只能靠猜。
    /// </summary>
    public static string AppTracePath => Path.Combine(WorkDirectory, "app-update.log");

    public static void Trace(string message)
    {
        try
        {
            Directory.CreateDirectory(WorkDirectory);
            File.AppendAllText(AppTracePath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch
        {
            // 记日志失败不能影响更新本身
        }
    }

    /// <summary>装更新包必须有的文件 —— 少一个就说明解压不完整，绝不能拿它去覆盖</summary>
    private static readonly string[] RequiredFiles =
    {
        "M3U8Downloader.exe",
        "M3U8Downloader.dll",
        "M3U8Downloader.deps.json",
        "M3U8Downloader.runtimeconfig.json",
        "hostpolicy.dll",
        "coreclr.dll",
        "System.Private.CoreLib.dll",
    };

    /// <summary>文件数下限：正常包有 500 个上下，太少必然是解压坏了</summary>
    private const int MinFileCount = 200;

    /// <summary>
    /// 下载并解压新版本，返回解压目录。
    /// 同一个版本已经下过且校验通过就直接复用 —— 重试时不必再拖一遍 87 MB。
    /// </summary>
    public static async Task<string> DownloadAndExtractAsync(
        ReleaseInfo release,
        string? proxyUrl,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(release.DownloadUrl))
            throw new InvalidOperationException("这个版本没有提供 Windows 包，请到发布页手动下载");

        var extractDir = Path.Combine(WorkDirectory, release.Version);
        if (Validate(extractDir, out _)) return extractDir;

        Directory.CreateDirectory(WorkDirectory);
        var zipPath = Path.Combine(WorkDirectory, $"{release.Version}.zip");

        await DownloadAsync(release.DownloadUrl, zipPath, proxyUrl, release.DownloadSize, progress, ct)
            .ConfigureAwait(false);

        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);

        try
        {
            ZipFile.ExtractToDirectory(zipPath, extractDir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"解压安装包失败：{ex.Message}", ex);
        }

        if (!Validate(extractDir, out var error))
            throw new InvalidOperationException($"下载的安装包不完整（{error}），已放弃更新");

        return extractDir;
    }

    /// <summary>
    /// 校验解压结果。这是**覆盖前的最后一道闸** ——
    /// 拿一个残缺的目录去覆盖，等于把用户的程序毁掉。
    /// </summary>
    public static bool Validate(string directory, out string error)
    {
        if (!Directory.Exists(directory))
        {
            error = "目录不存在";
            return false;
        }

        foreach (var name in RequiredFiles)
        {
            if (File.Exists(Path.Combine(directory, name))) continue;
            error = $"缺少 {name}";
            return false;
        }

        var count = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
        if (count < MinFileCount)
        {
            error = $"文件数异常（只有 {count} 个）";
            return false;
        }

        error = "";
        return true;
    }

    private static async Task DownloadAsync(
        string url, string destination, string? proxyUrl, long expectedSize,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        // 先按用户配的代理试；代理没开就退回直连 —— 和别处一样的策略。
        var normalized = ProxyHelper.Normalize(proxyUrl);
        if (normalized is not null)
        {
            try
            {
                await DownloadCoreAsync(url, destination, normalized, expectedSize, progress, ct)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                progress?.Report(new UpdateDownloadProgress(0, expectedSize));
            }
        }

        await DownloadCoreAsync(url, destination, null, expectedSize, progress, ct).ConfigureAwait(false);
    }

    private static async Task DownloadCoreAsync(
        string url, string destination, string? proxyUrl, long expectedSize,
        IProgress<UpdateDownloadProgress>? progress, CancellationToken ct)
    {
        using var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
            // 显式关掉，否则会退到系统代理（见 AGENTS.md 的硬约定）
            UseProxy = false,
        };

        if (proxyUrl is not null)
        {
            handler.Proxy = new WebProxy(new Uri(proxyUrl)) { BypassProxyOnLocal = true };
            handler.UseProxy = true;
        }

        // 87 MB 的下载，超时给足
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "M3U8Downloader-Update");

        using var resp = await client
            .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? expectedSize;

        await using var source = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(destination);

        var buffer = new byte[81920];
        long received = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            progress?.Report(new UpdateDownloadProgress(received, total));
        }
    }

    /// <summary>
    /// 写好更新脚本并**脱离本进程**启动它，然后调用方应当立刻退出程序。
    /// </summary>
    /// <param name="newVersionDir">新版本解压目录</param>
    /// <param name="targetDir">程序所在目录（= 要覆盖的目标）</param>
    /// <param name="exeName">主程序文件名</param>
    /// <param name="waitPid">要等它退出的进程；默认当前进程（测试时可指定别的）</param>
    public static void LaunchUpdater(
        string newVersionDir, string targetDir, string exeName, int? waitPid = null)
    {
        Directory.CreateDirectory(WorkDirectory);
        var scriptPath = Path.Combine(WorkDirectory, "apply-update.ps1");

        // 必须带 BOM。Windows PowerShell 5.1 读**无 BOM 的 UTF-8** 时会按系统 ANSI
        // （中文系统是 GBK）解码，脚本里的中文尾字节会把后面的引号吞掉，直接语法错误。
        // 见 docs/pitfalls.md 第 12 条 —— 写这个文件时正好又踩了一次。
        File.WriteAllText(scriptPath, UpdaterScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        var currentPid = waitPid ?? Environment.ProcessId;

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\" " +
                $"-WaitPid {currentPid} -SourceDir \"{newVersionDir}\" -TargetDir \"{targetDir}\" " +
                $"-ExeName \"{exeName}\" -LogPath \"{LogPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process.Start(psi);
    }

    /// <summary>
    /// 收尾脚本。刻意不做混淆、不下载任何东西、不碰注册表 ——
    /// 它要做的只是等、备份、复制、启动，一眼能看完。
    /// </summary>
    private const string UpdaterScript = """
        param(
            [Parameter(Mandatory=$true)][int]$WaitPid,
            [Parameter(Mandatory=$true)][string]$SourceDir,
            [Parameter(Mandatory=$true)][string]$TargetDir,
            [Parameter(Mandatory=$true)][string]$ExeName,
            [Parameter(Mandatory=$true)][string]$LogPath
        )

        function Write-Log([string]$Message) {
            $line = "{0}  {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
            Add-Content -Path $LogPath -Value $line -Encoding UTF8
        }

        Write-Log "==== 更新开始 ===="
        Write-Log "目标目录: $TargetDir"
        Write-Log "新版本:   $SourceDir"
        Write-Log "等待主进程 $WaitPid 退出"

        # 等主进程退出，最多 90 秒
        $deadline = (Get-Date).AddSeconds(90)
        while ((Get-Date) -lt $deadline) {
            if (-not (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 500
        }

        if (Get-Process -Id $WaitPid -ErrorAction SilentlyContinue) {
            Write-Log "主进程 90 秒内没退出，放弃更新（什么都没改）"
            exit 1
        }

        Write-Log "主进程已退出"
        Start-Sleep -Milliseconds 1500   # 等文件句柄彻底释放

        $backupDir = Join-Path (Split-Path $LogPath -Parent) 'backup'

        # 备份现有版本：万一复制坏了能退回去
        Write-Log "备份到 $backupDir"
        robocopy $TargetDir $backupDir /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        Write-Log "备份结束 (robocopy $LASTEXITCODE)"

        # 刻意用 /E 而不是 /MIR：
        # /MIR 会把目标目录里"多出来"的文件删掉，而用户下载的 FFmpeg 就在
        # 程序目录下的 ffmpeg\ 里、并不在更新包中 —— 用 /MIR 会连它一起删掉。
        Write-Log "应用更新"
        robocopy $SourceDir $TargetDir /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
        $code = $LASTEXITCODE
        Write-Log "复制结束 (robocopy $code)"

        if ($code -ge 8) {
            Write-Log "复制失败，从备份回滚"
            robocopy $backupDir $TargetDir /E /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            Write-Log "回滚结束 (robocopy $LASTEXITCODE)"
        }
        else {
            Write-Log "更新成功"
            # 清掉解压目录；zip 留着 —— 万一还要重试能省一次 87 MB 下载
            try {
                Remove-Item -LiteralPath $SourceDir -Recurse -Force -ErrorAction Stop
                Write-Log "已清理解压目录"
            }
            catch { Write-Log "清理解压目录失败：$_" }
        }

        $exe = Join-Path $TargetDir $ExeName
        if (Test-Path $exe) {
            Start-Process -FilePath $exe -WorkingDirectory $TargetDir
            Write-Log "已启动新版本"
        }
        else {
            Write-Log "找不到 $exe，请手动启动"
        }

        Write-Log "==== 更新结束 ===="
        """;
}
