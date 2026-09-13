using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace M3U8Downloader.Core;

/// <summary>
/// M3U8/HLS 播放列表解析器。
/// 支持：主清单（清晰度选择）、媒体清单、AES-128/192/256、EXT-X-MAP(fMP4)、
/// EXT-X-BYTERANGE、EXT-X-DISCONTINUITY、相对 URI 解析。
/// </summary>
public static partial class M3U8Parser
{
    [GeneratedRegex(@"([A-Z0-9\-]+)=(""[^""]*""|[^,]*)", RegexOptions.Compiled)]
    private static partial Regex AttributeRegex();

    /// <summary>解析播放列表文本</summary>
    /// <param name="content">m3u8 文本</param>
    /// <param name="baseUri">用于把相对地址解析为绝对地址的基准（即该 m3u8 自身的 URL）</param>
    public static HlsParseResult Parse(string content, string baseUri)
    {
        var result = new HlsParseResult { SourceUrl = baseUri };

        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidDataException("播放列表内容为空。");

        var lines = content
            .Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(l => l.Trim())
            .ToList();

        if (!lines.Any(l => l.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("不是有效的 M3U8 文件（缺少 #EXTM3U 头）。");

        // 先探测是否为媒体清单：出现 EXTINF 或 EXT-X-TARGETDURATION 即视为媒体清单
        bool looksLikeMedia = lines.Any(l =>
            l.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase));

        if (looksLikeMedia)
        {
            result.Media = ParseMedia(lines, baseUri);
        }
        else
        {
            ParseMaster(lines, baseUri, result.Variants);
            // 主清单里没有变体，也没有分片，可能是退化情况，按媒体清单再试一次
            if (result.Variants.Count == 0)
                result.Media = ParseMedia(lines, baseUri);
        }

        return result;
    }

    /// <summary>解析主清单，抽出所有清晰度变体</summary>
    private static void ParseMaster(List<string> lines, string baseUri, List<HlsVariant> variants)
    {
        Dictionary<string, string>? pendingAttrs = null;

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingAttrs = ParseAttributes(line["#EXT-X-STREAM-INF:".Length..]);
                continue;
            }

            if (line.StartsWith('#')) continue;

            // 非 # 行是变体地址，它归属于上一条 STREAM-INF
            if (pendingAttrs != null)
            {
                variants.Add(new HlsVariant
                {
                    Uri = ResolveUri(baseUri, line),
                    Bandwidth = ParseLong(pendingAttrs.GetValueOrDefault("BANDWIDTH")),
                    AverageBandwidth = ParseLong(pendingAttrs.GetValueOrDefault("AVERAGE-BANDWIDTH")),
                    Resolution = pendingAttrs.GetValueOrDefault("RESOLUTION"),
                    Codecs = pendingAttrs.GetValueOrDefault("CODECS"),
                    Name = pendingAttrs.GetValueOrDefault("NAME") ?? pendingAttrs.GetValueOrDefault("VIDEO"),
                });
                pendingAttrs = null;
            }
        }
    }

    /// <summary>解析媒体清单，抽出分片与加密信息</summary>
    private static HlsMediaPlaylist ParseMedia(List<string> lines, string baseUri)
    {
        var pl = new HlsMediaPlaylist { SourceUrl = baseUri };
        HlsKeyInfo currentKey = new();
        double pendingDuration = 0;
        string? pendingTitle = null;
        long? pendingRangeLen = null;
        long? pendingRangeOff = null;
        bool pendingDiscontinuity = false;
        string? currentMap = null;

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;

            if (line.StartsWith('#'))
            {
                if (line.StartsWith("#EXT-X-VERSION:", StringComparison.OrdinalIgnoreCase))
                {
                    pl.Version = (int)ParseLong(line["#EXT-X-VERSION:".Length..]);
                }
                else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
                {
                    pl.TargetDuration = ParseDouble(line["#EXT-X-TARGETDURATION:".Length..]);
                }
                else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
                {
                    pl.MediaSequence = ParseLong(line["#EXT-X-MEDIA-SEQUENCE:".Length..]);
                }
                else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
                {
                    currentKey = ParseKey(line["#EXT-X-KEY:".Length..], baseUri);
                }
                else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
                {
                    var attrs = ParseAttributes(line["#EXT-X-MAP:".Length..]);
                    var mapUri = attrs.GetValueOrDefault("URI");
                    currentMap = string.IsNullOrEmpty(mapUri) ? null : ResolveUri(baseUri, mapUri!);
                }
                else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line["#EXTINF:".Length..];
                    var comma = v.IndexOf(',');
                    if (comma >= 0)
                    {
                        pendingDuration = ParseDouble(v[..comma]);
                        var title = v[(comma + 1)..].Trim();
                        pendingTitle = title.Length > 0 ? title : null;
                    }
                    else pendingDuration = ParseDouble(v);
                }
                else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
                {
                    var v = line["#EXT-X-BYTERANGE:".Length..];
                    var at = v.IndexOf('@');
                    if (at >= 0)
                    {
                        pendingRangeLen = ParseLong(v[..at]);
                        pendingRangeOff = ParseLong(v[(at + 1)..]);
                    }
                    else pendingRangeLen = ParseLong(v);
                }
                else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
                {
                    pendingDiscontinuity = true;
                }
                else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
                {
                    pl.IsLive = false;
                }
                continue;
            }

            // 非 # 行 = 分片地址
            var seg = new HlsSegment
            {
                Index = pl.Segments.Count,
                Uri = ResolveUri(baseUri, line),
                Duration = pendingDuration,
                Title = pendingTitle,
                Key = currentKey,
                Discontinuity = pendingDiscontinuity,
                InitSegmentUri = currentMap,
                // 媒体序号 = MEDIA-SEQUENCE + 索引，用于推导缺省 IV
                MediaSequence = pl.MediaSequence + pl.Segments.Count,
            };

            if (pendingRangeLen.HasValue)
            {
                seg.ByteRangeLength = pendingRangeLen;
                // 未显式给出 offset 时，接在上一个同 URI 分片之后
                seg.ByteRangeOffset = pendingRangeOff ?? 0;
            }

            pl.Segments.Add(seg);

            pendingDuration = 0;
            pendingTitle = null;
            pendingRangeLen = null;
            pendingRangeOff = null;
            pendingDiscontinuity = false;
        }

        // 没有 ENDLIST 视为直播
        if (!lines.Any(l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase)))
            pl.IsLive = true;

        return pl;
    }

    /// <summary>解析 EXT-X-KEY 的值</summary>
    private static HlsKeyInfo ParseKey(string value, string baseUri)
    {
        var attrs = ParseAttributes(value);
        var methodStr = attrs.GetValueOrDefault("METHOD") ?? "NONE";

        var method = methodStr.ToUpperInvariant() switch
        {
            "NONE" => HlsEncryptionMethod.None,
            "AES-128" => HlsEncryptionMethod.Aes128,
            "AES-192" => HlsEncryptionMethod.Aes192,
            "AES-256" => HlsEncryptionMethod.Aes256,
            "SAMPLE-AES" => HlsEncryptionMethod.SampleAes,
            _ => HlsEncryptionMethod.None,
        };

        if (method == HlsEncryptionMethod.None)
            return new HlsKeyInfo { Method = HlsEncryptionMethod.None };

        var uri = attrs.GetValueOrDefault("URI");
        byte[]? iv = null;
        var ivStr = attrs.GetValueOrDefault("IV");
        if (!string.IsNullOrEmpty(ivStr))
            iv = ParseHexIv(ivStr!);

        return new HlsKeyInfo
        {
            Method = method,
            Uri = string.IsNullOrEmpty(uri) ? null : ResolveUri(baseUri, uri!),
            IV = iv,
            KeyFormat = attrs.GetValueOrDefault("KEYFORMAT"),
        };
    }

    /// <summary>解析 0x 开头的十六进制 IV</summary>
    private static byte[]? ParseHexIv(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (s.Length == 0 || s.Length % 2 != 0) return null;
        try
        {
            var bytes = Convert.FromHexString(s);
            if (bytes.Length == 16) return bytes;
            // 不足 16 字节则左侧补零
            if (bytes.Length < 16)
            {
                var full = new byte[16];
                Array.Copy(bytes, 0, full, 16 - bytes.Length, bytes.Length);
                return full;
            }
            return bytes[..16];
        }
        catch { return null; }
    }

    /// <summary>解析形如 KEY=VALUE,KEY="VALUE" 的属性串</summary>
    private static Dictionary<string, string> ParseAttributes(string s)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributeRegex().Matches(s))
        {
            var key = m.Groups[1].Value;
            var val = m.Groups[2].Value.Trim();
            if (val.Length >= 2 && val.StartsWith('"') && val.EndsWith('"'))
                val = val[1..^1];
            if (key.Length > 0) dict[key] = val;
        }
        return dict;
    }

    /// <summary>把可能是相对路径的地址解析为绝对地址</summary>
    public static string ResolveUri(string baseUri, string relative)
    {
        relative = relative.Trim();
        if (relative.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return relative;

        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var b))
            return relative;

        if (Uri.TryCreate(b, relative, out var abs))
            return abs.ToString();

        return relative;
    }

    private static long ParseLong(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static double ParseDouble(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
