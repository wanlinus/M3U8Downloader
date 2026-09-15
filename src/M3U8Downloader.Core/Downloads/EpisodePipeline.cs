using System.Diagnostics;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Staging;

namespace M3U8Downloader.Core.Downloads;

/// <summary>一集（或一个单文件）当前走到了流水线的哪一步</summary>
public enum EpisodeStage
{
    /// <summary>解析播放列表</summary>
    Resolving,

    /// <summary>下载分片</summary>
    Downloading,

    /// <summary>转封装 + 产物体检</summary>
    Finalizing,
}

/// <summary>流水线的最终状态</summary>
public enum EpisodeOutcomeStatus
{
    Pending,
    Completed,
    Failed,
    Canceled,
}

/// <summary>
/// 一集的下载任务描述。
///
/// 刻意**不含任何站点概念**（没有 SiteEpisode / 剧集 / 集号）——
/// 站点批量模式与单文件模式都归结成"一个已解析出的播放列表地址 + 一个暂存目录 + 一个输出文件名"。
/// </summary>
public sealed class EpisodeJob
{
    /// <summary>显示名：日志前缀与进度标题（站点模式是「第 12 集」，单文件模式是文件名）</summary>
    public required string Title { get; init; }

    /// <summary>播放列表地址（媒体清单或主清单都可以，主清单会按偏好挑清晰度）</summary>
    public required string PlaylistUrl { get; init; }

    /// <summary>主清单时的清晰度偏好（变体 URI）；为空则取最高清晰度</summary>
    public string? PreferredVariantUri { get; init; }

    /// <summary>最终产物放哪个目录</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>产物文件名（不含扩展名，扩展名由实际格式决定）</summary>
    public required string FileName { get; init; }

    /// <summary>暂存目录（分片与清单都放这里，由 <see cref="EpisodePipeline.CreateStagingDirectory"/> 生成）</summary>
    public required string StagingDirectory { get; init; }
}

/// <summary>流水线参数</summary>
public sealed class EpisodePipelineOptions
{
    /// <summary>分片并发</summary>
    public int SegmentConcurrency { get; init; } = 16;

    /// <summary>单分片最大重试次数</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>重试基础退避（毫秒）</summary>
    public int RetryBaseDelayMs { get; init; } = 500;

    /// <summary>单次请求超时（秒）</summary>
    public int TimeoutSeconds { get; init; } = 30;

    /// <summary>是否自动跳过疑似广告/无效分片</summary>
    public bool AutoSkipInvalidSegments { get; init; } = true;

    /// <summary>ffmpeg 路径；为空则保留 TS 原格式（时长核对仍走内置探测）</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>是否做全量解码检查（<c>ffmpeg -f null -</c>），默认开</summary>
    public bool FullDecodeCheck { get; init; } = true;
}

/// <summary>一集跑完流水线后的全部结论（报告与界面都从这里取数）</summary>
public sealed class EpisodeOutcome
{
    public required string Title { get; init; }

    public EpisodeOutcomeStatus Status { get; set; } = EpisodeOutcomeStatus.Pending;

    /// <summary>失败原因（引擎没给原因时会带上分片统计，便于排查）</summary>
    public string? Error { get; set; }

    public string? PlaylistUrl { get; set; }

    /// <summary>失败时保留的暂存目录；成功清理后为 null</summary>
    public string? StagingDirectory { get; set; }

    public string? OutputPath { get; set; }
    public long OutputBytes { get; set; }

    /// <summary>产物格式：mp4 / ts</summary>
    public string? Format { get; set; }

    /// <summary>产物容器探测结果（ffprobe）</summary>
    public ContainerProbe? Container { get; set; }

    /// <summary>全量解码检查结果</summary>
    public DecodeCheckResult? DecodeCheck { get; set; }

    /// <summary>时长核对结论（清单声明 vs 产物实际）</summary>
    public string? DurationVerdict { get; set; }

    /// <summary>合并字节核对结论（应拼入的分片之和 vs 产物大小）</summary>
    public string? MergeVerdict { get; set; }

    /// <summary>该播放列表声明的时长（秒）</summary>
    public double DurationSeconds { get; set; }

    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int FailedSegments { get; set; }
    public int SkippedSegments { get; set; }

