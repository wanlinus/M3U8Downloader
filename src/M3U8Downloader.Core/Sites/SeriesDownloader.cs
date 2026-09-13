using System.Text;
using System.Text.RegularExpressions;
using M3U8Downloader.Core.Downloads;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Sites;

/// <summary>单集下载状态</summary>
public enum EpisodeDownloadStatus
{
    Pending,
    Resolving,
    Downloading,
    Completed,
    Failed,
    Canceled,
}

/// <summary>单集下载结果</summary>
public sealed class EpisodeDownloadReport
{
    public required SiteEpisode Episode { get; init; }
    public EpisodeDownloadStatus Status { get; set; } = EpisodeDownloadStatus.Pending;
    public string? PlaylistUrl { get; set; }
    public string? OutputPath { get; set; }
    public long OutputBytes { get; set; }
    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int FailedSegments { get; set; }
    public int SkippedSegments { get; set; }
    public double Percent { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string? Error { get; set; }

    /// <summary>该集播放列表声明的时长（秒）</summary>
    public double DurationSeconds { get; set; }

    /// <summary>产物格式：mp4 / ts / m4s</summary>
    public string? Format { get; set; }

    /// <summary>失败时保留的暂存目录（便于排查或续传）</summary>
    public string? StagingDirectory { get; set; }

    /// <summary>产物容器探测结果（ffprobe）：格式、时长、每条流的编码与参数</summary>
    public ContainerProbe? Container { get; set; }

    /// <summary>全量解码检查结果（ffmpeg -f null -）：能发现"格式全对但内容坏了"</summary>
    public DecodeCheckResult? DecodeCheck { get; set; }

    /// <summary>时长核对结论（清单声明 vs 产物实际）</summary>
    public string? DurationVerdict { get; set; }

    /// <summary>合并字节核对结论（应拼入的分片之和 vs 产物大小）</summary>
    public string? MergeVerdict { get; set; }

    /// <summary>被判为插播广告而跳过的分片数</summary>
    public int SkippedAdSegments { get; set; }

    public string DisplayTitle => Episode.DisplayTitle;

    /// <summary>格式化的时长，便于报告展示</summary>
    public string DurationText => DurationSeconds > 0
        ? TimeSpan.FromSeconds(DurationSeconds).ToString(@"hh\:mm\:ss")
        : "-";

    public string SizeText => OutputBytes > 0 ? $"{OutputBytes / 1024.0 / 1024.0:0.0} MB" : "-";

    public override string ToString() => Status switch
    {
        EpisodeDownloadStatus.Completed => $"{DisplayTitle}  ✔ {SizeText}",
        EpisodeDownloadStatus.Failed => $"{DisplayTitle}  ✘ {Error}",
        EpisodeDownloadStatus.Canceled => $"{DisplayTitle}  ⊘ 已取消",
        _ => $"{DisplayTitle}  {Status} {Percent:0.0}%",
    };
}

/// <summary>整部剧的批量下载结果</summary>
public sealed class SeriesDownloadReport
{
    public required string Title { get; init; }
    public required string SiteName { get; init; }
    public List<EpisodeDownloadReport> Episodes { get; } = new();
    public List<string> Log { get; } = new();
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public TimeSpan Elapsed { get; set; }

    /// <summary>页面地址（写进报告用）</summary>
    public string? PageUrl { get; set; }

    /// <summary>输出目录</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>使用的播放源名称</summary>
    public string? SourceName { get; set; }

    /// <summary>产物格式（mp4 / ts）</summary>
    public string? OutputFormat { get; set; }

    /// <summary>写出的报告文件路径</summary>
    public string? ReportPath { get; set; }

    /// <summary>被跳过的插播广告分片总数</summary>
    public int TotalSkippedAds => Episodes.Sum(e => e.SkippedAdSegments);

    public int SucceededCount => Episodes.Count(e => e.Status == EpisodeDownloadStatus.Completed);
    public int FailedCount => Episodes.Count(e => e.Status == EpisodeDownloadStatus.Failed);
    public int CanceledCount => Episodes.Count(e => e.Status == EpisodeDownloadStatus.Canceled);
    public long TotalBytes => Episodes.Sum(e => e.OutputBytes);
    public double TotalDurationSeconds => Episodes.Sum(e => e.DurationSeconds);
    public bool Success => FailedCount == 0 && CanceledCount == 0 && Episodes.Count > 0;

    public override string ToString() =>
        $"{Title}：成功 {SucceededCount} / 失败 {FailedCount} / 取消 {CanceledCount}，" +
        $"共 {TotalBytes / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>单集进度快照（供界面展开查看每一集的进度）</summary>
public sealed class EpisodeProgressSnapshot
{
    public required int Number { get; init; }
    public required string Title { get; init; }

    private EpisodeDownloadStatus _status = EpisodeDownloadStatus.Pending;
    public EpisodeDownloadStatus Status
    {
        get => _status;
        set { _status = value; }
    }

    private double _percent;
    public double Percent
    {
        get => _percent;
        set { _percent = value; }
    }

    /// <summary>大小：下载中是已下载字节（实时增长），完成后是最终产物大小</summary>
    public long OutputBytes { get; set; }

    /// <summary>产物路径（完成后才有）—— 续传时用它判断这一集是否已经在磁盘上</summary>
    public string? OutputPath { get; set; }

    /// <summary>已下完的分片数 / 分片总数（界面显示"183/185 片"用）</summary>
    public int CompletedSegments { get; set; }
    public int TotalSegments { get; set; }

    public string Error { get; set; } = "";

