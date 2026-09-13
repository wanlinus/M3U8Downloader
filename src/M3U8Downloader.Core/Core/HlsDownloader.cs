using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using M3U8Downloader.Core.Net;
using M3U8Downloader.Core.Staging;

namespace M3U8Downloader.Core;

/// <summary>
/// HLS 下载引擎。
///
/// 设计要点（针对实际踩过的坑）：
/// 1. 下载前静态识别并剔除插播广告分片，避免任务整体失败；
/// 2. 密钥可回退：某分片自带 key 取不到时，尝试沿用同一播放列表中的可用密钥；
/// 3. 下载后校验 TS 同步字节，解不出来就换密钥再试，仍失败才计为失败；
/// 4. 断点续传：临时目录中的已完成分片自动跳过；
/// 5. 合并后做 188 字节包对齐校验，确保产物可播放。
///
/// **刻意不走代理**：视频源站绝大多数在国内，绕代理只会更慢；而且一旦
/// 「清单走代理、分片走直连」，出口 IP 不一致还可能触发部分站点的防盗链。
/// 代理只用于「获取 FFmpeg」这一件需要翻墙的事。
/// </summary>
public sealed class HlsDownloader : IDisposable
{
    private readonly HttpClient _http;

    /// <summary>代理客户端；没配代理时为 null</summary>
    private readonly HttpClient? _httpProxy;

    private readonly string? _proxyUrl;

    /// <summary>
    /// 已确认「直连不通、必须走代理」的主机。
    ///
    /// 记这一笔是关键：不记的话每个分片都要先白等一次连接超时才回退，
    /// 墙外站点（欧乐影院就是）一千个分片能白等几个小时。
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _proxyRequiredHosts = new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>「已改用代理」这类消息写到哪里（可选）</summary>
    public Action<string>? Log { get; set; }

    public HlsDownloader(Dictionary<string, string>? headers = null, int timeoutSeconds = 30, string? proxyUrl = null)
    {
        var directHandler = CreateHandler();
        _http = new HttpClient(directHandler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        ApplyHeaders(_http, headers);

        // 代理地址：显式传入 → 设置里填的。**默认不启用**，
        // 只有在某台主机直连不通时才会被用到（见 HttpGetAsync）
        _proxyUrl = ProxyHelper.Normalize(proxyUrl ?? TryReadProxyFromSettings());
        if (_proxyUrl is not null)
        {
            var proxyHandler = CreateHandler();
            proxyHandler.Proxy = new WebProxy(new Uri(_proxyUrl)) { BypassProxyOnLocal = true };
            proxyHandler.UseProxy = true;

            _httpProxy = new HttpClient(proxyHandler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
            ApplyHeaders(_httpProxy, headers);
        }
    }

    private static SocketsHttpHandler CreateHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        MaxConnectionsPerServer = 64,
        // 连不上就早点回退到代理，别让用户干等
        ConnectTimeout = TimeSpan.FromSeconds(8),
    };

    private static string? TryReadProxyFromSettings()
    {
        try { return Settings.AppSettingsStore.Load().ProxyUrl; }
        catch { return null; }
    }

    private static void ApplyHeaders(HttpClient client, Dictionary<string, string>? headers)
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        if (headers is null) return;

        foreach (var (k, v) in headers)
        {
            if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;
            if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
            {
                client.DefaultRequestHeaders.UserAgent.Clear();
                client.DefaultRequestHeaders.UserAgent.ParseAdd(v);
                continue;
            }
            client.DefaultRequestHeaders.Remove(k);
            try { client.DefaultRequestHeaders.TryAddWithoutValidation(k, v); } catch { }
        }
    }

    // ==================== 请求入口：直连 → 失败自动走代理 ====================

    /// <summary>
    /// 统一的 GET。直连优先；某台主机直连不通就记住它，之后同一主机直接走代理。
    /// 与站点解析同一套策略：通畅时零代理开销，不通时自动兜底。
    /// </summary>
    private async Task<HttpResponseMessage> HttpGetAsync(string url, HttpCompletionOption mode, CancellationToken ct)
    {
        var host = TryGetHost(url);
        var canFallback = host is not null && _httpProxy is not null;

        if (canFallback && _proxyRequiredHosts.ContainsKey(host!))
            return await _httpProxy!.GetAsync(url, mode, ct).ConfigureAwait(false);

        try
        {
            var resp = await _http.GetAsync(url, mode, ct).ConfigureAwait(false);

            // 连上了但被「按 IP 拒绝」：也换代理再试一次
            if (canFallback && ShouldRetryViaProxy(resp.StatusCode))
            {
                resp.Dispose();
                var proxied = await _httpProxy!.GetAsync(url, mode, ct).ConfigureAwait(false);
                MarkProxyRequired(host!);
                return proxied;
            }

            return resp;
        }
        catch (Exception ex) when (canFallback && IsConnectivityFailure(ex, ct))
        {
            // 直连不通、代理能通 → 记下这台主机；代理也不通就会抛出去（说明不是被墙）
            var resp = await _httpProxy!.GetAsync(url, mode, ct).ConfigureAwait(false);
            MarkProxyRequired(host!);
            return resp;
        }
    }

