using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Sites;
namespace M3U8Downloader.Core.Tasks;

/// <summary>下载任务状态</summary>
public enum SeriesTaskState {
    Queued,
    Running,

    /// <summary>
    /// 用户主动「暂停」。与 <see cref="Canceled"/> 的区别只在语义：
    /// 两者都会停下当前下载并保留暂存目录，但暂停后界面上给的是「继续下载」，
    /// 而不是像取消那样让用户以为这一趟白干了。
    /// </summary>
    Paused,

    Completed,
    PartiallyCompleted,
    Failed,
    Canceled,
}

/// <summary>任务里的一集（可绑定，实时显示该集进度）</summary>
public sealed class TaskEpisodeItem : INotifyPropertyChanged {
    public required int Number { get; init; }
    public required string Title { get; init; }

    /// <summary>
    /// 原始状态枚举。界面绑定的是 <see cref="StatusText"/>；
    /// 这个字段用于存盘与「继续下载」时判断哪几集还没下完。
    /// </summary>
    public EpisodeDownloadStatus State { get; set; } = EpisodeDownloadStatus.Pending;

    /// <summary>产物路径（完成后才有）—— 续传时据此确认这一集还在磁盘上</summary>
    public string? OutputPath { get; set; }

    private string _statusText = "等待中";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private double _percent;
    public double Percent {
        get => _percent;
        set { if (Set(ref _percent, value)) OnPropertyChanged(nameof(PercentText)); }
    }

