using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using M3U8Downloader.Core.Sites;

namespace M3U8Downloader.Core.Tasks;

/// <summary>下载任务状态</summary>
public enum SeriesTaskState
{
    Queued,
    Running,
    Completed,
    PartiallyCompleted,
    Failed,
    Canceled,
}

/// <summary>任务里的一集（可绑定，实时显示该集进度）</summary>
public sealed class TaskEpisodeItem : INotifyPropertyChanged
{
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
    public double Percent
    {
        get => _percent;
        set { if (Set(ref _percent, value)) OnPropertyChanged(nameof(PercentText)); }
    }

    private long _bytes;
    public long Bytes
    {
        get => _bytes;
        set { if (Set(ref _bytes, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private string? _error;
    public string? Error
    {
        get => _error;
        set { if (Set(ref _error, value)) OnPropertyChanged(nameof(HasError)); }
    }

    /// <summary>有错误信息时界面才显示那一行红字</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(_error);

    public string PercentText => $"{_percent:0.0}%";
    public string SizeText => _bytes > 0 ? $"{_bytes / 1024.0 / 1024.0:0.0} MB" : "";

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 一个「整部剧」下载任务。
/// 实现 INotifyPropertyChanged，界面可以直接绑定，不必再包一层。
/// </summary>
public sealed class SeriesTask : INotifyPropertyChanged
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..8];

    public required string Title { get; init; }
    public required string SiteName { get; init; }
    public required string PageUrl { get; init; }
    public required string OutputDirectory { get; init; }
    public int TotalEpisodes { get; init; }
    public int FirstEpisodeNumber { get; init; } = 1;

    /// <summary>使用的播放源名称</summary>
    public string? SourceName { get; init; }

    /// <summary>播放源 id —— 续传时用它重新选中同一个源</summary>
    public int? PreferredSourceId { get; init; }

    /// <summary>分集清单（实时更新每一集的进度）</summary>
    public ObservableCollection<TaskEpisodeItem> Episodes { get; } = new();

    // ---------------- 可变状态 ----------------

    private SeriesTaskState _state = SeriesTaskState.Queued;
    public SeriesTaskState State
    {
        get => _state;
        internal set
        {
            if (!Set(ref _state, value)) return;
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(NeedsRetry));
        }
    }

    private int _finishedEpisodes;
    public int FinishedEpisodes { get => _finishedEpisodes; internal set => Set(ref _finishedEpisodes, value); }

    private int _succeededEpisodes;
    public int SucceededEpisodes { get => _succeededEpisodes; internal set => Set(ref _succeededEpisodes, value); }

    private int _failedEpisodes;
    public int FailedEpisodes
    {
        get => _failedEpisodes;
        internal set
        {
            if (!Set(ref _failedEpisodes, value)) return;
            OnPropertyChanged(nameof(NeedsRetry));
        }
    }

    private double _percent;
    public double Percent { get => _percent; internal set => Set(ref _percent, value); }

    private long _downloadedBytes;
    public long DownloadedBytes
    {
        get => _downloadedBytes;
        internal set { if (Set(ref _downloadedBytes, value)) OnPropertyChanged(nameof(SizeText)); }
    }

    private double _speed;
    public double SpeedBytesPerSecond
    {
        get => _speed;
        internal set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedText)); }
    }

    /// <summary>
    /// 上传速度。本程序自身不上传任何数据，因此恒为 0；
    /// 保留该字段是为了让状态栏的「↓ / ↑」显示格式完整。
    /// </summary>
    public double UploadBytesPerSecond => 0;

    private string? _currentEpisode;
    public string? CurrentEpisode
    {
        get => _currentEpisode;
        internal set { if (Set(ref _currentEpisode, value)) OnPropertyChanged(nameof(CurrentEpisodeText)); }
    }

    private string? _message;
    /// <summary>状态说明文字。界面也会用它显示临时反馈（如"报告文件不存在"），因此公开可写。</summary>
    public string? Message { get => _message; set => Set(ref _message, value); }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    private DateTimeOffset? _finishedAt;
    public DateTimeOffset? FinishedAt { get => _finishedAt; internal set => Set(ref _finishedAt, value); }

    private TimeSpan _elapsed;
    public TimeSpan Elapsed
    {
        get => _elapsed;
        internal set { if (Set(ref _elapsed, value)) OnPropertyChanged(nameof(ElapsedText)); }
    }

    private string? _reportPath;
    public string? ReportPath
    {
        get => _reportPath;
        internal set { if (Set(ref _reportPath, value)) OnPropertyChanged(nameof(HasReport)); }
    }

    /// <summary>完整结果（完成后可查看明细）</summary>
    public SeriesDownloadReport? Report { get; internal set; }

    // ---------------- 供界面绑定 ----------------

    public bool HasReport => !string.IsNullOrWhiteSpace(_reportPath);
    public bool IsRunning => _state == SeriesTaskState.Running;
    public bool IsFinished => _state is SeriesTaskState.Completed or SeriesTaskState.PartiallyCompleted
        or SeriesTaskState.Failed or SeriesTaskState.Canceled;
    public bool CanCancel => _state is SeriesTaskState.Queued or SeriesTaskState.Running;

    /// <summary>是否值得提供「重试失败集」（有失败且已结束）</summary>
    public bool NeedsRetry => IsFinished && _failedEpisodes > 0;

    /// <summary>
    /// 是否值得提供「继续下载」：任务已停下，且还有没下完的集。
    /// 关掉程序再打开时会用到它 —— 恢复出来的任务就停在这一档上。
    /// </summary>
    public bool CanResume => IsFinished && Episodes.Any(e => e.State != EpisodeDownloadStatus.Completed);

    public string StateText => _state switch
    {
        SeriesTaskState.Queued => "排队中",
        SeriesTaskState.Running => "下载中",
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
    public string ProgressText
    {
        get
        {
            var parts = new List<string> { $"{_finishedEpisodes}/{TotalEpisodes} 集" };
            if (_failedEpisodes > 0) parts.Add($"失败 {_failedEpisodes}");
            if (_speed > 0 && IsRunning) parts.Add(SpeedText);
            if (!string.IsNullOrWhiteSpace(SizeText)) parts.Add(SizeText);
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    /// <summary>形如 "正在下载 第03集"</summary>
    public string CurrentEpisodeText => IsRunning && !string.IsNullOrWhiteSpace(_currentEpisode)
        ? $"正在下载 {_currentEpisode}"
        : "";

    public string EpisodeCountText => $"共 {TotalEpisodes} 集";

    /// <summary>分集清单的标题，形如 "分集进度（6/16）"</summary>
    public string EpisodeHeaderText => $"分集进度（{_finishedEpisodes}/{TotalEpisodes}）";

    public static string FormatSpeed(double bytesPerSecond) => bytesPerSecond switch
    {
        >= 1024 * 1024 => $"{bytesPerSecond / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{bytesPerSecond / 1024:0.0} KB/s",
        > 0 => $"{bytesPerSecond:0} B/s",
        _ => "0 B/s",
    };

    // 组合文本依赖多个字段，统一在这里刷新
    internal void RaiseTexts()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CurrentEpisodeText));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(ElapsedText));
        OnPropertyChanged(nameof(EpisodeHeaderText));
    }
    /// <summary>按快照同步分集清单（只在 UI 线程调用）</summary>
    internal void SyncEpisodes(List<EpisodeProgressSnapshot> snapshots)
    {
        foreach (var s in snapshots)
        {
            var item = Episodes.FirstOrDefault(e => e.Number == s.Number);
            if (item is null) continue;

            if (!string.Equals(item.StatusText, s.StatusText, StringComparison.Ordinal))
                item.StatusText = s.StatusText;

            if (item.State != s.Status) item.State = s.Status;

            if (!string.IsNullOrWhiteSpace(s.OutputPath)) item.OutputPath = s.OutputPath;

            if (Math.Abs(item.Percent - s.Percent) > 0.05)
                item.Percent = s.Percent;

            if (s.OutputBytes > 0 && item.Bytes != s.OutputBytes)
                item.Bytes = s.OutputBytes;

            if (!string.IsNullOrWhiteSpace(s.Error) && item.Error != s.Error)
                item.Error = s.Error;
        }

        OnPropertyChanged(nameof(CanResume));
    }

    // ---------------- 内部：运行所需的数据 ----------------

    /// <summary>剧集信息（含勾选状态），不参与界面绑定</summary>
    internal SiteSeries? Series { get; set; }

    /// <summary>下载参数</summary>
    internal SeriesDownloadOptions? Options { get; set; }

    // ---------------- 下载参数（续传时按原样重建） ----------------

    internal int EpisodeConcurrency { get; set; } = 2;
    internal int SegmentConcurrency { get; set; } = 16;
    internal int? PreferHeight { get; set; }
    internal string? FfmpegPath { get; set; }
    internal string FileNamePattern { get; set; } = "{title}.{number:00}";
    internal bool SeriesSubdirectory { get; set; } = true;

    /// <summary>用于取消本任务</summary>
    internal CancellationTokenSource Cts { get; } = new();

    public override string ToString() => $"[{StateText}] {Title} {ProgressText}";

    // ---------------- INotifyPropertyChanged ----------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
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
public sealed class DownloadTaskManager : IDisposable
{
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
        TaskStore? store = null)
    {
        _downloader = downloader ?? new SeriesDownloader();
        _uiInvoker = uiInvoker;
        _store = store;
    }

    /// <summary>全部任务（最新的在最前）</summary>
    public ObservableCollection<SeriesTask> Tasks { get; } = new();

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

    /// <summary>上传速度合计。本程序不上传数据，恒为 0。</summary>
    public double TotalUploadSpeed => 0;

    public int RunningCount => Tasks.Count(t => t.State == SeriesTaskState.Running);
    public int QueuedCount => Tasks.Count(t => t.State == SeriesTaskState.Queued);

    private void RunOnUi(Action action)
    {
        if (_uiInvoker is null) { action(); return; }
        _uiInvoker(action);
    }

    /// <summary>封送到 UI 线程并等它做完（恢复任务时需要先插进列表再继续处理）</summary>
    private Task RunOnUiAsync(Action action)
    {
        if (_uiInvoker is null)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        _uiInvoker(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    /// <summary>状态有变化：通知界面，并安排一次落盘（节流）</summary>
    private void RaiseChanged()
    {
        Changed?.Invoke(this, EventArgs.Empty);
        ScheduleSave();
    }

    // ==================== 落盘 / 恢复（断点续传） ====================

    /// <summary>
    /// 安排一次节流的落盘。进度回调每秒可能来几十次，不能每次都写文件，
    /// 但也不能拖太久 —— 否则用户在这中间关掉程序就会丢进度。
    /// </summary>
    private void ScheduleSave()
    {
        if (_store is null || _disposed) return;
        if (Interlocked.Exchange(ref _saveQueued, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(1200).ConfigureAwait(false); } catch { }
            Interlocked.Exchange(ref _saveQueued, 0);

            // 读 Tasks（绑定用的集合）必须在 UI 线程
            RunOnUi(SaveNow);
        });
    }

    /// <summary>
    /// 立即落盘。关窗时由界面在 UI 线程直接调用，保证「关掉程序」这一刻的进度已经写进文件。
    /// </summary>
    public void SaveNow()
    {
        if (_store is null) return;

        try
        {
            _store.Save(Tasks.Select(ToRecord).ToList());
        }
        catch
        {
            // 存盘失败不该影响下载
        }
    }

    private SeriesTaskRecord ToRecord(SeriesTask t)
    {
        var record = new SeriesTaskRecord
        {
            Id = t.Id,
            Title = t.Title,
            SiteName = t.SiteName,
            PageUrl = t.PageUrl,
            OutputDirectory = t.OutputDirectory,
            SourceName = t.SourceName,
            PreferredSourceId = t.PreferredSourceId,
            State = t.State.ToString(),
            Percent = t.Percent,
            DownloadedBytes = t.DownloadedBytes,
            ReportPath = t.ReportPath,
            Message = t.Message,
            CreatedAt = t.CreatedAt,
            FinishedAt = t.FinishedAt,
            EpisodeConcurrency = t.EpisodeConcurrency,
            SegmentConcurrency = t.SegmentConcurrency,
            PreferHeight = t.PreferHeight,
            FfmpegPath = t.FfmpegPath,
            FileNamePattern = t.FileNamePattern,
            SeriesSubdirectory = t.SeriesSubdirectory,
        };

        foreach (var e in t.Episodes)
        {
            record.Episodes.Add(new TaskEpisodeRecord
            {
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

    private static SeriesTask FromRecord(SeriesTaskRecord r)
    {
        var ordered = r.Episodes.OrderBy(e => e.Number).ToList();

        var task = new SeriesTask
        {
            Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id,
            Title = r.Title,
            SiteName = r.SiteName,
            PageUrl = r.PageUrl,
            OutputDirectory = r.OutputDirectory,
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

        foreach (var e in ordered)
        {
            var state = Enum.TryParse<EpisodeDownloadStatus>(e.Status, ignoreCase: true, out var parsed)
                ? parsed
                : EpisodeDownloadStatus.Pending;

            task.Episodes.Add(new TaskEpisodeItem
            {
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

        // 上次正在下载/排队的任务，恢复后先停在「可继续」的状态上，
        // 由 RestoreAsync 决定是否自动接着下。
        var restoredState = r.ParsedState;
        task.State = restoredState switch
        {
            SeriesTaskState.Running or SeriesTaskState.Queued => SeriesTaskState.PartiallyCompleted,
            _ => restoredState,
        };

        task.Message = string.IsNullOrWhiteSpace(r.Message)
            ? "已从上次的进度恢复"
            : r.Message;
        task.Percent = r.Percent;
        task.DownloadedBytes = r.DownloadedBytes;
        task.ReportPath = r.ReportPath;
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

    internal static string DescribeStatus(EpisodeDownloadStatus status) => status switch
    {
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
    public async Task<int> RestoreAsync(CancellationToken ct = default)
    {
        if (_store is null) return 0;
        if (Interlocked.Exchange(ref _restoring, 1) == 1) return 0;

        List<SeriesTask> restored = new();
        try
        {
            var records = _store.Load();
            if (records.Count == 0) return 0;

            await RunOnUiAsync(() =>
            {
                // Tasks 是「最新的在最前」，所以按创建时间正序依次插到最前面
                foreach (var r in records.OrderBy(r => r.CreatedAt))
                {
                    if (string.IsNullOrWhiteSpace(r.PageUrl)) continue;

                    var task = FromRecord(r);
                    Tasks.Insert(0, task);
                    restored.Add(task);
                }
            }).ConfigureAwait(false);

            if (restored.Count == 0) return 0;

            foreach (var task in restored)
            {
                ct.ThrowIfCancellationRequested();

                // 已完成 / 用户主动取消的，不自动接着下
                if (task.State is SeriesTaskState.Completed or SeriesTaskState.Canceled) continue;
                if (!task.CanResume) continue;

                RunOnUi(() =>
                {
                    task.Message = "正在恢复…";
                    ScheduleSave();
                });

                try
                {
                    await ResumeAsync(task, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    RunOnUi(() =>
                    {
                        task.Message = $"恢复失败：{ex.Message}";
                        RaiseChanged();
                    });
                }
            }

            return restored.Count;
        }
        finally
        {
            Interlocked.Exchange(ref _restoring, 0);
        }
    }

    /// <summary>
    /// 继续下载：重新解析站点，只把「还没下完的集」排进队列。
    ///
    /// 已完成的集不会重复下载（还要确认产物文件确实还在）；
    /// 没下完的集会复用原来的暂存目录，引擎按清单指纹校验后从断点继续。
    /// </summary>
    public async Task<bool> ResumeAsync(SeriesTask task, CancellationToken ct = default)
    {
        if (_disposed) return false;
        if (task.State == SeriesTaskState.Running) return false;

        lock (_gate)
        {
            if (_pending.Contains(task)) return false;
        }

        var numbers = task.Episodes.Select(e => e.Number).ToHashSet();
        if (numbers.Count == 0) return false;

        RunOnUi(() => { task.Message = "正在解析站点…"; RaiseChanged(); });

        // 1. 重新解析整部剧：m3u8 直链有效期很短，上次存下来的多半已经失效
        SiteSeries parsed;
        try
        {
            parsed = await ParseSeriesAsync(task.PageUrl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RunOnUi(() =>
            {
                task.Message = $"继续下载失败（{ex.Message}），可稍后重试";
                RaiseChanged();
            });
            return false;
        }

        // 2. 选回同一个播放源上的同一批集
        var sourceId = task.PreferredSourceId
                       ?? parsed.Sources.FirstOrDefault(s => s.Name == task.SourceName)?.Id
                       ?? parsed.Sources.FirstOrDefault()?.Id;

        var wanted = parsed.AllEpisodes
            .Where(e => e.SourceId == sourceId && numbers.Contains(e.Number))
            .ToList();

        if (wanted.Count == 0)
            wanted = parsed.AllEpisodes.Where(e => numbers.Contains(e.Number)).ToList();

        if (wanted.Count == 0)
        {
            RunOnUi(() =>
            {
                task.Message = "继续下载失败：站点上找不到原来的剧集";
                RaiseChanged();
            });
            return false;
        }

        foreach (var e in parsed.AllEpisodes) e.IsSelected = false;
        foreach (var e in wanted) e.IsSelected = true;

        // 3. 分成「已经下好」与「还没下完」两拨
        var previous = new List<EpisodeDownloadReport>();
        var unfinished = new HashSet<int>();
        var staleNumbers = new List<int>();

        foreach (var ep in wanted)
        {
            var item = task.Episodes.FirstOrDefault(x => x.Number == ep.Number);
            var fileMissing = item is not null
                              && !string.IsNullOrWhiteSpace(item.OutputPath)
                              && !File.Exists(item.OutputPath);

            var done = item is not null
                       && item.State == EpisodeDownloadStatus.Completed
                       && !fileMissing;

            if (done)
            {
                previous.Add(new EpisodeDownloadReport
                {
                    Episode = ep,
                    Status = EpisodeDownloadStatus.Completed,
                    OutputPath = item!.OutputPath,
                    OutputBytes = item.Bytes,
                });
            }
            else
            {
                unfinished.Add(ep.Number);
                if (item is not null && item.State == EpisodeDownloadStatus.Completed)
                    staleNumbers.Add(item.Number);
            }
        }

        // 记录说下好了、文件却不在了 —— 当作没下过，重新下
        if (staleNumbers.Count > 0)
        {
            RunOnUi(() =>
            {
                foreach (var number in staleNumbers)
                {
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

        if (unfinished.Count == 0)
        {
            RunOnUi(() =>
            {
                task.State = SeriesTaskState.Completed;
                task.Percent = 100;
                task.FinishedEpisodes = task.Episodes.Count;
                task.SucceededEpisodes = task.Episodes.Count;
                task.FailedEpisodes = 0;
                task.FinishedAt = DateTimeOffset.Now;
                task.Message = $"全部 {task.Episodes.Count} 集都已在磁盘上，无需续传。";
                task.RaiseTexts();
                RaiseChanged();
            });
            return false;
        }

        // 4. 只把没下完的集交给下载器；已下好的作为「上次的成果」带进报告
        var resumeSeries = SeriesDownloader.SelectOnly(parsed, unfinished) ?? parsed;
        var options = BuildOptions(task);
        options.PreviousEpisodes = previous;

        // 注意：必须在「入队」之前把这些写完。worker 一取到任务就会读 Series/Options，
        // 而 RunOnUi 是异步封送 —— 写晚了 worker 只会看到 null，任务会直接判失败。
        await RunOnUiAsync(() =>
        {
            task.Series = resumeSeries;
            task.Options = options;
            task.Message = previous.Count > 0
                ? $"续传 {unfinished.Count} 集（跳过已完成的 {previous.Count} 集）"
                : $"续传 {unfinished.Count} 集";
            RaiseChanged();
        }).ConfigureAwait(false);

        await QueueTaskAsync(task).ConfigureAwait(false);
        return true;
    }

    /// <summary>按任务上记住的参数重建下载参数（续传用）</summary>
    private static SeriesDownloadOptions BuildOptions(SeriesTask task) => new()
    {
        OutputDirectory = task.OutputDirectory,
        EpisodeConcurrency = task.EpisodeConcurrency,
        SegmentConcurrency = task.SegmentConcurrency,
        PreferHeight = task.PreferHeight,
        FfmpegPath = task.FfmpegPath,
        FileNamePattern = task.FileNamePattern,
        SeriesSubdirectory = task.SeriesSubdirectory,
        WriteReport = true,
    };

    /// <summary>
    /// 把任务放进待执行队列（任务本身已经在界面列表里了）。
    /// 先把状态改成「排队中」再入队，worker 才不会在状态还没落定时就把它当运行中处理。
    /// </summary>
    private async Task QueueTaskAsync(SeriesTask task)
    {
        await RunOnUiAsync(() =>
        {
            if (task.State != SeriesTaskState.Queued)
            {
                task.State = SeriesTaskState.Queued;
                task.FinishedAt = null;
            }
            RaiseChanged();
        }).ConfigureAwait(false);

        lock (_gate) _pending.Add(task);
        EnsureWorker();
    }

    /// <summary>把一部剧加入队列，立即返回（不等待下载完成）</summary>
    public SeriesTask Enqueue(SiteSeries series, SeriesDownloadOptions options)
    {
        var episodes = series.SelectedEpisodes.ToList();

        var task = new SeriesTask
        {
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
        };

        // 入队时就把分集清单建好，用户不用等开始下载才看得到
        foreach (var ep in episodes.OrderBy(e => e.Number))
            task.Episodes.Add(new TaskEpisodeItem { Number = ep.Number, Title = ep.DisplayTitle });

        task.RaiseTexts();

        lock (_gate) _pending.Add(task);

        RunOnUi(() =>
        {
            Tasks.Insert(0, task);          // 新任务排在最上面
            RaiseChanged();
        });

        EnsureWorker();
        return task;
    }

    /// <summary>取消一个任务（排队中的直接从待执行队列里摘掉）</summary>
    public void Cancel(SeriesTask task)
    {
        try { task.Cts.Cancel(); } catch { }

        lock (_gate) _pending.Remove(task);

        if (task.State == SeriesTaskState.Queued)
        {
            RunOnUi(() =>
            {
                // worker 可能刚好把它取走了，那就让 worker 自己收尾
                if (task.State != SeriesTaskState.Queued) return;
                task.State = SeriesTaskState.Canceled;
                task.Message = "已取消（尚未开始）";
                task.FinishedAt = DateTimeOffset.Now;
                RaiseChanged();
            });
        }
    }

    /// <summary>从列表里移除（运行中的会先取消）</summary>
    public void Remove(SeriesTask task)
    {
        if (task.CanCancel) Cancel(task);
        RunOnUi(() =>
        {
            Tasks.Remove(task);
            RaiseChanged();
        });
    }

    public void ClearFinished()
    {
        RunOnUi(() =>
        {
            foreach (var t in Tasks.Where(t => t.IsFinished).ToList()) Tasks.Remove(t);
            RaiseChanged();
        });
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            if (_workerRunning || _disposed) return;
            _workerRunning = true;
        }

        _ = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync()
    {
        while (true)
        {
            SeriesTask? task;
            lock (_gate)
            {
                // FIFO：先入队的先下载
                task = _pending.Count > 0 ? _pending[0] : null;
                if (task is not null) _pending.RemoveAt(0);

                if (task is null || _disposed)
                {
                    _workerRunning = false;
                    return;
                }
            }

            // 还在排队时就被取消的，直接收尾，不要真的去下载
            if (task.Cts.IsCancellationRequested)
            {
                RunOnUi(() =>
                {
                    if (task.State == SeriesTaskState.Canceled) return;
                    task.State = SeriesTaskState.Canceled;
                    task.Message = "已取消（尚未开始）";
                    task.FinishedAt = DateTimeOffset.Now;
                    RaiseChanged();
                });
                continue;
            }

            RunOnUi(() =>
            {
                task.State = SeriesTaskState.Running;
                task.Message = "开始下载…";
                RaiseChanged();
            });

            try
            {
                await RunOneAsync(task).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                RunOnUi(() =>
                {
                    task.State = SeriesTaskState.Failed;
                    task.Message = ex.Message;
                    task.FinishedAt = DateTimeOffset.Now;
                });
            }

            RunOnUi(() => RaiseChanged());
        }
    }

    private async Task RunOneAsync(SeriesTask task)
    {
        var series = task.Series;
        var options = task.Options;
        if (series is null || options is null)
        {
            RunOnUi(() =>
            {
                task.State = SeriesTaskState.Failed;
                task.Message = "任务数据缺失";
            });
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();

        // 进度回调来自后台线程 —— 必须封送回 UI 线程再写绑定属性
        var progress = new Progress<SeriesDownloadProgress>(p => RunOnUi(() =>
        {
            task.Percent = p.OverallPercent;
            task.FinishedEpisodes = p.FinishedEpisodes;
            task.SucceededEpisodes = p.SucceededEpisodes;
            task.FailedEpisodes = p.FailedEpisodes;
            task.DownloadedBytes = p.DownloadedBytes;
            task.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
            task.CurrentEpisode = p.CurrentEpisodeTitle;
            task.Elapsed = clock.Elapsed;
            task.SyncEpisodes(p.Episodes);
            task.RaiseTexts();
        }));

        var report = await _downloader
            .DownloadAsync(series, options, progress, task.Cts.Token)
            .ConfigureAwait(false);

        clock.Stop();

        // 完成后按报告回填每一集的最终状态
        RunOnUi(() =>
        {
            foreach (var ep in report.Episodes)
            {
                var item = task.Episodes.FirstOrDefault(e => e.Number == ep.Episode.Number);
                if (item is null) continue;

                item.StatusText = ep.Status switch
                {
                    EpisodeDownloadStatus.Completed => "已完成",
                    EpisodeDownloadStatus.Failed => "失败",
                    EpisodeDownloadStatus.Canceled => "已取消",
                    _ => item.StatusText,
                };
                if (ep.Status == EpisodeDownloadStatus.Completed) item.Percent = 100;
                item.Bytes = ep.OutputBytes;
                item.Error = ep.Error;
            }

            task.Report = report;
            task.ReportPath = report.ReportPath;
            task.Elapsed = report.Elapsed;
            task.Percent = 100;
            task.SpeedBytesPerSecond = 0;
            task.FinishedEpisodes = report.Episodes.Count;
            task.SucceededEpisodes = report.SucceededCount;
            task.FailedEpisodes = report.FailedCount + report.CanceledCount;
            task.FinishedAt = DateTimeOffset.Now;
            task.CurrentEpisode = null;

            task.State = report switch
            {
                { CanceledCount: > 0 } => SeriesTaskState.Canceled,
                { FailedCount: 0 } => SeriesTaskState.Completed,
                { SucceededCount: > 0 } => SeriesTaskState.PartiallyCompleted,
                _ => SeriesTaskState.Failed,
            };

            task.Message = task.State switch
            {
                SeriesTaskState.Completed =>
                    $"全部 {report.SucceededCount} 集完成，{report.TotalBytes / 1024.0 / 1024.0:0.0} MB",
                SeriesTaskState.PartiallyCompleted =>
                    $"完成 {report.SucceededCount} 集，失败 {report.FailedCount} 集（暂存目录已保留，可重试）",
                SeriesTaskState.Canceled => $"已取消（完成 {report.SucceededCount} 集）",
                _ => $"失败 {report.FailedCount} 集",
            };

            task.RaiseTexts();
        });
    }

    /// <summary>重试：把失败的那些集再排一次队（暂存目录会被清单指纹自动校验）</summary>
    public SeriesTask? RetryFailed(SeriesTask task)
    {
        if (task.Report is null || task.Series is null || task.Options is null) return null;

        var failedNumbers = task.Report.Episodes
            .Where(e => e.Status is EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled)
            .Select(e => e.Episode.Number)
            .ToHashSet();

        if (failedNumbers.Count == 0) return null;

        // 复制一份剧集数据，只勾选失败的那些集
        var retrySeries = SeriesDownloader.SelectOnly(task.Series, failedNumbers);
        if (retrySeries is null) return null;

        return Enqueue(retrySeries, task.Options);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _pending.Clear();
        }

        foreach (var t in Tasks.Where(t => t.CanCancel))
        {
            try { t.Cts.Cancel(); } catch { }
        }

        _downloader.Dispose();
    }
}