    public string StatusText => Status switch
    {
        EpisodeDownloadStatus.Pending => "等待中",
        EpisodeDownloadStatus.Resolving => "解析中",
        EpisodeDownloadStatus.Downloading => "下载中",
        EpisodeDownloadStatus.Completed => "已完成",
        EpisodeDownloadStatus.Failed => "失败",
        EpisodeDownloadStatus.Canceled => "已取消",
        _ => Status.ToString(),
    };
}

/// <summary>批量下载的聚合进度（供 UI 绑定一条总进度条）</summary>
public sealed class SeriesDownloadProgress
{
    public required string Title { get; init; }
    public int TotalEpisodes { get; init; }
    public int FinishedEpisodes { get; set; }
    public int SucceededEpisodes { get; set; }
    public int FailedEpisodes { get; set; }

    /// <summary>每一集的进度快照</summary>
    public List<EpisodeProgressSnapshot> Episodes { get; } = new();

    /// <summary>当前正在下载的集</summary>
    public string? CurrentEpisodeTitle { get; set; }
    public double CurrentEpisodePercent { get; set; }

    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>
    /// 本集已完成/已取消/已失败的集数（不含等待中的）。
    /// 暂停时用它算真实进度：<see cref="OverallPercent"/> 在取消路径上不可靠
    /// （进度回调可能停在只报了一部分集的时刻），拿它当"总进度"会显示成 100%。
    /// </summary>
    public int CompletedEpisodes { get; set; }

    /// <summary>本集已下完的分片数（用于界面显示"正在下载 第12集 · 183/185 片"）</summary>
    public int CompletedSegments { get; set; }

    /// <summary>本集分片总数（播放列表还没解析出来时为 0）</summary>
    public int TotalSegments { get; set; }

    /// <summary>
    /// 整部剧的总体进度（0-100）：所有分集进度的平均值。
    /// 用平均值而不是"已完成集数 + 当前集百分比"，是因为多集并发时后者只反映其中一集，
    /// 进度会明显偏慢；平均值对每一集的推进都有响应，看起来是连续增长的。
    /// </summary>
    public double OverallPercent
    {
        get
        {
            if (TotalEpisodes == 0) return 0;
            if (Episodes.Count == 0) return 0;

            double sum = 0;
            foreach (var e in Episodes) sum += e.Percent;
            return Math.Clamp(sum / TotalEpisodes, 0, 100);
        }
    }

    public string SpeedText => SpeedBytesPerSecond switch
    {
        >= 1024 * 1024 => $"{SpeedBytesPerSecond / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{SpeedBytesPerSecond / 1024:0.0} KB/s",
        _ => $"{SpeedBytesPerSecond:0} B/s",
    };
}

/// <summary>批量下载参数</summary>
public sealed class SeriesDownloadOptions
{
    /// <summary>
    /// 同时下载几集。注意总连接数 ≈ EpisodeConcurrency × SegmentConcurrency，
    /// 默认 2×16=32，别调太高，否则容易被 CDN 限速或直接拒绝。
    /// </summary>
    public int EpisodeConcurrency { get; set; } = 2;

    /// <summary>单集内部的分片并发</summary>
    public int SegmentConcurrency { get; set; } = 16;

    public int MaxRetries { get; set; } = 3;
    public int RetryBaseDelayMs { get; set; } = 500;
    public int TimeoutSeconds { get; set; } = 30;
    public bool AutoSkipInvalidSegments { get; set; } = true;

    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 暂存根目录。留空则使用下载目录下的 <c>.m3u8tmp</c> —— 放在下载目录里便于发现与清理，
    /// 不会散落在系统临时目录里。
    /// </summary>
    public string? TempRootDirectory { get; init; }

    /// <summary>ffmpeg 路径。为空时无法转 MP4，会退回保留 TS 并在报告里说明。</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>整部剧下载完成后写出下载报告（Markdown），默认开启</summary>
    public bool WriteReport { get; set; } = true;

    /// <summary>
    /// 是否对每集产物做**全量解码检查**（<c>ffmpeg -f null -</c>，默认开）。
    /// 这是唯一能发现"格式全对但内容坏了"的检查，代价是要把产物整条解一遍
    /// （实测 400 MB 约 35 秒）。批量下大剧想省时间可以关掉，报告里会标明"未执行"。
    /// </summary>
    public bool FullDecodeCheck { get; set; } = true;

    /// <summary>文件名模板。支持 {title} 剧名、{number} 集号、{number:00} 补零集号、{site} 站点名。</summary>
    public string FileNamePattern { get; set; } = "{title}.{number:00}";

    /// <summary>是否给整部剧建一个子目录</summary>
    public bool SeriesSubdirectory { get; set; } = true;

    /// <summary>
    /// 期望清晰度高度（如 1080）。留空 = 取最高清晰度。
    /// 仅当播放列表是主清单时生效。
    /// </summary>
    public int? PreferHeight { get; set; }

    /// <summary>额外请求头，会与剧集解析出的 Referer/Origin 合并（同名以这里为准）</summary>
    public Dictionary<string, string> ExtraHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 续传：上次已经下载完成的集（含产物路径）。
    /// 传进来以后，报告与"整部剧"的总进度会把它们一起算上，
    /// 否则续传一次的报告会缺掉之前下好的那些集。
    /// </summary>
    public List<EpisodeDownloadReport>? PreviousEpisodes { get; set; }
}

/// <summary>
/// 批量下载协调器：把「一整部剧」拆成 N 个单集任务交给 <see cref="HlsDownloader"/>。
///
/// 设计要点：
/// - 复用同一个 <see cref="HlsDownloader"/> 实例：它的 HttpClient 是线程安全的，
///   且密钥缓存是每次 DownloadAsync 内的局部变量，可安全并发；
/// - 用信号量限制「同时下载几集」，避免 集数 × 分片并发 把连接数打爆；
/// - 每集独立临时目录，天然支持断点续传（复用引擎已有的分片落盘逻辑）。
/// </summary>
public sealed class SeriesDownloader : IDisposable
{
    private readonly SiteResolver _resolver;
    private readonly SiteContext _siteContext;
    private readonly HlsDownloader _hls;
    private readonly bool _ownsContext;

