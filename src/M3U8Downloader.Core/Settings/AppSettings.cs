using System.Text.Json.Serialization;

namespace M3U8Downloader.Core.Settings;

/// <summary>
/// 应用设置。
///
/// 所有字段都有合理默认值，且读取失败时整体回退到默认值 ——
/// 这个文件是要跟着安装包分发给别人的，不能因为用户改坏一个字段就启动失败。
/// </summary>
public sealed class AppSettings
{
    /// <summary>用户手工指定的 ffmpeg.exe 完整路径（留空则自动探测）</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>自定义 ffmpeg 下载源（留空用内置默认）。国内可换成镜像。</summary>
    public string? FfmpegDownloadUrl { get; set; }

    /// <summary>默认保存目录（留空则用系统「下载」目录）</summary>
    public string? DefaultOutputDirectory { get; set; }

    /// <summary>站点批量下载：同时下载几集</summary>
    public int EpisodeConcurrency { get; set; } = 2;

    /// <summary>单集内部的分片并发</summary>
    public int SegmentConcurrency { get; set; } = 16;

    /// <summary>是否自动跳过疑似插播广告分片</summary>
    public bool AutoSkipInvalidSegments { get; set; } = true;

    /// <summary>按网站下载时是否自动建「剧名」子目录</summary>
    public bool SeriesSubdirectory { get; set; } = true;

    /// <summary>
    /// 每集下完后是否做**全量解码检查**（<c>ffmpeg -f null -</c>，默认开）。
    ///
    /// 它是唯一能发现"容器/时长都对、但内容里混了源站坏包"的检查；
    /// 代价是要把产物整条解一遍（实测 400 MB 约 35 秒）。
    /// 关掉之后报告里的"解码检查"列会显示为未执行，其余校验不受影响。
    /// </summary>
    public bool FullDecodeCheck { get; set; } = true;

    /// <summary>自定义 User-Agent（留空用内置的浏览器 UA）</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// 是否在**下载 FFmpeg 时**使用代理（默认 false）。
    ///
    /// 只作用于「获取 FFmpeg」这一件事 —— 国内直连 GitHub 往往不通。
    /// **视频下载与站点解析始终直连**，不受此开关影响：
    /// 源站基本都在国内，绕代理更慢，出口 IP 变化还可能触发防盗链。
    /// </summary>
    public bool ProxyEnabled { get; set; }

    /// <summary>代理地址，如 <c>http://127.0.0.1:7897</c>（也可只填 <c>127.0.0.1:7897</c>）</summary>
    public string? ProxyUrl { get; set; }

    /// <summary>启动时是否检查更新（预留，暂未实现）</summary>
    public bool CheckUpdateOnStartup { get; set; }

    [JsonIgnore]
    public static AppSettings Default => new();

    /// <summary>把越界/非法的值拉回合法范围（读到脏数据时兜底）</summary>
    public void Normalize()
    {
        EpisodeConcurrency = Math.Clamp(EpisodeConcurrency, 1, 8);
        SegmentConcurrency = Math.Clamp(SegmentConcurrency, 1, 64);

        if (FfmpegPath is not null)
        {
            FfmpegPath = FfmpegPath.Trim();
            if (FfmpegPath.Length == 0) FfmpegPath = null;
        }

        if (FfmpegDownloadUrl is not null)
        {
            FfmpegDownloadUrl = FfmpegDownloadUrl.Trim();
            if (FfmpegDownloadUrl.Length == 0) FfmpegDownloadUrl = null;
        }

        if (DefaultOutputDirectory is not null)
        {
            DefaultOutputDirectory = DefaultOutputDirectory.Trim();
            if (DefaultOutputDirectory.Length == 0) DefaultOutputDirectory = null;
        }

        if (UserAgent is not null)
        {
            UserAgent = UserAgent.Trim();
            if (UserAgent.Length == 0) UserAgent = null;
        }

        if (ProxyUrl is not null)
        {
            ProxyUrl = ProxyUrl.Trim();
            if (ProxyUrl.Length == 0) ProxyUrl = null;
        }
    }
}
