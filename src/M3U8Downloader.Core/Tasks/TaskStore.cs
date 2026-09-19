using M3U8Downloader.Core.Storage;

namespace M3U8Downloader.Core.Tasks;

/// <summary>
/// 站点上一集的**稳定**元数据快照。
///
/// 为什么要存它：首次下载时就该把"站点上全部集"记下来（不只是用户勾了的那几集），
/// 之后点「续下」不必联网解析页面，也能列出全部集、判断哪些下过 ——
/// 站点解析失败（断网、站点挂了、代理没开）时照样能续下。
///
/// 为什么**不存 m3u8 直链**：直链带时效签名、很快就会失效；而且下载流程本来就是
/// 每集下载前现解析一次（<c>SeriesDownloader</c> 里的 ResolvePlaylistUrlAsync）。
/// 所以只要留下播放页地址与站点集标识，这些元数据就一直可用。
/// </summary>
public sealed class EpisodeMetadata {
    /// <summary>集号（1 基）</summary>
    public int Number { get; set; }

    /// <summary>显示名，如「第01集」</summary>
    public string Title { get; set; } = "";

    /// <summary>播放页绝对地址</summary>
    public string PageUrl { get; set; } = "";

    /// <summary>站点自定义的集标识（如努努的 ep_slug），解析直链时要用</summary>
    public string? Key { get; set; }

    /// <summary>所属播放源</summary>
    public int SourceId { get; set; }
}

/// <summary>
/// 一部剧的站点快照：播放源信息 + 站点上**全部集**的元数据。
///
/// 这是「续下不必联网」的依据，首轮入队时就存下来。
/// 必须连 <see cref="Headers"/> 一起存 —— 下载分片与清单时要带 Referer/Origin
/// （<c>SeriesDownloader</c> 里 <c>headers = new(series.Headers)</c>），
/// 从快照重建的 SiteSeries 少了这些头，站点会直接 403。
///
/// 而适配器是**按每集的 PageUrl 的 host** 挑的（<c>SiteResolver.ResolvePlaylistUrlAsync</c>），
/// 所以只要有 PageUrl + Key 就能重新解析出直链，不用存会过期的 m3u8 直链。
/// </summary>
public sealed class SeriesSnapshot {
    /// <summary>站点类型名（存枚举名而不是值，改枚举也不会读坏旧文件）</summary>
    public string Kind { get; set; } = nameof(Sites.SiteKind.Generic);

    public string SiteName { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string SeriesId { get; set; } = "";
    public string Title { get; set; } = "";

    /// <summary>站点建议的请求头（Referer / Origin / User-Agent）</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>选定的播放源 id</summary>
    public int? SourceId { get; set; }

    /// <summary>抓取时间（界面可以显示"集列表更新于…"）</summary>
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>站点上该源的全部集，按集号升序</summary>
    public List<EpisodeMetadata> Episodes { get; set; } = new();
}

/// <summary>任务里一集的持久化记录</summary>
public sealed class TaskEpisodeRecord {
    public int Number { get; set; }
    public string Title { get; set; } = "";

    /// <summary><see cref="Sites.EpisodeDownloadStatus"/> 的名字（用枚举名而不是中文，改文案也不会读坏旧文件）</summary>
    public string Status { get; set; } = nameof(Sites.EpisodeDownloadStatus.Pending);

    public double Percent { get; set; }
    public long Bytes { get; set; }

