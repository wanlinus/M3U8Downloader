namespace M3U8Downloader.Core.Tasks;

/// <summary>
/// 一条「这一集下过」的历史记录。
///
/// 为什么要单独记一份：任务是会被清理的 —— 用户点「清理已完成」、重装程序、
/// 换台机器，任务列表里就什么都没有了，但视频还好好躺在文件夹里。
/// 下次想补更时，这份历史就是那本"下过的账"。
/// </summary>
public sealed record DownloadHistoryEntry
{
    /// <summary>剧集页面地址 —— 同一部剧的标识（站点换域名前的旧记录也按它归并）</summary>
    public required string PageUrl { get; init; }

    public required string SiteName { get; init; }

    public required string SeriesTitle { get; init; }

    /// <summary>集号（1 基）</summary>
    public required int EpisodeNumber { get; init; }

    /// <summary>产物路径（记录时的样子；文件可能已经被移动或删除）</summary>
    public string? FilePath { get; init; }

    /// <summary>记录时的文件大小（0 = 未知）</summary>
    public long FileBytes { get; init; }

    public DateTimeOffset DownloadedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// 下载历史的存储。
///
/// Core 只定义接口：图形界面用 SQLite 实现（<c>%APPDATA%\M3U8Downloader\downloads.db</c>），
/// 命令行不接（<see cref="NullDownloadHistoryStore"/>）。
///
/// 为什么 SQLite 不写在 Core 里：「Core 不引任何第三方包」是仓库的硬约定 ——
/// 另外三个项目都依赖它，一旦引进来，命令行、自检、界面全都会被带上原生库。
/// 所以存储实现落在界面层，这里只留协议。
///
/// 实现方**必须自己吞掉异常**：历史记不上不该影响下载。
/// </summary>
public interface IDownloadHistoryStore
{
    /// <summary>记一集（同一部剧的同一集重下时覆盖旧记录）</summary>
    void Record(DownloadHistoryEntry entry);

    /// <summary>取某部剧下过的所有集（按集号升序）</summary>
    IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl);

    /// <summary>全部历史（按时间倒序）</summary>
    IReadOnlyList<DownloadHistoryEntry> All();
}

/// <summary>不记账的实现：命令行、自检或用户没开历史时用它</summary>
public sealed class NullDownloadHistoryStore : IDownloadHistoryStore
{
    public static readonly NullDownloadHistoryStore Instance = new();

    public void Record(DownloadHistoryEntry entry) { }

    public IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl) =>
        Array.Empty<DownloadHistoryEntry>();

    public IReadOnlyList<DownloadHistoryEntry> All() => Array.Empty<DownloadHistoryEntry>();
}
