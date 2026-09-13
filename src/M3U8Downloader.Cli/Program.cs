using System.Diagnostics;
using System.Text;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Net;
using M3U8Downloader.Core.Settings;
using M3U8Downloader.Core.Sites;

namespace M3U8Downloader.Cli;

/// <summary>
/// 命令行版 m3u8 下载器。
/// 与图形界面共用同一套下载引擎（M3U8Downloader.Core），便于脚本调用与自动化测试。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var log = new StringBuilder();
        void Write(string line)
        {
            Console.WriteLine(line);
            log.AppendLine(line);
        }

        try
        {
            string? url = null, outputDir = null, fileName = null;
            string? referer = null, origin = null, userAgent = null, logFile = null, listVariants = null;
            int concurrency = 16, retries = 3;
            bool skipAds = true, dryRun = false;

            // 站点/剧集模式
            string? seriesUrl = null, episodesSpec = null;
            int epConcurrency = 2, preferHeight = 0;
            bool listOnly = false, allSources = false;

            // FFmpeg / 代理
            bool ffmpegStatus = false, ffmpegDownload = false, proxyTest = false;
            string? ffmpegUrl = null, ffmpegPathArg = null, proxyArg = null;

            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string? Next() => i + 1 < args.Length ? args[++i] : null;

                switch (a.ToLowerInvariant())
                {
                    case "--url": url = Next(); break;
                    case "-o": case "--out": outputDir = Next(); break;
                    case "-n": case "--name": fileName = Next(); break;
                    case "--referer": referer = Next(); break;
                    case "--origin": origin = Next(); break;
                    case "--ua": userAgent = Next(); break;
                    case "-c": case "--concurrency": int.TryParse(Next(), out concurrency); break;
                    case "--retries": int.TryParse(Next(), out retries); break;
                    case "--no-skip-ads": skipAds = false; break;
                    case "--dry-run": dryRun = true; break;
                    case "--log": logFile = Next(); break;
                    case "--variants": listVariants = Next() ?? ""; break;

                    // ---- 站点/剧集模式 ----
                    case "--series": seriesUrl = Next(); break;
                    case "--episodes": episodesSpec = Next(); break;
                    case "--list": listOnly = true; break;
                    case "--all-sources": allSources = true; break;
                    case "--ep-concurrency": int.TryParse(Next(), out epConcurrency); break;
                    case "--height": int.TryParse(Next(), out preferHeight); break;

                    // ---- FFmpeg / 代理 ----
                    case "--ffmpeg-status": ffmpegStatus = true; break;
                    case "--ffmpeg-download": ffmpegDownload = true; break;
                    case "--ffmpeg-url": ffmpegUrl = Next(); break;
                    case "--ffmpeg-path": ffmpegPathArg = Next(); break;
                    case "--proxy": proxyArg = Next(); break;
                    case "--proxy-test": proxyTest = true; break;

                    case "-h": case "--help": PrintHelp(Write); return 0;
                    default:
                        if (!a.StartsWith('-') && url == null) url = a;
                        break;
                }
            }

            if (string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(seriesUrl)
                && !ffmpegStatus && !ffmpegDownload && !proxyTest)
            {
                PrintHelp(Write);
                return 2;
            }

            // ---------- 代理连通性测试（与界面共用 Core 里的同一份实现） ----------
            if (proxyTest)
            {
                var target = proxyArg ?? AppSettingsStore.Load().ProxyUrl;
                if (string.IsNullOrWhiteSpace(target))
                {
                    Write("✘ 没有可测试的代理地址。用法：m3u8dl --proxy-test --proxy http://127.0.0.1:7897");
                    return 1;
                }

                Write($"正在通过 {target} 测试连通性…");
                var test = await ProxyHelper.TestAsync(target);
                Write("  " + test.Message);
                Write($"  （规范化后：{ProxyHelper.Normalize(target) ?? "格式无效"}）");
                if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
                return test.Success ? 0 : 1;
            }

            // ---------- FFmpeg 状态 / 安装（不涉及下载视频） ----------
            if (ffmpegStatus || ffmpegDownload || ffmpegPathArg != null)
            {
                var settings = AppSettingsStore.Load();
                if (ffmpegPathArg != null) settings.FfmpegPath = ffmpegPathArg;
                if (ffmpegUrl != null) settings.FfmpegDownloadUrl = ffmpegUrl;
                if (proxyArg != null) { settings.ProxyEnabled = true; settings.ProxyUrl = proxyArg; }

                if (ffmpegDownload)
                {
                    var autoUrl = settings.FfmpegDownloadUrl ?? FfmpegInstaller.GetDefaultDownloadUrl();
                    Write("正在下载 FFmpeg（约 200 MB，请耐心等待）");
                    Write($"  下载源 : {autoUrl}");
                    Write($"  代理   : {(settings.ProxyEnabled ? settings.ProxyUrl : "未启用")}");
                    Write($"  安装到 : {FfmpegLocator.ManagedDirectory}");

                    var lastLine = DateTime.UtcNow;
                    var installProgress = new Progress<FfmpegInstallProgress>(p =>
                    {
                        if ((DateTime.UtcNow - lastLine).TotalMilliseconds < 200) return;
                        lastLine = DateTime.UtcNow;
                        Console.Write($"\r  {p.Describe()}                    ");
                    });

                    var installResult = await FfmpegInstaller.InstallAsync(
                        settings.FfmpegDownloadUrl, settings.ProxyEnabled ? settings.ProxyUrl : null, installProgress);

                    Console.WriteLine();
                    if (!installResult.Success)
                    {
                        Write("✘ 安装失败：" + installResult.Error);
                        if (installResult.Error != null && installResult.Error.Contains("下载失败"))
                            Write("  提示：可加 --proxy http://127.0.0.1:7897 ，或用 --ffmpeg-url 指定镜像地址。");
                        if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
                        return 1;
                    }

                    Write($"✔ 安装完成：ffmpeg {installResult.Version}");
                    Write($"  {installResult.FfmpegPath}");
                    if (installResult.FfprobePath != null) Write($"  {installResult.FfprobePath}");
                    else if (installResult.Error != null) Write($"  注意：{installResult.Error}");

                    // 装完顺带刷新一次状态
                    settings.FfmpegPath = null;
                    AppSettingsStore.Save(settings);
                }

                var status = await FfmpegLocator.DetectAsync(settings);
                Write(new string('-', 72));
                Write($"  {AppInfo.ProductName} {AppInfo.Version}  ·  {AppInfo.LicenseName}");
                Write($"  {AppInfo.ProjectUrl}");
                Write($"  FFmpeg 状态：{(status.IsInstalled ? "可用" : "不可用")}");
                foreach (var line in status.Describe().Split('\n')) Write("  " + line);
                Write($"  管理目录  ：{FfmpegLocator.ManagedDirectory}");
                Write($"  设置文件  ：{AppSettingsStore.SettingsFilePath}");

                if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
                return status.IsInstalled ? 0 : 1;
            }

            var headers = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(referer)) headers["Referer"] = referer!;
            if (!string.IsNullOrWhiteSpace(origin)) headers["Origin"] = origin!;
            if (!string.IsNullOrWhiteSpace(userAgent)) headers["User-Agent"] = userAgent!;

            outputDir ??= KnownFolders.Downloads;
            Directory.CreateDirectory(outputDir);

            // ---------- 站点/剧集模式：给一个播放页地址，自动列出整部剧并批量下载 ----------
            if (seriesUrl != null)
            {
                return await RunSeriesModeAsync(seriesUrl, episodesSpec, listOnly || dryRun, allSources,
                    preferHeight, epConcurrency, concurrency, retries, skipAds,
                    outputDir, logFile, log, Write);
            }

            var urlText = url!;
            fileName ??= SanitizeFileName(
                Path.GetFileNameWithoutExtension(new Uri(urlText).AbsolutePath.TrimEnd('/')));
            if (string.IsNullOrWhiteSpace(fileName)) fileName = "video";

            var outputPath = Path.Combine(outputDir, fileName + ".ts");
            var tempDir = Path.Combine(Path.GetTempPath(), "M3U8Downloader",
                SanitizeFileName(fileName) + "_" + Math.Abs(urlText.GetHashCode()));

            Write("M3U8 下载器 · 命令行模式");
            Write($"  地址     : {url}");
            if (!dryRun) Write($"  输出     : {outputPath}");
            Write($"  临时目录 : {tempDir}");
            Write($"  并发/重试: {concurrency} / {retries}");
            Write($"  跳过广告 : {(skipAds ? "是" : "否")}");
            Write($"  请求头   : {(headers.Count == 0 ? "无" : string.Join(", ", headers.Select(kv => kv.Key)))}");
            Write(new string('-', 72));

            var sw = Stopwatch.StartNew();
            using var downloader = new HlsDownloader(headers);

            // 仅列出清晰度
            if (listVariants != null)
            {
                var text = await downloader.FetchPlaylistTextAsync(urlText);
                var parsed = M3U8Parser.Parse(text, urlText);
                if (parsed.IsMaster)
                {
                    Write($"主清单，共 {parsed.Variants.Count} 个清晰度：");
                    foreach (var v in parsed.Variants)
                        Write($"   {v.DisplayName}\n      {v.Uri}");
                }
                else
                {
                    Write($"这是媒体清单：{parsed.Media?.Segments.Count ?? 0} 个分片，" +
                          $"时长 {TimeSpan.FromSeconds(parsed.Media?.TotalDuration ?? 0):hh\\:mm\\:ss}");
                }
                if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
                return 0;
            }

            var (media, parseLogs) = await downloader.ResolveMediaPlaylistAsync(urlText);
            foreach (var l in parseLogs) Write("  " + l);
            Write($"  分片数={media.Segments.Count}  总时长={TimeSpan.FromSeconds(media.TotalDuration):hh\\:mm\\:ss}  " +
                  $"直播={(media.IsLive ? "是" : "否")}  fMP4={(media.IsFmp4 ? "是" : "否")}");

            // ---------- 广告/无效分片分析 ----------
            var report = SegmentInspector.Inspect(media);
            Write(new string('-', 72));
            if (report.Suspects.Count > 0)
            {
                Write($"  ⚠ 检测到 {report.Suspects.Count} 个疑似插播广告/无效分片：");
                foreach (var r in report.Reasons) Write("     · " + r);
                foreach (var s in report.Suspects.Take(8))
                    Write($"     [{s.Index}] {s.FileName} ({s.Duration:0.##}s) {s.ValidityNote}");
                if (report.Suspects.Count > 8) Write($"     …另有 {report.Suspects.Count - 8} 个同类分片");
                Write(skipAds
                    ? "  → 已启用自动跳过，这些分片不会导致任务失败。"
                    : "  → 未启用自动跳过，任务可能因这些分片失败。");
            }
            else
            {
                Write("  未发现异目录/重复分片，播放列表干净。");
            }
            Write(new string('-', 72));

            if (dryRun)
            {
                Write("  --dry-run：仅分析，不下载。");
                if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
                return 0;
            }

            var options = new DownloadOptions
            {
                Concurrency = Math.Clamp(concurrency, 1, 64),
                MaxRetries = Math.Clamp(retries, 0, 10),
                Headers = headers,
                AutoSkipInvalidSegments = skipAds,
                TempDirectory = tempDir,
                OutputPath = outputPath,
            };

            var lastReport = DateTime.UtcNow;
            var progress = new Progress<DownloadProgress>(p =>
            {
                if ((DateTime.UtcNow - lastReport).TotalSeconds < 1) return;
                lastReport = DateTime.UtcNow;
                Console.Write($"\r  进度 {p.Percent,5:0.0}%  " +
                              $"{p.CompletedSegments + p.FailedSegments + p.SkippedSegments}/{p.TotalSegments} 分片  " +
                              $"{p.SizeText}  {p.SpeedText}  " +
                              (p.Eta.HasValue ? $"剩余 {p.Eta.Value:hh\\:mm\\:ss}" : "") + "        ");
            });

            var result = await downloader.DownloadAsync(media, options, progress);
            Console.WriteLine();

            Write(new string('-', 72));
            foreach (var m in result.Messages) Write("  · " + m);
            Write($"  分片统计: 成功 {result.CompletedSegments} / 跳过 {result.SkippedSegments} / 失败 {result.FailedSegments} / 共 {result.TotalSegments}");
            Write($"  耗时    : {result.Elapsed:hh\\:mm\\:ss}");
            if (result.OutputBytes > 0)
                Write($"  产物    : {result.OutputPath}  ({result.OutputBytes / 1024.0 / 1024.0:0.0} MB)");
            Write(result.Success ? "  结果    : 成功" : $"  结果    : 失败  {result.Error}");

            if (logFile != null)
                await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);

            return result.Success ? 0 : 1;
        }
        // 预期内的失败（不支持的站点、页面结构变了等）只打一句话，
        // 别把调用栈甩给用户；真正的意外才打完整异常。
        catch (Exception ex) when (ex is NotSupportedException
                                      or InvalidOperationException
                                      or ArgumentException
                                      or System.Net.Http.HttpRequestException)
        {
            Write("✘ " + ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            Write("异常: " + ex);
            return 1;
        }
    }

    /// <summary>
    /// 站点/剧集模式：识别站点 → 列出剧集 → 按选择批量下载。
    /// </summary>
    private static async Task<int> RunSeriesModeAsync(
        string seriesUrl, string? episodesSpec, bool listOnly, bool allSources,
        int preferHeight, int epConcurrency, int segmentConcurrency, int retries, bool skipAds,
        string outputDir, string? logFile, StringBuilder log, Action<string> Write)
    {
        using var seriesDownloader = new SeriesDownloader();

        Write("M3U8 下载器 · 站点/剧集批量模式");
        Write(new string('-', 72));
        Write($"  输入地址 : {seriesUrl}");

        var parsed = await seriesDownloader.ParseAsync(seriesUrl);

        foreach (var l in parsed.Log) Write("  · " + l);
        Write($"  站点     : {parsed.SiteName}   [{parsed.Kind}]");
        Write($"  剧名     : {parsed.Title}");
        Write($"  剧集 ID  : {parsed.SeriesId}");
        Write($"  播放源   : {string.Join("  |  ", parsed.Sources.Select(s => s.ToString()))}");
        Write($"  请求头   : {string.Join(", ", parsed.Headers.Keys)}");

        // ---- 选集：默认只勾当前播放源，避免多源重复下载同一集 ----
        var preferredSource = parsed.Sources.FirstOrDefault(s => s.Episodes.Any(e => e.IsSelected))?.Id
                              ?? parsed.Sources.FirstOrDefault()?.Id ?? 1;

        if (allSources)
            foreach (var e in parsed.AllEpisodes) e.IsSelected = true;
        else
            SeriesDownloader.SelectSource(parsed, preferredSource);

        if (!string.IsNullOrWhiteSpace(episodesSpec))
        {
            SeriesDownloader.ApplySelection(parsed, episodesSpec!);
            if (!allSources)
                foreach (var e in parsed.AllEpisodes)
                    e.IsSelected = e.IsSelected && e.SourceId == preferredSource;
        }

        Write(new string('-', 72));
        Write($"  已勾选 {parsed.SelectedEpisodes.Count()} / {parsed.TotalEpisodes} 集" +
              (string.IsNullOrWhiteSpace(episodesSpec) ? "" : $"（选集规则 {episodesSpec}）"));
        foreach (var ep in parsed.AllEpisodes.OrderBy(e => e.SourceId).ThenBy(e => e.Number))
        {
            Write($"    [{(ep.IsSelected ? "✓" : " ")}] sid={ep.SourceId}  {ep.DisplayTitle,-12} " +
                  $"{ep.PageUrl}{(ep.PlaylistUrl is null ? "" : "   ← 直链已解析")}");
        }
        Write(new string('-', 72));

        var options = new SeriesDownloadOptions
        {
            EpisodeConcurrency = Math.Clamp(epConcurrency, 1, 8),
            SegmentConcurrency = Math.Clamp(segmentConcurrency, 1, 64),
            MaxRetries = Math.Clamp(retries, 0, 10),
            AutoSkipInvalidSegments = skipAds,
            OutputDirectory = outputDir,
            TempRootDirectory = Path.Combine(Path.GetTempPath(), "M3U8Downloader", "series"),
            PreferHeight = preferHeight > 0 ? preferHeight : null,
        };

        // 实际落盘位置 = 系统「下载」目录（或 -o 指定）+ 自动生成的「剧名」子目录
        var resolvedDir = SeriesDownloader.ResolveSeriesDirectory(parsed, options);
        Write($"  保存到   : {resolvedDir}");

        if (listOnly)
        {
            Write("  --list / --dry-run：仅列出剧集，不下载。");
            if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
            return 0;
        }

        Write($"  集/分片并发: {options.EpisodeConcurrency} 集 × {options.SegmentConcurrency} 分片 " +
              $"(≈{options.EpisodeConcurrency * options.SegmentConcurrency} 连接)");
        if (options.PreferHeight is not null) Write($"  目标清晰度: {options.PreferHeight}p");
        Write(new string('-', 72));

        var lastReport = DateTime.UtcNow;
        var progress = new Progress<SeriesDownloadProgress>(p =>
        {
            if ((DateTime.UtcNow - lastReport).TotalSeconds < 1) return;
            lastReport = DateTime.UtcNow;
            Console.Write($"\r  总进度 {p.OverallPercent,5:0.0}%  集 {p.FinishedEpisodes}/{p.TotalEpisodes}" +
                          $"  当前 {p.CurrentEpisodeTitle} {p.CurrentEpisodePercent:0.0}%" +
                          $"  {p.SpeedText}        ");
        });

        var report = await seriesDownloader.DownloadAsync(parsed, options, progress);
        Console.WriteLine();

        Write(new string('-', 72));
        foreach (var e in report.Episodes.OrderBy(x => x.Episode.Number)) Write("  · " + e);
        foreach (var l in report.Log.Where(l => l.Contains("清晰度"))) Write("  · " + l);
        Write($"  {report}");
        Write($"  耗时     : {report.Elapsed:hh\\:mm\\:ss}");
        Write($"  结果     : {(report.Success ? "全部成功" : "存在失败/取消")}");

        if (logFile != null) await File.WriteAllTextAsync(logFile, log.ToString(), Encoding.UTF8);
        return report.Success ? 0 : 1;
    }

    private static void PrintHelp(Action<string> write)
    {
        write("M3U8 下载器 · 命令行模式");
        write("");
        write("用法: m3u8dl <m3u8地址> [选项]");
        write("");
        write("选项:");
        write("  -o, --out <目录>        输出目录（默认系统的「下载」目录）");
        write("  -n, --name <文件名>     输出文件名（不含扩展名）");
        write("      --referer <地址>    附加 Referer 请求头");
        write("      --origin <地址>     附加 Origin 请求头");
        write("      --ua <字符串>       自定义 User-Agent");
        write("  -c, --concurrency <n>   并发下载数，默认 16");
        write("      --retries <n>       单分片重试次数，默认 3");
        write("      --no-skip-ads       不自动跳过疑似广告分片");
        write("      --dry-run           只解析和分析，不下载");
        write("      --variants          只列出所有清晰度");
        write("      --log <文件>        把完整日志写入文件");
        write("");
        write("站点/剧集批量模式:");
        write("      --series <播放页>   识别站点并列出整部剧的剧集，确认后批量下载");
        write("      --list              只列出剧集，不下载（同 --dry-run）");
        write("      --episodes <选集>   选集，如 1-8,12（默认当前播放源全部）");
        write("      --all-sources       多播放源时全部勾选（默认只用当前源，避免重复）");
        write("      --ep-concurrency <n> 同时下载几集，默认 2");
        write("      --height <n>        目标清晰度高度，如 1080（默认最高）");
        write("");
        write("FFmpeg / 代理:");
        write("      --ffmpeg-status     查看 FFmpeg 检测结果（自定义路径 → 应用内下载 → 系统 PATH）");
        write("      --ffmpeg-download   应用内下载并安装 FFmpeg（约 200MB）");
        write("      --ffmpeg-url <url>  指定 FFmpeg 下载源（默认 BtbN 官方构建，可换镜像）");
        write("      --ffmpeg-path <exe> 手动指定 ffmpeg.exe 路径");
        write("      --proxy <url>       使用代理，如 http://127.0.0.1:7897");
        write("      --proxy-test        测试代理连通性（与界面「测试」按钮同一实现）");
        write("  -h, --help              显示帮助");
        write("");
        write("示例:");
        write("  m3u8dl https://example.com/index.m3u8 -o D:\\videos -n 第01集");
        write("  m3u8dl https://example.com/index.m3u8 --dry-run");
        write("  m3u8dl --series https://www.qmao.net/vodplay/30450-1-1.html --list");
        write("  m3u8dl --series https://www.qmao.net/vodplay/30450-1-1.html --episodes 1-8 -o D:\\剧集");
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