    /// <summary>
    /// 站点批量下载协调器。
    /// 站点解析与分片下载**都不走代理**（源站基本在国内），
    /// 代理只用于「获取 FFmpeg」。
    /// </summary>
    public SeriesDownloader(SiteResolver? resolver = null, SiteContext? siteContext = null)
    {
        _resolver = resolver ?? SiteResolver.CreateDefault();
        _siteContext = siteContext ?? new SiteContext();
        _ownsContext = siteContext is null;
        _hls = new HlsDownloader(null, 30);
    }

    public SiteResolver Resolver => _resolver;
    public SiteContext SiteContext => _siteContext;

    /// <summary>识别站点并解析剧集列表（UI 的「解析」按钮调这个）</summary>
    public Task<SiteSeries> ParseAsync(string url, CancellationToken ct = default) =>
        _resolver.ParseAsync(url, _siteContext, ct);

    /// <summary>批量下载已勾选的剧集</summary>
    public async Task<SeriesDownloadReport> DownloadAsync(
        SiteSeries series,
        SeriesDownloadOptions options,
        IProgress<SeriesDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var episodes = series.SelectedEpisodes.ToList();
        var report = new SeriesDownloadReport { Title = series.Title, SiteName = series.SiteName };
        if (episodes.Count == 0)
        {
            report.Log.Add("没有勾选任何剧集");
            return report;
        }

        // 站点请求头 + 用户额外请求头
        var headers = new Dictionary<string, string>(series.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in options.ExtraHeaders) headers[kv.Key] = kv.Value;

        var seriesDir = ResolveSeriesDirectory(series, options);
        Directory.CreateDirectory(seriesDir);
        report.OutputDirectory = seriesDir;
        report.PageUrl = series.PageUrl;
        report.SourceName = series.Sources.FirstOrDefault(s => s.Episodes.Any(e => e.IsSelected))?.ToString();
        report.Log.Add($"输出目录：{seriesDir}");

        // 续传：把上次已经下好的集先放进报告与进度里，
        // 这样报告不会缺集，总进度也是「整部剧」的口径。
        var previous = options.PreviousEpisodes ?? new List<EpisodeDownloadReport>();
        foreach (var prev in previous)
        {
            if (prev.Status != EpisodeDownloadStatus.Completed) continue;
            report.Episodes.Add(prev);
        }
        var previousCompleted = report.Episodes.Count;

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var state = new SeriesDownloadProgress
        {
            Title = series.Title,
            TotalEpisodes = episodes.Count + previousCompleted,
            FinishedEpisodes = previousCompleted,
            SucceededEpisodes = previousCompleted,
        };
        var gate = new SemaphoreSlim(Math.Max(1, options.EpisodeConcurrency));
        var sync = new object();

        // 正在下载的集：集号 → (已下载字节, 最近一次有效速度, 该速度的时间戳)。
        // 用来把"已下载量 / 总速度"汇总到整部剧的进度上（多集并发时速度要相加）。
        // 速度为什么要单独记时间：引擎每 0.5 秒才算一次速度，其余上报都是 0，
        // 直接覆盖会让界面上的速度不停闪成 0；超过 2 秒没新样本才当作 0。
        var inFlight = new Dictionary<int, (long Bytes, double Speed, long SpeedTicks)>();

        // 先把所有要下载的集建成"待下载"快照，界面一进来就能看到分集清单
        foreach (var ep in episodes.OrderBy(e => e.Number))
            state.Episodes.Add(new EpisodeProgressSnapshot { Number = ep.Number, Title = ep.DisplayTitle });

        // 上次已完成的集也要进快照（100%），否则总进度（分集平均值）会偏高
        foreach (var prev in previous)
        {
            if (prev.Status != EpisodeDownloadStatus.Completed) continue;
            state.Episodes.Add(new EpisodeProgressSnapshot
            {
                Number = prev.Episode.Number,
                Title = prev.Episode.DisplayTitle,
                Status = EpisodeDownloadStatus.Completed,
                Percent = 100,
                OutputBytes = prev.OutputBytes,
                OutputPath = prev.OutputPath,
            });
        }

        if (previousCompleted > 0)
        {
            report.Log.Add($"续传：跳过上次已完成的 {previousCompleted} 集，本次需要下载 {episodes.Count} 集。");
        }

        // 清晰度只需挑一次，全集复用同一个 variant
        string? preferredVariant = null;
        if (options.PreferHeight is not null)
        {
            try
            {
                var probe = episodes.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.PlaylistUrl))
                            ?? episodes[0];
                var probeUrl = await _resolver.ResolvePlaylistUrlAsync(series, probe, _siteContext, ct)
                    .ConfigureAwait(false);
                preferredVariant = await PickVariantAsync(probeUrl, options.PreferHeight.Value, report.Log, ct)
                    .ConfigureAwait(false);
                if (preferredVariant is not null)
                    report.Log.Add($"已选定清晰度：{preferredVariant}");
            }
            catch (Exception ex)
            {
                report.Log.Add($"清晰度探测失败，改用最高清晰度：{ex.Message}");
            }
        }

        var tasks = episodes.Select(episode => RunOneAsync(episode)).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        clock.Stop();