    /// <summary>
    /// 同上，但请求要现造（分片带 Range 头，而 HttpRequestMessage 不能重复发送，
    /// 所以传的是"怎么造这个请求"而不是请求本身）。
    /// </summary>
    private async Task<HttpResponseMessage> HttpSendAsync(
        Func<HttpRequestMessage> createRequest, string url, HttpCompletionOption mode, CancellationToken ct)
    {
        var host = TryGetHost(url);
        var canFallback = host is not null && _httpProxy is not null;

        if (canFallback && _proxyRequiredHosts.ContainsKey(host!))
        {
            using var proxied = createRequest();
            return await _httpProxy!.SendAsync(proxied, mode, ct).ConfigureAwait(false);
        }

        try
        {
            HttpResponseMessage resp;
            using (var direct = createRequest())
                resp = await _http.SendAsync(direct, mode, ct).ConfigureAwait(false);

            if (canFallback && ShouldRetryViaProxy(resp.StatusCode))
            {
                resp.Dispose();
                using var proxied = createRequest();
                var retried = await _httpProxy!.SendAsync(proxied, mode, ct).ConfigureAwait(false);
                MarkProxyRequired(host!);
                return retried;
            }

            return resp;
        }
        catch (Exception ex) when (canFallback && IsConnectivityFailure(ex, ct))
        {
            using var proxied = createRequest();
            var resp = await _httpProxy!.SendAsync(proxied, mode, ct).ConfigureAwait(false);
            MarkProxyRequired(host!);
            return resp;
        }
    }

    /// <summary>
    /// 这个状态码值不值得换代理再试。
    ///
    /// 除「连接根本不通」之外，还要管 403/451：墙外 CDN 常用「直接按 IP 拒绝」这一招 ——
    /// 实测欧乐影院的 europe.olemovienews.com 就是这样：国内直连 403、代理 200，
    /// 而且我们连 TLS 都握得上，所以只看「连接失败」会漏掉它。
    /// 404/410 是资源真没了，换代理没意义。
    /// </summary>
    private static bool ShouldRetryViaProxy(HttpStatusCode status) =>
        status is HttpStatusCode.Forbidden
            or HttpStatusCode.UnavailableForLegalReasons
            or HttpStatusCode.TooManyRequests;

    private void MarkProxyRequired(string host)
    {
        if (_proxyRequiredHosts.TryAdd(host, true))
            Log?.Invoke($"{host} 直连不通，已改用代理下载（{_proxyUrl}）。");
    }

    private static string? TryGetHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;