    /// <summary>纯下载耗时（不含转封装与产物体检）</summary>
    public TimeSpan Elapsed { get; set; }

    public bool Success => Status == EpisodeOutcomeStatus.Completed;
}

/// <summary>
/// 一集的完整流水线：解析清单 → 下载到暂存目录 → 转封装 MP4 → 产物体检 → 清理暂存。
///
/// 抽出来的原因：这段流程原先只存在于 <c>SeriesDownloader</c> 里，单文件模式于是自己又写了一遍
/// （且少了转封装与产物体检）。现在两条路径都走这里，校验强度不会再出现"站点有、单文件没有"的落差。
///
/// 职责边界：
/// - 本类只管"一集"，不做并发调度、不碰界面 —— 集间并发与进度聚合留在 <c>SeriesDownloader</c>；
/// - 暂存目录由调用方用 <see cref="CreateStagingDirectory"/> 取（可能要复用上次的，**不能**在这里新建）；
/// - 日志统一走构造时传入的 <c>log</c> 回调，避免多集并发时直接往同一个 List 里塞。
/// </summary>
public sealed class EpisodePipeline
{
    private readonly HlsDownloader _hls;
    private readonly Dictionary<string, string> _headers;
    private readonly EpisodePipelineOptions _options;
    private readonly Action<string> _log;

    public EpisodePipeline(
        HlsDownloader hls,
        Dictionary<string, string> headers,
        EpisodePipelineOptions options,
        Action<string>? log = null)
    {
        _hls = hls;
        _headers = headers;
        _options = options;
        _log = log ?? (_ => { });
    }