        report.Elapsed = clock.Elapsed;
        report.OutputFormat = report.Episodes
            .Select(e => e.Format)
            .FirstOrDefault(f => !string.IsNullOrWhiteSpace(f)) ?? "ts";
        report.Log.Add($"批量任务结束：成功 {report.SucceededCount}，失败 {report.FailedCount}，取消 {report.CanceledCount}");

        // 整部剧结束后写出下载报告
        if (options.WriteReport)
        {
            var reportPath = TryWriteReportMarkdown(report, seriesDir);
            if (reportPath is not null)
            {
                report.ReportPath = reportPath;
                report.Log.Add($"下载报告已写出：{reportPath}");
            }
        }

        return report;

        async Task RunOneAsync(SiteEpisode episode)
        {
            var item = new EpisodeDownloadReport { Episode = episode };
            lock (sync) report.Episodes.Add(item);

            // 注意：这一段**不能**并进下面那个 try —— 那个 try 的 finally 会 Release 名额，
            // 而这里根本没拿到名额，Release 会把信号量计数搞坏（后续集会挤进多余的名额）。
            try
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 用户暂停/取消时，还在排队等「同时下载几集」名额的这一集会让 WaitAsync 抛异常。
                // 不能让它冒到外面去：那会让整批下载以异常收场，任务被判成「失败」，
                // 连报告也拿不到（界面上就只剩一句莫名的 "The operation was canceled."）。
                // 这里按「还没开始下载」处理 —— 暂存目录里的分片原样保留，可以继续下。
                item.Status = EpisodeDownloadStatus.Canceled;
                SetEpisodeSnapshot(episode, EpisodeDownloadStatus.Canceled, 0, 0, null);
                lock (sync)
                {
                    state.FinishedEpisodes++;
                    report.Log.Add($"[{item.DisplayTitle}] 已停止（尚未开始下载）");
                }
                Publish();
                return;
            }

            try
            {
                if (ct.IsCancellationRequested)
                {
                    item.Status = EpisodeDownloadStatus.Canceled;
                    return;
                }

                // ---- 1. 解析该集 m3u8 直链 ----
                item.Status = EpisodeDownloadStatus.Resolving;
                Publish();
                var playlistUrl = await _resolver
                    .ResolvePlaylistUrlAsync(series, episode, _siteContext, ct)
                    .ConfigureAwait(false);
                item.PlaylistUrl = playlistUrl;

                // ---- 2. 解析媒体清单（主清单自动挑清晰度）----
                var (media, parseLogs) =
                    await _hls.ResolveMediaPlaylistAsync(playlistUrl, preferredVariant, ct).ConfigureAwait(false);
                item.TotalSegments = media.Segments.Count;

                // ---- 3. 逐集下载 ----
                item.Status = EpisodeDownloadStatus.Downloading;
                Publish();

                var fileName = BuildFileName(options.FileNamePattern, series, episode);

                // 暂存目录：放在**下载目录**里（便于发现与清理），名字带随机串，
                // 避免同一集并发/重试时几个暂存目录撞在一起。
                var stagingDir = EpisodePipeline.CreateStagingDirectory(seriesDir, episode.Number);
                item.StagingDirectory = stagingDir;

                var inner = new Progress<DownloadProgress>(p =>
                {
                    item.Percent = p.Percent;
                    item.CompletedSegments = p.CompletedSegments;
                    item.FailedSegments = p.FailedSegments;
                    item.SkippedSegments = p.SkippedSegments;
                    item.SpeedBytesPerSecond = p.SpeedBytesPerSecond;

                    // 记下这一集的实时字节/速度，供整部剧的汇总进度使用
                    lock (sync)
                    {
                        var prev = inFlight.TryGetValue(episode.Number, out var old) ? old : default;
                        var hasSpeed = p.SpeedBytesPerSecond > 0;
                        inFlight[episode.Number] = (
                            p.DownloadedBytes,
                            hasSpeed ? p.SpeedBytesPerSecond : prev.Speed,
                            hasSpeed ? Environment.TickCount64 : prev.SpeedTicks);
                    }

                    SetEpisodeSnapshot(episode, EpisodeDownloadStatus.Downloading,
                        Math.Clamp(p.Percent, 0, 100), p.DownloadedBytes, null,
                        segments: (p.CompletedSegments, p.TotalSegments));
                    Publish(currentEpisode: episode, currentPercent: p.Percent);
                });

                // 一集的完整流程（下载 → 转 MP4 → 产物体检 → 清理暂存）在 Core 的
                // EpisodePipeline 里，与「单文件模式」共用同一份实现；
                // 这里只负责集间并发、进度聚合与报告簿记。
                var outcome = await new EpisodePipeline(_hls, headers, new EpisodePipelineOptions
                {
                    SegmentConcurrency = options.SegmentConcurrency,
                    MaxRetries = options.MaxRetries,
                    RetryBaseDelayMs = options.RetryBaseDelayMs,
                    TimeoutSeconds = options.TimeoutSeconds,
                    AutoSkipInvalidSegments = options.AutoSkipInvalidSegments,
                    FfmpegPath = options.FfmpegPath,
                    FullDecodeCheck = options.FullDecodeCheck,
                }, line =>
                {
                    // 多集并发，日志列表要串行写入
                    lock (sync) report.Log.Add(line);
                })
                .RunAsync(new EpisodeJob
                {
                    Title = episode.DisplayTitle,
                    PlaylistUrl = playlistUrl,
                    PreferredVariantUri = preferredVariant,
                    OutputDirectory = seriesDir,
                    FileName = fileName,
                    StagingDirectory = stagingDir,
                }, inner, stage =>
                {
                    // 阶段变化同步到界面（解析中 → 下载中）；转封装阶段不再改状态
                    if (stage == EpisodeStage.Resolving) item.Status = EpisodeDownloadStatus.Resolving;
                    else if (stage == EpisodeStage.Downloading) item.Status = EpisodeDownloadStatus.Downloading;
                    else return;

                    Publish();
                }, ct).ConfigureAwait(false);

                // 下载耗时由流水线在「下载结束那一刻」定格（转 MP4 与产物校验都算在后面，
                // 否则报告里的"耗时"会把这两步也算进去，大文件能差出几十秒）
                item.Elapsed = outcome.Elapsed;
                item.DurationSeconds = outcome.DurationSeconds;
                item.TotalSegments = outcome.TotalSegments;
                item.CompletedSegments = outcome.CompletedSegments;
                item.FailedSegments = outcome.FailedSegments;
                item.SkippedSegments = outcome.SkippedSegments;
                item.SkippedAdSegments = outcome.SkippedSegments;
                item.MergeVerdict = outcome.MergeVerdict;
                item.StagingDirectory = outcome.StagingDirectory;
                item.Percent = 100;

                if (outcome.Success)
                {
                    item.OutputPath = outcome.OutputPath;
                    item.OutputBytes = outcome.OutputBytes;
                    item.Format = outcome.Format;
                    item.DurationVerdict = outcome.DurationVerdict;
                    item.Container = outcome.Container;
                    item.DecodeCheck = outcome.DecodeCheck;

                    item.Status = EpisodeDownloadStatus.Completed;
                    SetEpisodeSnapshot(episode, EpisodeDownloadStatus.Completed, 100, outcome.OutputBytes, null,
                        outcome.OutputPath, segments: (item.CompletedSegments, item.TotalSegments));
                    lock (sync) state.SucceededEpisodes++;
                }
                else
                {
                    // 取消也是"返回值"：流水线不抛异常，所以两条出路都在这里收口
                    item.Status = outcome.Status == EpisodeOutcomeStatus.Canceled
                        ? EpisodeDownloadStatus.Canceled
                        : EpisodeDownloadStatus.Failed;

                    if (item.Status == EpisodeDownloadStatus.Failed)
                    {
                        item.Error = outcome.Error ?? "下载未完成。";
                        lock (sync) state.FailedEpisodes++;
                    }

                    SetEpisodeSnapshot(episode, item.Status, item.Percent, 0, item.Error,
                        segments: (item.CompletedSegments, item.TotalSegments));
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = EpisodeDownloadStatus.Canceled;
                SetEpisodeSnapshot(episode, EpisodeDownloadStatus.Canceled, item.Percent, 0, "已取消");
            }
            catch (Exception ex)
            {
                item.Status = EpisodeDownloadStatus.Failed;
                item.Error = ex.Message;
                SetEpisodeSnapshot(episode, EpisodeDownloadStatus.Failed, item.Percent, 0, ex.Message);
                lock (sync) { state.FailedEpisodes++; report.Log.Add($"[{item.DisplayTitle}] 异常：{ex.Message}"); }
            }
            finally
            {
                lock (sync)
                {
                    inFlight.Remove(episode.Number);

                    if (item.Status is EpisodeDownloadStatus.Completed
                        or EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled)
                    {
                        state.FinishedEpisodes++;
                    }
                }
                Publish();
                gate.Release();
            }
        }

