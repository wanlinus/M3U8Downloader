namespace M3U8Downloader.Core;

/// <summary>HLS 加密方式</summary>
public enum HlsEncryptionMethod {
    None,
    Aes128,
    Aes192,
    Aes256,
    SampleAes,
}

/// <summary>
/// 一个 #EXT-X-KEY 生效区间。播放列表中可能出现多个 KEY 声明，
/// 每个分片归属于它之前最近的那个 KEY。
/// </summary>
public sealed class HlsKeyInfo {
    public HlsEncryptionMethod Method { get; init; } = HlsEncryptionMethod.None;

    /// <summary>密钥地址（已解析为绝对地址）。Method=None 时为 null。</summary>
    public string? Uri { get; init; }

    /// <summary>IV，16 字节。未显式给出时为 null，此时用媒体序号推导。</summary>
    public byte[]? IV { get; init; }

    public string? KeyFormat { get; init; }

    /// <summary>密钥字节缓存（同一 KEY 只拉取一次）。</summary>
    public byte[]? KeyData { get; set; }

    public bool IsEncrypted => Method != HlsEncryptionMethod.None;

    public override string ToString() =>
        IsEncrypted ? $"{Method} {Uri}" : "NONE(明文)";
}

/// <summary>分片类型判定，用于识别无效/广告分片</summary>
public enum SegmentValidity {
    /// <summary>尚未检测</summary>
    Unknown,
    /// <summary>正常分片</summary>
    Ok,
    /// <summary>与主播放列表不同目录，疑似插播广告</summary>
    SuspectForeign,
    /// <summary>
    /// 分片编号脱离了正片的连续编号带，疑似同目录插播广告。
    ///
    /// 与 <see cref="SuspectForeign"/> 的区别：这类广告**同目录、不重复、密钥也正常**，
    /// 前三条规则一条都盖不住，只能靠"编号序列断裂 + 两侧 DISCONTINUITY"认出来。
    /// </summary>
    SuspectInserted,
    /// <summary>明文段混入加密流，且其密钥不可得 —— 必失败</summary>
    Undecryptable,
    /// <summary>下载后校验失败</summary>
    Invalid,
    /// <summary>已按策略跳过</summary>
    Skipped,
}

/// <summary>单个媒体分片</summary>
public sealed class HlsSegment {
    /// <summary>在播放列表中的序号（0 基）</summary>
    public int Index { get; set; }

    /// <summary>分片绝对地址</summary>
    public required string Uri { get; init; }

    /// <summary>#EXTINF 时长（秒）</summary>
    public double Duration { get; set; }

    /// <summary>#EXT-X-TITLE，通常为空的节目名</summary>
    public string? Title { get; set; }

    /// <summary>生效的加密信息</summary>
    public HlsKeyInfo Key { get; set; } = new();

    /// <summary>
    /// 该分片的媒体序号（= 播放列表 MEDIA-SEQUENCE + 索引）。
    /// EXT-X-KEY 未显式给出 IV 时，按 HLS 规范用它推导 IV。
    /// </summary>
    public long MediaSequence { get; set; }

    /// <summary>#EXT-X-BYTERANGE 字节范围下载（少见但需要支持）</summary>
    public long? ByteRangeOffset { get; set; }
    public long? ByteRangeLength { get; set; }

    /// <summary>#EXT-X-DISCONTINUITY：与上一分片存在编码断层</summary>
    public bool Discontinuity { get; set; }

    /// <summary>#EXT-X-MAP 指定的初始化分片（fMP4）</summary>
    public string? InitSegmentUri { get; set; }

    /// <summary>有效性判定结果</summary>
    public SegmentValidity Validity { get; set; } = SegmentValidity.Unknown;

    /// <summary>判定说明（展示给用户）</summary>
    public string? ValidityNote { get; set; }

    /// <summary>是否应参与下载</summary>
    public bool IsDownloadable => Validity is SegmentValidity.Unknown or SegmentValidity.Ok or SegmentValidity.SuspectForeign;

    public string FileName => Path.GetFileName(new Uri(Uri).AbsolutePath);

    public override string ToString() => $"[{Index}] {Duration:0.##}s {FileName}";
}

/// <summary>媒体播放列表（含分片列表）</summary>
public sealed class HlsMediaPlaylist {
    public required string SourceUrl { get; init; }
    public int Version { get; set; }
    public double TargetDuration { get; set; }
    public long MediaSequence { get; set; }
    public bool IsLive { get; set; }
    public List<HlsSegment> Segments { get; } = new();

    public double TotalDuration => Segments.Sum(s => s.Duration);

    /// <summary>已解密可直接拼接的流（mpegts）还是 fMP4</summary>
    public bool IsFmp4 => Segments.Any(s => s.InitSegmentUri != null);
}

/// <summary>主播放列表中的一个清晰度变体</summary>
public sealed class HlsVariant {
    public required string Uri { get; init; }
    public long Bandwidth { get; set; }
    public long AverageBandwidth { get; set; }
    public string? Resolution { get; set; }
    public string? Codecs { get; set; }
    public string? Name { get; set; }

    public string DisplayName {
        get {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Resolution)) parts.Add(Resolution!);
            if (Bandwidth > 0) parts.Add($"{Bandwidth / 1000} kbps");
            if (!string.IsNullOrEmpty(Name)) parts.Add(Name!);
            return parts.Count > 0 ? string.Join("  ·  ", parts) : Uri;
        }
    }
}

/// <summary>解析结果：可能是主清单，也可能是媒体清单</summary>
public sealed class HlsParseResult {
    public required string SourceUrl { get; init; }
    public List<HlsVariant> Variants { get; } = new();
    public HlsMediaPlaylist? Media { get; set; }
    public bool IsMaster => Variants.Count > 0;
}