    /// <summary>跑完一集。**不抛异常**：失败与取消都体现在返回值里</summary>
    public async Task<EpisodeOutcome> RunAsync(
        EpisodeJob job,
        IProgress<DownloadProgress>? progress = null,
        Action<EpisodeStage>? onStage = null,
        CancellationToken ct = default)
    {
        var outcome = new EpisodeOutcome
        {
            Title = job.Title,
            PlaylistUrl = job.PlaylistUrl,
            StagingDirectory = job.StagingDirectory,
        };

        var clock = Stopwatch.StartNew();

        try
        {
            // ---- 1. 解析媒体清单（主清单自动挑清晰度）----
            onStage?.Invoke(EpisodeStage.Resolving);
            var (media, parseLogs) = await _hls
                .ResolveMediaPlaylistAsync(job.PlaylistUrl, job.PreferredVariantUri, ct)
                .ConfigureAwait(false);

            foreach (var line in parseLogs) _log($"[{job.Title}] {line}");

            outcome.TotalSegments = media.Segments.Count;
            outcome.DurationSeconds = media.TotalDuration;

            // ---- 2. 下载到暂存目录（先合并成中间文件，转封装成功后再删暂存）----
            onStage?.Invoke(EpisodeStage.Downloading);

            var intermediate = Path.Combine(job.StagingDirectory, media.IsFmp4 ? "merged.mp4" : "merged.ts");

            var downloadOptions = new DownloadOptions
            {
                Concurrency = Math.Clamp(_options.SegmentConcurrency, 1, 64),
                MaxRetries = Math.Clamp(_options.MaxRetries, 0, 10),
                RetryBaseDelayMs = _options.RetryBaseDelayMs,
                TimeoutSeconds = _options.TimeoutSeconds,
                AutoSkipInvalidSegments = _options.AutoSkipInvalidSegments,
                Headers = _headers,
                TempDirectory = job.StagingDirectory,
                OutputPath = intermediate,
                // 暂存目录的生死由本方法统一管理（要先转 MP4 再删）
                DeleteTempOnSuccess = false,
            };

            var result = await _hls.DownloadAsync(media, downloadOptions, progress, ct).ConfigureAwait(false);
            clock.Stop();

            outcome.MergeVerdict = result.Messages
                .FirstOrDefault(m => m.StartsWith("合并核对", StringComparison.Ordinal));

            // 下载耗时要在转封装之前定格：转 MP4 与产物校验都算在后面，
            // 否则报告里的"耗时"会把这两步也算进去（大文件能差出几十秒）
            outcome.Elapsed = clock.Elapsed;
            outcome.TotalSegments = result.TotalSegments > 0 ? result.TotalSegments : outcome.TotalSegments;
            outcome.CompletedSegments = result.CompletedSegments;
            outcome.FailedSegments = result.FailedSegments;
            outcome.SkippedSegments = result.SkippedSegments;

            foreach (var message in result.Messages) _log($"[{job.Title}] {message}");

            if (!result.Success)
            {
                outcome.Status = EpisodeOutcomeStatus.Failed;
                // 引擎偶尔会在没给出 Error 的情况下判失败，这时把分片统计打出来 ——
                // 只显示"未知错误"对排查毫无帮助。
                outcome.Error = result.Error
                    ?? $"分片统计 成功 {result.CompletedSegments} / 跳过 {result.SkippedSegments} / " +
                       $"失败 {result.FailedSegments} / 共 {result.TotalSegments}，输出 {result.OutputBytes} 字节";
                _log($"[{job.Title}] 失败：{outcome.Error}（暂存目录已保留：{job.StagingDirectory}）");
                return outcome;
            }

            // ---- 3. 转成常用格式（MP4）----
            onStage?.Invoke(EpisodeStage.Finalizing);

            // 清单声明的时长要减掉被跳过的广告片，否则"时长核对"会误判成缺片
            var skipped = result.SkippedSegmentIndices.ToHashSet();
            var expectedSeconds = media.Segments
                .Where(s => !skipped.Contains(s.Index))
                .Sum(s => s.Duration);

            var final = await FinalizeOutputAsync(
                job, intermediate, media.IsFmp4, expectedSeconds, ct).ConfigureAwait(false);

            outcome.OutputPath = final.Path;
            outcome.OutputBytes = final.Bytes;
            outcome.Format = final.Format;
            outcome.DurationVerdict = final.DurationVerdict;

            // ---- 4. 产物体检：容器信息 + 全量解码检查 ----
            await InspectProductAsync(outcome, final.Path, ct).ConfigureAwait(false);

            // ---- 5. 全部成功 → 删除暂存目录（含中间文件）----
            if (StagingStore.TryRemoveStagingDirectory(job.StagingDirectory))
            {
                outcome.StagingDirectory = null;
                _log($"[{job.Title}] 已清理暂存目录。");
            }
            else
            {
                _log($"[{job.Title}] 暂存目录未能删除（可能被占用）：{job.StagingDirectory}");
            }

            outcome.Status = EpisodeOutcomeStatus.Completed;
            _log($"[{job.Title}] 完成 → {final.Path}");
            return outcome;
        }
        catch (OperationCanceledException)
        {
            outcome.Status = EpisodeOutcomeStatus.Canceled;
            return outcome;
        }
        catch (Exception ex)
        {
            outcome.Status = EpisodeOutcomeStatus.Failed;
            outcome.Error = ex.Message;
            _log($"[{job.Title}] 异常：{ex.Message}");
            return outcome;
        }
        finally
        {
            clock.Stop();
        }
    }

    /// <summary>
    /// 取暂存目录，名字形如 <c>.m3u8tmp-007-a1b2c3d4</c>。
    ///
    /// - 放在下载目录里（而不是系统临时目录）：便于用户发现与清理，出问题时也容易找到；
    /// - **已存在就直接复用**：里面可能存着上次下到一半的分片，这就是断点续传的关键。
    ///   分片能不能用由引擎按「清单指纹」判断（播放列表变了就整批清掉重下），所以复用是安全的；
    /// - 只有确实没有历史目录时才新建一个带随机串的，避免与并发下载互相踩。
    /// </summary>
    public static string CreateStagingDirectory(string directory, int episodeNumber) =>
        CreateStagingDirectory(directory, episodeNumber.ToString("000"));