        void SetEpisodeSnapshot(SiteEpisode ep, EpisodeDownloadStatus status,
            double percent, long bytes, string? error, string? outputPath = null,
            (int Completed, int Total)? segments = null)
        {
            lock (sync)
            {
                var snapshot = state.Episodes.FirstOrDefault(s => s.Number == ep.Number);
                if (snapshot is null) return;

                snapshot.Status = status;
                snapshot.Percent = percent;
                if (bytes > 0) snapshot.OutputBytes = bytes;
                if (!string.IsNullOrWhiteSpace(error)) snapshot.Error = error!;
                if (!string.IsNullOrWhiteSpace(outputPath)) snapshot.OutputPath = outputPath;

                if (segments is { } s)
                {
                    snapshot.CompletedSegments = s.Completed;
                    snapshot.TotalSegments = s.Total;
                }
            }
        }

        void Publish(SiteEpisode? currentEpisode = null, double currentPercent = -1)
        {
            if (progress is null) return;
            lock (sync)
            {
                if (currentEpisode is not null)
                {
                    state.CurrentEpisodeTitle = currentEpisode.DisplayTitle;
                    state.CurrentEpisodePercent = currentPercent;

                    // 同步这一集的快照，供界面展开查看每集进度
                    var snapshot = state.Episodes.FirstOrDefault(s => s.Number == currentEpisode.Number);
                    if (snapshot is not null)
                    {
                        snapshot.Percent = currentPercent;
                        if (snapshot.Status is EpisodeDownloadStatus.Pending or EpisodeDownloadStatus.Downloading)
                            snapshot.Status = EpisodeDownloadStatus.Downloading;
                    }
                }

                // 汇总：已完成集的产物字节 + 进行中集的实时字节；速度是所有进行中集之和
                long liveBytes = 0;
                double liveSpeed = 0;
                var now = Environment.TickCount64;
                foreach (var v in inFlight.Values)
                {
                    liveBytes += v.Bytes;
                    if (v.Speed > 0 && now - v.SpeedTicks <= 2000) liveSpeed += v.Speed;
                }

                var finishedBytes = report.Episodes.Sum(e => e.OutputBytes);
                state.DownloadedBytes = finishedBytes + liveBytes;
                state.TotalBytes = finishedBytes;
                state.SpeedBytesPerSecond = inFlight.Count > 0 ? liveSpeed : 0;
                state.CompletedEpisodes = report.Episodes.Count(e =>
                    e.Status is EpisodeDownloadStatus.Completed
                        or EpisodeDownloadStatus.Failed
                        or EpisodeDownloadStatus.Canceled);

                // 正在下载的那一集的分片进度：界面上靠它显示"183/185 片"，
                // 一集分片多的时候（成百上千片）百分比会长时间卡在同一个数上，
                // 只有分片数在动 —— 用户得看得见才算"有进度"。
                if (currentEpisode is not null)
                {
                    var current = report.Episodes.FirstOrDefault(e => e.Episode.Number == currentEpisode.Number);
                    state.CompletedSegments = current?.CompletedSegments ?? 0;
                    state.TotalSegments = current?.TotalSegments ?? 0;
                }

                progress.Report(state);
            }
        }
    }

