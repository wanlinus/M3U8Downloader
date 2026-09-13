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
public sealed class SegmentInspector
{
    /// <summary>判定结果</summary>
    public sealed class Report
    {
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
    public static Report Inspect(HlsMediaPlaylist playlist, double foreignDirThreshold = 0.5)
    {
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

        foreach (var g in byDir.Skip(1))
        {
            // 少数分片落在其他目录 —— 插播广告的典型特征
            if ((double)g.Count / playlist.Segments.Count <= foreignDirThreshold)
            {
                foreach (var seg in g.Segments)
                {
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

        foreach (var seg in playlist.Segments)
        {
            if (uriCount[seg.Uri] >= 3 && seg.Validity == SegmentValidity.SuspectForeign)
            {
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
            repeated.Count <= playlist.Segments.Count * foreignDirThreshold)
        {
            foreach (var seg in repeated)
            {
                seg.Validity = SegmentValidity.SuspectForeign;
                seg.ValidityNote = $"同一分片重复出现 {uriCount[seg.Uri]} 次（疑似循环插播广告）";
            }
            report.Reasons.Add($"发现 {repeated.Count} 个重复分片（同地址出现 ≥3 次），已标记为疑似广告。");
        }

        // ---------- 3. 加密上下文突变：整条流加密，个别分片却是明文 ----------
        var encryptedCount = playlist.Segments.Count(s => s.Key.IsEncrypted);
        var isMostlyEncrypted = encryptedCount > playlist.Segments.Count * 0.5;

        foreach (var seg in playlist.Segments)
        {
            if (seg.Validity == SegmentValidity.Unknown) continue;
            if (isMostlyEncrypted && !seg.Key.IsEncrypted)
                seg.ValidityNote += "；该片为明文段，与整条加密流不一致";
        }

        if (isMostlyEncrypted)
        {
            var plainSegs = playlist.Segments.Where(s => !s.Key.IsEncrypted).ToList();
            if (plainSegs.Count > 0)
                report.Reasons.Add($"整条流为 {encryptedCount} 片加密，但有 {plainSegs.Count} 片被声明为明文（METHOD=NONE）。");
        }

        report.Suspects.AddRange(playlist.Segments.Where(s =>
            s.Validity is SegmentValidity.SuspectForeign or SegmentValidity.Undecryptable));

        return report;
    }

    /// <summary>
    /// 在静态分析基础上，实际探测被怀疑分片的密钥是否可取。
    /// 取不到密钥的加密分片必然失败，直接升级为 Undecryptable。
    /// </summary>
    public static async Task VerifyKeyAvailabilityAsync(
        HlsMediaPlaylist playlist,
        HttpClient http,
        Report report,
        CancellationToken ct = default)
    {
        // 收集所有被怀疑分片引用的密钥地址
        var suspectKeyUris = report.Suspects
            .Where(s => s.Key.IsEncrypted && s.Key.Uri != null)
            .Select(s => s.Key.Uri!)
            .Distinct()
            .ToList();

        if (suspectKeyUris.Count == 0) return;

        foreach (var keyUri in suspectKeyUris)
        {
            ct.ThrowIfCancellationRequested();
            bool ok;
            try
            {
                using var resp = await http.GetAsync(keyUri, HttpCompletionOption.ResponseHeadersRead, ct);
                ok = resp.IsSuccessStatusCode;
            }
            catch { ok = false; }

            if (!ok)
            {
                var affected = report.Suspects.Where(s => s.Key.Uri == keyUri).ToList();
                foreach (var seg in affected)
                {
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
    public static bool LooksLikeValidTs(byte[] data)
    {
        if (data.Length < 188) return false;
        if (data[0] != 0x47) return false;
        // 抽查若干个包起点
        int checks = Math.Min(20, data.Length / 188);
        for (int i = 1; i <= checks; i++)
        {
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
    public static bool LooksLikeTs(byte[] data)
    {
        if (data.Length < 188) return false;
        if (data[0] != 0x47) return false;
        // 校验开头 4 个包位置是否同步（不要求全文件，容忍少量异常）
        for (int i = 1; i <= 3; i++)
        {
            int off = i * 188;
            if (off >= data.Length) break;
            if (data[off] != 0x47) return false;
        }
        return true;
    }

    private static string GetDirectory(string uri)
    {
        try
        {
            var p = new Uri(uri).AbsolutePath;
            var i = p.LastIndexOf('/');

            // 注意：分片直接放在域名根目录时（如 /seg0.ts），LastIndexOf('/') == 0。
            // 这时必须归到根目录 "/"，否则每个分片都会被算成"各自的目录"，
            // 目录聚类会把除第一片以外的所有分片误判成插播广告并整批跳过。
            if (i < 0) return uri;
            return i == 0 ? "/" : p[..i];
        }
        catch { return uri; }
    }

    private static string ShortDir(string dir)
    {
        var parts = dir.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length <= 3 ? dir : "…/" + string.Join('/', parts[^3..]);
    }
}
