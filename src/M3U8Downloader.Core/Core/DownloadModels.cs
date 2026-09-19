namespace M3U8Downloader.Core;

/// <summary>下载参数</summary>
public sealed class DownloadOptions {
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
public sealed class DownloadProgress {
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

    public string SpeedText => SpeedBytesPerSecond switch {
        >= 1024 * 1024 => $"{SpeedBytesPerSecond / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{SpeedBytesPerSecond / 1024:0.0} KB/s",
        _ => $"{SpeedBytesPerSecond:0} B/s",
    };

    public string SizeText => $"{DownloadedBytes / 1024.0 / 1024.0:0.0} MB";
}

/// <summary>下载结果</summary>
public sealed class DownloadResult {
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

    /// <summary>
    /// 被跳过的分片序号（广告/无效片）。产物里没有它们，所以
    /// "清单时长 vs 产物时长"的核对必须把它们排除，否则会误判成缺片。
    /// </summary>
    public List<int> SkippedSegmentIndices { get; } = new();

    /// <summary>
    /// 分片失败原因汇总，形如 <c>响应被截断：声明 233136 字节，实际收到 100000 字节（87 片）</c>。
    /// 以前这些原因被 catch 吞掉，只留下"有 N 个分片失败"，无法排查。
    /// </summary>
    public List<string> FailureReasons { get; } = new();

    /// <summary>失败样本（最多 5 条，形如 <c>#10 A8LUxiVu.ts → HTTP 503</c>）</summary>
    public List<string> FailureSamples { get; } = new();
}

/// <summary>单个分片的下载状态（用于断点续传）</summary>
public sealed class SegmentState {
    public int Index { get; set; }
    public bool Completed { get; set; }
    public long Bytes { get; set; }
}

/// <summary>一条流（视频/音频/字幕）的容器信息，来自 ffprobe</summary>
public sealed class MediaStreamInfo {
    public int Index { get; set; }
    public string CodecType { get; set; } = "";
    public string CodecName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string FrameRate { get; set; } = "";
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public long FrameCount { get; set; }

    /// <summary>给人看的一行描述，如 <c>h264 1920x818 25fps</c> / <c>aac 44100Hz 2ch</c></summary>
    public string Describe() {
        if (CodecType == "video") {
            var fps = FormatFps(FrameRate);
            var size = Width > 0 && Height > 0 ? $" {Width}x{Height}" : "";
            return $"{CodecName}{size}{fps}".Trim();
        }

        if (CodecType == "audio") {
            var rate = SampleRate > 0 ? $" {SampleRate}Hz" : "";
            var ch = Channels > 0 ? $" {Channels}ch" : "";
            return $"{CodecName}{rate}{ch}".Trim();
        }

        return $"{CodecType}/{CodecName}";
    }

    /// <summary>把 ffprobe 的 "25/1" 变成 " 25fps"；拿不到就返回空串</summary>
    public static string FormatFps(string rFrameRate) {
        var parts = rFrameRate.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], out var num)
            && double.TryParse(parts[1], out var den)
            && den > 0) {
            var fps = num / den;
            return fps == Math.Floor(fps) ? $" {fps:0}fps" : $" {fps:0.###}fps";
        }

        return "";
    }
}

/// <summary>产物容器探测结果（时长/流结构），由 ffprobe 得到</summary>
public sealed class ContainerProbe {
    /// <summary>容器格式，如 <c>mov,mp4,m4a</c></summary>
    public string FormatName { get; set; } = "";

    /// <summary>产物真实时长（秒）</summary>
    public double DurationSeconds { get; set; }

    /// <summary>容器声明的总码率（bps）</summary>
    public long BitRate { get; set; }

    public List<MediaStreamInfo> Streams { get; } = new();

    public bool HasVideo => Streams.Any(s => s.CodecType == "video");
    public bool HasAudio => Streams.Any(s => s.CodecType == "audio");
}

/// <summary>
/// 全量解码检查结果（<c>ffmpeg -f null -</c>）。
///
/// 为什么必须做这一步：容器、时长、包对齐**全对**也不代表内容没坏 ——
/// 源站返回的坏包会原样拼进产物，只有把它整条解一遍才看得出来。
///
/// 注意「输出阶段」的告警不计入 <see cref="Issues"/>（见 <see cref="IgnoredMuxerWarnings"/>）：
/// 那是检查方式自己产生的噪声，不代表文件有问题。
/// </summary>
public sealed class DecodeCheckResult {
    /// <summary>ffmpeg 退出码（0 = 整条流解码器都吃下去了）</summary>
    public int ExitCode { get; set; }

    /// <summary>是否完整解码通过</summary>
    public bool Passed { get; set; }

    /// <summary>告警/错误计数（按类型归类）</summary>
    public List<string> Issues { get; } = new();

    /// <summary>
    /// 已被忽略的「输出阶段」告警条数（muxer 时间戳提示）。
    ///
    /// 带 B 帧的源必然产生这类提示 —— 它来自 <c>-f null</c> 输出侧的重新计时，
    /// 与产物内容无关。数量记下来只是为了能在日志里说清楚"检查到底看到了什么"，
    /// **不参与 <see cref="Passed"/> 判定**。
    /// </summary>
    public int IgnoredMuxerWarnings { get; set; }

    /// <summary>检查耗时</summary>
    public TimeSpan Elapsed { get; set; }

    public string Describe() => Passed
        ? "解码通过"
        : $"解码异常（{string.Join("；", Issues)}）";
}
