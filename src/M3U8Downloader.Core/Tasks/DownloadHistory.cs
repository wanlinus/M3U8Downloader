using M3U8Downloader.Core.Sites;

namespace M3U8Downloader.Core.Tasks;

/// <summary>
/// 一条「这一集下过」的历史记录。
///
/// 为什么要单独记一份：任务是会被清理的 —— 用户点「清理已完成」、重装程序、
/// 换台机器，任务列表里就什么都没有了，但视频还好好躺在文件夹里。
/// 下次想补更时，这份历史就是那本"下过的账"。
/// </summary>
public sealed record DownloadHistoryEntry {
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
public interface IDownloadHistoryStore {
    /// <summary>记一集（同一部剧的同一集重下时覆盖旧记录）</summary>
    void Record(DownloadHistoryEntry entry);

    /// <summary>
    /// 销掉一部剧的全部记录：用户把这个任务从列表里**移除**了 ——
    /// 任务都没了，它那本"下过的账"也不该继续留着。
    ///
    /// 按 <paramref name="pageUrl"/> 整部销，不是只销任务里现有的那几集：
    /// 用户在续下时可能只勾了一部分，历史里还躺着更早下过、任务记录里已经没有的集。
    /// 「清理已完成」不走这条路（那只是收拾列表，账还要留着，见接口说明）。
    /// </summary>
    void Forget(string pageUrl);

    /// <summary>取某部剧下过的所有集（按集号升序）</summary>
    IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl);

    /// <summary>全部历史（按时间倒序）</summary>
    IReadOnlyList<DownloadHistoryEntry> All();
}

/// <summary>不记账的实现：命令行、自检或用户没开历史时用它</summary>
public sealed class NullDownloadHistoryStore : IDownloadHistoryStore {
    public static readonly NullDownloadHistoryStore Instance = new();

    public void Record(DownloadHistoryEntry entry) { }

    public void Forget(string pageUrl) { }

    public IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl) =>
        Array.Empty<DownloadHistoryEntry>();

    public IReadOnlyList<DownloadHistoryEntry> All() => Array.Empty<DownloadHistoryEntry>();
}

/// <summary>
/// 把一份下载报告里"真的下好了"的集记进历史。
///
/// 为什么单独成一个类："哪些集算下过"是一条必须和**读的那一侧**严格对齐的规则，
/// 独立出来才好被自检直接盯住（阶段 Q 就有一条断言专门守它），
/// 也不用把它藏在两千多行的任务队列里。
/// 规则只有一条但很关键：**只记 <see cref="EpisodeDownloadStatus.Completed"/> 且产物路径非空的**，
/// 失败/取消的集绝不进历史 —— 历史是"下过"的账，掺进没下成的集，
/// 下次补更时就会误报"曾下载过"。
/// </summary>
public static class DownloadHistoryRecorder {
    public static void Record(
        IDownloadHistoryStore history,
        string pageUrl,
        string siteName,
        string seriesTitle,
        IEnumerable<EpisodeDownloadReport> episodes) {
        if (history is null or NullDownloadHistoryStore) return;
        if (string.IsNullOrWhiteSpace(pageUrl)) return;

        foreach (var episode in episodes) {
            if (episode.Status != EpisodeDownloadStatus.Completed) continue;
            if (string.IsNullOrWhiteSpace(episode.OutputPath)) continue;

            long bytes = 0;
            try { bytes = new FileInfo(episode.OutputPath).Length; } catch { /* 文件没了就记 0 */ }

            try {
                history.Record(new DownloadHistoryEntry {
                    PageUrl = pageUrl,
                    SiteName = siteName,
                    SeriesTitle = seriesTitle,
                    EpisodeNumber = episode.Episode.Number,
                    FilePath = episode.OutputPath,
                    FileBytes = bytes,
                });
            } catch {
                // 历史是辅助，记不上就算了（比如数据库被别的进程锁着）
            }
        }
    }
}
