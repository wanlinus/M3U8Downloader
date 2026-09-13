using System.Text.RegularExpressions;

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

    public string DisplayTitle => Episode.DisplayTitle;

    public override string ToString() => Status switch
    {
        EpisodeDownloadStatus.Completed => $"{DisplayTitle}  ✔ {OutputBytes / 1024.0 / 1024.0:0.0} MB",
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

    public int SucceededCount => Episodes.Count(e => e.Status == EpisodeDownloadStatus.Completed);
    public int FailedCount => Episodes.Count(e => e.Status == EpisodeDownloadStatus.Failed);
    public int CanceledCount => Episodes.Count(e => e.Status == EpisodeDownloadStatus.Canceled);
    public long TotalBytes => Episodes.Sum(e => e.OutputBytes);
    public bool Success => FailedCount == 0 && CanceledCount == 0 && Episodes.Count > 0;

    public override string ToString() =>
        $"{Title}：成功 {SucceededCount} / 失败 {FailedCount} / 取消 {CanceledCount}，共 {TotalBytes / 1024.0 / 1024.0:0.0} MB";
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

    public double OverallPercent => TotalEpisodes == 0 ? 0
        : (FinishedEpisodes + CurrentEpisodePercent / 100.0) * 100.0 / TotalEpisodes;

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
    public required string TempRootDirectory { get; init; }

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
        report.Log.Add($"批量任务结束：成功 {report.SucceededCount}，失败 {report.FailedCount}，取消 {report.CanceledCount}");
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
                var outputPath = Path.Combine(seriesDir, fileName + ".ts");
                var tempDir = Path.Combine(options.TempRootDirectory,
                    $"{series.SeriesId}_{episode.SourceId}_{episode.Number:0000}");

                var downloadOptions = new DownloadOptions
                {
                    Concurrency = Math.Clamp(options.SegmentConcurrency, 1, 64),
                    MaxRetries = Math.Clamp(options.MaxRetries, 0, 10),
                    RetryBaseDelayMs = options.RetryBaseDelayMs,
                    TimeoutSeconds = options.TimeoutSeconds,
                    AutoSkipInvalidSegments = options.AutoSkipInvalidSegments,
                    Headers = headers,
                    TempDirectory = tempDir,
                    OutputPath = outputPath,
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
                item.OutputPath = result.OutputPath;
                item.OutputBytes = result.OutputBytes;
                item.CompletedSegments = result.CompletedSegments;
                item.FailedSegments = result.FailedSegments;
                item.SkippedSegments = result.SkippedSegments;
                item.Percent = 100;

                if (result.Success)
                {
                    item.Status = EpisodeDownloadStatus.Completed;
                    lock (sync) { state.SucceededEpisodes++; report.Log.Add($"[{item.DisplayTitle}] 完成 {playlistUrl}"); }
                    if (parseLogs.Count > 0) lock (sync) report.Log.AddRange(
                        parseLogs.Select(l => $"[{item.DisplayTitle}] {l}"));
                }
                else
                {
                    item.Status = EpisodeDownloadStatus.Failed;
                    item.Error = result.Error ?? "未知错误";
                    lock (sync) { state.FailedEpisodes++; report.Log.Add($"[{item.DisplayTitle}] 失败：{item.Error}"); }
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
