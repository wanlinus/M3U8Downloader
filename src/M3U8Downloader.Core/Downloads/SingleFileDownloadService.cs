using System.Text;

namespace M3U8Downloader.Core.Downloads;

/// <summary>单文件下载参数</summary>
public sealed class SingleFileDownloadOptions
{
    /// <summary>m3u8 地址（媒体清单或主清单都可以）</summary>
    public required string Url { get; init; }

    /// <summary>附加请求头（Referer / Origin / User-Agent 等）</summary>
    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>输出目录；留空则用当前目录</summary>
    public string? OutputDirectory { get; init; }

    /// <summary>输出文件名（不含扩展名）；留空则从地址里推断</summary>
    public string? FileName { get; init; }

    /// <summary>分片并发</summary>
    public int SegmentConcurrency { get; init; } = 16;

    /// <summary>单分片最大重试次数</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>单次请求超时（秒）</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>是否自动跳过疑似广告/无效分片</summary>
    public bool AutoSkipInvalidSegments { get; init; } = true;

    /// <summary>ffmpeg 路径；为空则保留 TS，不转 MP4（时长核对仍走内置探测）</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>是否做全量解码检查（默认开；400 MB 约 35 秒）</summary>
    public bool FullDecodeCheck { get; init; } = true;

    /// <summary>下载结束后是否写出 Markdown 报告</summary>
    public bool WriteReport { get; init; } = true;
}

/// <summary>单文件下载进度</summary>
public sealed class SingleFileProgress
{
    /// <summary>当前阶段（解析 / 下载 / 转封装体检）</summary>
    public string Phase { get; set; } = "准备中";

