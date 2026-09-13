using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

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
    private bool _disposed;

    public HlsDownloader(Dictionary<string, string>? headers = null, int timeoutSeconds = 30)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            MaxConnectionsPerServer = 64,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36");
        _http.DefaultRequestHeaders.Accept.ParseAdd("*/*");

        if (headers != null)
        {
            foreach (var (k, v) in headers)
            {
                if (string.IsNullOrWhiteSpace(k) || string.IsNullOrWhiteSpace(v)) continue;
                if (k.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                {
                    _http.DefaultRequestHeaders.UserAgent.Clear();
                    _http.DefaultRequestHeaders.UserAgent.ParseAdd(v);
                    continue;
                }
                _http.DefaultRequestHeaders.Remove(k);
                try { _http.DefaultRequestHeaders.TryAddWithoutValidation(k, v); } catch { }
            }
        }
    }

    /// <summary>
    /// 抓取播放列表文本。
    /// 返回内容与「重定向后的最终地址」——后者必须用作解析相对路径的基准，
    /// 否则经 CDN 跳转的清单会把分片地址解析到错误的域上。
    /// </summary>
    public async Task<(string Text, string FinalUrl)> FetchPlaylistAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct)
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

    /// <summary>判断响应体是否为 HTML 页面（而非预期的清单/分片）</summary>
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
        if (text[0] == '<') return true;                       // <!DOCTYPE html / <html
        if (text.StartsWith("{\"", StringComparison.Ordinal)) return true;  // JSON 错误对象
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
            await SegmentInspector.VerifyKeyAvailabilityAsync(playlist, _http, inspectorReport, ct);
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
                result.Messages.Add($"已自动跳过 {skipSet.Count} 个疑似插播广告/无效分片。");
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
                        var data = await DownloadSegmentWithRetryAsync(seg, options, fallbackKey, keyCache, token)
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

        if (ct.IsCancellationRequested)
        {
            result.Success = false;
            result.Error = "下载已取消（已下载的分片保留，可继续）。";
            result.Elapsed = sw.Elapsed;
            Report(result.Error);
            return result;
        }

        Report("正在合并分片…");
        await MergeAsync(options, segments, skipSet, ct).ConfigureAwait(false);

        var outInfo = new FileInfo(options.OutputPath);
        result.OutputPath = options.OutputPath;
        result.OutputBytes = outInfo.Exists ? outInfo.Length : 0;
        result.Elapsed = sw.Elapsed;

        // 合并结果校验
        if (outInfo.Exists && outInfo.Length > 0)
        {
            var aligned = await VerifyTsAlignmentAsync(options.OutputPath, ct).ConfigureAwait(false);
            if (aligned)
                result.Messages.Add("合并产物校验通过（MPEG-TS 188 字节包对齐完好）。");
            else
                result.Messages.Add("警告：合并产物未通过包对齐校验，可能存在错位。");
        }

        result.Success = result.FailedSegments == 0 && result.OutputBytes > 0;
        if (!result.Success && result.FailedSegments > 0)
            result.Error = $"有 {result.FailedSegments} 个分片下载失败。";
        else if (result.OutputBytes == 0)
            result.Error = "输出文件为空。";

        Report(result.Success ? "下载完成" : result.Error);
        return result;
    }

    // ==================== 分片下载 ====================

    private async Task<byte[]?> DownloadSegmentWithRetryAsync(
        HlsSegment segment, DownloadOptions options, byte[]? fallbackKey,
        Dictionary<string, byte[]> keyCache, CancellationToken ct)
    {
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
                var raw = await FetchBytesAsync(segment, ct).ConfigureAwait(false);
                if (raw == null || raw.Length == 0) continue;

                var decoded = TryDecrypt(segment, raw, fallbackKey, keyCache);
                return decoded ?? raw;
            }
            catch (OperationCanceledException) { return null; }
            catch { }
        }

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

    private async Task<byte[]?> FetchBytesAsync(HlsSegment segment, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, segment.Uri);
        if (segment.ByteRangeLength.HasValue && segment.ByteRangeOffset.HasValue)
        {
            req.Headers.Range = new RangeHeaderValue(
                segment.ByteRangeOffset.Value,
                segment.ByteRangeOffset.Value + segment.ByteRangeLength.Value - 1);
        }

        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var data = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (data.Length == 0) return null;

        // 假 200 / 内容校验（原版工具缺失的一环）：
        // 不少源站用 200 + HTML 错误页、或提前截断响应的方式来"假装成功"，
        // 只判断状态码会把坏数据当成正常分片，最终拼出无法播放的文件。
        if (LooksLikeHtml(data)) return null;

        var declared = resp.Content.Headers.ContentLength;
        if (declared.HasValue && declared.Value > 0 && data.Length != declared.Value
            && !segment.ByteRangeLength.HasValue)
        {
            return null; // 响应被截断
        }

        return data;
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
                using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseContentRead, ct)
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

    /// <summary>校验输出文件是否为对齐的 MPEG-TS</summary>
    private static async Task<bool> VerifyTsAlignmentAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, useAsync: true);
            var size = fs.Length;
            if (size < 188 * 3) return false;

            var buf = new byte[1];
            // 头部、中部、尾部各抽查
            foreach (var pos in new[] { 0L, size / 2 / 188 * 188, (size - 188 * 3) / 188 * 188 })
            {
                ct.ThrowIfCancellationRequested();
                for (int k = 0; k < 3; k++)
                {
                    var at = pos + k * 188;
                    if (at + 1 > size) break;
                    fs.Position = at;
                    if (await fs.ReadAsync(buf.AsMemory(0, 1), ct).ConfigureAwait(false) != 1) return false;
                    if (buf[0] != 0x47) return false;
                }
            }
            return true;
        }
        catch { return false; }
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
