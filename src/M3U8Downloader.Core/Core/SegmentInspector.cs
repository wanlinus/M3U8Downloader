using System.Security.Cryptography;
using System.Text;

namespace M3U8Downloader.Core;

/// <summary>
/// 分片有效性检查器。
///
/// 这是本工具针对"源站动态插播广告"问题的核心逻辑。
/// 实测案例：某源站每次请求 m3u8 都会把 12 个分片替换成一组广告
/// （指向另一个日期目录、且该目录的 key.key 已 404），导致下载器恒定失败 12 片。
/// 本检查器在下载前就把这些分片识别出来并剔除，避免任务失败。
/// </summary>
public sealed class SegmentInspector {
    /// <summary>判定结果</summary>
    public sealed class Report {
        public int TotalSegments { get; set; }
        /// <summary>主流目录（正常分片所在目录）</summary>
        public string? MainDirectory { get; set; }
        /// <summary>被判定为广告/无效的分片</summary>
        public List<HlsSegment> Suspects { get; } = new();
        /// <summary>判定理由汇总（用于展示）</summary>
        public List<string> Reasons { get; } = new();

        public int SkippedCount => Suspects.Count;
    }

    /// <summary>
    /// 对媒体清单做静态分析（不需要额外网络请求）。
    /// </summary>
    /// <param name="playlist">媒体清单</param>
    /// <param name="foreignDirThreshold">异目录占比低于该值时才认定为广告（0.5 = 半数以下）</param>
    public static Report Inspect(HlsMediaPlaylist playlist, double foreignDirThreshold = 0.5) {
        var report = new Report { TotalSegments = playlist.Segments.Count };
        if (playlist.Segments.Count == 0) return report;

        // ---------- 1. 目录聚类：正常分片必然集中在同一目录 ----------
        var byDir = playlist.Segments
            .GroupBy(s => GetDirectory(s.Uri))
            .Select(g => new { Directory = g.Key, Count = g.Count(), Segments = g.ToList() })
            .OrderByDescending(g => g.Count)
            .ToList();

        var mainDir = byDir[0].Directory;
        var mainCount = byDir[0].Count;
        report.MainDirectory = mainDir;

        foreach (var g in byDir.Skip(1)) {
            // 少数分片落在其他目录 —— 插播广告的典型特征
            if ((double)g.Count / playlist.Segments.Count <= foreignDirThreshold) {
                foreach (var seg in g.Segments) {
                    seg.Validity = SegmentValidity.SuspectForeign;
                    seg.ValidityNote = $"来自其他目录（{ShortDir(g.Directory)}），非主流目录 {ShortDir(mainDir)}";
                }
                report.Reasons.Add(
                    $"发现 {g.Count} 个分片来自异目录 {ShortDir(g.Directory)}（主流目录 {ShortDir(mainDir)} 含 {mainCount} 片），已标记为疑似插播广告。");
            }
        }

        // ---------- 2. 重复分片：广告常是同一组小片段反复出现 ----------
        var uriCount = playlist.Segments
            .GroupBy(s => s.Uri)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var seg in playlist.Segments) {
            if (uriCount[seg.Uri] >= 3 && seg.Validity == SegmentValidity.SuspectForeign) {
                // 同一个异目录分片在列表里出现 3 次以上（分别在开头/中部/结尾），
                // 这是广告循环插入的铁证，强化标记
                seg.ValidityNote += $"；且同一分片重复出现 {uriCount[seg.Uri]} 次（广告循环插入特征）";
            }
        }

        // 整个列表里出现 3 次以上的分片，即便目录相同也值得警惕（部分源站复用同目录广告）
        var repeated = playlist.Segments
            .Where(s => uriCount[s.Uri] >= 3 && s.Validity == SegmentValidity.Unknown)
            .ToList();
        if (repeated.Count > 0 &&
            repeated.Count <= playlist.Segments.Count * foreignDirThreshold) {
            foreach (var seg in repeated) {
                seg.Validity = SegmentValidity.SuspectForeign;
                seg.ValidityNote = $"同一分片重复出现 {uriCount[seg.Uri]} 次（疑似循环插播广告）";
            }
            report.Reasons.Add($"发现 {repeated.Count} 个重复分片（同地址出现 ≥3 次），已标记为疑似广告。");
        }

        // ---------- 3. 加密上下文突变：整条流加密，个别分片却是明文 ----------
        var encryptedCount = playlist.Segments.Count(s => s.Key.IsEncrypted);
        var isMostlyEncrypted = encryptedCount > playlist.Segments.Count * 0.5;

        foreach (var seg in playlist.Segments) {
            if (seg.Validity == SegmentValidity.Unknown) continue;
            if (isMostlyEncrypted && !seg.Key.IsEncrypted)
                seg.ValidityNote += "；该片为明文段，与整条加密流不一致";
        }