    /// <summary>
    /// 同上的通用版本。<paramref name="tag"/> 是目录名里的标识：
    /// 站点模式用集号（<c>007</c>），单文件模式用 <c>single</c> ——
    /// 两者互不干扰，各自都能命中自己的历史目录继续续传。
    /// </summary>
    public static string CreateStagingDirectory(string directory, string tag)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                var candidates = Directory.GetDirectories(directory, $".m3u8tmp-{tag}-*");
                if (candidates.Length > 0)
                {
                    // 同集有多个残留时取最近用过的那个
                    return candidates
                        .OrderByDescending(Directory.GetLastWriteTimeUtc)
                        .First();
                }
            }
        }
        catch
        {
            // 目录枚举失败就按新建处理
        }

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(directory, $".m3u8tmp-{tag}-{suffix}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 把合并好的中间文件转成常用格式（MP4）。ffmpeg 不可用时退回保留 TS。
    ///
    /// <paramref name="expectedSeconds"/> 是清单声明的时长（跳过广告片后重算）。
    /// 转完后会用 ffprobe/ffmpeg 读产物**真实时长**比对 ——
    /// "ffmpeg 退出码 0 + 文件非空"只能说明封装成功，**说明不了没缺片**：
    /// 少一段照样能转出 MP4。时长对不上就写进日志，由调用方决定是否算完整。
    /// </summary>
    private async Task<(string Path, long Bytes, string Format, string? DurationVerdict)> FinalizeOutputAsync(
        EpisodeJob job, string intermediate, bool alreadyFmp4, double expectedSeconds, CancellationToken ct)
    {
        var ffmpegPath = _options.FfmpegPath;
        var finalPath = Path.Combine(job.OutputDirectory, job.FileName + ".mp4");

        if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
        {
            var remux = await FfmpegRunner.RemuxToMp4Async(ffmpegPath!, intermediate, finalPath, ct)
                .ConfigureAwait(false);

            if (remux.Success && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                _log($"[{job.Title}] 已转封装为 MP4（流复制，无画质损失）。");
                var verdict = await CheckDurationAsync(ffmpegPath, finalPath, expectedSeconds, job.Title, ct)
                    .ConfigureAwait(false);
                return (finalPath, new FileInfo(finalPath).Length, "mp4", verdict);
            }

            _log($"[{job.Title}] 转 MP4 失败，改为保留原始格式：{remux.Error}");
            try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
        }
        else
        {
            _log($"[{job.Title}] 未配置 FFmpeg，跳过转 MP4；如需 MP4 请在设置里指定或下载 FFmpeg。");
        }

        // 退路：把中间文件搬到输出目录并保留原格式
        var fallbackExt = alreadyFmp4 ? ".mp4" : ".ts";
        var fallbackPath = Path.Combine(job.OutputDirectory, job.FileName + fallbackExt);
        try
        {
            if (File.Exists(fallbackPath)) File.Delete(fallbackPath);
            File.Move(intermediate, fallbackPath);
        }
        catch (Exception ex)
        {
            _log($"[{job.Title}] 移动产物失败：{ex.Message}");
            return (intermediate, new FileInfo(intermediate).Length, alreadyFmp4 ? "mp4" : "ts", null);
        }

        // 没有 ffmpeg 时也要核对：内置探测不依赖外部工具
        var fallbackVerdict = await CheckDurationAsync(ffmpegPath, fallbackPath, expectedSeconds, job.Title, ct)
            .ConfigureAwait(false);

        return (fallbackPath, new FileInfo(fallbackPath).Length, alreadyFmp4 ? "mp4" : "ts", fallbackVerdict);
    }

    /// <summary>
    /// 产物体检：容器信息（ffprobe）+ 全量解码检查（ffmpeg -f null -）。
    ///
    /// 这两项是"报告里该有的东西"：
    /// - 容器信息回答"下出来的是什么"（格式/时长/码率/编码/分辨率/帧率/声道）；
    /// - 解码检查回答"内容是不是好的"—— 包对齐、字节数、时长全对，
    ///   源站返回的坏包依然会留在产物里，只有整条解一遍才知道。
    /// </summary>
    private async Task InspectProductAsync(EpisodeOutcome outcome, string path, CancellationToken ct)
    {
        var ffmpegPath = _options.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath)) return;

        try
        {
            outcome.Container = await FfmpegRunner.TryProbeContainerAsync(ffmpegPath!, path, ct).ConfigureAwait(false);
            if (outcome.Container is { Streams.Count: > 0 })
            {
                _log($"[{outcome.Title}] 容器信息：{outcome.Container.FormatName}，" +
                     $"时长 {TimeSpan.FromSeconds(outcome.Container.DurationSeconds):hh\\:mm\\:ss}" +
                     (outcome.Container.BitRate > 0 ? $"，码率 {outcome.Container.BitRate / 1000} kbps" : "") +
                     $"；流：{string.Join(" + ", outcome.Container.Streams.Select(s => s.Describe()))}");
            }
        }
        catch
        {
            // 探测失败不影响产物
        }

        try
        {
            if (!_options.FullDecodeCheck)
            {
                _log($"[{outcome.Title}] 已跳过全量解码检查（设置里关掉了；容器与时长核对不受影响）。");
                return;
            }

            outcome.DecodeCheck = await FfmpegRunner.RunDecodeCheckAsync(ffmpegPath!, path, ct).ConfigureAwait(false);
            if (outcome.DecodeCheck is not null)
            {
                // 输出阶段的时间戳提示单列出来说，免得用户以为产物坏了
                var ignored = outcome.DecodeCheck.IgnoredMuxerWarnings > 0
                    ? $"（另有 {outcome.DecodeCheck.IgnoredMuxerWarnings} 条输出阶段时间戳提示，带 B 帧的源必然出现，与产物无关）"
                    : "";

                _log(outcome.DecodeCheck.Passed
                    ? $"[{outcome.Title}] 全量解码检查通过（{outcome.DecodeCheck.Elapsed.TotalSeconds:0.0}s）{ignored}。"
                    : $"[{outcome.Title}] ⚠ 全量解码检查发现异常（退出码 {outcome.DecodeCheck.ExitCode}）：" +
                      $"{string.Join("；", outcome.DecodeCheck.Issues)}。这通常是源站数据问题，不是拼接错位{ignored}。");
            }
        }
        catch
        {
            // 同上
        }
    }

    /// <summary>
    /// 读产物真实时长并与清单声明值比对，差距过大时写一条醒目日志。
    ///
    /// 先用**内置探测**（TS 累加 PCR / MP4 读 mvhd）—— 不依赖外部工具，任何机器都能做；
    /// 内置读不出来再退回 ffmpeg/ffprobe。两边都读不出来就明确报告"读不出"，
    /// 而不是默默当通过（那等于没校验）。
    /// </summary>
    private async Task<string?> CheckDurationAsync(
        string? ffmpegPath, string path, double expectedSeconds, string title, CancellationToken ct)
    {
        if (expectedSeconds <= 0) return null;

        var source = "内置探测";
        double? actual = MediaDurationProbe.TryProbeSeconds(path);

        if (actual is null && !string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
        {
            source = "ffmpeg";
            try
            {
                actual = await FfmpegRunner.TryGetDurationSecondsAsync(ffmpegPath!, path, ct).ConfigureAwait(false);
            }
            catch
            {
                actual = null;
            }
        }

        if (actual is null)
        {
            var note = $"读不出产物时长（清单声明 {expectedSeconds:0.0}s）";
            _log($"[{title}] ⚠ 时长核对未完成：{note}；产物已保留，建议用播放器确认是否能完整播放。");
            return $"未完成（{note}）";
        }

        // 容差：TS 里 PTS/DTS 与容器时长本来就有零点几秒的出入，广告片跳过也会造成偏差，
        // 所以只在差距明显（>5% 且 >3 秒）时才判定为"缺片"。
        var diff = Math.Abs(actual.Value - expectedSeconds);
        var tolerance = Math.Max(3.0, expectedSeconds * 0.05);

        if (diff > tolerance)
        {
            _log($"[{title}] ⚠ 时长核对不通过（{source}）：清单声明 {expectedSeconds:0.0}s，" +
                 $"产物实际 {actual.Value:0.0}s，相差 {diff:0.0}s（可能缺片，建议核对原播放列表）。");
            return $"不通过：产物 {actual.Value:0.0}s vs 清单 {expectedSeconds:0.0}s（差 {diff:0.0}s）";
        }

        _log($"[{title}] 时长核对通过（{source}）：产物 {actual.Value:0.0}s，清单声明 {expectedSeconds:0.0}s。");
        return $"通过：{actual.Value:0.0}s / 声明 {expectedSeconds:0.0}s（{source}）";
    }
}