    private long _bytes;
    public long Bytes {
        get => _bytes;
        set { if (Set(ref _bytes, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private string? _error;
    public string? Error {
        get => _error;
        set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    /// <summary>已下完的分片数（下载中才有意义）</summary>
    private int _completedSegments;
    public int CompletedSegments {
        get => _completedSegments;
        set { if (Set(ref _completedSegments, value)) OnPropertyChanged(nameof(SegmentText)); }
    }

    /// <summary>分片总数（播放列表还没解析出来时为 0）</summary>
    private int _totalSegments;
    public int TotalSegments {
        get => _totalSegments;
        set { if (Set(ref _totalSegments, value)) OnPropertyChanged(nameof(SegmentText)); }
    }

    /// <summary>有错误信息时界面才显示那一行红字</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public string PercentText => $"{_percent:0.0}%";
    public string SizeText => _bytes > 0 ? $"{_bytes / 1024.0 / 1024.0:0.0} MB" : "";

    /// <summary>
    /// 形如 "183/185 片"。一集动辄几百上千片，百分比会长时间停在同一个数上，
    /// 分片计数才是"确实在动"的直观证据；还没解析出分片总数时显示已下片数。
    /// </summary>
    public string SegmentText => _totalSegments > 0
        ? $"{_completedSegments}/{_totalSegments} 片"
        : _completedSegments > 0 ? $"{_completedSegments} 片" : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 「补转 MP4」的结果。
/// </summary>
/// <param name="Converted">成功转了几集</param>
/// <param name="Skipped">跳过几集（已经是 MP4，或产物文件已不在）</param>
/// <param name="Failed">失败几集</param>
/// <param name="Messages">逐集说明（失败原因、原文件删不掉之类）</param>
public sealed record Mp4RemuxOutcome(int Converted, int Skipped, int Failed, List<string> Messages) {
    /// <summary>一句话总结，直接给用户看</summary>
    public string Describe() {
        if (Converted == 0 && Failed == 0)
            return "没有需要转换的集（都已经在是 MP4，或产物文件已不在）。";

        var text = $"已转好 {Converted} 集";
        if (Failed > 0) text += $"，{Failed} 集失败";
        return text + "。";
    }

    /// <summary>是否需要提醒用户注意（有失败）</summary>
    public bool HasProblem => Failed > 0 || Messages.Count > 0;
}

/// <summary>「续下更新」勾选列表里的一项</summary>
/// <param name="Number">集号</param>
/// <param name="Title">显示名</param>
/// <param name="IsNew">true = 站点上刚更新出来、任务里还没有的集</param>
/// <param name="Note">给用户看的短标签，如「已下载」「新增」「上次失败」</param>
/// <param name="IsDownloaded">
/// 产物文件已经在磁盘上 —— 界面据此把这一项置灰、不许勾。
/// 判据是**磁盘上的文件**而不是任务记录：记录会被清理，文件不会。
/// </param>
/// <param name="FilePath">已下载时的产物路径</param>
public sealed record FetchNewCandidate(
    int Number, string Title, bool IsNew, string Note, bool IsDownloaded, string? FilePath);

/// <summary>
/// 「续下更新」的预检结果。
///
/// 存在的理由：站点更新了几集、用户想补哪几集，只有用户自己知道 ——
/// 一路全下会把用户没要的集也拖下来。所以先预检、把**全部集**列出来给用户看，
/// 已经下过的置灰，剩下的由他勾（选中的集号再交给
/// <see cref="DownloadTaskManager.ResumeAsync"/>）。
///
/// <see cref="Parsed"/> 一并带回来是为了省掉第二次页面请求。
/// </summary>
public sealed class FetchNewPreview {
    /// <summary>这次预检重新解析出来的站点数据（挑集的几秒里站点不会变，可直接复用）</summary>
    public required SiteSeries Parsed { get; init; }

    /// <summary>选定的播放源 id</summary>
    public required int? SourceId { get; init; }

    /// <summary>产物目录（用来扫「哪几集已经在磁盘上」的那个目录）</summary>
    public required string Directory { get; init; }

    /// <summary>站点上该源的全部集：已下载的 + 待补的，按集号升序</summary>
    public required List<FetchNewCandidate> Candidates { get; init; }

    /// <summary>磁盘上已经有文件的集数（界面里置灰的那些）</summary>
    public int DownloadedCount => Candidates.Count(c => c.IsDownloaded);

    /// <summary>还需要补下的集数</summary>
    public int PendingCount => Candidates.Count(c => !c.IsDownloaded);

    /// <summary>其中站点上新更新、且还没下载的集数</summary>
    public int NewCount => Candidates.Count(c => c.IsNew && !c.IsDownloaded);

    /// <summary>
    /// 这份清单是不是刚从站点抓回来的。
    /// false = 用的任务里保存的**本地快照**（站点解析不了时的降级路径）。
    /// </summary>
    public bool Refreshed { get; init; } = true;

    /// <summary>没能刷新成功时的原因（界面据此说明"列表可能不是最新的"）</summary>
    public string? RefreshError { get; init; }

    /// <summary>这份清单依据的快照时间</summary>
    public DateTimeOffset? SnapshotAt { get; init; }
}

/// <summary>刷新集快照的结果：新发现的集（界面增量补进勾选框）+ 站点现在的总集数</summary>
/// <param name="NewCandidates">快照里原来没有的集（已核对过磁盘）</param>
/// <param name="TotalOnSite">站点上该播放源现在的总集数</param>
/// <param name="RefreshedAt">刷新时间</param>
public sealed record SnapshotRefreshResult(
    List<FetchNewCandidate> NewCandidates, int TotalOnSite, DateTimeOffset RefreshedAt);

/// <summary>
/// 一个「整部剧」下载任务。
/// 实现 INotifyPropertyChanged，界面可以直接绑定，不必再包一层。
/// </summary>
public sealed class SeriesTask : INotifyPropertyChanged {
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    public required string Title { get; init; }
    public required string SiteName { get; init; }
    public required string PageUrl { get; init; }
    public required string OutputDirectory { get; init; }

    /// <summary>
    /// 这批视频**实际**会落进哪个目录（= 输出目录 + 「剧名 - 站点」子目录）。
    ///
    /// 界面上显示的是它，而不是 <see cref="OutputDirectory"/>：用户填的往往只是「下载」
    /// 这种根目录，看不出东西到底下到哪个文件夹里。每开一轮下载都会重算一次
    /// （站点子目录名可能随解析结果变）。
    /// </summary>
    public string? ResolvedDirectory {
        get => _resolvedDirectory;
        set {
            if (Set(ref _resolvedDirectory, value)) OnPropertyChanged(nameof(DisplayDirectory));
        }
    }

    private string? _resolvedDirectory;

    /// <summary>界面显示用的目录（还没算出来时退回用户填的根目录）</summary>
    public string DisplayDirectory =>
        string.IsNullOrWhiteSpace(_resolvedDirectory) ? OutputDirectory : _resolvedDirectory!;
    public int FirstEpisodeNumber { get; init; } = 1;

    private int _totalEpisodes;

    /// <summary>
    /// 任务包含的集数。
    /// 可写：「续下更新」往任务里追加了新集之后要跟着涨，
    /// 否则界面会出现「26/24 集」、进度分母也还是旧数。
    /// 不落盘 —— 恢复时按 Episodes 数量重算（见 <see cref="FromRecord"/>）。
    /// </summary>
    public int TotalEpisodes {
        get => _totalEpisodes;
        set {
            if (!Set(ref _totalEpisodes, value)) return;
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(EpisodeCountText));
            OnPropertyChanged(nameof(EpisodeHeaderText));
        }
    }

    /// <summary>使用的播放源名称</summary>
    public string? SourceName { get; init; }

    /// <summary>
    /// 播放源 id —— 续传时用它重新选中同一个源。
    /// 可写（非 init）：重新解析后站点换了源 id 时，调用方需要能改掉它。
    /// </summary>
    public int? PreferredSourceId { get; set; }

    /// <summary>分集清单（实时更新每一集的进度）</summary>
    public ObservableCollection<TaskEpisodeItem> Episodes { get; } = new();

    /// <summary>
    /// 站点快照：播放源信息 + 站点上**全部集**的元数据（不只本任务要下的那些）。
    ///
    /// 首轮入队时就填好；「续下」优先用它 —— 不必联网就能列出全部集、
    /// 判断哪些已经在磁盘上，站点解析失败时照样能续下。
    /// <see cref="DownloadTaskManager.RefreshEpisodesSnapshotAsync"/> 会把它刷成最新的。
    /// </summary>
    public SeriesSnapshot? Snapshot { get; internal set; }

    /// <summary>
    /// 这一轮的集数据是不是来自本地快照。
    /// 是的话，收尾时若有集失败就顺手刷新一次快照 —— 很可能是站点改版让播放页
    /// 地址失效了，刷完用户点「重试失败集」就能用上新地址（不落盘）。
    /// </summary>
    internal bool RoundUsedSnapshot { get; set; }

    // ---------------- 可变状态 ----------------

    private SeriesTaskState _state = SeriesTaskState.Queued;
    public SeriesTaskState State {
        get => _state;
        internal set {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(IsRoundFinished));
            OnPropertyChanged(nameof(NeedsRetry));
            OnPropertyChanged(nameof(CanResume));
            OnPropertyChanged(nameof(CanFetchNewEpisodes));
        }
    }

    private int _finishedEpisodes;
    public int FinishedEpisodes { get => _finishedEpisodes; internal set => Set(ref _finishedEpisodes, value); }

    private int _succeededEpisodes;
    public int SucceededEpisodes { get => _succeededEpisodes; internal set => Set(ref _succeededEpisodes, value); }

    private int _skippedAdSegments;

    /// <summary>
    /// 本次下载自动跳过的插播广告分片数。
    ///
    /// 单独记下来并显示给用户，是因为「少下了几十秒」这件事用户自己**察觉不到**：
    /// 不做声的话这个功能等于白做 —— 用户只会觉得"怎么短了一截"，
    /// 甚至怀疑是下载丢了内容。显式说出来，它才是一个用户能感知到的价值。
    /// </summary>
    public int SkippedAdSegments {
        get => _skippedAdSegments;
        internal set {
            if (!Set(ref _skippedAdSegments, value)) return;
            OnPropertyChanged(nameof(HasSkippedAds));
            OnPropertyChanged(nameof(SkippedAdsText));
        }
    }

    /// <summary>跳过了广告才显示那一行提示</summary>
    public bool HasSkippedAds => _skippedAdSegments > 0;

    /// <summary>卡片上那一行提示 —— 只报数量，不解释原理</summary>
    public string SkippedAdsText => _skippedAdSegments > 0
        ? $"已过滤 {_skippedAdSegments} 个广告分片"
        : "";

    private int _failedEpisodes;
    public int FailedEpisodes {
        get => _failedEpisodes;
        internal set {
            if (!Set(ref _failedEpisodes, value)) return;
            OnPropertyChanged(nameof(NeedsRetry));
        }
    }

    private double _percent;
    public double Percent { get => _percent; internal set => Set(ref _percent, value); }

    private long _downloadedBytes;
    public long DownloadedBytes {
        get => _downloadedBytes;
        internal set { if (Set(ref _downloadedBytes, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private double _speed;
    public double SpeedBytesPerSecond {
        get => _speed;
        internal set {
            if (!Set(ref _speed, value)) return;
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(DownloadStatusText));
        }
    }

    /// <summary>
    /// 上传速度。本程序自身不上传任何数据，因此恒为 0；
    /// 保留该字段是为了让状态栏的「↓ / ↑」显示格式完整。
    /// </summary>
    public double UploadBytesPerSecond => 0;

    private string? _currentEpisode;
    public string? CurrentEpisode {
        get => _currentEpisode;
        internal set {
            if (!Set(ref _currentEpisode, value)) return;
            OnPropertyChanged(nameof(CurrentEpisodeText));
            OnPropertyChanged(nameof(MessageText));
        }
    }

    /// <summary>
    /// 当前集的细化进度，形如 "62% · 183/185 片"。
    /// 一集上千个分片时，百分比会长时间停在同一个数字上，分片计数才是"确实在动"的证据。
    /// </summary>
    private string _currentDetail = "";
    internal string CurrentDetail {
        get => _currentDetail;
        set {
            if (!Set(ref _currentDetail, value)) return;
            OnPropertyChanged(nameof(CurrentEpisodeText));
            OnPropertyChanged(nameof(MessageText));
        }
    }

    private string? _message;
    /// <summary>状态说明文字。界面也会用它显示临时反馈（如"报告文件不存在"），因此公开可写。</summary>
    public string? Message {
        get => _message;
        set {
            if (!Set(ref _message, value)) return;
            OnPropertyChanged(nameof(MessageText));
            OnPropertyChanged(nameof(DownloadStatusText));
        }
    }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    private DateTimeOffset? _finishedAt;
    public DateTimeOffset? FinishedAt { get => _finishedAt; internal set => Set(ref _finishedAt, value); }

    private TimeSpan _elapsed;
    public TimeSpan Elapsed {
        get => _elapsed;
        internal set {
            if (!Set(ref _elapsed, value)) return;
            OnPropertyChanged(nameof(ElapsedText));
            OnPropertyChanged(nameof(DownloadStatusText));
        }
    }

    private string? _reportPath;
    public string? ReportPath {
        get => _reportPath;
        internal set { if (Set(ref _reportPath, value)) OnPropertyChanged(nameof(HasReport)); }
    }

    /// <summary>完整结果（完成后可查看明细）</summary>
    public SeriesDownloadReport? Report { get; internal set; }

    // ---------------- 供界面绑定 ----------------

    public bool HasReport => !string.IsNullOrWhiteSpace(_reportPath);
    public bool IsRunning => _state == SeriesTaskState.Running;
    public bool IsFinished => _state is SeriesTaskState.Completed or SeriesTaskState.PartiallyCompleted
        or SeriesTaskState.Failed or SeriesTaskState.Canceled or SeriesTaskState.Paused;

    /// <summary>
    /// 这一轮是不是**真的跑完了**：完成 / 部分完成 / 失败。
    ///
    /// 与 <see cref="IsFinished"/> 的区别：**暂停与取消是用户主动打断，不算跑完**。
    /// 「已自动过滤 N 个广告」这类完成提示必须用这个判据 —— 用 IsFinished 的话，
    /// 用户一点暂停就弹窗，像是程序在催他什么（实机反馈过这个问题）。
    /// </summary>
    public bool IsRoundFinished => _state is SeriesTaskState.Completed
        or SeriesTaskState.PartiallyCompleted or SeriesTaskState.Failed;
    public bool CanCancel => _state is SeriesTaskState.Queued or SeriesTaskState.Running;

    /// <summary>可以「暂停」：任务正在跑（或还在排队）</summary>
    public bool CanPause => _state is SeriesTaskState.Queued or SeriesTaskState.Running;

    /// <summary>是否值得提供「重试失败集」（有失败且已结束）</summary>
    public bool NeedsRetry => IsFinished && _failedEpisodes > 0;

    /// <summary>
    /// 失败集的真实数量。暂停时 <see cref="FailedEpisodes"/> 会被界面清零
    /// （见 <see cref="DownloadTaskManager"/> 里的说明），这里留着原值。
    /// </summary>
    internal int HiddenFailedEpisodes { get; set; }

    /// <summary>
    /// 是否值得提供「继续下载」：任务已停下，且还有没下完的集。
    /// 关掉程序再打开时会用到它 —— 恢复出来的任务就停在这一档上。
    /// </summary>
    public bool CanResume => IsFinished && Episodes.Any(e => e.State != EpisodeDownloadStatus.Completed);

    /// <summary>
    /// 是否值得提供「续下更新」：任务已停下就可以 —— 哪怕上次的集全下完了，
    /// 站点也可能刚更新出新的一集（热播剧每天补更的场景）。
    /// 与 <see cref="CanResume"/> 互不包含：全下完时 CanResume=false，这里仍然 true。
    /// </summary>
    public bool CanFetchNewEpisodes => IsFinished;

    public string StateText => _state switch {
        SeriesTaskState.Queued => "排队中",
        SeriesTaskState.Running => "下载中",
        SeriesTaskState.Paused => "已暂停",
        SeriesTaskState.Completed => "已完成",
        SeriesTaskState.PartiallyCompleted => "部分完成",
        SeriesTaskState.Failed => "失败",
        SeriesTaskState.Canceled => "已取消",
        _ => _state.ToString(),
    };

    public string SpeedText => FormatSpeed(_speed);
    public string ElapsedText => _elapsed > TimeSpan.Zero ? _elapsed.ToString(@"hh\:mm\:ss") : "";
    public string SizeText => _downloadedBytes > 0 ? $"{_downloadedBytes / 1024.0 / 1024.0:0.0} MB" : "";

    /// <summary>形如 "6/16 集 · 失败 1 · 12.3 MB/s · 1.2 GB"</summary>
    public string ProgressText {
        get {
            var parts = new List<string> { $"{_finishedEpisodes}/{TotalEpisodes} 集" };
            if (_failedEpisodes > 0) parts.Add($"失败 {_failedEpisodes}");
            if (_speed > 0 && IsRunning) parts.Add(SpeedText);
            if (!string.IsNullOrWhiteSpace(SizeText)) parts.Add(SizeText);
            if (_state == SeriesTaskState.Paused) parts.Add("已暂停");
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    /// <summary>形如 "正在下载 第03集 · 62% · 183/185 片"</summary>
    public string CurrentEpisodeText {
        get {
            if (!IsRunning || string.IsNullOrWhiteSpace(_currentEpisode)) return "";
            return string.IsNullOrWhiteSpace(_currentDetail)
                ? $"正在下载 {_currentEpisode}"
                : $"正在下载 {_currentEpisode} · {_currentDetail}";
        }
    }

    /// <summary>
    /// 任务行右侧的说明文字：正在下载时把「当前是第几集」和状态说明拼成一句。
    /// 界面上这一行横向位置紧张，所以合成一个 TextBlock 显示。
    /// </summary>
    public string MessageText {
        get {
            var current = CurrentEpisodeText;
            if (string.IsNullOrWhiteSpace(current)) return _message ?? "";
            if (string.IsNullOrWhiteSpace(_message)) return current;
            return $"{current} · {_message}";
        }
    }

    /// <summary>形如 "1/21 集" —— 已完成 / 总数</summary>
    public string EpisodeCountText => $"{_finishedEpisodes}/{TotalEpisodes} 集";

    /// <summary>分集清单的标题，形如 "分集进度（6/16）"（自检与控制台输出在用）</summary>
    public string EpisodeHeaderText => $"分集进度（{_finishedEpisodes}/{TotalEpisodes}）";

    /// <summary>
    /// 任务行右侧那一句：下载中优先显示速度与耗时（进度本身由进度条表达），
    /// 停下来之后显示说明文字（为什么停的、下一步该干什么）。
    /// </summary>
    public string DownloadStatusText {
        get {
            if (!IsRunning) return MessageText;

            var parts = new List<string> { SpeedText };
            if (!string.IsNullOrWhiteSpace(ElapsedText)) parts.Add(ElapsedText);
            if (!string.IsNullOrWhiteSpace(SizeText)) parts.Add(SizeText);
            return string.Join(" · ", parts);
        }
    }

    public static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
        > 0 => $"{bytesPerSecond:0} B/s",
        _ => "0 B/s",
    };

    // 组合文本依赖多个字段，统一在这里刷新
    internal void RaiseTexts() {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CurrentEpisodeText));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(DownloadStatusText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(EpisodeHeaderText));
        OnPropertyChanged(nameof(EpisodeCountText));
    }
    /// <summary>按快照同步分集清单（只在 UI 线程调用）</summary>
    internal void SyncEpisodes(List<EpisodeProgressSnapshot> snapshots) {
        foreach (var s in snapshots) {
            var item = Episodes.FirstOrDefault(e => e.Number == s.Number);
            if (item is null) continue;

            if (!string.Equals(item.StatusText, s.StatusText, StringComparison.Ordinal))
                item.StatusText = s.StatusText;

            if (item.State != s.Status) item.State = s.Status;

            if (!string.IsNullOrWhiteSpace(s.OutputPath)) item.OutputPath = s.OutputPath;

            if (Math.Abs(item.Percent - s.Percent) > 0.05)
                item.Percent = s.Percent;

            if (item.CompletedSegments != s.CompletedSegments)
                item.CompletedSegments = s.CompletedSegments;

            if (item.TotalSegments != s.TotalSegments)
                item.TotalSegments = s.TotalSegments;

            if (s.OutputBytes > 0 && item.Bytes != s.OutputBytes)
                item.Bytes = s.OutputBytes;

            if (!string.IsNullOrWhiteSpace(s.Error) && item.Error != s.Error)
                item.Error = s.Error;
        }

        OnPropertyChanged(nameof(CanResume));
    }

    // ---------------- 测试 / 布局预览用的状态注入 ----------------

    /// <summary>
    /// 直接摆出任务的整体状态（不经过下载器）。
    ///
    /// 存在的理由：任务的各种状态平时只有 <see cref="DownloadTaskManager"/> 能写，
    /// 而「界面长什么样」这件事必须能脱离真实下载来验证 —— 自检要造出
    /// 「部分完成 / 已暂停 / 失败」这些状态，界面的布局预览也要造几条假任务。
    /// 正式运行路径不会调用它。
    /// </summary>
    public void ApplySimulatedProgress(int percent, int finished, int succeeded, int failed,
        long bytes, TimeSpan elapsed, string? message = null, string? currentEpisode = null,
        double speedBytesPerSecond = 0) {
        Percent = percent;
        FinishedEpisodes = finished;
        SucceededEpisodes = succeeded;
        FailedEpisodes = failed;
        DownloadedBytes = bytes;
        Elapsed = elapsed;
        CurrentEpisode = currentEpisode;
        SpeedBytesPerSecond = speedBytesPerSecond;
        if (message is not null) Message = message;
        RaiseTexts();
    }

    /// <summary>
    /// 强制任务状态，仅供自检与界面布局预览使用（见 <see cref="ApplySimulatedProgress"/>）。
    /// </summary>
    public void ApplySimulatedState(SeriesTaskState state, string? message = null) {
        State = state;
        if (message is not null) Message = message;
        RaiseTexts();
    }

    // ---------------- 内部：运行所需的数据 ----------------

    /// <summary>
    /// 剧集信息（含勾选状态）。
    /// 「这一轮到底会下哪几集」完全由它的 <see cref="SiteSeries.SelectedEpisodes"/> 决定，
    /// 所以做成只读公开的 —— 自检据此断言"续传只下原本选中的集"。
    /// </summary>
    internal SiteSeries? Series { get; set; }

    /// <summary>只读地看一眼这一轮会下哪几集（界面/自检用，改不了它）</summary>
    public IReadOnlyList<int> PlannedEpisodeNumbers =>
        Series is null ? Array.Empty<int>() : Series.SelectedEpisodes.Select(e => e.Number).ToList();

    /// <summary>
    /// 这一轮选中项的明细，形如 <c>sid1#12</c>。
    /// 多源站点里同一集号可能在多个源上出现，只比较集号看不出"下了两遍"，
    /// 自检就是靠它发现"计划 2 集（12,12）"的。
    /// </summary>
    public IReadOnlyList<string> PlannedSelection =>
        Series is null
            ? Array.Empty<string>()
            : Series.SelectedEpisodes.Select(e => $"sid{e.SourceId}#{e.Number}").ToList();

    /// <summary>这一轮手上那份剧集数据的规模，形如 <c>2 源/42 集</c>（自检排障用）</summary>
    public string PlannedSeriesSummary => Series is null
        ? "(无)"
        : $"{Series.Sources.Count} 源/{Series.TotalEpisodes} 集/选中 {Series.SelectedEpisodes.Count()}" +
          $" [{string.Join(",", Series.Sources.Select(s => $"sid{s.Id}:{s.Episodes.Count}集,选{s.Episodes.Count(x => x.IsSelected)}"))}]";

    /// <summary>下载参数</summary>
    internal SeriesDownloadOptions? Options { get; set; }

    // ---------------- 下载参数（续传时按原样重建） ----------------

    internal int EpisodeConcurrency { get; set; } = 2;
    internal int SegmentConcurrency { get; set; } = 16;
    internal int? PreferHeight { get; set; }
    internal string? FfmpegPath { get; set; }
    internal string FileNamePattern { get; set; } = "{title}.{number:00}";
    internal bool SeriesSubdirectory { get; set; } = true;

    /// <summary>自动跳过疑似插播广告分片 —— 「重试失败集」要按原样沿用</summary>
    internal bool AutoSkipInvalidSegments { get; set; } = true;

    /// <summary>下载完成后是否对产物做全量解码检查 —— 续传/重试都要按原样沿用</summary>
    internal bool FullDecodeCheck { get; set; } = true;


    /// <summary>
    /// 用于取消本任务。**可替换**：暂停/取消会把当前这个取消标记用完，
    /// 再次下载（继续/重试）必须换一个全新的，否则新任务一启动就立刻被判取消。
    /// 读写都在 <see cref="DownloadTaskManager"/> 内部，不要在界面线程直接碰。
    /// </summary>
    internal CancellationTokenSource Cts { get; set; } = new();

    /// <summary>本次停止是用户点了「暂停」（true）还是「取消」（false）</summary>
    internal bool PauseRequested { get; set; }

    public override string ToString() => $"[{StateText}] {Title} {ProgressText}";

    // ---------------- INotifyPropertyChanged ----------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 下载任务队列。
///
/// 设计取舍：
/// - **串行执行**：一次只跑一部剧。每部剧内部已有「集并发 × 分片并发」，
///   再叠多任务并发会把连接数打爆，也会让进度难以理解；
/// - 任务先入队再执行，界面立刻能看到它，不会阻塞页面；
/// - 单集失败不影响其它集；全部结束后给出报告。
///
/// ⚠️ 线程约定：<see cref="SeriesTask"/> 的属性会被界面绑定，因此**所有状态写入都必须
/// 回到 UI 线程**。WinUI 里从后台线程修改绑定属性不会刷新界面（表现就是"卡在下载中"），
/// 所以这里统一通过 <c>uiInvoker</c> 封送。
/// </summary>
public sealed class DownloadTaskManager : IDisposable {
    private readonly SeriesDownloader _downloader;
    private readonly Action<Action>? _uiInvoker;
    private readonly TaskStore? _store;
    private readonly object _gate = new();
    private bool _workerRunning;
    private bool _disposed;
    private int _saveQueued;
    private int _restoring;

    /// <summary>
    /// 待执行队列（在 <see cref="_gate"/> 保护下访问）。
    /// 不用 <see cref="Tasks"/> 当队列，因为后者是绑定到界面的 ObservableCollection，
    /// 只允许 UI 线程碰；后台 worker 去遍历它会和界面插入/删除撞车。
    /// </summary>
    private readonly List<SeriesTask> _pending = new();

    /// <param name="downloader">下载器；留空则自建</param>
    /// <param name="uiInvoker">
    /// 把动作封送到 UI 线程（例如 <c>a =&gt; DispatcherQueue.TryEnqueue(() =&gt; a())</c>）。
    /// 命令行/无界面测试传 null 即可直接执行。
    /// </param>
    /// <param name="store">
    /// 任务列表的落盘仓库。传了就支持「关掉程序再打开继续下」；
    /// 传 null 则任务只活在内存里（自检/命令行用）。
    /// </param>
    public DownloadTaskManager(SeriesDownloader? downloader = null, Action<Action>? uiInvoker = null,
        TaskStore? store = null) {
        _downloader = downloader ?? new SeriesDownloader();
        _uiInvoker = uiInvoker;
        _store = store;
    }

    /// <summary>全部任务（最新的在最前）</summary>
    public ObservableCollection<SeriesTask> Tasks { get; } = new();

    /// <summary>
    /// 下载历史（哪部剧的哪一集下过）。默认不记账；图形界面启动时注入 SQLite 实现。
    ///
    /// 它是"下过没有"的**辅助**依据 —— 权威依据始终是磁盘上的文件
    /// （见 <see cref="EpisodeFileScanner"/>）。历史的价值在于文件被改名/搬走后
    /// 还能说清"这集当初下过"，以及任务列表被清理后仍留有账。
    /// </summary>
    public IDownloadHistoryStore History { get; set; } = NullDownloadHistoryStore.Instance;

    /// <summary>任务状态有任何变化时触发（供存盘/通知使用）</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 剧集解析器（恢复任务时要把页面重新解析一遍）。
    /// 默认走 <see cref="SeriesDownloader.ParseAsync"/>；
    /// 无网自检或接入别的源时可以替换掉。
    /// </summary>
    public Func<string, CancellationToken, Task<SiteSeries>>? SeriesParser { get; set; }

    private Task<SiteSeries> ParseSeriesAsync(string pageUrl, CancellationToken ct) =>
        SeriesParser is not null ? SeriesParser(pageUrl, ct) : _downloader.ParseAsync(pageUrl, ct);

    /// <summary>所有运行中任务的下载速度合计</summary>
    public double TotalDownloadSpeed => Tasks.Where(t => t.IsRunning).Sum(t => t.SpeedBytesPerSecond);

    /// <summary>
    /// 诊断日志（默认关闭）。开启后会把「哪一轮领到了哪几集、何时结束、结果如何」
    /// 记到 <c>%APPDATA%\M3U8Downloader\logs\</c>，用于排查
    /// 「暂停了还在下」「继续下载停不下来」这类只看界面判断不了的问题。
    /// 由使用方显式设置（图形界面在启动时用 <see cref="TaskDiagnostics.Create"/> 打开）。
    /// </summary>
    public TaskDiagnostics Diagnostics { get; set; } = TaskDiagnostics.Disabled;

    /// <summary>日志用：把将要下载的集号压成 "1-21" 这种紧凑形式</summary>
    private static string DescribeNumbers(IEnumerable<int> numbers) {
        var ordered = numbers.Distinct().OrderBy(n => n).ToList();
        if (ordered.Count == 0) return "(空)";
        if (ordered.Count == 1) return ordered[0].ToString();

        var parts = new List<string>();
        var start = ordered[0];
        var prev = ordered[0];

        for (var i = 1; i <= ordered.Count; i++) {
            var current = i < ordered.Count ? ordered[i] : int.MinValue;
            if (i < ordered.Count && current == prev + 1) { prev = current; continue; }

            parts.Add(start == prev ? $"{start}" : $"{start}-{prev}");
            start = prev = current;
        }

        return string.Join(",", parts);
    }

    /// <summary>上传速度合计。本程序不上传数据，恒为 0。</summary>
    public double TotalUploadSpeed => 0;

    public int RunningCount => Tasks.Count(t => t.State == SeriesTaskState.Running);
    public int QueuedCount => Tasks.Count(t => t.State == SeriesTaskState.Queued);

    private void RunOnUi(Action action) {
        if (_uiInvoker is null) { action(); return; }
        _uiInvoker(action);
    }

    /// <summary>封送到 UI 线程并等它做完（恢复任务时需要先插进列表再继续处理）</summary>
    private Task RunOnUiAsync(Action action) {
        if (_uiInvoker is null) {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        _uiInvoker(() => {
            try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>
    /// 把任务里已完成、但还不是 MP4 的产物补转成 MP4。
    ///
    /// 用途只有一个：**下载那会儿还没装 FFmpeg** —— 产物按 TS 留下了，
    /// 事后装好 FFmpeg 再想转，原来的流程里没有任何补救手段（只能重下）。
    ///
    /// 所以它是纯粹的补救操作：不重下任何东西、不改任务的下载状态，
    /// 某一集转失败也只是这一集没转成，原文件原样保留。
    /// 转成功才删原 .ts（流复制是无损的，留着白占一倍空间）。
    /// </summary>
    public async Task<Mp4RemuxOutcome> RemuxToMp4Async(
        SeriesTask task, string ffmpegPath, IProgress<string>? progress = null,
        CancellationToken ct = default) {
        // 集合必须在 UI 线程上快照：本方法跑在后台线程，直接遍历界面绑定的
        // ObservableCollection 会与之撞车（见 pitfalls 第 14 条）
        TaskEpisodeItem[] episodes = Array.Empty<TaskEpisodeItem>();
        await RunOnUiAsync(() => episodes = task.Episodes.OrderBy(e => e.Number).ToArray())
            .ConfigureAwait(false);

        var todo = episodes.Where(NeedsRemux).ToList();
        var converted = 0;
        var failed = 0;
        var messages = new List<string>();
        var renames = new List<(string Old, string New)>();

        foreach (var ep in todo) {
            ct.ThrowIfCancellationRequested();

            var src = ep.OutputPath!;
            var dst = Path.ChangeExtension(src, ".mp4");

            progress?.Report($"正在转 MP4：第 {ep.Number:00} 集（{converted + failed + 1}/{todo.Count}）");

            var r = await FfmpegRunner.RemuxToMp4Async(ffmpegPath, src, dst, ct).ConfigureAwait(false);
            if (!r.Success || !File.Exists(dst) || new FileInfo(dst).Length == 0) {
                failed++;
                messages.Add($"第 {ep.Number:00} 集转失败：{r.Error ?? "产物为空"}");
                try { if (File.Exists(dst)) File.Delete(dst); } catch { /* 半成品留着也没用，删不掉就算了 */ }
                continue;
            }

            // 转好了再删原文件：万一 MP4 有问题，至少 TS 还在
            try {
                File.Delete(src);
            } catch (Exception ex) {
                messages.Add($"第 {ep.Number:00} 集已转好，但原文件删不掉（{ex.Message}）");
            }

            await RunOnUiAsync(() => ep.OutputPath = dst).ConfigureAwait(false);
            renames.Add((Path.GetFileName(src), Path.GetFileName(dst)));
            converted++;
        }

        // 产物路径变了，得落盘 —— 否则下次「继续下载」会以为这一集的文件不见了
        if (converted > 0) {
            UpdateReportAfterRemux(task, renames);
            ScheduleSave();
        }

        return new Mp4RemuxOutcome(converted, episodes.Length - todo.Count, failed, messages);
    }

    /// <summary>
    /// 补转之后把下载报告里的产物名改过来。
    ///
    /// 报告是**下载那一刻**的快照，里面写的还是 `.ts`；补转之后那个文件已经不存在了，
    /// 用户照着报告去目录里找会找不到。这里只替换文件名与「产物格式」那一行，
    /// 其余内容一个字都不动 —— 报告终究是历史记录，不该被重写成另一份东西。
    /// </summary>
    private static void UpdateReportAfterRemux(
        SeriesTask task, IReadOnlyList<(string Old, string New)> renames) {
        var path = task.ReportPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try {
            var text = File.ReadAllText(path);
            foreach (var (oldName, newName) in renames)
                text = text.Replace(oldName, newName, StringComparison.Ordinal);

            text = text.Replace("- **产物格式**：TS", "- **产物格式**：MP4", StringComparison.Ordinal);

            File.WriteAllText(path, text);
        } catch {
            // 报告改不动不影响主流程：产物本身已经转好了
        }
    }

    /// <summary>
    /// 这一集需不需要补转：已完成、产物记录还在、文件确实在磁盘上、而且还不是 MP4。
    /// 「文件确实在」这一条不能省 —— 用户可能自己删过或挪过。
    /// </summary>
    private static bool NeedsRemux(TaskEpisodeItem ep) =>
        ep.State == EpisodeDownloadStatus.Completed
        && !string.IsNullOrWhiteSpace(ep.OutputPath)
        && File.Exists(ep.OutputPath)
        && !ep.OutputPath!.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

    /// <summary>状态有变化：通知界面，并安排一次落盘（节流）</summary>
    private void RaiseChanged() {
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    // ==================== 落盘 / 恢复（断点续传） ====================

    /// <summary>
    /// 安排一次节流的落盘。进度回调每秒可能来几十次，不能每次都写文件，
    /// 但也不能拖太久 —— 否则用户在这中间关掉程序就会丢进度。
    /// </summary>
    private void ScheduleSave() {
        if (_store is null || _disposed) return;
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;

        _ = Task.Run(async () => {
            try { await Task.Delay(1200).ConfigureAwait(false); } catch { }
            Interlocked.Exchange(ref _saveQueued, 0);

            // 读 Tasks（绑定用的集合）必须在 UI 线程
            RunOnUi(SaveNow);
        });
    }

    /// <summary>
    /// 立即落盘。关窗时由界面在 UI 线程直接调用，保证「关掉程序」这一刻的进度已经写进文件。
    /// </summary>
    public void SaveNow() {
        if (_store is null) return;

        try {
            _store.Save(Tasks.Select(ToRecord).ToList());
        } catch {
            // 存盘失败不该影响下载
        }
    }

    private SeriesTaskRecord ToRecord(SeriesTask t) {
        var record = new SeriesTaskRecord {
            Id = t.Id,
            Title = t.Title,
            SiteName = t.SiteName,
            PageUrl = t.PageUrl,
            OutputDirectory = t.OutputDirectory,
            ResolvedDirectory = t.ResolvedDirectory,
            SourceName = t.SourceName,
            PreferredSourceId = t.PreferredSourceId,
            State = t.State.ToString(),
            Percent = t.Percent,
            DownloadedBytes = t.DownloadedBytes,
            ReportPath = t.ReportPath,
            SkippedAdSegments = t.SkippedAdSegments,
            Message = t.Message,
            CreatedAt = t.CreatedAt,
            FinishedAt = t.FinishedAt,
            EpisodeConcurrency = t.EpisodeConcurrency,
            SegmentConcurrency = t.SegmentConcurrency,
            PreferHeight = t.PreferHeight,
            FfmpegPath = t.FfmpegPath,
            FileNamePattern = t.FileNamePattern,
            SeriesSubdirectory = t.SeriesSubdirectory,

            // 站点快照：续下不必联网的依据（老记录里没有这个字段，读到就是 null）
            Snapshot = t.Snapshot,
        };

        foreach (var e in t.Episodes) {
            record.Episodes.Add(new TaskEpisodeRecord {
                Number = e.Number,
                Title = e.Title,
                Status = e.State.ToString(),
                Percent = e.Percent,
                Bytes = e.Bytes,
                OutputPath = e.OutputPath,
                Error = e.Error,
            });
        }

        return record;
    }

    private static SeriesTask FromRecord(SeriesTaskRecord r) {
        var ordered = r.Episodes.OrderBy(e => e.Number).ToList();

        var task = new SeriesTask {
            Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id,
            Title = r.Title,
            SiteName = r.SiteName,
            PageUrl = r.PageUrl,
            OutputDirectory = r.OutputDirectory,
            ResolvedDirectory = r.ResolvedDirectory,
            TotalEpisodes = ordered.Count,
            FirstEpisodeNumber = ordered.Count > 0 ? ordered.Min(e => e.Number) : 1,
            SourceName = r.SourceName,
            PreferredSourceId = r.PreferredSourceId,
            CreatedAt = r.CreatedAt,
            Elapsed = TimeSpan.Zero,
            EpisodeConcurrency = r.EpisodeConcurrency,
            SegmentConcurrency = r.SegmentConcurrency,
            PreferHeight = r.PreferHeight,
            FfmpegPath = r.FfmpegPath,
            FileNamePattern = r.FileNamePattern,
            SeriesSubdirectory = r.SeriesSubdirectory,
        };

        foreach (var e in ordered) {
            var state = Enum.TryParse<EpisodeDownloadStatus>(e.Status, ignoreCase: true, out var parsed)
                ? parsed
                : EpisodeDownloadStatus.Pending;

            task.Episodes.Add(new TaskEpisodeItem {
                Number = e.Number,
                Title = e.Title,
                State = state,
                StatusText = DescribeStatus(state),
                Percent = e.Percent,
                Bytes = e.Bytes,
                OutputPath = e.OutputPath,
                Error = e.Error,
            });
        }

        // 站点快照（老记录里没有 → null，第一次续下会联网解析并补上）
        task.Snapshot = r.Snapshot;

        // 上次正在下载/排队的任务，恢复后先停在「可继续」的状态上，
        // 由 RestoreAsync 决定是否自动接着下。
        // 上次是「已暂停」的保持暂停 —— 用户要求停下，就别自作主张又跑起来。
        var restoredState = r.ParsedState;
        task.State = restoredState switch {
            SeriesTaskState.Running or SeriesTaskState.Queued => SeriesTaskState.PartiallyCompleted,
            _ => restoredState,
        };

        task.Message = string.IsNullOrWhiteSpace(r.Message)
            ? "已从上次的进度恢复"
            : r.Message;
        task.Percent = r.Percent;
        task.DownloadedBytes = r.DownloadedBytes;
        task.ReportPath = r.ReportPath;
        task.SkippedAdSegments = r.SkippedAdSegments;
        task.FinishedAt = r.FinishedAt;

        var done = task.Episodes.Count(e => e.State == EpisodeDownloadStatus.Completed);
        var failed = task.Episodes.Count(e => e.State == EpisodeDownloadStatus.Failed);
        task.FinishedEpisodes = task.Episodes.Count(e =>
            e.State is EpisodeDownloadStatus.Completed or EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled);
        task.SucceededEpisodes = done;
        task.FailedEpisodes = failed;
        task.RaiseTexts();

        return task;
    }

    internal static string DescribeStatus(EpisodeDownloadStatus status) => status switch {
        EpisodeDownloadStatus.Pending => "等待中",
        EpisodeDownloadStatus.Resolving => "解析中",
        EpisodeDownloadStatus.Downloading => "下载中",
        EpisodeDownloadStatus.Completed => "已完成",
        EpisodeDownloadStatus.Failed => "失败",
        EpisodeDownloadStatus.Canceled => "已取消",
        _ => status.ToString(),
    };

    /// <summary>
    /// 从磁盘恢复任务列表，并把没下完的任务自动排队续传。
    /// 已完成的任务只恢复显示，不会重新下载。
    /// </summary>
    /// <returns>恢复的任务数</returns>
    public async Task<int> RestoreAsync(CancellationToken ct = default) {
        if (_store is null) return 0;
        if (Interlocked.Exchange(ref _restoring, 1) == 1) return 0;

        List<SeriesTask> restored = new();
        try {
            var records = _store.Load();
            if (records.Count == 0) return 0;

            await RunOnUiAsync(() => {
                // Tasks 是「最新的在最前」，所以按创建时间正序依次插到最前面
                foreach (var r in records.OrderBy(r => r.CreatedAt)) {
                    if (string.IsNullOrWhiteSpace(r.PageUrl)) continue;

                    var task = FromRecord(r);
                    Tasks.Insert(0, task);
                    restored.Add(task);
                }
            }).ConfigureAwait(false);

            if (restored.Count == 0) return 0;

            foreach (var task in restored) {
                ct.ThrowIfCancellationRequested();

                // 已完成 / 用户主动取消的，不自动接着下；
                // 「已暂停」是用户明确要求停下的，也只恢复显示，等他点「继续下载」
                if (task.State is SeriesTaskState.Completed or SeriesTaskState.Canceled
                    or SeriesTaskState.Paused) continue;
                if (!task.CanResume) continue;

                RunOnUi(() => {
                    task.Message = "正在恢复…";
                    ScheduleSave();
                });

                try {
                    await ResumeAsync(task, ct).ConfigureAwait(false);
                } catch (Exception ex) {
                    RunOnUi(() => {
                        task.Message = $"恢复失败：{ex.Message}";
                        RaiseChanged();
                    });
                }
            }

            return restored.Count;
        } finally {
            Interlocked.Exchange(ref _restoring, 0);
        }
    }

    /// <summary>
    /// 「续下更新」的预检：重新解析站点，列出该源的**全部集**并标出哪些已经下过，
    /// 供界面弹勾选框（已下载的置灰）。
    ///
    /// 这个方法**不改动任何状态**（不追加集、不入队、不写任务消息）。
    ///
    /// 「下过没有」的判据是**磁盘上的文件**，按任务的产物目录 + 文件名模板正向算出来核对，
    /// 而不是只信任务记录 —— 记录会被「清理已完成」、重装、换机器清掉，文件却还在。
    /// 任务记录与下载历史只用来解释"为什么没有"（上次失败 / 曾下载过但文件已不在）。
    ///
    /// 用户勾完把选中的集号与这里返回的 <see cref="FetchNewPreview"/> 一起交给
    /// <see cref="ResumeAsync"/>，既省掉一次页面请求，也保证"下什么"由用户决定。
    /// </summary>
    public async Task<FetchNewPreview?> PreviewFetchNewAsync(SeriesTask task, CancellationToken ct = default) {
        if (_disposed) return null;
        if (task.State == SeriesTaskState.Running) return null;

        SiteSeries parsed;
        try {
            parsed = await ParseSeriesAsync(task.PageUrl, ct).ConfigureAwait(false);
        } catch (Exception ex) {
            // 站点解析不了（断网 / 站点挂了 / 代理没开）→ 退回本地快照：
            // 至少让用户把没下完的集补上，而不是一集都下不了
            var fallback = await PreviewFromSnapshotAsync(task, ct).ConfigureAwait(false);
            if (fallback is null) throw;   // 连快照都没有：照实报错，不假装成功

            Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 站点解析失败，改用本地快照：" +
                              $"{ex.Message}");
            return new FetchNewPreview {
                Parsed = fallback.Parsed,
                SourceId = fallback.SourceId,
                Directory = fallback.Directory,
                Candidates = fallback.Candidates,
                Refreshed = false,
                RefreshError = ex.Message,
                SnapshotAt = fallback.SnapshotAt,
            };
        }

        var sourceId = task.PreferredSourceId
                       ?? parsed.Sources.FirstOrDefault(s => s.Name == task.SourceName)?.Id
                       ?? parsed.Sources.FirstOrDefault()?.Id;

        // 站点上这一源的全部集（同一集号挂在多个源上时只取一个）
        var siteEpisodes = parsed.AllEpisodes
            .Where(e => e.SourceId == sourceId)
            .GroupBy(e => e.Number)
            .Select(g => g.First())
            .OrderBy(e => e.Number)
            .ToList();

        var candidates = await BuildCandidatesAsync(task, parsed, siteEpisodes, ct).ConfigureAwait(false);

        var preview = new FetchNewPreview {
            Parsed = parsed,
            SourceId = sourceId,
            Directory = ResolveProductDirectory(task, parsed),
            Candidates = candidates,
            Refreshed = true,
            SnapshotAt = DateTimeOffset.Now,
        };

        // 顺手把快照刷新成最新的（这样下次续下可以直接用本地数据）。
        // 带上旧快照：请求头要合并，别把原来存好的 Referer 冲掉。
        SeriesSnapshot? previousSnapshot = null;
        await RunOnUiAsync(() => previousSnapshot = task.Snapshot).ConfigureAwait(false);
        await SaveEpisodesSnapshotAsync(task, parsed, sourceId, siteEpisodes, previousSnapshot)
            .ConfigureAwait(false);

        Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 续下预检（联网）：" +
                          $"目录 {preview.Directory}；磁盘已有 " +
                          $"{DescribeNumbers(candidates.Where(c => c.IsDownloaded).Select(c => c.Number))}；" +
                          $"待补 {DescribeNumbers(candidates.Where(c => !c.IsDownloaded).Select(c => c.Number))}");

        return preview;
    }

    /// <summary>
    /// 用任务里保存的站点快照算一份续下预检 —— **完全不联网**。
    ///
    /// 这是「续下」的首选路径：快照在首轮入队时就存好了（站点上全部集的集号、
    /// 标题、播放页地址、集标识，外加站点请求头），所以点一下就能立刻列出全部集。
    /// 界面拿到它先弹窗，同时在后台跑 <see cref="RefreshEpisodesSnapshotAsync"/>
    /// 把站点新更新的集补进来。
    ///
    /// 返回 null = 没有快照（老任务）或任务没停下，调用方退回联网解析。
    /// </summary>
    public async Task<FetchNewPreview?> PreviewFromSnapshotAsync(SeriesTask task, CancellationToken ct = default) {
        if (_disposed) return null;
        if (task.State == SeriesTaskState.Running) return null;

        SeriesSnapshot? snapshot = null;
        await RunOnUiAsync(() => snapshot = task.Snapshot).ConfigureAwait(false);

        if (snapshot is null || snapshot.Episodes.Count == 0) return null;

        var sourceId = snapshot.SourceId ?? snapshot.Episodes.FirstOrDefault()?.SourceId;

        // 从快照重建一份可用的 SiteSeries（含请求头）：下载流程要拿它取 Referer
        var siteEpisodes = snapshot.Episodes
            .GroupBy(m => m.Number)
            .Select(g => ToSiteEpisode(g.First()))
            .OrderBy(e => e.Number)
            .ToList();
        var series = BuildSeriesFromSnapshot(task, snapshot, siteEpisodes);

        var candidates = await BuildCandidatesAsync(task, series, siteEpisodes, ct).ConfigureAwait(false);

        var preview = new FetchNewPreview {
            Parsed = series,
            SourceId = sourceId,
            Directory = ResolveProductDirectory(task, series),
            Candidates = candidates,
            Refreshed = false,       // 数据来自本地快照
            SnapshotAt = snapshot.CapturedAt,
        };

        Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 续下预检（本地快照，" +
                          $"{snapshot.CapturedAt:MM-dd HH:mm}）：" +
                          $"待补 {DescribeNumbers(candidates.Where(c => !c.IsDownloaded).Select(c => c.Number))}");

        return preview;
    }

    /// <summary>
    /// 重新抓页面、刷新任务的站点快照，并返回**快照里原来没有的集**（新更新的）。
    ///
    /// 由界面在弹窗的同时后台调用：成功就把新集补进勾选框；失败会抛异常，
    /// 由调用方降级（继续用快照，不影响已列出的集下载）。
    /// </summary>
    public async Task<SnapshotRefreshResult> RefreshEpisodesSnapshotAsync(SeriesTask task,
        CancellationToken ct = default) {
        var parsed = await ParseSeriesAsync(task.PageUrl, ct).ConfigureAwait(false);

        var sourceId = task.PreferredSourceId
                       ?? parsed.Sources.FirstOrDefault(s => s.Name == task.SourceName)?.Id
                       ?? parsed.Sources.FirstOrDefault()?.Id;

        var siteEpisodes = parsed.AllEpisodes
            .Where(e => e.SourceId == sourceId)
            .GroupBy(e => e.Number)
            .Select(g => g.First())
            .OrderBy(e => e.Number)
            .ToList();

        HashSet<int> known = new();
        SeriesSnapshot? previous = null;
        await RunOnUiAsync(() => {
            previous = task.Snapshot;
            known = previous?.Episodes.Select(m => m.Number).ToHashSet() ?? new HashSet<int>();
        }).ConfigureAwait(false);

        var fresh = siteEpisodes.Where(e => !known.Contains(e.Number)).ToList();

        await SaveEpisodesSnapshotAsync(task, parsed, sourceId, siteEpisodes, previous).ConfigureAwait(false);

        // 新集也可能已经在磁盘上（用户自己下过/别的任务下过）—— 逐集核一下再交给界面
        var directory = ResolveProductDirectory(task, parsed);
        var candidates = new List<FetchNewCandidate>();
        foreach (var ep in fresh) {
            var path = await Task.Run(() =>
                    EpisodeFileScanner.FindEpisodeFile(directory, task.FileNamePattern, parsed, ep.Number), ct)
                .ConfigureAwait(false);

            candidates.Add(path is not null
                ? new FetchNewCandidate(ep.Number, ep.DisplayTitle, IsNew: true, Note: "已下载",
                    IsDownloaded: true, FilePath: path)
                : new FetchNewCandidate(ep.Number, ep.DisplayTitle, IsNew: true, Note: "新增",
                    IsDownloaded: false, FilePath: null));
        }

        Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 刷新集快照：" +
                          $"站点共 {siteEpisodes.Count} 集，新发现 {DescribeNumbers(fresh.Select(e => e.Number))}");

        return new SnapshotRefreshResult(candidates, siteEpisodes.Count, DateTimeOffset.Now);
    }

    /// <summary>
    /// 从"站点上的集 + 任务现状 + 磁盘 + 下载历史"算出续下候选。
    /// 联网预检与本地快照预检共用这一段 —— 两条路径的判定必须完全一致。
    /// </summary>
    private async Task<List<FetchNewCandidate>> BuildCandidatesAsync(SeriesTask task, SiteSeries series,
        IReadOnlyList<SiteEpisode> siteEpisodes, CancellationToken ct) {
        // 任务里每一集的现状。绑定用的集合只能在 UI 线程上读（pitfalls 第 14 条），
        // 先快照一份再在后台算。
        TaskEpisodeItem[] known = Array.Empty<TaskEpisodeItem>();
        await RunOnUiAsync(() => known = task.Episodes.ToArray()).ConfigureAwait(false);

        var directory = ResolveProductDirectory(task, series);

        // 扫目录是纯磁盘 IO，扔到后台别堵着调用方（界面正等这个结果弹窗）
        var onDisk = await Task.Run(() => EpisodeFileScanner.ScanExisting(
                directory, task.FileNamePattern, series, siteEpisodes.Select(e => e.Number)), ct)
            .ConfigureAwait(false);

        // 下载历史：文件被改名/搬走时，还能提醒用户"这集记录里下过"（带上当时的大小）
        Dictionary<int, DownloadHistoryEntry> history;
        try {
            history = History.FindBySeries(task.PageUrl)
                .GroupBy(h => h.EpisodeNumber)
                .ToDictionary(g => g.Key, g => g.First());
        } catch {
            history = new Dictionary<int, DownloadHistoryEntry>();
        }

        var candidates = new List<FetchNewCandidate>();
        foreach (var ep in siteEpisodes) {
            // 磁盘上就有文件 —— 这就是"下过了"，置灰不让重下
            if (onDisk.TryGetValue(ep.Number, out var path)) {
                candidates.Add(new FetchNewCandidate(ep.Number, ep.DisplayTitle,
                    IsNew: false, Note: "已下载", IsDownloaded: true, FilePath: path));
                continue;
            }

            var item = known.FirstOrDefault(x => x.Number == ep.Number);
            var isNew = item is null;
            var note = item?.State switch {
                EpisodeDownloadStatus.Failed => "上次失败",
                EpisodeDownloadStatus.Canceled => "上次取消",
                EpisodeDownloadStatus.Completed => "产物已丢失",
                null => "新增",
                _ => "未下完",
            };

            // 任务里没有（被清理了/换了机器）但历史里下过 —— 说法要准确，免得用户以为是新的，
            // 顺带报一下当初下出来多大（历史里存着，此时正是它派用场的地方）
            if (history.TryGetValue(ep.Number, out var past) && note is "新增" or "产物已丢失") {
                note = past.FileBytes > 0
                    ? $"曾下载过（{past.FileBytes / 1024.0 / 1024.0:0.0} MB），文件已不在"
                    : "曾下载过，文件已不在";
            }

            candidates.Add(new FetchNewCandidate(ep.Number, ep.DisplayTitle,
                isNew, note, IsDownloaded: false, FilePath: null));
        }

        return candidates;
    }

    /// <summary>产物目录：任务跑过就用它记下来的；没跑成过就按参数算一遍</summary>
    private static string ResolveProductDirectory(SeriesTask task, SiteSeries series) {
        var directory = task.ResolvedDirectory;
        if (!string.IsNullOrWhiteSpace(directory)) return directory;

        try {
            return SeriesDownloader.ResolveSeriesDirectory(series, BuildOptions(task));
        } catch {
            return task.OutputDirectory;
        }
    }

    /// <summary>把站点上的全部集记进任务（首轮快照）</summary>
    private void CaptureEpisodesSnapshot(SeriesTask task, SiteSeries series) {
        var sourceId = task.PreferredSourceId
                       ?? series.Sources.FirstOrDefault(s => s.Episodes.Any(e => e.IsSelected))?.Id
                       ?? series.Sources.FirstOrDefault()?.Id;

        CaptureEpisodesSnapshot(task, series, sourceId);
    }

    /// <summary>把站点上的全部集记进任务（首轮快照，指定播放源）</summary>
    private void CaptureEpisodesSnapshot(SeriesTask task, SiteSeries series, int? sourceId) {
        var episodes = series.AllEpisodes
            .Where(e => sourceId is null || e.SourceId == sourceId)
            .GroupBy(e => e.Number)
            .Select(g => g.First())
            .OrderBy(e => e.Number)
            .ToList();

        if (episodes.Count == 0) return;

        var snapshot = BuildSnapshot(series, sourceId, episodes);
        RunOnUi(() => {
            task.Snapshot = snapshot;
            ScheduleSave();
        });
    }

    /// <summary>刷新快照（用已经解析好的站点数据），并落盘</summary>
    private async Task SaveEpisodesSnapshotAsync(SeriesTask task, SiteSeries series, int? sourceId,
        IReadOnlyList<SiteEpisode> siteEpisodes, SeriesSnapshot? previous = null) {
        var snapshot = BuildSnapshot(series, sourceId, siteEpisodes, previous);

        await RunOnUiAsync(() => {
            task.Snapshot = snapshot;
            ScheduleSave();
            RaiseChanged();
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// 造一份站点快照。
    ///
    /// 请求头要**合并**而不是照抄新解析的结果：有些解析路径（通用兜底）拿不到
    /// Referer/Origin，直接覆盖就把原来存好的头冲掉了 —— 下次用快照续下时
    /// 分片请求没了 Referer，站点直接 403。所以旧头先铺底，新头再覆盖。
    /// </summary>
    private static SeriesSnapshot BuildSnapshot(SiteSeries series, int? sourceId,
        IReadOnlyList<SiteEpisode> episodes, SeriesSnapshot? previous = null) {
        var snapshot = new SeriesSnapshot {
            Kind = series.Kind.ToString(),
            SiteName = series.SiteName,
            PageUrl = series.PageUrl,
            SeriesId = series.SeriesId,
            Title = series.Title,
            SourceId = sourceId,
            CapturedAt = DateTimeOffset.Now,
            Episodes = episodes.Select(ToMetadata).ToList(),
        };

        if (previous is not null)
            foreach (var kv in previous.Headers) snapshot.Headers[kv.Key] = kv.Value;
        foreach (var kv in series.Headers) snapshot.Headers[kv.Key] = kv.Value;

        return snapshot;
    }

    private static EpisodeMetadata ToMetadata(SiteEpisode ep) => new() {
        Number = ep.Number,
        Title = string.IsNullOrWhiteSpace(ep.Title) ? ep.DisplayTitle : ep.Title,
        PageUrl = ep.PageUrl,
        Key = ep.Key,
        SourceId = ep.SourceId,
    };

    private static SiteEpisode ToSiteEpisode(EpisodeMetadata m) => new() {
        Number = m.Number,
        SourceId = m.SourceId,
        PageUrl = m.PageUrl,
        Title = m.Title,
        Key = m.Key,
    };

    /// <summary>
    /// 从站点快照重建一份可用的 <see cref="SiteSeries"/>。
    ///
    /// 两点必须照原样带回来：
    /// - **请求头**（Referer / Origin / UA）—— 下载分片与清单时要带，缺了会被站点 403；
    /// - 播放源与每集的 <c>PageUrl</c> / <c>Key</c> —— 适配器是按 PageUrl 的 host 挑的，
    ///   直链在每集下载前现解析（<c>SiteResolver.ResolvePlaylistUrlAsync</c>）。
    /// </summary>
    private static SiteSeries BuildSeriesFromSnapshot(SeriesTask task, SeriesSnapshot snapshot,
        IReadOnlyList<SiteEpisode> episodes) {
        var series = new SiteSeries {
            Kind = Enum.TryParse<SiteKind>(snapshot.Kind, ignoreCase: true, out var kind)
                ? kind
                : SiteKind.Generic,
            SiteName = string.IsNullOrWhiteSpace(snapshot.SiteName) ? task.SiteName : snapshot.SiteName,
            PageUrl = string.IsNullOrWhiteSpace(snapshot.PageUrl) ? task.PageUrl : snapshot.PageUrl,
            SeriesId = snapshot.SeriesId,
            Title = string.IsNullOrWhiteSpace(snapshot.Title) ? task.Title : snapshot.Title,
            PreferredSourceId = snapshot.SourceId,
        };

        foreach (var kv in snapshot.Headers) series.Headers[kv.Key] = kv.Value;

        var source = new SitePlaySource {
            Id = snapshot.SourceId ?? episodes.FirstOrDefault()?.SourceId ?? 0,
            Name = task.SourceName ?? "",
        };
        foreach (var ep in episodes) source.Episodes.Add(ep);
        series.Sources.Add(source);

        return series;
    }

    /// <summary>
    /// 继续下载：重新解析站点，只把「还没下完的集」排进队列。
    ///
    /// 已完成的集不会重复下载（还要确认产物文件确实还在）；
    /// 没下完的集会复用原来的暂存目录，引擎按清单指纹校验后从断点继续。
    /// </summary>
    /// <param name="task">目标任务</param>
    /// <param name="ct">取消标记</param>
    /// <param name="includeNewEpisodes">
    /// 「续下更新」：站点上比任务多出来的集（热播剧隔天补更）也一并下。
    /// 新集会先追加进 <see cref="SeriesTask.Episodes"/>（界面立刻能看到），
    /// 未完成的旧集照样续传 —— 上次下失败的那几集不会被丢掉。
    /// </param>
    /// <param name="preview">
    /// 「续下更新」预检的结果（见 <see cref="PreviewFetchNewAsync"/>）。
    /// 传进来就省掉一次页面请求；为 null 时按老路子重新解析站点。
    /// </param>
    /// <param name="onlyNumbers">
    /// 只下这些集号（用户在勾选框里挑出来的）。为 null 表示照任务里原有的集走。
    /// 续下场景下它是**必须**的：站点更新了几集、用户想补哪几集，只有用户知道。
    /// </param>
    public async Task<bool> ResumeAsync(SeriesTask task, CancellationToken ct = default,
        bool includeNewEpisodes = false, FetchNewPreview? preview = null,
        IReadOnlySet<int>? onlyNumbers = null) {
        if (_disposed) return false;
        if (task.State == SeriesTaskState.Running) return false;

        lock (_gate) {
            if (_pending.Contains(task)) return false;
        }

        // 关键：先把上一轮用掉的取消标记换新。
        // 下面「重新解析站点」用的就是这个 token —— 暂停/取消之后它已经是取消态，
        // 不换新的话续传会在解析阶段立刻抛 OperationCanceledException，
        // 界面上就变成一句莫名的 "The operation was canceled."。
        ResetStopSignal(task);
        Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 用户点了继续下载");

        // 用户勾了哪几集就下哪几集；没勾（onlyNumbers 为 null）时按任务里原有的集走
        var numbers = onlyNumbers is not null
            ? onlyNumbers.ToHashSet()
            : task.Episodes.Select(e => e.Number).ToHashSet();
        if (numbers.Count == 0) return false;

        RunOnUi(() => { task.Message = "正在解析站点…"; RaiseChanged(); });

        // 1. 重新解析整部剧：m3u8 直链有效期很短，上次存下来的多半已经失效
        SiteSeries parsed;
        if (preview is not null) {
            // 预检刚抓过一遍页面，直接复用 —— 用户挑集的那几秒里站点不会变
            parsed = preview.Parsed;
        } else {
            try {
                parsed = await ParseSeriesAsync(task.PageUrl, ct).ConfigureAwait(false);
            } catch (Exception ex) {
                RunOnUi(() => {
                    task.Message = $"继续下载失败（{ex.Message}），可稍后重试";
                    RaiseChanged();
                });
                return false;
            }
        }

        // 2. 选回同一个播放源上的同一批集
        //    筛选条件**必须同时**带上集号：只按源筛的话，源里那些"当初根本没勾"的集
        //    会被一起选上 —— 用户明明只下第 12 集，一点继续下载就变成整部剧重下。
        var sourceId = task.PreferredSourceId
                       ?? parsed.Sources.FirstOrDefault(s => s.Name == task.SourceName)?.Id
                       ?? parsed.Sources.FirstOrDefault()?.Id;

        // 自己联网解析的这条路（「继续下载」、或老任务第一次续下）顺手把集快照补上：
        // 否则用户只点「继续下载」的话，快照永远补不上，下次续下还是得联网。
        if (preview is null) {
            var snapshotEpisodes = parsed.AllEpisodes
                .Where(e => e.SourceId == sourceId)
                .GroupBy(e => e.Number)
                .Select(g => g.First())
                .OrderBy(e => e.Number)
                .ToList();

            SeriesSnapshot? before = null;
            await RunOnUiAsync(() => before = task.Snapshot).ConfigureAwait(false);
            await SaveEpisodesSnapshotAsync(task, parsed, sourceId, snapshotEpisodes, before)
                .ConfigureAwait(false);
        }

        // 「续下更新」：找出站点上**新出现**、任务里还没有的集（限定同一播放源）。
        // 热播剧每天补更几集 —— 用户昨天下过、今天只想补新的那几集。
        // 必须并入 numbers 之后再选集，下面的筛选/去重/分拨逻辑就全都复用，
        // 不用另起一条下载路径。只勾了一部分时，没勾的集**不追加**（用户选择优先）。
        var newOnSite = new List<SiteEpisode>();
        if (includeNewEpisodes) {
            var known = task.Episodes.Select(e => e.Number).ToHashSet();
            newOnSite = parsed.AllEpisodes
                .Where(e => e.SourceId == sourceId
                            && !known.Contains(e.Number)
                            && (onlyNumbers is null || onlyNumbers.Contains(e.Number)))
                .GroupBy(e => e.Number)
                .Select(g => g.First())
                .ToList();

            if (newOnSite.Count > 0) {
                numbers.UnionWith(newOnSite.Select(e => e.Number));
                Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} " +
                                  $"续下发现新集：{DescribeNumbers(newOnSite.Select(e => e.Number))}");
            }
        }

        var wanted = parsed.AllEpisodes
            .Where(e => e.SourceId == sourceId && numbers.Contains(e.Number))
            .ToList();

        if (wanted.Count < numbers.Count) {
            // 原来的源对不上了（源改版 / re-parse 后 id 变了）：退一步"按集号找"。
            // 但**每个集号只取一个**（优先同名源，其次列表靠前的源）——
            // 站点把同一集挂在多个源上是常态，不去重就会把同一集下两遍。
            var already = wanted.Select(e => e.Number).ToHashSet();
            var extra = parsed.AllEpisodes
                .Where(e => numbers.Contains(e.Number) && !already.Contains(e.Number))
                .GroupBy(e => e.Number)
                .Select(g => g.OrderBy(e => e.SourceId == sourceId ? 0 : 1)
                              .ThenBy(e => e.SourceId)
                              .First())
                .ToList();

            if (extra.Count > 0) {
                wanted.AddRange(extra);
                Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} " +
                                  $"⚠ 首选源（sid={sourceId}）缺少部分集，已按集号在其它源补齐：" +
                                  $"{DescribeNumbers(extra.Select(e => e.Number))}");
            }
        }

        // 兜底：把选择**重新钉死**在 wanted 上，并保证每个集号只留一个选中项。
        // 多源站点里"同一集号存在于多个源"是常态，只清一次是不够的 ——
        // 之前就出现过 wanted 已是 1 集、parsed 里却选中了 2 个同号集，
        // 结果引擎把同一集下了两遍（表现为"计划 2 集（12,12）"）。
        var keep = wanted.GroupBy(e => e.Number).Select(g => g.First()).ToList();
        foreach (var e in parsed.AllEpisodes) e.IsSelected = false;
        foreach (var e in keep) e.IsSelected = true;
        wanted = keep;

        if (wanted.Count == 0) {
            RunOnUi(() => {
                task.Message = $"继续下载失败：站点上找不到原来的剧集（集号 {DescribeNumbers(numbers)}）";
                RaiseChanged();
            });
            return false;
        }

        var planned = parsed.SelectedEpisodes.Select(e => e.Number).ToList();
        if (planned.Count != wanted.Count || planned.Any(n => !numbers.Contains(n))) {
            Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} " +
                              $"⚠ 选集异常：期望 {DescribeNumbers(numbers)}，实际 {DescribeNumbers(planned)}");
        }

        // 3. 分成「已经下好」与「还没下完」两拨
        var previous = new List<EpisodeDownloadReport>();
        var unfinished = new HashSet<int>();
        var staleNumbers = new List<int>();

        foreach (var ep in wanted) {
            var item = task.Episodes.FirstOrDefault(x => x.Number == ep.Number);
            var fileMissing = item is not null
                              && !string.IsNullOrWhiteSpace(item.OutputPath)
                              && !File.Exists(item.OutputPath);

            var done = item is not null
                       && item.State == EpisodeDownloadStatus.Completed
                       && !fileMissing;

            if (done) {
                previous.Add(new EpisodeDownloadReport {
                    Episode = ep,
                    Status = EpisodeDownloadStatus.Completed,
                    OutputPath = item!.OutputPath,
                    OutputBytes = item.Bytes,
                });
            } else {
                unfinished.Add(ep.Number);
                if (item is not null && item.State == EpisodeDownloadStatus.Completed)
                    staleNumbers.Add(item.Number);
            }
        }

        // 记录说下好了、文件却不在了 —— 当作没下过，重新下
        if (staleNumbers.Count > 0) {
            RunOnUi(() => {
                foreach (var number in staleNumbers) {
                    var item = task.Episodes.FirstOrDefault(x => x.Number == number);
                    if (item is null) continue;
                    item.State = EpisodeDownloadStatus.Pending;
                    item.StatusText = "等待中";
                    item.Percent = 0;
                    item.OutputPath = null;
                    item.Error = null;
                }
                task.Message = $"有 {staleNumbers.Count} 集的产物已不在磁盘上，将重新下载";
                RaiseChanged();
            });
        }

        // 「续下更新」：把新集插进分集清单（必须在 UI 线程 —— 绑定的
        // ObservableCollection，见 pitfalls 第 14 条），并等它真正执行完：
        // worker 回填进度按集号在 Episodes 里找行，插晚了就丢进度。
        if (newOnSite.Count > 0) {
            var added = newOnSite.ToList();
            await RunOnUiAsync(() => {
                foreach (var ep in added) {
                    task.Episodes.Add(new TaskEpisodeItem {
                        Number = ep.Number,
                        Title = string.IsNullOrWhiteSpace(ep.Title)
                            ? $"第{ep.Number:00}集"
                            : ep.Title,
                    });
                }
                task.TotalEpisodes = task.Episodes.Count;
                task.Message = $"发现新更新的 {added.Count} 集：" +
                               $"{DescribeNumbers(added.Select(e => e.Number))}，开始续下";
                task.RaiseTexts();
                RaiseChanged();
            }).ConfigureAwait(false);
        }

        if (unfinished.Count == 0) {
            RunOnUi(() => {
                task.State = SeriesTaskState.Completed;
                task.Percent = 100;
                task.FinishedEpisodes = task.Episodes.Count;
                task.SucceededEpisodes = task.Episodes.Count;
                task.FailedEpisodes = 0;
                task.FinishedAt = DateTimeOffset.Now;
                task.Message = includeNewEpisodes
                    ? $"没有发现新更新的集，已有的 {task.Episodes.Count} 集都已在磁盘上。"
                    : $"全部 {task.Episodes.Count} 集都已在磁盘上，无需续传。";
                task.RaiseTexts();
                RaiseChanged();
            });
            return false;
        }

        // 4. 只把没下完的集交给下载器；已下好的作为「上次的成果」带进报告
        //    限定源：多源站点里同号集有多个副本，不限定就会把同一集下好几遍
        var resumeSourceIds = wanted.Select(e => e.SourceId).Distinct().ToHashSet();
        var resumeSeries = SeriesDownloader.SelectOnly(parsed, unfinished, resumeSourceIds) ?? parsed;
        var options = BuildOptions(task);
        options.PreviousEpisodes = previous;

        // 有新集时把「新增了几集」显式说出来 —— 用户只会看到进度在动，
        // 不说"这几集是新发现的"就不知道续下到底做了什么（AGENTS：显式说出价值）。
        var newDesc = newOnSite.Count > 0
            ? $"（新增 {newOnSite.Count} 集：{DescribeNumbers(newOnSite.Select(e => e.Number))}）"
            : "";
        var startMessage = previous.Count > 0
            ? $"续传 {unfinished.Count} 集{newDesc}，跳过已完成的 {previous.Count} 集"
            : $"续传 {unfinished.Count} 集{newDesc}";

        // 记下这一轮的集数据来源：来自本地快照时，收尾若有集失败会顺手刷新元数据
        var usedSnapshot = preview is { Refreshed: false };
        await RunOnUiAsync(() => task.RoundUsedSnapshot = usedSnapshot).ConfigureAwait(false);

        await StartRoundAsync(task, resumeSeries, options, startMessage).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// 把「这一轮要下的集」和参数装进任务并入队。
    ///
    /// 注意：必须在「入队」之前把这些写完。worker 一取到任务就会读 Series/Options，
    /// 而 RunOnUi 是异步封送 —— 写晚了 worker 只会看到 null，任务会直接判失败。
    /// </summary>
    private async Task StartRoundAsync(SeriesTask task, SiteSeries series, SeriesDownloadOptions options,
        string? message = null) {
        await RunOnUiAsync(() => {
            task.Series = series;
            task.Options = options;

            // 顺手把「实际输出目录」算出来给界面显示：目录名形如「交锋 - 努努影院」，
            // 光看用户填的根目录根本不知道东西下到哪了
            task.ResolvedDirectory = SeriesDownloader.ResolveSeriesDirectory(series, options);

            // 记下"这一轮到底要下哪几集"。界面上看不出来，但排查
            // 「继续下载之后下了别的集」这类问题时，这一行就是判据。
            Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} " +
                              $"本轮计划：{DescribeNumbers(series.SelectedEpisodes.Select(e => e.Number))}");

            // 暂停时被藏起来的失败集数，一开新的一轮就放回去
            if (task.HiddenFailedEpisodes > 0) {
                task.FailedEpisodes = task.HiddenFailedEpisodes;
                task.HiddenFailedEpisodes = 0;
            }

            if (!string.IsNullOrWhiteSpace(message)) task.Message = message;
            RaiseChanged();
        }).ConfigureAwait(false);

        await QueueTaskAsync(task).ConfigureAwait(false);
    }

    /// <summary>按任务上记住的参数重建下载参数（续传/重试用）</summary>
    private static SeriesDownloadOptions BuildOptions(SeriesTask task) => new() {
        OutputDirectory = task.OutputDirectory,
        EpisodeConcurrency = task.EpisodeConcurrency,
        SegmentConcurrency = task.SegmentConcurrency,
        PreferHeight = task.PreferHeight,
        FfmpegPath = task.FfmpegPath,
        FileNamePattern = task.FileNamePattern,
        SeriesSubdirectory = task.SeriesSubdirectory,
        AutoSkipInvalidSegments = task.AutoSkipInvalidSegments,
        FullDecodeCheck = task.FullDecodeCheck,
        WriteReport = true,
    };

    /// <summary>
    /// 把任务放进待执行队列（任务本身已经在界面列表里了）。
    /// 先把状态改成「排队中」再入队，worker 才不会在状态还没落定时就把它当运行中处理。
    /// </summary>
    private async Task QueueTaskAsync(SeriesTask task) {
        await RunOnUiAsync(() => {
            // 上一轮停止时用掉的取消标记要换新：否则新的一轮一启动就会被判取消
            ResetStopSignal(task);

            if (task.State != SeriesTaskState.Queued) {
                task.State = SeriesTaskState.Queued;
                task.FinishedAt = null;
            }
            RaiseChanged();
        }).ConfigureAwait(false);

        lock (_gate) _pending.Add(task);
        EnsureWorker();
    }

    /// <summary>
    /// 换一个新的取消标记，并清掉「暂停」意图。
    /// 每次真正开始下载前都必须调一次。
    /// </summary>
    private static void ResetStopSignal(SeriesTask task) {
        var old = task.Cts;
        task.Cts = new CancellationTokenSource();
        task.PauseRequested = false;
        if (!old.IsCancellationRequested) return;

        // 旧标记已经用过，任务也已经停了 —— 这里可以安全释放。
        // 万一还有收尾代码在引用它，Dispose 抛异常也不该影响下载。
        try { old.Dispose(); } catch { }
    }

    /// <summary>把一部剧加入队列，立即返回（不等待下载完成）</summary>
    public SeriesTask Enqueue(SiteSeries series, SeriesDownloadOptions options) {
        var episodes = series.SelectedEpisodes.ToList();

        var task = new SeriesTask {
            Title = series.Title,
            SiteName = series.SiteName,
            PageUrl = series.PageUrl,
            OutputDirectory = options.OutputDirectory,
            TotalEpisodes = episodes.Count,
            FirstEpisodeNumber = episodes.Count > 0 ? episodes.Min(e => e.Number) : 1,
            SourceName = series.Sources.FirstOrDefault(s => s.Episodes.Any(e => e.IsSelected))?.ToString(),
            PreferredSourceId = series.PreferredSourceId ?? episodes.FirstOrDefault()?.SourceId,
            Series = series,
            Options = options,
            EpisodeConcurrency = options.EpisodeConcurrency,
            SegmentConcurrency = options.SegmentConcurrency,
            PreferHeight = options.PreferHeight,
            FfmpegPath = options.FfmpegPath,
            FileNamePattern = options.FileNamePattern,
            SeriesSubdirectory = options.SeriesSubdirectory,
            AutoSkipInvalidSegments = options.AutoSkipInvalidSegments,
            FullDecodeCheck = options.FullDecodeCheck,
        };

        // 入队时就把分集清单建好，用户不用等开始下载才看得到
        foreach (var ep in episodes.OrderBy(e => e.Number))
            task.Episodes.Add(new TaskEpisodeItem { Number = ep.Number, Title = ep.DisplayTitle });

        // 顺手把该源上**全部集**的元数据存下来（不只是勾选的）：
        // 以后点「续下」不必联网就能列出全部集，站点解析不了也能续下。
        CaptureEpisodesSnapshot(task, series);

        task.RaiseTexts();

        lock (_gate) _pending.Add(task);

        RunOnUi(() => {
            Tasks.Insert(0, task);          // 新任务排在最上面
            RaiseChanged();
        });

        EnsureWorker();
        return task;
    }

    /// <summary>
    /// 「暂停」：停下这一轮下载，但保留已下载的分片与每一集的状态。
    /// 之后点「继续下载」会重新解析站点、只补没下完的集，暂存目录里的分片照旧复用。
    /// </summary>
    public void Pause(SeriesTask task) {
        if (!task.CanPause) return;

        Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 用户点了暂停（{task.StateText}）");

        // 先立起「这是暂停」的意图，再触发取消 —— 顺序反了收尾代码会把它当取消
        task.PauseRequested = true;
        Cancel(task);
    }

    /// <summary>取消一个任务（排队中的直接从待执行队列里摘掉）</summary>
    public void Cancel(SeriesTask task) {
        try { task.Cts.Cancel(); } catch { }

        lock (_gate) _pending.Remove(task);

        if (task.State == SeriesTaskState.Queued) {
            RunOnUi(() => {
                // worker 可能刚好把它取走了，那就让 worker 自己收尾
                if (task.State != SeriesTaskState.Queued) return;
                ApplyStoppedState(task, "尚未开始");
                RaiseChanged();
            });
        }
    }

    /// <summary>
    /// 任务停止后要落到哪个状态：用户点的是「暂停」还是「取消」。
    /// </summary>
    private static void ApplyStoppedState(SeriesTask task, string when) {
        if (task.PauseRequested) {
            task.State = SeriesTaskState.Paused;
            task.Message = $"已暂停（{when}）。点「继续下载」接着下，已下载的分片会保留。";
        } else {
            task.State = SeriesTaskState.Canceled;
            task.Message = $"已取消（{when}）";
        }
        task.FinishedAt = DateTimeOffset.Now;
    }

    /// <summary>
    /// 从列表里移除（运行中的会先取消），并把这部剧的**下载历史一起销掉** ——
    /// 任务都删了、账还留着的话，下次再下这部剧会冒出"当初下过、文件已不在"的提示，
    /// 而那个文件是用户自己连同任务一起清掉的。
    ///
    /// 「清理已完成」不销账：那只是收拾列表，视频还在盘上，
    /// 留着账才能在文件被删掉时说出"这集下过"。
    /// </summary>
    public void Remove(SeriesTask task) {
        if (task.CanCancel) Cancel(task);

        // 先取地址：Cancel 之后 worker 收尾还会动这个对象，摘出列表前把要用的值拿稳
        var pageUrl = task.PageUrl;

        RunOnUi(() => {
            Tasks.Remove(task);
            RaiseChanged();
        });

        ForgetHistory(pageUrl);
    }

    /// <summary>
    /// 销掉一部剧的下载历史。扔到线程池上做：这是按钮点出来的，
    /// 而库文件 I/O（开库、写盘）不该压在 UI 线程。删不掉也不影响移除本身。
    /// </summary>
    private void ForgetHistory(string pageUrl) {
        var history = History;
        if (history is null or NullDownloadHistoryStore) return;
        if (string.IsNullOrWhiteSpace(pageUrl)) return;

        _ = Task.Run(() => {
            try { history.Forget(pageUrl); } catch { /* 接口实现方本该自己吞异常，这里再兜一层：历史永远不该挡住用户操作 */ }
        });
    }

    public void ClearFinished() {
        RunOnUi(() => {
            foreach (var t in Tasks.Where(t => t.IsFinished).ToList()) Tasks.Remove(t);
            RaiseChanged();
        });
    }

    private void EnsureWorker() {
        lock (_gate) {
            if (_workerRunning || _disposed) return;
            _workerRunning = true;
        }

        _ = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync() {
        while (true) {
            SeriesTask? task;
            lock (_gate) {
                // FIFO：先入队的先下载
                task = _pending.Count > 0 ? _pending[0] : null;
                if (task is not null) _pending.RemoveAt(0);

                if (task is null || _disposed) {
                    _workerRunning = false;
                    return;
                }
            }

            // 还在排队时就被取消/暂停的，直接收尾，不要真的去下载
            if (task.Cts.IsCancellationRequested) {
                RunOnUi(() => {
                    if (task.State is SeriesTaskState.Canceled or SeriesTaskState.Paused) return;
                    ApplyStoppedState(task, "尚未开始");
                    RaiseChanged();
                });
                continue;
            }

            RunOnUi(() => {
                task.State = SeriesTaskState.Running;
                task.Message = "开始下载…";
                Diagnostics.Write($"{Diagnostics.Tag(task.Id, task.Title)} 状态 → 下载中");
                RaiseChanged();
            });

            try {
                await RunOneAsync(task).ConfigureAwait(false);
            } catch (Exception ex) {
                RunOnUi(() => {
                    task.State = SeriesTaskState.Failed;
                    task.Message = ex.Message;
                    task.FinishedAt = DateTimeOffset.Now;
                });
            }

            RunOnUi(() => RaiseChanged());
        }
    }

    private async Task RunOneAsync(SeriesTask task) {
        var series = task.Series;
        var options = task.Options;
        if (series is null || options is null) {
            RunOnUi(() => {
                task.State = SeriesTaskState.Failed;
                task.Message = "任务数据缺失";
            });
            return;
        }

        // 这一轮自己的取消标记：任务可能在跑的过程中被暂停/取消，
        // 那时 Cts 会被换成新的，这里不能再去读属性
        var cts = task.Cts;
        var ct = cts.Token;

        var tag = Diagnostics.Tag(task.Id, task.Title);
        var thisRound = series.SelectedEpisodes.Select(e => e.Number).ToList();
        Diagnostics.Write($"{tag} 开始下载：{thisRound.Count} 集（{DescribeNumbers(thisRound)}）" +
                          $"，并发 {options.EpisodeConcurrency} 集 × {options.SegmentConcurrency} 分片");

        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 进度回调来自后台线程 —— 必须封送回 UI 线程再写绑定属性。
        //
        // 这里必须**限流**：引擎每几十毫秒上报一次，而每一次都会给 UI 线程排一个闭包，
        // UI 线程被这些写入淹掉之后，进度条看起来就是"不动"的（消息也停在一句话上）。
        // 100ms 一次（10 次/秒）对眼睛来说完全够用，UI 却轻松很多。
        //
        // 注意：限流只针对"下载过程中"的高频上报。轮次收尾（last=true）必须放行 ——
        // 那一次带着各集的最终状态，被吃掉的话分集行会永远停在「下载中」。
        var lastUiPush = 0L;
        var lastEpisodeStates = new Dictionary<int, EpisodeDownloadStatus>();

        // 这一轮是否已经收尾。Progress<T> 的回调是在线程池上**异步**跑的，
        // 引擎最后一次 Report()（里面的 Percent 是它的收尾口径，未必等于真实进度）
        // 完全可能晚于下面的收尾写入到达 —— 那时它会把刚写好的状态覆盖掉。
        // 用 StrongBox 包一层，才能对捕获的变量用 Volatile（C# 不允许 ref 捕获变量）。
        var roundFinished = new StrongBox<bool>(false);

        void PushToUi(SeriesDownloadProgress p, bool force) {
            var now = Environment.TickCount64;
            if (!force && now - Interlocked.Read(ref lastUiPush) < 100) return;
            Interlocked.Exchange(ref lastUiPush, now);

            RunOnUi(() => {
                // 闸门必须在**真正执行时**再判一次。
                // 只在排队前判是不够的：这个闭包可能已经进了 UI 队列，
                // 等它执行时本轮早就收尾了，它会把收尾写好的进度又覆盖回去
                // （引擎最后一次上报里的 Percent 是它自己的收尾口径，可能还是 100%）。
                if (Volatile.Read(ref roundFinished.Value)) return;

                task.Percent = p.OverallPercent;
                task.FinishedEpisodes = p.FinishedEpisodes;
                task.SucceededEpisodes = p.SucceededEpisodes;
                task.FailedEpisodes = p.FailedEpisodes;
                task.DownloadedBytes = p.DownloadedBytes;
                task.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
                task.CurrentEpisode = p.CurrentEpisodeTitle;

                // 当前集的细化进度（百分比 + 分片计数）
                var current = p.CurrentEpisodeTitle is { Length: > 0 } title
                    ? p.Episodes.FirstOrDefault(e => e.Title == title)
                    : null;
                task.CurrentDetail = current is null
                    ? ""
                    : $"{current.Percent:0}%" +
                      (current.TotalSegments > 0 ? $" · {current.CompletedSegments}/{current.TotalSegments} 片" : "");

                task.Elapsed = clock.Elapsed;
                task.SyncEpisodes(p.Episodes);
                task.RaiseTexts();

                // 诊断：只在某集的"档位"真的变了时记一行，避免把日志写成流水账
                if (!Diagnostics.Enabled) return;
                foreach (var s in p.Episodes) {
                    var before = lastEpisodeStates.TryGetValue(s.Number, out var old)
                        ? old
                        : EpisodeDownloadStatus.Pending;
                    if (before == s.Status) continue;
                    lastEpisodeStates[s.Number] = s.Status;
                    Diagnostics.Write($"{tag} 第{s.Number:00}集 {before} → {s.Status}" +
                                      $"（{s.Percent:0}%，总 {p.OverallPercent:0.0}%）");
                }
            });
        }

        var progress = new Progress<SeriesDownloadProgress>(p => PushToUi(p, force: false));

        var report = await _downloader
            .DownloadAsync(series, options, progress, ct)
            .ConfigureAwait(false);

        clock.Stop();

        // 闸门关上：此后引擎补发的进度回调一律不再往界面上写
        Volatile.Write(ref roundFinished.Value, true);

        // 收尾时必须补一次进度推送：过程中的高频上报被限流，最后一次带着
        // 「各集最终状态」的上报很可能刚好落在限流窗口里被丢掉，
        // 那样分集行会一直停在「下载中」。这里强制推一次，并等到它真的执行完 ——
        // 下面的收尾逻辑要按「这一轮实际完成到哪一步」来写。
        var pausedAtEnd = task.PauseRequested;

        // 暂停时的整体进度要与引擎同一口径：**各集进度的平均值**（见 SeriesDownloadProgress.OverallPercent）。
        // 不能沿用进度回调最后给的那个数 —— 取消路径上它可能停在"只报了一部分集"的时刻，
        // 或者被前面那次强制推送覆盖成 100%，看起来就像"已经下完了"。
        var finalPercent = Math.Clamp(
            report.Episodes.Sum(e => Math.Clamp(e.Percent, 0, 100)) / Math.Max(1, task.TotalEpisodes),
            0, 100);

        await RunOnUiAsync(() => {
            foreach (var s in report.Episodes) {
                var item = task.Episodes.FirstOrDefault(e => e.Number == s.Episode.Number);
                if (item is null) continue;

                if (s.Status == EpisodeDownloadStatus.Completed) item.Percent = 100;
                if (item.CompletedSegments != s.CompletedSegments) item.CompletedSegments = s.CompletedSegments;
                if (item.TotalSegments != s.TotalSegments) item.TotalSegments = s.TotalSegments;

                // 归一化：暂停时被取消/没跑完的集，分集行上不能留着「下载中」。
                // 判据用**报告的结果**而不是行上可能过期的状态 —— 用户可能正好在
                // 「分片下载完、界面还没回填」的空档里按了暂停，这时行上还是「下载中」。
                if (!pausedAtEnd) continue;

                if (s.Status == EpisodeDownloadStatus.Completed) {
                    item.State = EpisodeDownloadStatus.Completed;
                    item.StatusText = "已完成";
                    if (!string.IsNullOrWhiteSpace(s.OutputPath)) item.OutputPath = s.OutputPath;
                } else if (s.Status == EpisodeDownloadStatus.Canceled
                           || item.State is EpisodeDownloadStatus.Downloading or EpisodeDownloadStatus.Resolving) {
                    // 真正的「失败」原样保留（见下面收尾逻辑里的说明），其余视为被暂停打断
                    item.State = EpisodeDownloadStatus.Pending;
                    item.StatusText = "已暂停";
                    item.Error = null;
                }
            }

            task.Percent = finalPercent;
            task.DownloadedBytes = report.TotalBytes;
            task.SpeedBytesPerSecond = 0;
        }).ConfigureAwait(false);

        // 完成后按报告回填每一集的最终状态
        RunOnUi(() => {
            var paused = task.PauseRequested;

            // 跳过的广告数必须说出来：用户看不出"少了几十秒"，
            // 不声不响的话这个功能做了也等于没做
            task.SkippedAdSegments = report.TotalSkippedAds;

            foreach (var ep in report.Episodes) {
                var item = task.Episodes.FirstOrDefault(e => e.Number == ep.Episode.Number);
                if (item is null) continue;

                // 暂停时只把「被暂停打断的取消」改写成「已暂停」，**真正的失败照实保留**：
                // 用户看到的截图里就有这种行（"100.0% 失败"）——
                // 分片在暂停那一刻恰好失败，把它涂成「已暂停」等于把真实问题藏起来，
                // 继续下载的时候还得再撞一次。
                // 判据用 ep 上的结果而不是 item 上可能过期的状态 ——
                // 用户可能在下载刚跑完、界面还没回填的空档里按了暂停。
                if (paused && ep.Status == EpisodeDownloadStatus.Canceled) {
                    item.State = EpisodeDownloadStatus.Pending;
                    item.StatusText = "已暂停";
                    item.Error = null;
                    continue;
                }

                // 状态、产物路径必须在这里写死，不能等引擎的最后一批进度快照：
                // 快照走 Progress<T> 异步到达，很可能落在收尾闸门（roundFinished）
                // 之后被整批拦掉 —— 那样行上写着「已完成」，State 却还停在
                // Downloading、OutputPath 也是空，「继续下载/续下」就会把这集
                // 当成没下过的再下一遍（阶段 N 自检抓到的就是这个）。
                item.State = ep.Status;
                item.StatusText = ep.Status switch {
                    EpisodeDownloadStatus.Completed => "已完成",
                    EpisodeDownloadStatus.Failed => "失败",
                    EpisodeDownloadStatus.Canceled => "已取消",
                    _ => item.StatusText,
                };
                if (ep.Status == EpisodeDownloadStatus.Completed) item.Percent = 100;
                item.Bytes = ep.OutputBytes;
                item.Error = ep.Error;
                if (!string.IsNullOrWhiteSpace(ep.OutputPath)) item.OutputPath = ep.OutputPath;
            }

            task.Report = report;
            task.ReportPath = report.ReportPath;
            task.Elapsed = report.Elapsed;
            task.SpeedBytesPerSecond = 0;
            task.FinishedAt = DateTimeOffset.Now;
            task.CurrentEpisode = null;
            task.CurrentDetail = "";

            // 已完成的集数从头数一遍，不要只报本轮的 SucceededCount：
            // 暂停时明明已经下好的集会被说成「完成 0 集」，
            // 而报告里还带着「上一轮就下好的」那些集（PreviousEpisodes）。
            var completedTotal = report.Episodes.Count(e => e.Status == EpisodeDownloadStatus.Completed);

            // 进度按**真实完成情况**写，不能写死 100%：
            // 暂停（或者只有部分集成功）时把进度条拉到满格，看起来就像"已经下完了"。
            task.Percent = finalPercent;
            task.FinishedEpisodes = report.Episodes.Count;
            task.SucceededEpisodes = completedTotal;

            // 暂停时不让「重试失败集」按钮冒出来：这一轮是被主动停下的，
            // 该点的是「继续下载」；失败集数先记下来，继续/重试时再放回去。
            task.HiddenFailedEpisodes = paused ? report.FailedCount + report.CanceledCount : 0;
            task.FailedEpisodes = paused ? 0 : report.FailedCount + report.CanceledCount;

            task.State = paused
                ? SeriesTaskState.Paused
                : report switch {
                    { CanceledCount: > 0 } => SeriesTaskState.Canceled,
                    { FailedCount: 0 } => SeriesTaskState.Completed,
                    { SucceededCount: > 0 } => SeriesTaskState.PartiallyCompleted,
                    _ => SeriesTaskState.Failed,
                };

            task.Message = task.State switch {
                SeriesTaskState.Completed =>
                    $"全部 {completedTotal} 集完成，{report.TotalBytes / 1024.0 / 1024.0:0.0} MB",
                SeriesTaskState.Paused =>
                    $"已暂停：完成 {completedTotal} 集，" +
                    $"剩余 {Math.Max(0, task.TotalEpisodes - completedTotal)} 集未完成" +
                    "（已下载的分片保留，可继续）",
                SeriesTaskState.PartiallyCompleted =>
                    $"完成 {report.SucceededCount} 集，失败 {report.FailedCount} 集（暂存目录已保留，可重试）",
                SeriesTaskState.Canceled => $"已取消（完成 {report.SucceededCount} 集）",
                _ => $"失败 {report.FailedCount} 集",
            };

            task.RaiseTexts();

            // 诊断：一轮结束的完整交代 —— 花了多久、下了哪几集、结果如何
            Diagnostics.Write($"{tag} 本轮结束：{task.StateText}，{clock.Elapsed.TotalSeconds:0.0}s，" +
                              $"成功 {report.SucceededCount} / 失败 {report.FailedCount} / 取消 {report.CanceledCount}，" +
                              $"完成集 {DescribeNumbers(report.Episodes
                                  .Where(e => e.Status == EpisodeDownloadStatus.Completed)
                                  .Select(e => e.Episode.Number))}，" +
                              $"失败集 {DescribeNumbers(report.Episodes
                                  .Where(e => e.Status == EpisodeDownloadStatus.Failed)
                                  .Select(e => e.Episode.Number))}");
        });

        // 把这一轮下好的集记进下载历史。放在 UI 回填之后、worker 线程上做：
        // 写库是 IO，不该占用 UI 线程；而且历史记不上也不能影响下载结果。
        RecordHistory(task, report);

        // 这一轮用的是本地快照、又有集没下成 —— 很可能站点改版了（播放页地址失效），
        // 顺手重新拉一次集元数据：用户点「重试失败集」时就能用上新地址。
        // 拉不到就算了：原来的失败原因照实显示，不覆盖成"刷新失败"。
        if (task.RoundUsedSnapshot && report.SucceededCount < report.Episodes.Count) {
            try {
                var refreshed = await RefreshEpisodesSnapshotAsync(task, ct).ConfigureAwait(false);
                RunOnUi(() => {
                    task.RoundUsedSnapshot = false;
                    task.Message = $"{task.Message}；已重新拉取集列表（站点 {refreshed.TotalOnSite} 集），可重试失败集";
                    RaiseChanged();
                });
            } catch {
                // 刷新不了就保持原样
            }
        }
    }

    /// <summary>
    /// 把报告里"这一轮下好了"的集记进下载历史。
    ///
    /// 具体规则在 <see cref="DownloadHistoryRecorder"/> —— 单独成类是为了让"哪些集算下过"
    /// 这条规则能被自检直接盯住，而不是散在这个两千多行的类里。
    /// </summary>
    private void RecordHistory(SeriesTask task, SeriesDownloadReport report) =>
        DownloadHistoryRecorder.Record(History, task.PageUrl, task.SiteName, task.Title, report.Episodes);

    /// <summary>
    /// 重试：把失败（或被取消）的那些集再排一次队。
    ///
    /// **在原任务上重试，不再新建任务。**
    /// 早先的实现是 <c>Enqueue(SelectOnly(task.Series, failedNumbers), …)</c>，
    /// 也就是照抄原剧集列表再建一个新任务 —— 同一个任务会因此出现两份：
    /// 新任务把失败的那 21 集从头再列一遍，老任务仍然挂在那里显示「已取消」，
    /// 点一次「重试失败集」就多一份重复的集。现在改为就地重置这几集的状态并续跑，
    /// 任务列表里始终只有这一条。
    ///
    /// 已完成的集不重下：它们作为「上一轮的成果」传给下载器，只为把报告和总进度补全。
    /// 暂存目录里的分片由引擎按清单指纹校验后继续复用。
    /// </summary>
    /// <returns>已排入队列的原任务；没有可重试的集时返回 null</returns>
    public async Task<SeriesTask?> RetryFailedAsync(SeriesTask task) {
        var series = task.Series;
        if (task.Report is null || series is null) return null;
        if (!task.IsFinished) return null;                     // 正在跑：先暂停/取消再重试
        lock (_gate) { if (_pending.Contains(task)) return null; }

        // 从报告里取这一轮真正失败/取消的集号（报告是上一轮的结果，先快照下来）。
        // 同时记下它们所属的播放源 —— 多源站点里同号集有多个副本，
        // 只按集号重试会把同一集在别的源上再下一遍。
        var failed = task.Report.Episodes
            .Where(e => e.Status is EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled)
            .ToList();

        var retryNumbers = failed.Select(e => e.Episode.Number).ToHashSet();
        if (retryNumbers.Count == 0) return null;

        var retrySourceIds = failed.Select(e => e.Episode.SourceId).ToHashSet();

        // 复制一份剧集数据，只勾选要重试的那些集（限定在原来的源上）
        var retrySeries = SeriesDownloader.SelectOnly(series, retryNumbers, retrySourceIds);
        if (retrySeries is null) return null;

        // 已完成的集带进报告与总进度，但不重下
        var previous = task.Report.Episodes
            .Where(e => e.Status == EpisodeDownloadStatus.Completed)
            .ToList();

        var wasPaused = task.State == SeriesTaskState.Paused;
        var message = string.Join(" ", new[]
        {
            $"{(wasPaused ? "继续" : "重试")} {retryNumbers.Count} 集",
            previous.Count > 0 ? $"（已完成的 {previous.Count} 集自动跳过）" : "",
        }.Where(p => !string.IsNullOrWhiteSpace(p)));

        // 就地重置：这几集回到「等待中」，界面上的旧错误与进度条也一起清掉。
        // 这一步必须在入队前做完 —— worker 一取到任务就会往这些行上写进度。
        await RunOnUiAsync(() => {
            foreach (var item in task.Episodes) {
                if (!retryNumbers.Contains(item.Number)) continue;
                item.State = EpisodeDownloadStatus.Pending;
                item.StatusText = "等待中";
                item.Percent = 0;
                item.Error = null;
            }

            // 暂停时被藏起来的失败集数要放回去，否则重试成功后还挂着「失败 N」
            task.FailedEpisodes = task.HiddenFailedEpisodes;
            task.HiddenFailedEpisodes = 0;
            task.FinishedAt = null;
            task.SpeedBytesPerSecond = 0;
            task.CurrentEpisode = null;
            task.RaiseTexts();
            RaiseChanged();
        }).ConfigureAwait(false);

        var options = BuildOptions(task);
        options.PreviousEpisodes = previous;

        await StartRoundAsync(task, retrySeries, options, message).ConfigureAwait(false);
        return task;
    }

    public void Dispose() {
        lock (_gate) {
            _disposed = true;
            _pending.Clear();
        }

        foreach (var t in Tasks.Where(t => t.CanCancel)) {
            try { t.Cts.Cancel(); } catch { }
        }

        _downloader.Dispose();
    }
}
