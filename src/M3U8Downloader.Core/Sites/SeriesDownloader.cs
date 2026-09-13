using System.Text;
using System.Text.RegularExpressions;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Settings;
using M3U8Downloader.Core.Staging;

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

/// <summary>批量下载的聚合进度（供 UI 绑定一条总进度条）</summary>
public sealed class SeriesDownloadProgress
{
    public required string Title { get; init; }
    public int TotalEpisodes { get; init; }
    public int FinishedEpisodes { get; set; }
    public int SucceededEpisodes { get; set; }
    public int FailedEpisodes { get; set; }

    /// <summary>当前正在下载的集</summary>
    public string? CurrentEpisodeTitle { get; set; }
    public double CurrentEpisodePercent { get; set; }

    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }

    /// <summary>
    /// 整部剧的总体进度（0-100）。
    /// 注意必须截断到 100：当最后一集已完成、同时它的单集进度也是 100% 时，
    /// 直接算会得到 (1 + 1) / 1 = 200%。
    /// </summary>
    public double OverallPercent => TotalEpisodes == 0
        ? 0
        : Math.Min(100.0, (FinishedEpisodes + CurrentEpisodePercent / 100.0) * 100.0 / TotalEpisodes);

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

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var state = new SeriesDownloadProgress { Title = series.Title, TotalEpisodes = episodes.Count };
        var gate = new SemaphoreSlim(Math.Max(1, options.EpisodeConcurrency));
        var sync = new object();

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

            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (ct.IsCancellationRequested)
                {
                    item.Status = EpisodeDownloadStatus.Canceled;
                    return;
                }

                var one = System.Diagnostics.Stopwatch.StartNew();

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
                var stagingDir = CreateStagingDirectory(seriesDir, episode.Number);
                item.StagingDirectory = stagingDir;

                // 先合并成暂存目录里的中间文件；最终产物转成 MP4 后再删暂存目录
                var intermediate = Path.Combine(stagingDir,
                    media.IsFmp4 ? "merged.mp4" : "merged.ts");

                var downloadOptions = new DownloadOptions
                {
                    Concurrency = Math.Clamp(options.SegmentConcurrency, 1, 64),
                    MaxRetries = Math.Clamp(options.MaxRetries, 0, 10),
                    RetryBaseDelayMs = options.RetryBaseDelayMs,
                    TimeoutSeconds = options.TimeoutSeconds,
                    AutoSkipInvalidSegments = options.AutoSkipInvalidSegments,
                    Headers = headers,
                    TempDirectory = stagingDir,
                    OutputPath = intermediate,
                    // 暂存目录的生死由本方法统一管理（要先转 MP4 再删）
                    DeleteTempOnSuccess = false,
                };

                var inner = new Progress<DownloadProgress>(p =>
                {
                    item.Percent = p.Percent;
                    item.CompletedSegments = p.CompletedSegments;
                    item.FailedSegments = p.FailedSegments;
                    item.SkippedSegments = p.SkippedSegments;
                    item.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
                    Publish(currentEpisode: episode, currentPercent: p.Percent,
                        currentSpeed: p.SpeedBytesPerSecond);
                });

                var result = await _hls.DownloadAsync(media, downloadOptions, inner, ct).ConfigureAwait(false);
                one.Stop();

                item.Elapsed = one.Elapsed;
                item.DurationSeconds = media.TotalDuration;
                item.TotalSegments = result.TotalSegments > 0 ? result.TotalSegments : item.TotalSegments;
                item.CompletedSegments = result.CompletedSegments;
                item.FailedSegments = result.FailedSegments;
                item.SkippedSegments = result.SkippedSegments;
                item.SkippedAdSegments = result.SkippedSegments;
                item.Percent = 100;

                if (result.Success)
                {
                    // ---- 4. 转成常用格式（MP4）----
                    var final = await FinalizeOutputAsync(
                        seriesDir, fileName, intermediate, media.IsFmp4, options,
                        report.Log, item.DisplayTitle, ct).ConfigureAwait(false);

                    item.OutputPath = final.Path;
                    item.OutputBytes = final.Bytes;
                    item.Format = final.Format;

                    // ---- 5. 全部成功 → 删除暂存目录（含中间文件）----
                    if (StagingStore.TryRemoveStagingDirectory(stagingDir))
                    {
                        item.StagingDirectory = null;
                        lock (sync) report.Log.Add($"[{item.DisplayTitle}] 已清理暂存目录。");
                    }
                    else
                    {
                        lock (sync) report.Log.Add(
                            $"[{item.DisplayTitle}] 暂存目录未能删除（可能被占用）：{stagingDir}");
                    }

                    item.Status = EpisodeDownloadStatus.Completed;
                    lock (sync)
                    {
                        state.SucceededEpisodes++;
                        report.Log.Add($"[{item.DisplayTitle}] 完成 → {final.Path}");
                        foreach (var m in result.Messages) report.Log.Add($"[{item.DisplayTitle}] {m}");
                    }
                }
                else
                {
                    item.Status = EpisodeDownloadStatus.Failed;
                    // 引擎偶尔会在没给出 Error 的情况下判失败，这时把分片统计打出来 ——
                    // 只显示"未知错误"对排查毫无帮助。
                    item.Error = result.Error
                        ?? $"分片统计 成功 {result.CompletedSegments} / 跳过 {result.SkippedSegments} / " +
                           $"失败 {result.FailedSegments} / 共 {result.TotalSegments}，输出 {result.OutputBytes} 字节";
                    lock (sync)
                    {
                        state.FailedEpisodes++;
                        report.Log.Add($"[{item.DisplayTitle}] 失败：{item.Error}" +
                                       $"（暂存目录已保留：{stagingDir}）");
                        foreach (var m in result.Messages) report.Log.Add($"[{item.DisplayTitle}] {m}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = EpisodeDownloadStatus.Canceled;
            }
            catch (Exception ex)
            {
                item.Status = EpisodeDownloadStatus.Failed;
                item.Error = ex.Message;
                lock (sync) { state.FailedEpisodes++; report.Log.Add($"[{item.DisplayTitle}] 异常：{ex.Message}"); }
            }
            finally
            {
                if (item.Status is EpisodeDownloadStatus.Completed
                    or EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled)
                {
                    lock (sync) state.FinishedEpisodes++;
                }
                Publish();
                gate.Release();
            }
        }

        void Publish(SiteEpisode? currentEpisode = null, double currentPercent = -1, double currentSpeed = -1)
        {
            if (progress is null) return;
            lock (sync)
            {
                if (currentEpisode is not null)
                {
                    state.CurrentEpisodeTitle = currentEpisode.DisplayTitle;
                    state.CurrentEpisodePercent = currentPercent;
                }
                if (currentSpeed >= 0) state.SpeedBytesPerSecond = currentSpeed;
                state.TotalBytes = report.Episodes.Sum(e => e.OutputBytes);
                progress.Report(state);
            }
        }
    }

    /// <summary>
    /// 在下载目录里为本集创建一个暂存目录，名字形如
    /// <c>.m3u8tmp-007-a1b2c3d4</c>。
    ///
    /// - 放在下载目录里（而不是系统临时目录）：便于用户发现与清理，出问题时也容易找到；
    /// - 结尾的随机串保证同一集并发/重试时不会互相踩。
    /// </summary>
    public static string CreateStagingDirectory(string seriesDirectory, int episodeNumber)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(seriesDirectory, $".m3u8tmp-{episodeNumber:000}-{suffix}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>把合并好的中间文件转成常用格式（MP4）。ffmpeg 不可用时退回保留 TS。</summary>
    private static async Task<(string Path, long Bytes, string Format)> FinalizeOutputAsync(
        string seriesDir, string fileName, string intermediate, bool alreadyFmp4,
        SeriesDownloadOptions options, List<string> log, string episodeTitle, CancellationToken ct)
    {
        var ffmpegPath = options.FfmpegPath;
        var finalPath = Path.Combine(seriesDir, fileName + ".mp4");

        if (!string.IsNullOrWhiteSpace(ffmpegPath) && File.Exists(ffmpegPath))
        {
            var remux = await FfmpegRunner.RemuxToMp4Async(ffmpegPath!, intermediate, finalPath, ct)
                .ConfigureAwait(false);

            if (remux.Success && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
            {
                log.Add($"[{episodeTitle}] 已转封装为 MP4（流复制，无画质损失）。");
                return (finalPath, new FileInfo(finalPath).Length, "mp4");
            }

            log.Add($"[{episodeTitle}] 转 MP4 失败，改为保留原始格式：{remux.Error}");
            try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
        }
        else
        {
            log.Add($"[{episodeTitle}] 未配置 FFmpeg，跳过转 MP4；如需 MP4 请在设置里指定或下载 FFmpeg。");
        }

        // 退路：把中间文件搬到输出目录并保留原格式
        var fallbackExt = alreadyFmp4 ? ".mp4" : ".ts";
        var fallbackPath = Path.Combine(seriesDir, fileName + fallbackExt);
        try
        {
            if (File.Exists(fallbackPath)) File.Delete(fallbackPath);
            File.Move(intermediate, fallbackPath);
        }
        catch (Exception ex)
        {
            log.Add($"[{episodeTitle}] 移动产物失败：{ex.Message}");
            return (intermediate, new FileInfo(intermediate).Length, alreadyFmp4 ? "mp4" : "ts");
        }

        return (fallbackPath, new FileInfo(fallbackPath).Length, alreadyFmp4 ? "mp4" : "ts");
    }

    /// <summary>
    /// 复制一份剧集数据，仅勾选指定集号（用于「重试失败的分集」）。
    /// SiteSeries 的集合是只读的，因此这里重建一份；返回 null 表示没有可选的集。
    /// </summary>
    public static SiteSeries? SelectOnly(SiteSeries source, IReadOnlySet<int> episodeNumbers)
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
            foreach (var e in s.Episodes)
            {
                var selected = episodeNumbers.Contains(e.Number);
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

        sb.AppendLine("## 分集明细");
        sb.AppendLine();
        sb.AppendLine("| 集 | 状态 | 时长 | 大小 | 分片(成功/跳过/失败) | 产物 |");
        sb.AppendLine("|---|---|---|---|---|---|");
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
            sb.AppendLine($"| {e.Episode.Number} | {status} | {e.DurationText} | {e.SizeText} | " +
                          $"{e.CompletedSegments}/{e.SkippedSegments}/{e.FailedSegments} | {file} |");
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
