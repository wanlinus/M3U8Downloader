using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using M3U8Downloader.Core.Net;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Ffmpeg;

/// <summary>下载阶段</summary>
public enum FfmpegInstallStage
{
    Downloading,
    Unpacking,
    Verifying,
    Done,
}

/// <summary>下载进度</summary>
public sealed record FfmpegInstallProgress(long DownloadedBytes, long TotalBytes, FfmpegInstallStage Stage)
{
    public double Percent => TotalBytes > 0 ? DownloadedBytes * 100.0 / TotalBytes : 0;

    public string Describe() => Stage switch
    {
        FfmpegInstallStage.Downloading => TotalBytes > 0
            ? $"正在下载 {DownloadedBytes / 1024.0 / 1024.0:0.0} / {TotalBytes / 1024.0 / 1024.0:0.0} MB"
            : $"正在下载 {DownloadedBytes / 1024.0 / 1024.0:0.0} MB",
        FfmpegInstallStage.Unpacking => "正在解包…",
        FfmpegInstallStage.Verifying => "正在校验…",
        _ => "完成",
    };
}

/// <summary>安装结果</summary>
public sealed class FfmpegInstallResult
{
    public bool Success { get; init; }
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public string? Version { get; init; }
    public string? Error { get; init; }

    /// <summary>实际安装到的目录</summary>
    public string? Directory { get; init; }

    /// <summary>程序目录不可写，退回到了用户目录</summary>
    public bool UsedFallbackDirectory { get; init; }

    public static FfmpegInstallResult Fail(string error) => new() { Success = false, Error = error };
}

/// <summary>
/// 应用内下载并安装 FFmpeg（对齐 M3U8Quicker 的做法，但目标目录改为 LocalAppData）。
///
/// 设计要点：
/// - 默认取 BtbN 的官方构建（含 ffmpeg + ffprobe 两个可执行文件），
///   下载地址可在设置里覆盖 —— 国内用户常需要换镜像，写死会让功能不可用；
/// - 先解包到临时目录再拷正式目录，失败不会留下半个"看起来装好了"的目录；
/// - 装完必须跑一次 <c>-version</c> 才算成功。
///
/// 许可证提醒：FFmpeg 是独立的第三方组件，其 LGPL/GPL 条款不受本项目许可证影响，
/// 因此「关于」界面里必须给出相应声明（见 <see cref="AppInfo.ThirdPartyNotices"/>）。
/// </summary>
public static class FfmpegInstaller
{
    /// <summary>
    /// Windows x64 默认下载源。
    ///
    /// 刻意选用 **LGPL** 构建而不是 GPL：
    /// 本程序是 Apache-2.0 项目，只把 ffmpeg 当独立进程调用（转封装用 <c>-c copy</c>、外加 ffprobe 读元数据），
    /// 完全用不到 libx264 这类 GPL-only 组件。用 LGPL 构建可以让分发链路上少一层 GPL 义务，
    /// 体积也更小。需要 GPL 特性的用户可在设置里自行改成 GPL 地址。
    /// </summary>
    public const string DefaultWindowsX64Url =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl.zip";

    /// <summary>Windows arm64 默认下载源（同为 LGPL 构建）</summary>
    public const string DefaultWindowsArm64Url =
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-lgpl.zip";

    /// <summary>按当前运行环境挑默认下载源</summary>
    public static string GetDefaultDownloadUrl()
    {
        if (!OperatingSystem.IsWindows())
        {
            // 本项目当前只分发 Windows 版；其它平台请让用户在设置里填自己的构建地址
            return DefaultWindowsX64Url;
        }

        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? DefaultWindowsArm64Url
            : DefaultWindowsX64Url;
    }

