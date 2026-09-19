namespace M3U8Downloader.Core.Sites;

/// <summary>站点类型（对应一套 CMS 模板 / 一类页面结构）</summary>
public enum SiteKind {
    Unknown = 0,

    /// <summary>苹果 CMS（MacCMS v10）及其衍生模板。国内影视站占比最高。</summary>
    MacCms,

    /// <summary>努努影院（nnyy.in）自研站点：集号在 ep_slug，直链走 /_gp/{剧ID}/{ep_slug} 接口</summary>
    Nnyy,

    /// <summary>通用解析（未适配站点的兜底）：直接从页面里找 m3u8 与分集链接</summary>
    Generic,
}

/// <summary>页面上的一个播放源（MacCMS 里对应 URL 中的 sid）。同一部剧常有多个源。</summary>
public sealed class SitePlaySource {
    public required int Id { get; init; }

    /// <summary>源名称，如「360播放器」。能从 playerconfig.js 查到，取不到就留空。</summary>
    public string Name { get; set; } = "";

    public List<SiteEpisode> Episodes { get; } = new();

    public override string ToString() =>
        string.IsNullOrEmpty(Name) ? $"源{Id}（{Episodes.Count}集）" : $"{Name}（{Episodes.Count}集）";
}

/// <summary>一集</summary>
public sealed class SiteEpisode {
    /// <summary>集号（1 基）</summary>
    public required int Number { get; init; }

    /// <summary>所属播放源（sid）</summary>
    public required int SourceId { get; init; }

    /// <summary>播放页绝对地址</summary>
    public required string PageUrl { get; init; }

    /// <summary>显示名，如「第01集」。取不到时按集号生成。</summary>
    public string Title { get; set; } = "";

    /// <summary>m3u8 直链。列表页往往已含当前集，其余在下载前逐集解析。</summary>
    public string? PlaylistUrl { get; set; }

    /// <summary>
    /// 站点自定义的集标识（可选）。
    /// 有些站点的集**没有独立页面地址**，而是靠一个 slug 调接口取流
    /// （努努影院的 <c>ep_slug="ep12"</c>），这时把它存这里，解析直链时要用。
    /// </summary>
    public string? Key { get; init; }

    /// <summary>是否勾选下载（供 UI 确认列表使用）</summary>
    public bool IsSelected { get; set; } = true;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? $"第{Number:00}集" : Title;

    public override string ToString() => $"{DisplayTitle}  {PlaylistUrl ?? PageUrl}";
}

/// <summary>「站点识别 + 剧集解析」的结果</summary>
public sealed class SiteSeries {
    public required SiteKind Kind { get; init; }

    /// <summary>站点名，取自 &lt;title&gt; 或 host</summary>
    public required string SiteName { get; init; }

    /// <summary>用户输入的页面地址（已规范化为播放页）</summary>
    public required string PageUrl { get; init; }

    /// <summary>站点内的剧集 ID（MacCMS 的 vod_id）</summary>
    public required string SeriesId { get; init; }

    /// <summary>
    /// 用户所给页面所属的播放源（MacCMS 的 sid）。
    /// 多源弹窗用它作为默认选中项；null = 未能判定。
    /// </summary>
    public int? PreferredSourceId { get; set; }

    /// <summary>剧名</summary>
    public string Title { get; set; } = "";

    public string? CoverUrl { get; set; }
    public string? Category { get; set; }

    public List<SitePlaySource> Sources { get; } = new();

    /// <summary>建议附加的请求头（Referer / Origin / User-Agent），批量下载时透传给下载引擎</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>解析过程日志，便于排障</summary>
    public List<string> Log { get; } = new();

    public int TotalEpisodes => Sources.Sum(s => s.Episodes.Count);

    public IEnumerable<SiteEpisode> AllEpisodes => Sources.SelectMany(s => s.Episodes);

    public IEnumerable<SiteEpisode> SelectedEpisodes => AllEpisodes.Where(e => e.IsSelected);

    public override string ToString() =>
        $"{Title}（{SiteName} / {Kind}）共 {TotalEpisodes} 集，{Sources.Count} 个播放源";
}