    /// <summary>产物路径（已完成时有）—— 继续下载时据此判断这一集还在不在</summary>
    public string? OutputPath { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// 一个下载任务的持久化记录。程序重启后据此把任务列表和断点位置恢复回来。
/// </summary>
public sealed class SeriesTaskRecord {
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string OutputDirectory { get; set; } = "";

    /// <summary>实际产物目录（输出目录 + 「剧名 - 站点」子目录）；老记录里没有，为 null 时界面退回根目录</summary>
    public string? ResolvedDirectory { get; set; }

    public string? SourceName { get; set; }
    public int? PreferredSourceId { get; set; }

    /// <summary><see cref="SeriesTaskState"/> 的名字</summary>
    public string State { get; set; } = nameof(SeriesTaskState.Queued);

    public double Percent { get; set; }
    public long DownloadedBytes { get; set; }
    public string? ReportPath { get; set; }
    public string? Message { get; set; }

    /// <summary>本次下载自动跳过的插播广告分片数（老记录里没有这个字段，读到就是 0）</summary>
    public int SkippedAdSegments { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }

    // ---- 下载参数（继续下载时复用）----
    public int EpisodeConcurrency { get; set; } = 2;
    public int SegmentConcurrency { get; set; } = 16;
    public int? PreferHeight { get; set; }
    public string? FfmpegPath { get; set; }
    public string FileNamePattern { get; set; } = "{title}.{number:00}";
    public bool SeriesSubdirectory { get; set; } = true;

    public List<TaskEpisodeRecord> Episodes { get; set; } = new();

    // ---- 站点快照（「续下」不必联网的依据）----

    /// <summary>
    /// 首轮入队时存下的站点快照（含**全部集**，不只是用户勾选的那些）。
    /// 老记录里没有这个字段 → null，那类任务第一次续下会联网解析并顺手补上。
    /// </summary>
    public SeriesSnapshot? Snapshot { get; set; }

    public SeriesTaskState ParsedState =>
        Enum.TryParse<SeriesTaskState>(State, ignoreCase: true, out var s) ? s : SeriesTaskState.Queued;
}

/// <summary>
/// 任务列表的落盘入口。
///
/// **任务存在统一库（<c>data\m3u8.db</c>）里**了 —— <c>tasks</c> / <c>task_episodes</c> /
/// <c>task_snapshots</c> / <c>snapshot_episodes</c> 四张表，不再是独立的 <c>tasks.json</c>。
/// 具体怎么落库见 <see cref="Storage.SqliteTaskStore"/>。
///
/// 这里保留类名与 <see cref="Load"/> / <see cref="Save"/> / <see cref="Clear"/> 三个方法，
/// 是因为调用方（<c>DownloadTaskManager</c>、自检）只关心"存/取一整个列表"这件事；
/// 换成接口注入会把它们一起牵动，而"任务列表只有一个存储"这个前提没变。
///
/// 为什么要把整个任务列表（含每一集的进度与产物路径）都存下来：
/// 关掉程序再打开时，用户不需要重新贴地址、重新下已经下好的集；
/// 未完成的集靠暂存目录里的分片 + 清单指纹继续下（见 StagingManifest）。
/// </summary>
public sealed class TaskStore {
    private readonly SqliteTaskStore _store;

    /// <summary>用数据目录里的统一库</summary>
    public TaskStore() : this(SqliteDatabase.Default) { }

    /// <summary>指定库文件路径 —— 自检与一次性诊断用</summary>
    public TaskStore(string filePath) : this(new SqliteDatabase(filePath)) { }

    /// <summary>指定库实例</summary>
    public TaskStore(SqliteDatabase database) => _store = new SqliteTaskStore(database);

    /// <summary>存放任务列表的目录（= 数据目录）</summary>
    public static string DefaultDirectory => AppPaths.DataDirectory;

    /// <summary>数据文件（就是那个库）</summary>
    public static string DefaultFilePath => AppPaths.DatabaseFile;

    /// <summary>库文件路径</summary>
    public string FilePath => _store.FilePath;

    /// <summary>
    /// 读取任务记录。任何异常都返回空表 —— 任务列表坏了也不该让程序起不来。
    /// </summary>
    public List<SeriesTaskRecord> Load() => _store.Load();

    /// <summary>整体写入（一个事务）；返回是否成功（失败不抛，由调用方决定是否提示）</summary>
    public bool Save(IEnumerable<SeriesTaskRecord> records) => _store.Save(records);

    /// <summary>清空任务列表（用户点「清理已完成」并把列表清空时用）</summary>
    public void Clear() => _store.Clear();
}