    /// <summary>
    /// 下载并安装。整个过程不抛异常，失败通过返回值报告（便于界面直接显示）。
    /// </summary>
    /// <param name="downloadUrl">下载源；留空用按当前架构推断的默认源</param>
    /// <param name="proxyUrl">代理地址；留空表示不显式走代理（用系统默认）</param>
    /// <param name="progress">进度回调</param>
    /// <param name="ct">取消令牌</param>
    public static async Task<FfmpegInstallResult> InstallAsync(
        string? downloadUrl = null,
        string? proxyUrl = null,
        IProgress<FfmpegInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var url = string.IsNullOrWhiteSpace(downloadUrl) ? GetDefaultDownloadUrl() : downloadUrl!.Trim();
        var tempRoot = Path.Combine(Path.GetTempPath(), "M3U8Downloader-ffmpeg-" + Guid.NewGuid().ToString("N")[..8]);
        var archivePath = Path.Combine(tempRoot, "ffmpeg.zip");
        var extractDir = Path.Combine(tempRoot, "unpacked");

        try
        {
            Directory.CreateDirectory(tempRoot);

            // ---------- 1. 下载 ----------
            var downloadError = await DownloadAsync(url, archivePath, proxyUrl, progress, ct).ConfigureAwait(false);
            if (downloadError is not null) return FfmpegInstallResult.Fail(downloadError);

            // ---------- 2. 解包 ----------
            progress?.Report(new FfmpegInstallProgress(0, 0, FfmpegInstallStage.Unpacking));
            try
            {
                ZipFile.ExtractToDirectory(archivePath, extractDir, overwriteFiles: true);
            }
            catch (InvalidDataException)
            {
                return FfmpegInstallResult.Fail(
                    "下载到的不是有效的 zip 包。若下载源被网络拦截，通常返回的是 HTML 错误页；" +
                    "请检查网络，或在设置里换一个下载源。");
            }
            catch (Exception ex)
            {
                return FfmpegInstallResult.Fail($"解包失败：{ex.Message}");
            }

            // ---------- 3. 找出两个可执行文件（压缩包内通常还套一层目录） ----------
            var ffmpegSrc = FindBinary(extractDir, FfmpegLocator.FfmpegFileName);
            var ffprobeSrc = FindBinary(extractDir, FfmpegLocator.FfprobeFileName);

            if (ffmpegSrc is null)
                return FfmpegInstallResult.Fail(
                    "压缩包里没有找到 ffmpeg 可执行文件，请确认下载源提供的是完整构建。");

            // ---------- 4. 安装到程序目录下的 ffmpeg\（绿色免污染）；不可写则退回用户目录 ----------
            var targetDir = FfmpegLocator.PreferredInstallDirectory;
            var usingFallback = FfmpegLocator.IsUsingFallbackDirectory;
            Directory.CreateDirectory(targetDir);

            var ffmpegDest = Path.Combine(targetDir, FfmpegLocator.FfmpegFileName);
            CopyWithReplace(ffmpegSrc, ffmpegDest);

            string? ffprobeDest = null;
            if (ffprobeSrc is not null)
            {
                ffprobeDest = Path.Combine(targetDir, FfmpegLocator.FfprobeFileName);
                CopyWithReplace(ffprobeSrc, ffprobeDest);
            }

            // ---------- 5. 校验 ----------
            progress?.Report(new FfmpegInstallProgress(0, 0, FfmpegInstallStage.Verifying));

            var ffmpeg = await FfmpegLocator.ProbeSingleAsync(ffmpegDest, ct).ConfigureAwait(false);
            if (ffmpeg is null)
                return FfmpegInstallResult.Fail("安装完成但无法运行 ffmpeg，可能被杀毒软件拦截或文件损坏。");

            var ffprobe = ffprobeDest is not null
                ? await FfmpegLocator.ProbeSingleAsync(ffprobeDest, ct).ConfigureAwait(false)
                : null;

            progress?.Report(new FfmpegInstallProgress(0, 0, FfmpegInstallStage.Done));

            if (ffprobe is null)
            {
                return new FfmpegInstallResult
                {
                    Success = true,
                    FfmpegPath = ffmpegDest,
                    FfprobePath = null,
                    Version = ffmpeg.Version,
                    Directory = targetDir,
                    UsedFallbackDirectory = usingFallback,
                    Error = "ffmpeg 已装好，但压缩包里没有 ffprobe（转封装可用，媒体分析需要它）。",
                };
            }

            return new FfmpegInstallResult
            {
                Success = true,
                FfmpegPath = ffmpegDest,
                FfprobePath = ffprobeDest,
                Version = ffmpeg.Version,
                Directory = targetDir,
                UsedFallbackDirectory = usingFallback,
            };
        }
        catch (OperationCanceledException)
        {
            return FfmpegInstallResult.Fail("已取消下载。");
        }
        catch (Exception ex)
        {
            return FfmpegInstallResult.Fail($"安装失败：{ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static async Task<string?> DownloadAsync(
        string url, string destination, string? proxyUrl,
        IProgress<FfmpegInstallProgress>? progress, CancellationToken ct)
    {
        try
        {
            using var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
            };

            // 代理：国内直连 GitHub 基本拿不到 ffmpeg 包，必须支持
            var proxy = ProxyHelper.Create(new AppSettings
            {
                ProxyEnabled = !string.IsNullOrWhiteSpace(proxyUrl),
                ProxyUrl = proxyUrl,
            });
            if (proxy is not null) handler.Proxy = proxy;

            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };

            using var response = await client
                .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return $"下载失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}";

            var total = response.Content.Headers.ContentLength ?? 0;
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return "下载源返回的是网页而不是压缩包，请检查下载地址或网络环境。";

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var target = File.Create(destination);

            var buffer = new byte[81920];
            long downloaded = 0;
            progress?.Report(new FfmpegInstallProgress(0, total, FfmpegInstallStage.Downloading));

            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) break;

                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                downloaded += read;
                progress?.Report(new FfmpegInstallProgress(downloaded, total, FfmpegInstallStage.Downloading));
            }

            if (downloaded == 0) return "下载到的文件为空，请稍后重试或更换下载源。";
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return $"下载失败：{ex.Message}";
        }
    }

    /// <summary>在解包目录里递归找可执行文件（压缩包内层目录名随版本变化，不能写死）</summary>
    private static string? FindBinary(string root, string fileName)
    {
        try
        {
            return Directory
                .EnumerateFiles(root, fileName, SearchOption.AllDirectories)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static void CopyWithReplace(string source, string destination)
    {
        if (File.Exists(destination)) File.Delete(destination);
        File.Copy(source, destination, overwrite: true);
    }
}
