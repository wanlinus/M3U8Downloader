namespace M3U8Downloader.Core;

/// <summary>下载参数</summary>
public sealed class DownloadOptions
{
    /// <summary>并发下载数</summary>
    public int Concurrency { get; set; } = 16;

    /// <summary>单分片最大重试次数</summary>
    public int MaxRetries { get; set; } = 3;

    /// <summary>重试基础退避（毫秒），按次数指数增长</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>单次请求超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>附加请求头（Referer / Origin / User-Agent 等）</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>是否自动剔除疑似广告/无效分片</summary>
    public bool AutoSkipInvalidSegments { get; set; } = true;

    /// <summary>本集专属的暂存目录（分片与清单都放这里）</summary>
    public required string TempDirectory { get; init; }

    /// <summary>
    /// 下载 + 校验全部通过后是否删除暂存目录（默认 true）。
    /// **中间任何一步失败都会保留**，便于排查或下次续传。
    /// </summary>
    public bool DeleteTempOnSuccess { get; init; } = true;

    /// <summary>输出文件路径（.ts）</summary>
    public required string OutputPath { get; init; }
}

/// <summary>下载进度快照</summary>
public sealed class DownloadProgress
{
    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int FailedSegments { get; set; }
    public int SkippedSegments { get; set; }
    public long DownloadedBytes { get; set; }
    public long TotalBytes { get; set; }
    public double SpeedBytesPerSecond { get; set; }
    public TimeSpan Elapsed { get; set; }
    public TimeSpan? Eta { get; set; }
    public string? CurrentMessage { get; set; }

    public double Percent => TotalSegments == 0 ? 0
        : (CompletedSegments + FailedSegments + SkippedSegments) * 100.0 / TotalSegments;

    public string SpeedText => SpeedBytesPerSecond switch
    {
        >= 1024 * 1024 => $"{SpeedBytesPerSecond / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{SpeedBytesPerSecond / 1024:0.0} KB/s",
        _ => $"{SpeedBytesPerSecond:0} B/s",
    };

    public string SizeText => $"{DownloadedBytes / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>下载结果</summary>
public sealed class DownloadResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    public long OutputBytes { get; set; }
    public int TotalSegments { get; set; }
    public int CompletedSegments { get; set; }
    public int SkippedSegments { get; set; }
    public int FailedSegments { get; set; }
    public TimeSpan Elapsed { get; set; }
    public string? Error { get; set; }
    public List<string> Messages { get; } = new();
}

/// <summary>单个分片的下载状态（用于断点续传）</summary>
public sealed class SegmentState
{
    public int Index { get; set; }
    public bool Completed { get; set; }
    public long Bytes { get; set; }
}