    /// <summary>
    /// 复制一份剧集数据，仅勾选指定集号（用于「重试失败的分集」「续传」）。
    /// SiteSeries 的集合是只读的，因此这里重建一份；返回 null 表示没有可选的集。
    ///
    /// <paramref name="onlySourceId"/> 很关键：**必须**按 (源, 集号) 一起选。
    /// 多源站点里同一个集号会出现在多个源上，只按集号选会一次选中好几个同号集，
    /// 引擎就会把同一集下好几遍（实测：21 集 × 2 源时"计划 2 集（12,12）"）。
    /// </summary>
    public static SiteSeries? SelectOnly(SiteSeries source, IReadOnlySet<int> episodeNumbers,
        IReadOnlySet<int>? onlySourceIds = null)
    {
        var clone = new SiteSeries
        {
            Kind = source.Kind,
            SiteName = source.SiteName,
            PageUrl = source.PageUrl,
            SeriesId = source.SeriesId,
            Title = source.Title,
            Category = source.Category,
            CoverUrl = source.CoverUrl,
        };
        foreach (var kv in source.Headers) clone.Headers[kv.Key] = kv.Value;

        var anySelected = false;
        foreach (var s in source.Sources)
        {
            var copy = new SitePlaySource { Id = s.Id, Name = s.Name };
            var sourceMatched = onlySourceIds is null || onlySourceIds.Contains(s.Id);

            foreach (var e in s.Episodes)
            {
                var selected = sourceMatched && episodeNumbers.Contains(e.Number);
                if (selected) anySelected = true;

                copy.Episodes.Add(new SiteEpisode
                {
                    Number = e.Number,
                    SourceId = e.SourceId,
                    PageUrl = e.PageUrl,
                    Title = e.Title,
                    PlaylistUrl = e.PlaylistUrl,
                    IsSelected = selected,
                });
            }
            clone.Sources.Add(copy);
        }

        if (!anySelected) return null;
        clone.PreferredSourceId = source.PreferredSourceId;


        return clone;
    }