    /// <summary>
    /// 是不是「网络根本不通」（而不是站点返回了 4xx/5xx）。
    /// 只有这一类才值得换代理重试 —— 站点自己报错时换代理纯属白费。
    /// </summary>
    private static bool IsConnectivityFailure(Exception ex, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return false;   // 用户取消，不是网络问题

        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException or SocketException or TaskCanceledException) return true;
        }

        return false;
    }

    /// <summary>
    /// 抓取播放列表文本。
    /// 返回内容与「重定向后的最终地址」——后者必须用作解析相对路径的基准，
    /// 否则经 CDN 跳转的清单会把分片地址解析到错误的域上。
    /// </summary>
    public async Task<(string Text, string FinalUrl)> FetchPlaylistAsync(string url, CancellationToken ct = default)
    {
        using var resp = await HttpGetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // 假 200 嗅探：不少站点用 200 + HTML 错误页代替 404
        if (LooksLikeHtml(bytes))
            throw new InvalidDataException(
                $"该地址返回的是 HTML 页面而不是 m3u8 清单（通常意味着资源已失效或需要 Referer/防盗链校验）。");

        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? url;
        return (DecodeText(bytes), finalUrl);
    }

    /// <summary>兼容旧签名的简单版本</summary>
    public async Task<string> FetchPlaylistTextAsync(string url, CancellationToken ct = default)
        => (await FetchPlaylistAsync(url, ct).ConfigureAwait(false)).Text;

    /// <summary>
    /// 判断响应体是否为 HTML 页面（而非预期的清单/分片）。
    ///
    /// ⚠ 只在**该是明文**的场合用它。加密分片的响应是**密文**，
    /// 而密文是均匀随机的 —— 首字节约有 1/16 的概率正好是 <c>0x3C</c>（'&lt;'），
    /// 用"首字节是 &lt; 就算 HTML"去判，会稳定地把这批正常分片判成错误页。
    /// 实测某源站第 11 集 1400 片里恰好有 87 片（≈1/16）栽在这里，
    /// 表现为"每 16 片失败 1 片"的诡异规律。
    /// </summary>
    internal static bool LooksLikeHtml(ReadOnlySpan<byte> data)
    {
        var head = data.Length > 512 ? data[..512] : data;
        // 跳过前导空白与 BOM
        int i = 0;
        while (i < head.Length && (head[i] == 0x20 || head[i] == 0x09 || head[i] == 0x0A || head[i] == 0x0D)) i++;
        if (i + 3 < head.Length && head[i] == 0xEF && head[i + 1] == 0xBB && head[i + 2] == 0xBF) i += 3;
        if (i >= head.Length) return false;

        var text = System.Text.Encoding.ASCII.GetString(head[i..]).TrimStart();
        if (text.Length == 0) return false;

        // 只认真正像文档开头的形式；单个 '<' 不作数（密文里太常见）
        if (text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("<!DOCTYPE", StringComparison.Ordinal)) return true;
        if (text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("<head", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.StartsWith("<body", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Contains("<html", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }

    /// <summary>解析地址；若是主清单则自动挑选最高清晰度，返回媒体清单</summary>
    public async Task<(HlsMediaPlaylist Media, List<string> Log)> ResolveMediaPlaylistAsync(
        string url, string? preferredVariantUri = null, CancellationToken ct = default)
    {
        var log = new List<string>();
        var (text, finalUrl) = await FetchPlaylistAsync(url, ct).ConfigureAwait(false);

        // 关键：以「重定向后的最终地址」为基准解析相对路径，
        // 否则 CDN 跳转后会把分片解析到错误的域名/路径上。
        if (!string.Equals(finalUrl, url, StringComparison.OrdinalIgnoreCase))
            log.Add($"地址发生重定向，已以最终地址为基准解析：{finalUrl}");

        var parsed = M3U8Parser.Parse(text, finalUrl);

        if (parsed.IsMaster)
        {
            log.Add($"检测到主清单（清晰度列表），共 {parsed.Variants.Count} 个变体。");

            var chosen = preferredVariantUri != null
                ? parsed.Variants.FirstOrDefault(v => v.Uri == preferredVariantUri)
                : null;

            chosen ??= parsed.Variants
                .OrderByDescending(v => ParseHeight(v.Resolution))
                .ThenByDescending(v => v.Bandwidth)
                .First();

            log.Add($"已选择清晰度：{chosen.DisplayName}");

            var (mediaText, mediaFinalUrl) = await FetchPlaylistAsync(chosen.Uri, ct).ConfigureAwait(false);
            var mediaParsed = M3U8Parser.Parse(mediaText, mediaFinalUrl);
            if (mediaParsed.Media == null)
                throw new InvalidDataException("选中的变体不是有效的媒体清单。");

            return (mediaParsed.Media, log);
        }

        if (parsed.Media == null)
            throw new InvalidDataException("无法从该地址解析出媒体清单。");

        log.Add($"已解析媒体清单：{parsed.Media.Segments.Count} 个分片，总时长 {FormatDuration(parsed.Media.TotalDuration)}。");
        return (parsed.Media, log);
    }

    /// <summary>执行下载</summary>
    public async Task<DownloadResult> DownloadAsync(
        HlsMediaPlaylist playlist,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var result = new DownloadResult();
        var sw = Stopwatch.StartNew();

        Directory.CreateDirectory(options.TempDirectory);
        var outDir = Path.GetDirectoryName(options.OutputPath);
        if (!string.IsNullOrEmpty(outDir)) Directory.CreateDirectory(outDir);

        var segments = playlist.Segments;

        // ---------- 1. 广告/无效分片识别 ----------
        var inspectorReport = SegmentInspector.Inspect(playlist);
        foreach (var r in inspectorReport.Reasons) result.Messages.Add(r);

        if (options.AutoSkipInvalidSegments && inspectorReport.Suspects.Count > 0)
        {
            await SegmentInspector.VerifyKeyAvailabilityAsync(playlist,
                (keyUrl, token) => HttpGetAsync(keyUrl, HttpCompletionOption.ResponseHeadersRead, token),
                inspectorReport, ct);
        }

        var skipSet = new HashSet<int>();
        if (options.AutoSkipInvalidSegments)
        {
            foreach (var s in inspectorReport.Suspects)
            {
                s.Validity = s.Validity == SegmentValidity.Undecryptable
                    ? SegmentValidity.Undecryptable
                    : SegmentValidity.Skipped;
                skipSet.Add(s.Index);
            }
            if (skipSet.Count > 0)
            {
                var ratio = (double)skipSet.Count / segments.Count;
                result.Messages.Add($"已自动跳过 {skipSet.Count} 个疑似插播广告/无效分片（占 {ratio:P0}）。");

                // 跳过比例过高通常意味着识别有误（或整条流都不对），必须让用户看见
                if (ratio > 0.3)
                {
                    result.Messages.Add(
                        $"⚠ 跳过比例偏高（{skipSet.Count}/{segments.Count}），产物可能不完整，请核对原播放列表。");
                }
            }
        }
        else
        {
            foreach (var s in inspectorReport.Suspects) s.Validity = SegmentValidity.Ok;
            result.Messages.Add($"检测到 {inspectorReport.Suspects.Count} 个疑似广告分片，但未启用自动跳过。");
        }

        // ---------- 2. 准备解密密钥：按 key URI 缓存，取不到的记入失败集合 ----------
        var keyCache = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var unavailableKeys = new HashSet<string>(StringComparer.Ordinal);
        var fallbackKey = await ResolveFallbackKeyAsync(segments, keyCache, unavailableKeys, ct);
        if (fallbackKey != null)
            result.Messages.Add("已取得备用解密密钥，可用于分片自带密钥缺失的情况。");
        if (unavailableKeys.Count > 0)
            result.Messages.Add($"有 {unavailableKeys.Count} 个密钥地址无法获取，引用它们的分片将被跳过。");

        // ---------- 2b. 暂存清单：防止复用"上一次不同播放列表"留下的旧分片 ----------
        // 源站可能每次请求都重新生成列表（例如随机插入广告），于是"分片序号 ↔ 内容"
        // 的对应关系会变；直接续传就会拼出错位的文件，而且大小看着还挺正常。
        var manifest = new StagingManifest
        {
            PlaylistUrl = playlist.SourceUrl,
            OutputPath = options.OutputPath,
            SegmentCount = segments.Count,
            TotalDuration = playlist.TotalDuration,
            SkippedSegments = skipSet.OrderBy(i => i).ToList(),
            Fingerprint = StagingStore.Fingerprint(segments.Select(s => s.Uri)),
        };
        var manifestNote = PrepareStaging(options.TempDirectory, manifest);
        if (manifestNote is not null) result.Messages.Add(manifestNote);

        // ---------- 3. 断点续传：扫描已存在的分片 ----------
        var states = new SegmentState[segments.Count];
        int alreadyDone = 0;
        long alreadyBytes = 0;
        for (int i = 0; i < segments.Count; i++)
        {
            states[i] = new SegmentState { Index = i };
            var path = SegmentPath(options.TempDirectory, i);
            if (File.Exists(path))
            {
                var len = new FileInfo(path).Length;
                if (len > 0)
                {
                    states[i].Completed = true;
                    states[i].Bytes = len;
                    alreadyDone++;
                    alreadyBytes += len;
                }
            }
        }
        if (alreadyDone > 0)
            result.Messages.Add($"发现 {alreadyDone} 个已下载分片，将跳过（断点续传）。");

        // ---------- 4. 并发下载 ----------
        var totalBytes = Interlocked.Read(ref alreadyBytes);
        var completed = alreadyDone;
        var failed = 0;
        var progressLock = new object();
        var lastSpeedTicks = Stopwatch.StartNew();
        var lastSpeedBytes = totalBytes;

        void Report(string? message = null)
        {
            DownloadProgress snapshot;
            lock (progressLock)
            {
                double speed = 0;
                var elapsed = lastSpeedTicks.Elapsed.TotalSeconds;
                if (elapsed >= 0.5)
                {
                    speed = (totalBytes - lastSpeedBytes) / elapsed;
                    lastSpeedTicks.Restart();
                    lastSpeedBytes = totalBytes;
                }
                var remaining = segments.Count - (completed + failed + skipSet.Count);
                TimeSpan? eta = null;
                if (speed > 1024 && remaining > 0)
                {
                    var avgSegBytes = completed > 0 ? (double)totalBytes / completed : 300_000;
                    eta = TimeSpan.FromSeconds(remaining * avgSegBytes / speed);
                }
                snapshot = new DownloadProgress
                {
                    TotalSegments = segments.Count,
                    CompletedSegments = completed,
                    FailedSegments = failed,
                    SkippedSegments = skipSet.Count,
                    DownloadedBytes = totalBytes,
                    SpeedBytesPerSecond = speed,
                    Elapsed = sw.Elapsed,
                    Eta = eta,
                    CurrentMessage = message,
                };
            }
            progress?.Report(snapshot);
        }

        Report("开始下载…");

        var pending = Enumerable.Range(0, segments.Count)
            .Where(i => !states[i].Completed && !skipSet.Contains(i))
            .ToList();

        // 失败原因统计：分片失败时把"到底为什么"记下来（只记原因与条数，不记内容）
        var failureReasons = new Dictionary<string, int>(StringComparer.Ordinal);
        var failureSamples = new List<string>();
        var failureLock = new object();

        void NoteFailure(int index, string reason, string uri)
        {
            lock (failureLock)
            {
                failureReasons[reason] = failureReasons.TryGetValue(reason, out var n) ? n + 1 : 1;
                if (failureSamples.Count < 5)
                    failureSamples.Add($"#{index} {Path.GetFileName(new Uri(uri).AbsolutePath)} → {reason}");
            }
        }

        try
        {
            await Parallel.ForEachAsync(
                pending,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Max(1, options.Concurrency),
                    CancellationToken = ct,
                },
                async (index, token) =>
                {
                    try
                    {
                        var seg = segments[index];
                        var data = await DownloadSegmentWithRetryAsync(seg, options, fallbackKey, keyCache, token,
                                reason => NoteFailure(index, reason, seg.Uri))
                            .ConfigureAwait(false);

                        if (data is { Length: > 0 })
                        {
                            // 分片级校验：解不出合法 TS 就换另一个密钥再试一次
                            if (!SegmentInspector.LooksLikeValidTs(data))
                            {
                                var retried = TryWithAlternateKey(seg, data, fallbackKey, keyCache);
                                if (retried != null && SegmentInspector.LooksLikeValidTs(retried))
                                    data = retried;
                            }

                            var path = SegmentPath(options.TempDirectory, index);
                            await File.WriteAllBytesAsync(path, data, token).ConfigureAwait(false);

                            lock (progressLock)
                            {
                                states[index].Completed = true;
                                states[index].Bytes = data.Length;
                                completed++;
                                totalBytes += data.Length;
                            }
                        }
                        else
                        {
                            lock (progressLock) failed++;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 取消不计入失败
                    }
                    catch
                    {
                        lock (progressLock) failed++;
                    }
                    finally
                    {
                        Report();
                    }
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        // ---------- 5. 合并 ----------
        var finalCompleted = states.Count(s => s.Completed);
        var finalSkipped = skipSet.Count;
        var finalFailed = segments.Count - finalCompleted - finalSkipped;

        result.TotalSegments = segments.Count;
        result.CompletedSegments = finalCompleted;
        result.SkippedSegments = finalSkipped;
        result.FailedSegments = finalFailed;
        result.SkippedSegmentIndices.AddRange(skipSet.OrderBy(i => i));

        // 把失败原因写进结果：只报"有 N 个分片失败"是没法排查的
        lock (failureLock)
        {
            result.FailureReasons.AddRange(
                failureReasons.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}（{kv.Value} 片）"));
            result.FailureSamples.AddRange(failureSamples);
        }

        if (ct.IsCancellationRequested)
        {
            result.Success = false;
            result.Error = "下载已取消（已下载的分片保留，可继续）。";
            result.Elapsed = sw.Elapsed;
            Report(result.Error);
            return result;
        }

        // ---------- 4c. 落盘核对：应该存在的分片是否真在磁盘上、大小是否对得上 ----------
        // 合并这一步是"有什么就拼什么"，所以一旦某个分片文件缺失，产物会**静默缺一段**
        // 而 FailedSegments 仍然是 0 —— 这种"看起来成功、其实少内容"最危险。
        var missingOnDisk = new List<int>();
        var sizeMismatch = new List<int>();

        for (var i = 0; i < segments.Count; i++)
        {
            if (skipSet.Contains(i) || !states[i].Completed) continue;

            var path = SegmentPath(options.TempDirectory, i);
            if (!File.Exists(path)) { missingOnDisk.Add(i); continue; }

            var onDisk = new FileInfo(path).Length;
            if (states[i].Bytes > 0 && onDisk != states[i].Bytes) sizeMismatch.Add(i);
        }

        if (missingOnDisk.Count > 0 || sizeMismatch.Count > 0)
        {
            var detail = sizeMismatch.Count > 0
                ? $"，另有 {sizeMismatch.Count} 个分片大小与记录不符"
                : "";
            result.Messages.Add(
                $"⚠ 落盘核对异常：{missingOnDisk.Count} 个已下载分片在合并前不见了{detail}；这些分片不会被拼进产物。");
        }

        Report("正在合并分片…");
        await MergeAsync(options, segments, skipSet, ct).ConfigureAwait(false);

        var outInfo = new FileInfo(options.OutputPath);
        result.OutputPath = options.OutputPath;
        result.OutputBytes = outInfo.Exists ? outInfo.Length : 0;
        result.Elapsed = sw.Elapsed;

        // 合并必须"一个分片都不少"：把要拼进去的分片字节数加起来与实际产物比对。
        // 合并用的是纯字节拼接（不重封装），所以字节数不等就说明确实漏了或多了。
        var expectBytes = 0L;
        for (var i = 0; i < segments.Count; i++)
        {
            if (skipSet.Contains(i) || !states[i].Completed) continue;
            expectBytes += new FileInfo(SegmentPath(options.TempDirectory, i)).Length;
        }

        var bytesMatch = outInfo.Exists && outInfo.Length == expectBytes;
        result.Messages.Add(bytesMatch
            ? $"合并核对通过：{expectBytes / 1024.0 / 1024.0:0.0} MB 与预期一致（分片不多不少）。"
            : $"⚠ 合并结果与预期不符：产物 {outInfo.Length / 1024.0 / 1024.0:0.0} MB，" +
              $"按分片计算应为 {expectBytes / 1024.0 / 1024.0:0.0} MB。");

        // 合并结果校验：TS 产物做**全量**逐包对齐检查
        var alignmentOk = true;
        if (outInfo.Exists && outInfo.Length > 0)
        {
            if (playlist.IsFmp4)
            {
                result.Messages.Add("fMP4 产物：跳过 MPEG-TS 包对齐校验（由「合并字节数核对」覆盖）。");
            }
            else
            {
                alignmentOk = await VerifyTsAlignmentAsync(options.OutputPath, ct).ConfigureAwait(false);
                result.Messages.Add(alignmentOk
                    ? "合并产物校验通过（全量逐包检查，188 字节对齐完好）。"
                    : "合并产物未通过包对齐校验：可能拼接错位，暂存目录已保留以便排查。");
            }
        }

        result.Success = result.FailedSegments == 0 && result.OutputBytes > 0 && alignmentOk && bytesMatch;
        if (!result.Success && result.FailedSegments > 0)
        {
            var detail = result.FailureReasons.Count > 0
                ? "：" + string.Join("；", result.FailureReasons)
                : "";
            result.Error = $"有 {result.FailedSegments} 个分片下载失败{detail}";
        }
        else if (result.OutputBytes == 0)
            result.Error = "输出文件为空。";
        else if (!alignmentOk)
            result.Error = "产物未通过 TS 包对齐校验（可能拼接错位）。";

        // 只有「下载无失败 + 产物校验通过」才清理暂存目录；
        // 中间任何一步出错都保留，便于排查或下次续传。
        if (result.Success)
            CleanupStaging(options.TempDirectory, options, result);

        Report(result.Success ? "下载完成" : result.Error);
        return result;
    }

    // ==================== 分片下载 ====================

    private async Task<byte[]?> DownloadSegmentWithRetryAsync(
        HlsSegment segment, DownloadOptions options, byte[]? fallbackKey,
        Dictionary<string, byte[]> keyCache, CancellationToken ct,
        Action<string>? onFailure = null)
    {
        // 末尾一次失败的原因要留给调用方统计：分片失败最怕"只知道失败、不知道为什么"
        var lastReason = "(未尝试)";

        for (int attempt = 0; attempt <= options.MaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested) return null;
            if (attempt > 0)
            {
                var delay = options.RetryBaseDelayMs * (int)Math.Pow(2, attempt - 1);
                try { await Task.Delay(delay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }

            try
            {
                var (raw, failReason) = await FetchBytesAsync(segment, ct).ConfigureAwait(false);
                if (raw == null || raw.Length == 0)
                {
                    lastReason = failReason ?? "返回空内容";
                    continue;
                }

                var decoded = TryDecrypt(segment, raw, fallbackKey, keyCache);
                if (decoded == null) lastReason = "解密后不是合法的 TS（密钥/填充策略都不匹配）";
                return decoded ?? raw;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception ex)
            {
                lastReason = $"异常 {ex.GetType().Name}：{ex.Message}";
            }
        }

        onFailure?.Invoke(lastReason);
        return null;
    }

    /// <summary>
    /// 用「另一个」密钥再试一次。
    /// 场景：源站把分片标记为明文(METHOD=NONE)但内容其实是加密的，
    /// 或分片自带密钥取不到而备用密钥可用。
    /// </summary>
    private static byte[]? TryWithAlternateKey(
        HlsSegment segment, byte[] raw, byte[]? fallbackKey, Dictionary<string, byte[]> keyCache)
    {
        if (fallbackKey == null) return null;

        byte[]? ownKey = null;
        if (segment.Key.Uri != null) keyCache.TryGetValue(segment.Key.Uri, out ownKey);

        // 已经有自带密钥时，换备用密钥试
        if (ownKey != null)
            return TryDecryptWithKey(segment, raw, fallbackKey);

        return null;
    }

    private async Task<(byte[]? Data, string? FailReason)> FetchBytesAsync(HlsSegment segment, CancellationToken ct)
    {
        using var resp = await HttpSendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get, segment.Uri);
            if (segment.ByteRangeLength.HasValue && segment.ByteRangeOffset.HasValue)
            {
                request.Headers.Range = new RangeHeaderValue(
                    segment.ByteRangeOffset.Value,
                    segment.ByteRangeOffset.Value + segment.ByteRangeLength.Value - 1);
            }
            return request;
        }, segment.Uri, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return (null, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (data.Length == 0) return (null, "HTTP 200 但响应体为空");

        // 假 200 检查之一：Content-Type 直接声明是 HTML —— 这个最可靠，优先用
        var mediaType = resp.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null
            && (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
                || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
        {
            return (null, $"HTTP 200 但 Content-Type 是 {mediaType}，不是分片数据");
        }

        // 假 200 检查之二：内容看起来是 HTML 文档。
        // **加密分片不做这个检查** —— 密文是均匀随机的，首字节有 1/16 概率是 '<'，
        // 判了就会稳定误杀（实测某流 1400 片里 87 片栽在这上面）。
        if (!segment.Key.IsEncrypted && LooksLikeHtml(data))
            return (null, $"返回的是 HTML（{data.Length} 字节）而不是分片数据");

        var declared = resp.Content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > 0 && data.Length != declared.Value
            && !segment.ByteRangeLength.HasValue)
        {
            return (null, $"响应被截断：声明 {declared.Value} 字节，实际收到 {data.Length} 字节");
        }

        return (data, null);
    }

    // ==================== 解密 ====================

    /// <summary>
    /// 预取播放列表中出现次数最多的、可用的密钥作为备用密钥。
    /// 同时把所有尝试过的 key URI 结果写入缓存 / 不可用集合，避免重复请求。
    /// </summary>
    private async Task<byte[]?> ResolveFallbackKeyAsync(
        List<HlsSegment> segments,
        Dictionary<string, byte[]> keyCache,
        HashSet<string> unavailableKeys,
        CancellationToken ct)
    {
        var keyUris = segments
            .Where(s => s.Key.IsEncrypted && s.Key.Uri != null)
            .GroupBy(s => s.Key.Uri!)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .ToList();

        byte[]? best = null;
        foreach (var uri in keyUris)
        {
            ct.ThrowIfCancellationRequested();
            if (keyCache.ContainsKey(uri) || unavailableKeys.Contains(uri)) continue;

            try
            {
                using var resp = await HttpGetAsync(uri, HttpCompletionOption.ResponseContentRead, ct)
                    .ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    unavailableKeys.Add(uri);
                    continue;
                }
                var key = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                if (key.Length is 16 or 24 or 32)
                {
                    keyCache[uri] = key;
                    best ??= key;
                }
                else
                {
                    unavailableKeys.Add(uri);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { unavailableKeys.Add(uri); }
        }

        return best;
    }

    /// <summary>
    /// 解密尝试。策略：
    /// 1. 明文段（METHOD=NONE）直接返回；
    /// 2. 加密段用「分片自带密钥」解 —— 解密前会按 key URI 拉取并缓存；
    /// 3. 自带密钥不可得时，回退到同播放列表中的备用密钥。
    /// </summary>
    private static byte[]? TryDecrypt(
        HlsSegment segment, byte[] raw, byte[]? fallbackKey, Dictionary<string, byte[]> keyCache)
    {
        if (!segment.Key.IsEncrypted)
        {
            // 声明为明文。若内容本身就是合法 TS，直接用；
            // 否则可能是源站错误声明了 NONE，尝试用备用密钥解一次。
            if (SegmentInspector.LooksLikeValidTs(raw)) return raw;
            if (fallbackKey != null)
            {
                var alt = TryDecryptWithKey(segment, raw, fallbackKey);
                if (alt != null && SegmentInspector.LooksLikeValidTs(alt)) return alt;
            }
            return raw;
        }

        byte[]? ownKey = null;
        if (segment.Key.Uri != null) keyCache.TryGetValue(segment.Key.Uri, out ownKey);
        ownKey ??= segment.Key.KeyData;
        var effectiveKey = ownKey ?? fallbackKey;

        return TryDecryptWithKey(segment, raw, effectiveKey);
    }

    /// <summary>
    /// 用指定密钥解密并校验。
    ///
    /// 填充策略必须按序尝试，这是实际踩过的坑：
    /// HLS 的 AES-128 分片通常带 PKCS7 填充，若错误地用「无填充 + 截断到16字节倍数」
    /// 处理，每片会丢掉 4~15 字节，导致拼接后 188 字节 TS 包整体错位、
    /// 产物看似完整实则无法正常播放。
    /// </summary>
    private static byte[]? TryDecryptWithKey(HlsSegment segment, byte[] raw, byte[]? key)
    {
        if (key == null) return null;

        var iv = GetIv(segment);
        var keySize = GetKeySize(key);
        if (keySize == 0) return null;

        // 策略 1：PKCS7 填充（HLS 最常见）
        var padded = AesCbcDecrypt(raw, key, iv, keySize, PaddingMode.PKCS7);
        if (padded != null)
        {
            var unpadded = TryUnpadPkcs7(padded);
            if (unpadded != null && SegmentInspector.LooksLikeTs(unpadded)) return unpadded;
        }

        // 策略 2：无填充，并截断到 188 的整数倍（保证 TS 包对齐）
        var none = AesCbcDecrypt(raw, key, iv, keySize, PaddingMode.None);
        if (none != null)
        {
            var aligned = none.Length - (none.Length % 188);
            if (aligned > 0)
            {
                var cut = aligned == none.Length ? none : none[..aligned];
                if (SegmentInspector.LooksLikeTs(cut)) return cut;
            }

            // 策略 3：无填充原样（fMP4 等非 TS 载荷走这里）
            if (SegmentInspector.LooksLikeValidTs(none) || none.Length > 0) return none;
        }

        return null;
    }

    /// <summary>按密钥字节长度取得 128/192/256 位对应的 KeySize</summary>
    private static int GetKeySize(byte[] key) => key.Length switch
    {
        16 => 128,
        24 => 192,
        32 => 256,
        _ => 0,
    };

    /// <summary>
    /// AES-CBC 解密。
    /// 按「密钥实际字节长度」而不是 METHOD 字符串决定算法强度 ——
    /// 部分源站把 AES-192/256 错标成 AES-128，照字符串走会解不出来。
    /// </summary>
    private static byte[]? AesCbcDecrypt(byte[] data, byte[] key, byte[] iv, int keySize, PaddingMode padding)
    {
        try
        {
            using var aes = Aes.Create();
            aes.Mode = CipherMode.CBC;
            aes.Padding = padding;
            aes.KeySize = keySize;
            aes.Key = key.Length == keySize / 8 ? key : key;
            aes.IV = iv;

            // 无填充模式下密文长度必须是块大小整数倍
            var usable = padding == PaddingMode.None ? data.Length - (data.Length % 16) : data.Length;
            if (usable <= 0) return padding == PaddingMode.None ? null : Array.Empty<byte>();

            using var dec = aes.CreateDecryptor();
            var outBuf = new byte[usable + 16];
            int written;
            try
            {
                written = dec.TransformBlock(data, 0, usable, outBuf, 0);
                var final = dec.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                if (final.Length > 0)
                {
                    Array.Copy(final, 0, outBuf, written, final.Length);
                    written += final.Length;
                }
            }
            catch (CryptographicException)
            {
                // PKCS7 填充非法 —— 说明该分片其实没有填充
                return null;
            }

            return outBuf[..written];
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>校验并去掉 PKCS7 填充，不是合法填充则返回 null</summary>
    private static byte[]? TryUnpadPkcs7(byte[] data)
    {
        if (data.Length == 0 || data.Length % 16 != 0) return null;
        var pad = data[^1];
        if (pad is 0 or > 16 || pad > data.Length) return null;
        for (int i = data.Length - pad; i < data.Length; i++)
            if (data[i] != pad) return null;
        return data[..^pad];
    }

    /// <summary>
    /// 计算 IV：优先用播放列表显式给出的 IV；
    /// 否则按 HLS 规范用媒体序号（16 字节大端）作为 IV。
    /// </summary>
    private static byte[] GetIv(HlsSegment segment)
    {
        if (segment.Key.IV != null) return segment.Key.IV;
        var iv = new byte[16];
        // 用媒体序号（MEDIA-SEQUENCE + 索引），而不是单纯的列表索引 ——
        // 直播或断点续传场景下 MEDIA-SEQUENCE 非 0，用错会解出乱码
        var seq = (ulong)segment.MediaSequence;
        for (int i = 0; i < 8; i++)
            iv[15 - i] = (byte)(seq >> (8 * i));
        return iv;
    }

    // ==================== 合并与校验 ====================

    private static async Task MergeAsync(
        DownloadOptions options, List<HlsSegment> segments, HashSet<int> skipSet, CancellationToken ct)
    {
        // 先写临时文件，成功后再替换，避免中断破坏已有产物
        var tempOut = options.OutputPath + ".part";
        await using (var fs = new FileStream(tempOut, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 20, useAsync: true))
        {
            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (skipSet.Contains(i)) continue;
                var path = SegmentPath(options.TempDirectory, i);
                if (!File.Exists(path)) continue;

                await using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    1 << 20, useAsync: true);
                await src.CopyToAsync(fs, 1 << 20, ct).ConfigureAwait(false);
            }
        }

        if (File.Exists(options.OutputPath)) File.Delete(options.OutputPath);
        File.Move(tempOut, options.OutputPath);
    }

    /// <summary>
    /// 准备暂存目录并校验清单。
    ///
    /// 返回一句给用户看的说明（无需说明时返回 null）：
    /// - 清单一致 → 允许断点续传；
    /// - 清单不一致（列表被重新生成过）→ 丢弃旧分片重新下载；
    /// - 有分片但没有清单（旧版本残留）→ 同样丢弃，因为无法确认对应关系。
    /// </summary>
    private static string? PrepareStaging(string tempDirectory, StagingManifest current)
    {
        try
        {
            var existing = StagingStore.TryLoad(tempDirectory);

            if (existing is null)
            {
                var stray = StagingStore.ClearSegments(tempDirectory);
                StagingStore.Save(tempDirectory, current);
                return stray > 0
                    ? $"暂存目录里有 {stray} 个无清单的旧分片（无法确认与当前列表是否对应），已清理后重新下载。"
                    : null;
            }

            if (!string.Equals(existing.Fingerprint, current.Fingerprint, StringComparison.OrdinalIgnoreCase))
            {
                var removed = StagingStore.ClearSegments(tempDirectory);
                StagingStore.Save(tempDirectory, current);
                return $"检测到播放列表已重新生成（分片序号与内容的对应关系已变化），" +
                       $"已丢弃 {removed} 个旧分片并重新下载，避免拼出错位的文件。";
            }

            // 指纹一致：保留清单，分片可安全复用
            StagingStore.Save(tempDirectory, current);
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 全部成功后的收尾：删除暂存目录。
    ///
    /// 只有"下载无失败 + 产物校验通过"才会走到这里；
    /// 任何一步出错都**保留**暂存目录，便于排查或下次续传。
    /// </summary>
    private static void CleanupStaging(string tempDirectory, DownloadOptions options, DownloadResult result)
    {
        if (!options.DeleteTempOnSuccess) return;

        if (StagingStore.TryRemoveStagingDirectory(tempDirectory))
            result.Messages.Add("已清理暂存目录（下载与校验均已通过）。");
        else
            result.Messages.Add($"暂存目录未自动清理（目录内有其他文件，或正被占用）：{tempDirectory}");
    }

    /// <summary>
    /// 校验输出文件是否为对齐的 MPEG-TS：**逐包**检查同步字节。
    ///
    /// 早期实现只抽查"头、中、尾各 3 个字节"—— 中间任何位置错位都发现不了，
    /// 那种校验基本没有意义。实测 400MB 全量扫描约 1 秒，代价可以接受。
    /// </summary>
    private static async Task<bool> VerifyTsAlignmentAsync(string path, CancellationToken ct)
    {
        const int packetSize = 188;

        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 20, useAsync: true);

            var size = fs.Length;
            if (size < packetSize) return false;

            // 末尾出现半包 —— 必然发生了错位或截断
            if (size % packetSize != 0) return false;

            var buffer = new byte[packetSize * 4096];
            long offset = 0;

            while (offset < size)
            {
                ct.ThrowIfCancellationRequested();

                var want = (int)Math.Min(buffer.Length, size - offset);
                var read = await fs.ReadAtLeastAsync(buffer.AsMemory(0, want), want,
                    throwOnEndOfStream: false, ct).ConfigureAwait(false);
                if (read <= 0) break;

                for (var i = 0; i + packetSize <= read; i += packetSize)
                {
                    if (buffer[i] != 0x47) return false;
                }

                offset += read;
            }

            return offset == size;
        }
        catch
        {
            return false;
        }
    }

    private static string SegmentPath(string tempDir, int index) =>
        Path.Combine(tempDir, $"seg_{index:D6}.ts");

    private static int ParseHeight(string? resolution)
    {
        if (string.IsNullOrEmpty(resolution)) return 0;
        var parts = resolution.Split('x', 'X');
        return parts.Length == 2 && int.TryParse(parts[1], out var h) ? h : 0;
    }

    private static string FormatDuration(double seconds) =>
        TimeSpan.FromSeconds(seconds).ToString(@"hh\:mm\:ss");

    private static string DecodeText(byte[] bytes)
    {
        // 先按 UTF-8 严格解码，失败则按 GBK 兜底（部分小站用 GBK）
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            try
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                return System.Text.Encoding.GetEncoding("GBK").GetString(bytes);
            }
            catch { return System.Text.Encoding.UTF8.GetString(bytes); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