    public double Percent { get; set; }
    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int FailedSegments { get; set; }
    public int SkippedSegments { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan? Eta { get; set; }

    public string SpeedText => SpeedBytesPerSecond switch
    {
        >= 1024 * 1024 => $"{SpeedBytesPerSecond / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{SpeedBytesPerSecond / 1024:0.0} KB/s",
        _ => $"{SpeedBytesPerSecond:0} B/s",
    };

    public string SizeText => $"{DownloadedBytes / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>单文件下载结果</summary>
public sealed class SingleFileDownloadReport
{
    public required string Url { get; init; }

    public string? OutputDirectory { get; set; }
    public string? FileName { get; set; }

    /// <summary>流水线结论（分片统计、产物、各项校验）</summary>
    public EpisodeOutcome? Outcome { get; set; }

    /// <summary>被判为插播广告/无效的分片数（下载前分析出来的）</summary>
    public int SuspectSegments { get; set; }

    /// <summary>整趟耗时（解析 + 下载 + 转封装 + 体检）</summary>
    public TimeSpan Elapsed { get; set; }

    /// <summary>写出的报告路径（未开启或写失败时为 null）</summary>
    public string? ReportPath { get; set; }

    /// <summary>完整运行日志</summary>
    public List<string> Log { get; } = new();

    public bool Success => Outcome?.Status == EpisodeOutcomeStatus.Completed;
    public string? Error => Outcome?.Error;
    public string? OutputPath => Outcome?.OutputPath;
    public long OutputBytes => Outcome?.OutputBytes ?? 0;
    public string? Format => Outcome?.Format;
    public double DurationSeconds => Outcome?.DurationSeconds ?? 0;

    public override string ToString() => Success
        ? $"✔ {OutputPath}（{OutputBytes / 1024.0 / 1024.0:0.0} MB，{Elapsed:hh\\:mm\\:ss}）"
        : $"✘ {Error ?? "未完成"}";
}

/// <summary>只解析不下载时的分析结果（命令行 --dry-run / --list 用）</summary>
public sealed class SingleFileAnalysis
{
    public required HlsMediaPlaylist Media { get; init; }

    /// <summary>解析过程的日志</summary>
    public required List<string> Log { get; init; }

    /// <summary>广告/无效分片识别结果</summary>
    public required SegmentInspector.Report Inspection { get; init; }
}

/// <summary>
/// 单文件下载服务：把一个 m3u8 地址完整地下成一个带校验的产物。
///
/// 它**不自己实现下载流程**，而是把「一集」交给 <see cref="EpisodePipeline"/> ——
/// 与站点批量模式走的是同一条流水线（暂存目录 + 清单指纹续传 + 转 MP4 + 产物体检）。
/// 之前这段编排直接写在界面的 ViewModel 里，导致两条路径的实现与校验强度长期不一致。
/// </summary>
public sealed class SingleFileDownloadService
{
    private readonly Dictionary<string, string> _headers;

    public SingleFileDownloadService(Dictionary<string, string>? headers = null)
    {
        _headers = headers ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 下载一个 m3u8。**不抛异常**：失败/取消都体现在返回值里，
    /// 调用方（界面、命令行）只需要看 <see cref="SingleFileDownloadReport.Success"/>。
    /// </summary>
    public async Task<SingleFileDownloadReport> DownloadAsync(
        SingleFileDownloadOptions options,
        IProgress<SingleFileProgress>? progress = null,
        Action<string>? onLog = null,
        CancellationToken ct = default)
    {
        var report = new SingleFileDownloadReport { Url = options.Url };
        void Log(string message)
        {
            report.Log.Add(message);
            onLog?.Invoke(message);
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var outputDir = string.IsNullOrWhiteSpace(options.OutputDirectory) ? "." : options.OutputDirectory;
            Directory.CreateDirectory(outputDir);

            var fileName = ResolveFileName(options.FileName, options.Url);
            report.OutputDirectory = outputDir;
            report.FileName = fileName;

            var headers = new Dictionary<string, string>(_headers, StringComparer.OrdinalIgnoreCase);

            using var hls = new HlsDownloader(headers, Math.Clamp(options.TimeoutSeconds, 5, 300));

            progress?.Report(new SingleFileProgress { Phase = "正在解析播放列表…" });
            Log($"开始解析：{options.Url}");
            Log($"附加请求头：{(headers.Count == 0 ? "无" : string.Join(", ", headers.Keys))}");

            var (media, parseLogs) = await hls.ResolveMediaPlaylistAsync(options.Url, null, ct).ConfigureAwait(false);
            foreach (var line in parseLogs) Log(line);
            Log($"分片 {media.Segments.Count} 个，总时长 {TimeSpan.FromSeconds(media.TotalDuration):hh\\:mm\\:ss}，" +
                $"加密方式 {DescribeEncryption(media)}");

            // 下载前先把广告/无效分片分析结果展示出来
            var inspection = SegmentInspector.Inspect(media);
            report.SuspectSegments = inspection.Suspects.Count;
            if (inspection.Suspects.Count > 0)
            {
                Log(new string('-', 60));
                Log($"⚠ 检测到 {inspection.Suspects.Count} 个疑似插播广告/无效分片：");
                foreach (var reason in inspection.Reasons) Log("  · " + reason);
                foreach (var suspect in inspection.Suspects.Take(12))
                    Log($"  [{suspect.Index}] {suspect.FileName}  {suspect.Duration:0.##}s  {suspect.ValidityNote}");
                if (inspection.Suspects.Count > 12)
                    Log($"  …另有 {inspection.Suspects.Count - 12} 个同类分片");
                Log(options.AutoSkipInvalidSegments
                    ? "→ 已启用自动跳过，这些分片不会导致任务失败。"
                    : "→ 未启用自动跳过，任务可能因这些分片失败。");
                Log(new string('-', 60));
            }
            else
            {
                Log("未发现异目录/重复分片，播放列表看起来是干净的。");
            }

            // 暂存目录放在下载目录里（与站点模式一致）：出问题好找，续传也认得出
            var stagingDir = EpisodePipeline.CreateStagingDirectory(outputDir, "single");
            Log($"暂存目录：{stagingDir}");

            var pipeline = new EpisodePipeline(hls, headers, new EpisodePipelineOptions
            {
                SegmentConcurrency = options.SegmentConcurrency,
                MaxRetries = options.MaxRetries,
                TimeoutSeconds = options.TimeoutSeconds,
                AutoSkipInvalidSegments = options.AutoSkipInvalidSegments,
                FfmpegPath = options.FfmpegPath,
                FullDecodeCheck = options.FullDecodeCheck,
            }, Log);

            var inner = new Progress<DownloadProgress>(p => progress?.Report(new SingleFileProgress
            {
                Phase = "正在下载分片…",
                Percent = p.Percent,
                TotalSegments = p.TotalSegments,
                CompletedSegments = p.CompletedSegments,
                FailedSegments = p.FailedSegments,
                SkippedSegments = p.SkippedSegments,
                DownloadedBytes = p.DownloadedBytes,
                TotalBytes = p.TotalBytes,
                SpeedBytesPerSecond = p.SpeedBytesPerSecond,
                Elapsed = p.Elapsed,
                Eta = p.Eta,
            }));

            var outcome = await pipeline.RunAsync(new EpisodeJob
            {
                Title = fileName,
                PlaylistUrl = options.Url,
                OutputDirectory = outputDir,
                FileName = fileName,
                StagingDirectory = stagingDir,
            }, inner, stage => progress?.Report(new SingleFileProgress
            {
                Phase = stage switch
                {
                    EpisodeStage.Resolving => "正在解析播放列表…",
                    EpisodeStage.Downloading => "正在下载分片…",
                    _ => "正在转封装并体检产物…",
                },
            }), ct).ConfigureAwait(false);

            report.Outcome = outcome;
            clock.Stop();
            report.Elapsed = clock.Elapsed;

            Log(new string('-', 60));
            Log($"分片：成功 {outcome.CompletedSegments} / 跳过 {outcome.SkippedSegments} / " +
                $"失败 {outcome.FailedSegments} / 共 {outcome.TotalSegments}");
            Log($"耗时：{outcome.Elapsed:hh\\:mm\\:ss}");

            if (outcome.Success)
            {
                Log($"✔ 输出文件：{outcome.OutputPath}");
                Log($"  大小：{outcome.OutputBytes / 1024.0 / 1024.0:0.0} MB");

                if (options.WriteReport)
                {
                    var path = TryWriteReportMarkdown(report, outputDir);
                    if (path is not null)
                    {
                        report.ReportPath = path;
                        Log($"下载报告已写出：{path}");
                    }
                }
            }
            else if (outcome.Status == EpisodeOutcomeStatus.Canceled)
            {
                Log("已取消。已下载的分片已保留，重新开始会接着下。");
            }
            else
            {
                Log("✘ " + (outcome.Error ?? "下载未完成。"));
            }

            return report;
        }
        catch (OperationCanceledException)
        {
            report.Outcome = new EpisodeOutcome { Title = report.FileName ?? "video", Status = EpisodeOutcomeStatus.Canceled };
            Log("已取消。已下载的分片已保留，重新开始会接着下。");
            return report;
        }
        catch (Exception ex)
        {
            report.Outcome = new EpisodeOutcome
            {
                Title = report.FileName ?? "video",
                Status = EpisodeOutcomeStatus.Failed,
                Error = ex.Message,
            };
            Log("✘ 异常：" + ex.Message);
            return report;
        }
        finally
        {
            clock.Stop();
        }
    }

    /// <summary>解析地址、分析分片，但**不下载**（命令行 --dry-run 用）</summary>
    public async Task<SingleFileAnalysis> AnalyzeAsync(string url, CancellationToken ct = default)
    {
        using var hls = new HlsDownloader(_headers, 30);
        var (media, parseLogs) = await hls.ResolveMediaPlaylistAsync(url, null, ct).ConfigureAwait(false);

        return new SingleFileAnalysis
        {
            Media = media,
            Log = parseLogs,
            Inspection = SegmentInspector.Inspect(media),
        };
    }

    /// <summary>把文件名里的非法字符换掉；空则回落到 "video"</summary>
    public static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "video" : name.Trim();
    }

    /// <summary>没给文件名时从地址里推断（取最后一段路径）</summary>
    public static string ResolveFileName(string? fileName, string url)
    {
        if (!string.IsNullOrWhiteSpace(fileName)) return SanitizeFileName(fileName);

        try
        {
            var path = new Uri(url).AbsolutePath.TrimEnd('/');
            var last = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(last)) return SanitizeFileName(last);
        }
        catch
        {
            // 地址不是合法 URI 时直接用默认名
        }

        return "video";
    }

    /// <summary>
    /// 写出单文件下载报告（Markdown），与产物放在同一个目录里。
    /// 结构对齐站点模式的报告：**校验结论单独成表**，不埋在日志里。
    /// </summary>
    public static string? TryWriteReportMarkdown(SingleFileDownloadReport report, string outputDirectory)
    {
        try
        {
            var name = report.FileName ?? "video";
            var path = Path.Combine(outputDirectory, name + "-下载报告.md");
            File.WriteAllText(path, BuildReportMarkdown(report), Encoding.UTF8);
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>生成报告正文</summary>
    public static string BuildReportMarkdown(SingleFileDownloadReport report)
    {
        var outcome = report.Outcome;
        var sb = new StringBuilder();

        sb.AppendLine($"# {report.FileName ?? "视频"} 下载报告");
        sb.AppendLine();
        sb.AppendLine($"- 来源地址：`{report.Url}`");
        sb.AppendLine($"- 输出目录：`{report.OutputDirectory}`");
        sb.AppendLine($"- 耗时：{report.Elapsed:hh\\:mm\\:ss}");
        sb.AppendLine($"- 自动跳过的广告/无效分片：{outcome?.SkippedSegments ?? 0} 个" +
                      (report.SuspectSegments > 0 ? $"（下载前判定 {report.SuspectSegments} 个疑似）" : ""));
        sb.AppendLine();

        if (outcome is null)
        {
            sb.AppendLine("未产生结果。");
            return sb.ToString();
        }

        if (outcome.Success)
        {
            sb.AppendLine($"- 产物：`{outcome.OutputPath}`（{outcome.Format}，" +
                          $"{outcome.OutputBytes / 1024.0 / 1024.0:0.0} MB）");
            sb.AppendLine($"- 时长：{TimeSpan.FromSeconds(outcome.DurationSeconds):hh\\:mm\\:ss}");
        }
        else
        {
            sb.AppendLine($"- 结果：{(outcome.Status == EpisodeOutcomeStatus.Canceled ? "已取消" : "失败")}");
            sb.AppendLine($"- 原因：{outcome.Error ?? "未说明"}");
            if (!string.IsNullOrWhiteSpace(outcome.StagingDirectory))
                sb.AppendLine($"- 暂存目录（分片已保留，可续传）：`{outcome.StagingDirectory}`");
        }

        sb.AppendLine();
        sb.AppendLine("## 分片统计");
        sb.AppendLine();
        sb.AppendLine("| 项目 | 数值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 分片总数 | {outcome.TotalSegments} |");
        sb.AppendLine($"| 成功 | {outcome.CompletedSegments} |");
        sb.AppendLine($"| 跳过（广告/无效） | {outcome.SkippedSegments} |");
        sb.AppendLine($"| 失败 | {outcome.FailedSegments} |");
        sb.AppendLine($"| 纯下载耗时 | {outcome.Elapsed:hh\\:mm\\:ss} |");
        sb.AppendLine();

        sb.AppendLine("## 产物校验");
        sb.AppendLine();
        sb.AppendLine("| 校验项 | 结果 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 分片落盘与合并字节核对 | {DescribeVerdict(outcome.MergeVerdict)} |");
        sb.AppendLine($"| 时长核对（清单声明 vs 产物实际） | {DescribeVerdict(outcome.DurationVerdict)} |");
        sb.AppendLine($"| 容器探测（格式/编码/分辨率/帧率） | {DescribeContainer(outcome.Container)} |");
        sb.AppendLine($"| 全量解码检查（ffmpeg -f null -） | {DescribeDecode(outcome.DecodeCheck)} |");
        sb.AppendLine();

        if (outcome.DecodeCheck is { Passed: false, Issues.Count: > 0 })
        {
            sb.AppendLine("> 解码检查发现的告警：");
            foreach (var issue in outcome.DecodeCheck.Issues) sb.AppendLine($"> - {issue}");
            sb.AppendLine(">");
            sb.AppendLine("> 通常是源站数据问题，不是拼接错位。");
            sb.AppendLine();
        }

        sb.AppendLine("## 运行日志");
        sb.AppendLine();
        sb.AppendLine("```");
        foreach (var line in report.Log) sb.AppendLine(line);
        sb.AppendLine("```");

        return sb.ToString();
    }

    private static string DescribeVerdict(string? verdict) =>
        string.IsNullOrWhiteSpace(verdict) ? "未执行" : verdict!;

    private static string DescribeContainer(ContainerProbe? probe) => probe is { Streams.Count: > 0 }
        ? $"已记录：{probe.FormatName}，" +
          $"{string.Join(" + ", probe.Streams.Select(s => s.Describe()))}"
        : "未执行";

    private static string DescribeDecode(DecodeCheckResult? check) => check is null
        ? "未执行"
        : check.Passed
            ? $"✔ 通过（{check.Elapsed.TotalSeconds:0.0}s）"
            : $"⚠ {check.Issues.Count} 类告警（退出码 {check.ExitCode}）";

    private static string DescribeEncryption(HlsMediaPlaylist media)
    {
        var keys = media.Segments.Select(s => s.Key.Method).Distinct().ToList();
        if (keys.Count == 1) return keys[0] == HlsEncryptionMethod.None ? "无加密" : keys[0].ToString();
        return string.Join(" + ", keys);
    }
}