    /// <summary>在 0..N 集之间做区间选择，供 UI 的「选集」用（如 "1-8,12"）</summary>
    public static void ApplySelection(SiteSeries series, string spec)
    {
        var all = series.AllEpisodes.ToList();
        foreach (var e in all) e.IsSelected = false;

        foreach (var token in spec.Split(new[] { ',', '，', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.Trim();
            var dash = t.IndexOf('-');
            if (dash > 0 &&
                int.TryParse(t[..dash], out var from) &&
                int.TryParse(t[(dash + 1)..], out var to))
            {
                if (from > to) (from, to) = (to, from);
                foreach (var e in all.Where(e => e.Number >= from && e.Number <= to)) e.IsSelected = true;
            }
            else if (int.TryParse(t, out var only))
            {
                foreach (var e in all.Where(e => e.Number == only)) e.IsSelected = true;
            }
        }
    }

    /// <summary>只在某个播放源内勾选（多源时避免同集重复下载）</summary>
    public static void SelectSource(SiteSeries series, int sourceId)
    {
        foreach (var e in series.AllEpisodes) e.IsSelected = e.SourceId == sourceId;
    }

    private async Task<string?> PickVariantAsync(string playlistUrl, int preferHeight, List<string> log, CancellationToken ct)
    {
        var text = await _hls.FetchPlaylistTextAsync(playlistUrl, ct).ConfigureAwait(false);
        var parsed = M3U8Parser.Parse(text, playlistUrl);
        if (!parsed.IsMaster) return null;

        var withHeight = parsed.Variants
            .Select(v => (Variant: v, Height: ParseHeight(v.Resolution)))
            .Where(x => x.Height > 0)
            .ToList();
        if (withHeight.Count == 0) return null;

        // 优先取 <= 期望高度的最高档；都不满足则取最低档，避免把小水管拉爆
        var candidate = withHeight.Where(x => x.Height <= preferHeight)
                                  .OrderByDescending(x => x.Height)
                                  .FirstOrDefault();
        if (candidate.Variant is null)
            candidate = withHeight.OrderBy(x => x.Height).First();

        log.Add($"清晰度候选 {withHeight.Count} 档，目标 {preferHeight}p，选中 {candidate.Height}p");
        return candidate.Variant.Uri;
    }

    private static int ParseHeight(string? resolution)
    {
        if (string.IsNullOrWhiteSpace(resolution)) return 0;
        var m = Regex.Match(resolution, @"(\d+)\s*[xX*]\s*(\d+)");
        if (m.Success && int.TryParse(m.Groups[2].Value, out var h)) return h;
        return int.TryParse(resolution, out var only) ? only : 0;
    }

    /// <summary>
    /// 解析整部剧的实际输出目录：按网站下载时自动套一层「剧名」目录，
    /// 下载好的视频都放进这个目录里。
    ///
    /// 两个细节：
    /// - OutputDirectory 为空时回落到系统的「下载」文件夹；
    /// - 用户选的目录本身就叫这个名字时不再重复套一层（避免出现 <c>交锋\交锋\</c>）。
    /// </summary>
    public static string ResolveSeriesDirectory(SiteSeries series, SeriesDownloadOptions options)
    {
        var root = string.IsNullOrWhiteSpace(options.OutputDirectory)
            ? KnownFolders.Downloads
            : options.OutputDirectory;

        // 归一化掉尾部分隔符（对 "D:\" 这类根路径会自动保持原样）
        root = Path.TrimEndingDirectorySeparator(root);

        if (!options.SeriesSubdirectory) return root;

        var folderName = Sanitize(series.Title);
        var leaf = Path.GetFileName(root);
        if (string.Equals(leaf, folderName, StringComparison.OrdinalIgnoreCase)) return root;

        return Path.Combine(root, folderName);
    }

    /// <summary>
    /// 写出整部剧的下载报告（Markdown），文件名形如 <c>交锋-下载报告.md</c>。
    /// 报告与视频放在同一个目录里，便于对照查看。
    /// </summary>
    public static string? TryWriteReportMarkdown(SeriesDownloadReport report, string seriesDirectory)
    {
        try
        {
            Directory.CreateDirectory(seriesDirectory);
            var path = Path.Combine(seriesDirectory,
                $"{Sanitize(report.Title)}-下载报告.md");

            File.WriteAllText(path, BuildReportMarkdown(report), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 分集明细里"时长"列的显示值。
    /// 优先用**产物实际时长**（ffprobe 探到的，最可信），其次清单声明，
    /// 最后才回落到下载报告里的值。**绝不能拿 report.Elapsed（整批总耗时）冒充单集时长** ——
    /// 之前那版报告就把续传时的总耗时 00:47:09 写成了第 11 集的时长。
    /// </summary>
    private static string FormatDurationText(EpisodeDownloadReport episode, TimeSpan fallback)
    {
        var seconds = episode.Container?.DurationSeconds ?? 0;
        if (seconds <= 0) seconds = episode.DurationSeconds;
        if (seconds <= 0) seconds = fallback.TotalSeconds;

        return seconds > 0 ? TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss") : "-";
    }

    /// <summary>把 ffprobe 的 "25/1" 这类分数帧率变成 "25fps"</summary>
    private static string FormatFrameRate(string rFrameRate)
    {
        var parts = rFrameRate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], out var num)
            && double.TryParse(parts[1], out var den)
            && den > 0)
        {
            var fps = num / den;
            return fps == Math.Floor(fps) ? $"{fps:0}fps" : $"{fps:0.###}fps";
        }

        return rFrameRate;
    }

    /// <summary>生成报告正文</summary>
    public static string BuildReportMarkdown(SeriesDownloadReport report)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"# {report.Title} · 下载报告");
        sb.AppendLine();
        sb.AppendLine($"- **站点**：{report.SiteName}");
        if (!string.IsNullOrWhiteSpace(report.PageUrl))
            sb.AppendLine($"- **页面**：{report.PageUrl}");
        if (!string.IsNullOrWhiteSpace(report.SourceName))
            sb.AppendLine($"- **播放源**：{report.SourceName}");
        if (!string.IsNullOrWhiteSpace(report.OutputDirectory))
            sb.AppendLine($"- **输出目录**：`{report.OutputDirectory}`");
        sb.AppendLine($"- **开始时间**：{report.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- **总耗时**：{report.Elapsed:hh\\:mm\\:ss}");
        sb.AppendLine($"- **产物格式**：{report.OutputFormat?.ToUpperInvariant() ?? "-"}");
        sb.AppendLine();

        sb.AppendLine("## 汇总");
        sb.AppendLine();
        sb.AppendLine("| 项目 | 数值 |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| 成功 | {report.SucceededCount} 集 |");
        sb.AppendLine($"| 失败 | {report.FailedCount} 集 |");
        sb.AppendLine($"| 取消 | {report.CanceledCount} 集 |");
        sb.AppendLine($"| 总大小 | {report.TotalBytes / 1024.0 / 1024.0:0.0} MB |");
        sb.AppendLine($"| 总时长 | {TimeSpan.FromSeconds(report.TotalDurationSeconds):hh\\:mm\\:ss} |");
        sb.AppendLine($"| 自动跳过的广告分片 | {report.TotalSkippedAds} 片 |");
        sb.AppendLine();

        // ---------------- 产物校验 ----------------
        var completed = report.Episodes
            .Where(e => e.Status == EpisodeDownloadStatus.Completed)
            .ToList();

        if (completed.Count > 0)
        {
            var probed = completed.Where(e => e.Container is not null).ToList();
            var decoded = completed.Where(e => e.DecodeCheck is not null).ToList();
            var durationChecked = completed.Where(e => e.DurationVerdict is not null).ToList();
            var durationFailed = durationChecked
                .Where(e => e.DurationVerdict!.StartsWith("不通过", StringComparison.Ordinal)).ToList();
            var decodeFailed = decoded.Where(e => e.DecodeCheck!.Passed == false).ToList();

            sb.AppendLine("## 产物校验");
            sb.AppendLine();
            sb.AppendLine("| 校验项 | 覆盖 | 结果 |");
            sb.AppendLine("|---|---|---|");
            sb.AppendLine($"| 分片落盘与合并字节核对 | {completed.Count} 集 | " +
                          $"{(completed.All(e => e.MergeVerdict?.StartsWith("合并核对通过") == true) ? "✔ 全部通过（不多不少）" : "见运行日志")} |");
            sb.AppendLine($"| 时长核对（清单声明 vs 产物实际） | {durationChecked.Count}/{completed.Count} 集 | " +
                          $"{(durationChecked.Count == 0 ? "未执行" : durationFailed.Count == 0 ? "✔ 全部通过" : $"✘ {durationFailed.Count} 集不通过")} |");
            sb.AppendLine($"| 容器探测（格式/编码/分辨率/帧率） | {probed.Count}/{completed.Count} 集 | " +
                          $"{(probed.Count == 0 ? "未执行（未配置 ffprobe）" : "✔ 已记录，见明细")} |");
            sb.AppendLine($"| 全量解码检查（ffmpeg -f null -） | {decoded.Count}/{completed.Count} 集 | " +
                          $"{(decoded.Count == 0 ? "未执行（未配置 ffmpeg）" :
                              decodeFailed.Count == 0 ? "✔ 全部通过" : $"⚠ {decodeFailed.Count} 集有解码告警")} |");
            sb.AppendLine();

            if (decodeFailed.Count > 0)
            {
                sb.AppendLine("> ⚠ **解码告警**：下面这些集的容器与时长都正常，但整条流解码时 ffmpeg 报了错。");
                sb.AppendLine("> 通常是**源站返回的数据有问题**（坏包被原样拼进了产物），不是拼接错位；");
                sb.AppendLine("> 播放时可能表现为花屏、卡顿或音画不同步。");
                sb.AppendLine();
                foreach (var e in decodeFailed)
                {
                    sb.AppendLine($"- **第 {e.Episode.Number:00} 集**（`{Path.GetFileName(e.OutputPath)}`）：" +
                                  $"{string.Join("；", e.DecodeCheck!.Issues)}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("## 分集明细");
        sb.AppendLine();
        sb.AppendLine("| 集 | 状态 | 时长 | 大小 | 分片(成功/跳过/失败) | 编码 | 分辨率 / 帧率 | 音频 | 时长核对 | 解码检查 | 产物 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var e in report.Episodes.OrderBy(x => x.Episode.Number))
        {
            var status = e.Status switch
            {
                EpisodeDownloadStatus.Completed => "✔ 完成",
                EpisodeDownloadStatus.Failed => "✘ 失败",
                EpisodeDownloadStatus.Canceled => "⊘ 取消",
                _ => e.Status.ToString(),
            };
            var file = string.IsNullOrWhiteSpace(e.OutputPath)
                ? "-"
                : $"`{Path.GetFileName(e.OutputPath)}`";

            var video = e.Container?.Streams.FirstOrDefault(s => s.CodecType == "video");
            var audio = e.Container?.Streams.FirstOrDefault(s => s.CodecType == "audio");

            var codec = video is null ? "-" : video.CodecName.ToUpperInvariant();
            var geometry = video is null
                ? "-"
                : $"{video.Width}x{video.Height}" +
                  (video.FrameRate.Length > 2 && video.FrameRate != "0/0"
                      ? $" / {FormatFrameRate(video.FrameRate)}"
                      : "");
            var audioText = audio is null
                ? "-"
                : $"{audio.CodecName.ToUpperInvariant()} {audio.Channels}ch";

            var durationCell = e.DurationVerdict is null
                ? "-"
                : e.DurationVerdict.StartsWith("通过", StringComparison.Ordinal) ? "✔ 通过"
                : e.DurationVerdict.StartsWith("不通过", StringComparison.Ordinal) ? "✘ 不通过"
                : "? 未完成";

            var decodeCell = e.DecodeCheck is null
                ? "-"
                : e.DecodeCheck.Passed ? "✔ 通过" : "⚠ 有告警";

            sb.AppendLine($"| {e.Episode.Number} | {status} | {FormatDurationText(e, e.Elapsed)} | {e.SizeText} | " +
                          $"{e.CompletedSegments}/{e.SkippedSegments}/{e.FailedSegments} | {codec} | {geometry} | " +
                          $"{audioText} | {durationCell} | {decodeCell} | {file} |");
        }
        sb.AppendLine();

        var failures = report.Episodes
            .Where(e => e.Status is EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled)
            .ToList();

        if (failures.Count > 0)
        {
            sb.AppendLine("## 失败与取消");
            sb.AppendLine();
            foreach (var e in failures)
            {
                sb.AppendLine($"- **第{e.Episode.Number:00}集**：{e.Error ?? "已取消"}");
                if (!string.IsNullOrWhiteSpace(e.StagingDirectory))
                    sb.AppendLine($"  - 暂存目录已保留：`{e.StagingDirectory}`");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## 运行日志");
        sb.AppendLine();
        sb.AppendLine("```text");
        foreach (var line in report.Log) sb.AppendLine(line);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"由 {AppInfo.ProductName} {AppInfo.Version} 生成  ·  {AppInfo.ProjectUrl}");

        return sb.ToString();
    }

    private static string BuildFileName(string pattern, SiteSeries series, SiteEpisode episode)
    {
        var name = pattern
            .Replace("{title}", series.Title)
            .Replace("{site}", series.SiteName)
            .Replace("{number:00}", episode.Number.ToString("00"))
            .Replace("{number:000}", episode.Number.ToString("000"))
            .Replace("{number}", episode.Number.ToString());
        return Sanitize(name);
    }

    internal static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var cleaned = new string(chars).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(cleaned) ? "video" : cleaned;
    }

    public void Dispose()
    {
        _hls.Dispose();
        if (_ownsContext) _siteContext.Dispose();
    }
}