        if (isMostlyEncrypted) {
            var plainSegs = playlist.Segments.Where(s => !s.Key.IsEncrypted).ToList();
            if (plainSegs.Count > 0)
                report.Reasons.Add($"整条流为 {encryptedCount} 片加密，但有 {plainSegs.Count} 片被声明为明文（METHOD=NONE）。");
        }

        // ---------- 4. 编号带断裂：同目录、不重复、密钥正常的插播广告 ----------
        DetectNumberingGap(playlist, report);

        report.Suspects.AddRange(playlist.Segments.Where(s =>
            s.Validity is SegmentValidity.SuspectForeign
                        or SegmentValidity.SuspectInserted
                        or SegmentValidity.Undecryptable));

        return report;
    }

    /// <summary>少于这么多片就谈不上"编号带"</summary>
    private const int MinSegmentsForNumbering = 20;

    /// <summary>主编号带至少要占的比例</summary>
    private const double NumberingBandRatio = 0.7;

    /// <summary>落单分片的数量上限（插播广告不会这么长）</summary>
    private const int MaxInsertedSegments = 30;

    /// <summary>
    /// 规则 4：分片名的「编号带」断裂。
    ///
    /// 实测场景（影迷界影院《交锋》第 25 集）：源站在正片里插了两段各 7 片的赌博广告，
    /// 它们**同目录、不重复、密钥也正常** —— 前三条规则一条都盖不住，于是被完整下载。
    /// 而广告是 1920x1080、正片是 1080x460，同一个视频轨道里分辨率中途突变，
    /// 转成 MP4 后多数播放器解不出来，表现出来就是「广告有声音、没画面」。
    ///
    /// 特征很干净：正片的编号严格 +1 递增，插播段来自另一个编号段 ——
    ///     正片 …000000…000073 │ 广告 …359691…359697 │ 正片 …000074…000493 │ …
    /// 做法就是取每个分片名的尾随数字，找最长的那条「连续 +1」的带，掉在带外的即为插播。
    ///
    /// 判错就会把正片当广告删掉，所以门槛设得很死，任何一条不满足就整条放弃
    /// （宁可留着广告，也不能删正片）：
    ///   · 90% 以上的分片要能取到尾随数字（纯 hash 命名的站点直接放弃）；
    ///   · 编号必须各不相同；
    ///   · 主编号带占比 ≥ 70%（占比低说明编号本来就乱，那是"删过号"不是"插播"）；
    ///   · 落单分片 ≤ 30 片；
    ///   · 落单分片必须**连续成段**，且每段都被 #EXT-X-DISCONTINUITY 夹住
    ///     —— 编号跳变与时间戳断层两个信号同时命中才动手。
    /// </summary>
    private static void DetectNumberingGap(HlsMediaPlaylist playlist, Report report) {
        var segments = playlist.Segments;
        if (segments.Count < MinSegmentsForNumbering) return;

        // 1) 取尾随数字
        var numbered = new List<(HlsSegment Seg, long Number)>(segments.Count);
        foreach (var seg in segments) {
            if (TryGetTrailingNumber(seg.Uri, out var n)) numbered.Add((seg, n));
        }
        if (numbered.Count < segments.Count * 0.9) return;
        if (numbered.Select(x => x.Number).Distinct().Count() != numbered.Count) return;

        // 2) 找最长的连续 +1 带
        var sorted = numbered.OrderBy(x => x.Number).ToList();
        var bestStart = 0;
        var bestLen = 1;
        var runStart = 0;
        for (var i = 1; i <= sorted.Count; i++) {
            var continues = i < sorted.Count && sorted[i].Number == sorted[i - 1].Number + 1;
            if (continues) continue;

            if (i - runStart > bestLen) { bestLen = i - runStart; bestStart = runStart; }
            runStart = i;
        }

        // 3) 门槛
        if (bestLen < sorted.Count * NumberingBandRatio) return;

        var inBand = new HashSet<HlsSegment>();
        for (var i = bestStart; i < bestStart + bestLen; i++) inBand.Add(sorted[i].Seg);

        var outsiders = new HashSet<HlsSegment>(segments.Where(s => !inBand.Contains(s)));
        if (outsiders.Count == 0 || outsiders.Count > MaxInsertedSegments) return;

        // 4) 落单的必须连续成段，且每段都被 DISCONTINUITY 夹住
        var runs = new List<(int Start, int End)>();
        for (var i = 0; i < segments.Count; i++) {
            if (!outsiders.Contains(segments[i])) continue;
            if (runs.Count > 0 && runs[^1].End == i - 1) runs[^1] = (runs[^1].Start, i);
            else runs.Add((i, i));
        }

        foreach (var (start, end) in runs) {
            // 段首那片自己要带断层标记，段后紧邻的那片也要带 —— 一前一后把它夹住
            var leading = segments[start].Discontinuity;
            var trailing = end + 1 < segments.Count && segments[end + 1].Discontinuity;
            if (!leading || !trailing) return;   // 验证不过 → 整条规则放弃
        }

        // 5) 通过：标记为插播
        foreach (var seg in outsiders) {
            seg.Validity = SegmentValidity.SuspectInserted;
            seg.ValidityNote = "分片编号跳出了正片的连续编号带，且两侧都有编码断层标记，疑似同目录插播广告";
        }

        report.Reasons.Add(
            $"发现 {outsiders.Count} 个分片脱离了正片的连续编号带（分 {runs.Count} 段，两侧均有 #EXT-X-DISCONTINUITY），已标记为疑似插播广告。");
    }

    /// <summary>
    /// 取文件名末尾的连续数字。
    ///
    /// 少于 3 位数字不予采信 —— "seg1.ts" 这种顺序号区分度太低，
    /// 拿它去找"编号带"只会把正常分片也卷进来。
    /// </summary>
    private static bool TryGetTrailingNumber(string uri, out long number) {
        number = 0;

        string name;
        try {
            var path = new Uri(uri).AbsolutePath;
            name = path[(path.LastIndexOf('/') + 1)..];
        } catch { return false; }

        var dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];

        var i = name.Length;
        while (i > 0 && char.IsAsciiDigit(name[i - 1])) i--;
        if (name.Length - i < 3) return false;

        return long.TryParse(name.AsSpan(i), out number);
    }

    /// <summary>
    /// 在静态分析基础上，实际探测被怀疑分片的密钥是否可取。
    /// 取不到密钥的加密分片必然失败，直接升级为 Undecryptable。
    ///
    /// 这里收的是**取页面的方法**而不是一个 HttpClient：下载器那边要按主机决定
    /// 直连还是代理（墙外站点的密钥也在墙外），直接把客户端传进来就绕过了那层回退，
    /// 网络不通会被误判成「密钥不可用 → 这一片是广告」。
    /// </summary>
    public static async Task VerifyKeyAvailabilityAsync(
        HlsMediaPlaylist playlist,
        Func<string, CancellationToken, Task<HttpResponseMessage>> getAsync,
        Report report,
        CancellationToken ct = default) {
        // 收集所有被怀疑分片引用的密钥地址
        var suspectKeyUris = report.Suspects
            .Where(s => s.Key.IsEncrypted && s.Key.Uri != null)
            .Select(s => s.Key.Uri!)
            .Distinct()
            .ToList();

        if (suspectKeyUris.Count == 0) return;

        foreach (var keyUri in suspectKeyUris) {
            ct.ThrowIfCancellationRequested();
            bool ok;
            try {
                using var resp = await getAsync(keyUri, ct);
                ok = resp.IsSuccessStatusCode;
            } catch (OperationCanceledException) { throw; } catch { ok = false; }

            if (!ok) {
                var affected = report.Suspects.Where(s => s.Key.Uri == keyUri).ToList();
                foreach (var seg in affected) {
                    seg.Validity = SegmentValidity.Undecryptable;
                    seg.ValidityNote = $"密钥不可获取（{keyUri} 返回错误），该分片必然解密失败";
                }
                report.Reasons.Add($"疑似广告段的密钥地址无法获取：{keyUri}（影响 {affected.Count} 片）");
            }
        }
    }

    /// <summary>
    /// 判断某个分片解密后是否为合法 MPEG-TS。
    /// TS 包固定 188 字节，首字节必须是同步字节 0x47。
    /// </summary>
    public static bool LooksLikeValidTs(byte[] data) {
        if (data.Length < 188) return false;
        if (data[0] != 0x47) return false;
        // 抽查若干个包起点
        int checks = Math.Min(20, data.Length / 188);
        for (int i = 1; i <= checks; i++) {
            int off = i * 188;
            if (off >= data.Length) break;
            if (data[off] != 0x47) return false;
        }
        return true;
    }

    /// <summary>
    /// 宽松版 TS 判定：只要求首字节为 0x47 且开头若干包同步。
    /// 用于在「PKCS7 填充」与「无填充」等多种解密结果中挑选正确的那一个 ——
    /// 严格版会把仅尾部填充不同的正确结果误判掉。
    /// </summary>
    public static bool LooksLikeTs(byte[] data) {
        if (data.Length < 188) return false;
        if (data[0] != 0x47) return false;
        // 校验开头 4 个包位置是否同步（不要求全文件，容忍少量异常）
        for (int i = 1; i <= 3; i++) {
            int off = i * 188;
            if (off >= data.Length) break;
            if (data[off] != 0x47) return false;
        }
        return true;
    }

    private static string GetDirectory(string uri) {
        try {
            var p = new Uri(uri).AbsolutePath;
            var i = p.LastIndexOf('/');

            // 注意：分片直接放在域名根目录时（如 /seg0.ts），LastIndexOf('/') == 0。
            // 这时必须归到根目录 "/"，否则每个分片都会被算成"各自的目录"，
            // 目录聚类会把除第一片以外的所有分片误判成插播广告并整批跳过。
            if (i < 0) return uri;
            return i == 0 ? "/" : p[..i];
        } catch { return uri; }
    }

    private static string ShortDir(string dir) {
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? dir : "…/" + string.Join('/', parts[^3..]);
    }
}
