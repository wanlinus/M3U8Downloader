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

/// <summary>
/// 一个「整部剧」下载任务。
/// 实现了 INotifyPropertyChanged，界面可以直接绑定，不必再包一层。
/// </summary>
public sealed class SeriesTask : INotifyPropertyChanged
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..8];

    public required string Title { get; init; }
    public required string SiteName { get; init; }
    public required string PageUrl { get; init; }
    public required string OutputDirectory { get; init; }
    public int TotalEpisodes { get; init; }
    public int FirstEpisodeNumber { get; init; } = 1;

    /// <summary>使用的播放源名称</summary>
    public string? SourceName { get; init; }

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
    public long DownloadedBytes { get => _downloadedBytes; internal set => Set(ref _downloadedBytes, value); }

    private double _speed;
    public double SpeedBytesPerSecond
    {
        get => _speed;
        internal set { if (Set(ref _speed, value)) OnPropertyChanged(nameof(SpeedText)); }
    }

    private string? _currentEpisode;
    public string? CurrentEpisode { get => _currentEpisode; internal set => Set(ref _currentEpisode, value); }

    private string? _message;
    /// <summary>状态说明文字。界面也会用它显示临时反馈（如"报告文件不存在"），因此公开可写。</summary>
    public string? Message { get => _message; set => Set(ref _message, value); }

    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.Now;

    private DateTimeOffset? _finishedAt;
    public DateTimeOffset? FinishedAt { get => _finishedAt; internal set => Set(ref _finishedAt, value); }

    private TimeSpan _elapsed;
    public TimeSpan Elapsed { get => _elapsed; internal set => Set(ref _elapsed, value); }

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

    public string SpeedText => _speed switch
    {
        >= 1024 * 1024 => $"{_speed / 1024 / 1024:0.00} MB/s",
        >= 1024 => $"{_speed / 1024:0.0} KB/s",
        > 0 => $"{_speed:0} B/s",
        _ => "",
    };

    /// <summary>形如 "6/16 集 · 12.3 MB/s"</summary>
    public string ProgressText
    {
        get
        {
            var parts = new List<string> { $"{_finishedEpisodes}/{TotalEpisodes} 集" };
            if (_failedEpisodes > 0) parts.Add($"失败 {_failedEpisodes}");
            if (_speed > 0 && IsRunning) parts.Add(SpeedText);
            if (_downloadedBytes > 0) parts.Add($"{_downloadedBytes / 1024.0 / 1024.0:0.0} MB");
            return string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        }
    }

    /// <summary>形如 "第03集" 的当前集提示</summary>
    public string CurrentEpisodeText => IsRunning && !string.IsNullOrWhiteSpace(_currentEpisode)
        ? $"正在下载 {_currentEpisode}"
        : "";

    // 上面几个组合文本依赖多个字段，统一在这里刷新
    internal void RaiseTexts()
    {
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(CurrentEpisodeText));
    }

    // ---------------- 内部：运行所需的数据 ----------------

    /// <summary>剧集信息（含勾选状态），不参与界面绑定</summary>
    internal SiteSeries? Series { get; set; }

    /// <summary>下载参数</summary>
    internal SeriesDownloadOptions? Options { get; set; }

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
/// </summary>
public sealed class DownloadTaskManager : IDisposable
{
    private readonly SeriesDownloader _downloader;
    private readonly object _gate = new();
    private bool _workerRunning;
    private bool _disposed;

    public DownloadTaskManager(SeriesDownloader? downloader = null) =>
        _downloader = downloader ?? new SeriesDownloader();

    /// <summary>全部任务（最新的在最前）</summary>
    public ObservableCollection<SeriesTask> Tasks { get; } = new();

    /// <summary>任务状态有任何变化时触发（供存盘/通知使用）</summary>
    public event EventHandler? Changed;

    public int RunningCount => Tasks.Count(t => t.State == SeriesTaskState.Running);
    public int QueuedCount => Tasks.Count(t => t.State == SeriesTaskState.Queued);

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
            Series = series,
            Options = options,
        };
        task.RaiseTexts();

        Tasks.Insert(0, task);          // 新任务排在最上面
        Changed?.Invoke(this, EventArgs.Empty);

        EnsureWorker();
        return task;
    }

    /// <summary>取消一个任务（排队中的直接标记为取消）</summary>
    public void Cancel(SeriesTask task)
    {
        try { task.Cts.Cancel(); } catch { }
        if (task.State == SeriesTaskState.Queued)
        {
            task.State = SeriesTaskState.Canceled;
            task.Message = "已取消（尚未开始）";
            task.FinishedAt = DateTimeOffset.Now;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>从列表里移除（运行中的会先取消）</summary>
    public void Remove(SeriesTask task)
    {
        if (task.CanCancel) Cancel(task);
        Tasks.Remove(task);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ClearFinished()
    {
        foreach (var t in Tasks.Where(t => t.IsFinished).ToList()) Tasks.Remove(t);
        Changed?.Invoke(this, EventArgs.Empty);
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
                task = Tasks.LastOrDefault(t => t.State == SeriesTaskState.Queued);
                if (task is null)
                {
                    _workerRunning = false;
                    return;
                }
                task.State = SeriesTaskState.Running;
                task.Message = "开始下载…";
            }

            Changed?.Invoke(this, EventArgs.Empty);

            try
            {
                await RunOneAsync(task).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                task.State = SeriesTaskState.Failed;
                task.Message = ex.Message;
                task.FinishedAt = DateTimeOffset.Now;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task RunOneAsync(SeriesTask task)
    {
        var series = task.Series;
        var options = task.Options;
        if (series is null || options is null)
        {
            task.State = SeriesTaskState.Failed;
            task.Message = "任务数据缺失";
            return;
        }

        var progress = new Progress<SeriesDownloadProgress>(p =>
        {
            task.Percent = p.OverallPercent;
            task.FinishedEpisodes = p.FinishedEpisodes;
            task.SucceededEpisodes = p.SucceededEpisodes;
            task.FailedEpisodes = p.FailedEpisodes;
            task.DownloadedBytes = p.DownloadedBytes;
            task.SpeedBytesPerSecond = p.SpeedBytesPerSecond;
            task.CurrentEpisode = p.CurrentEpisodeTitle;
            task.Elapsed = TimeSpan.FromSeconds(0);   // 由下方统一赋值
            task.RaiseTexts();
        });

        var report = await _downloader
            .DownloadAsync(series, options, progress, task.Cts.Token)
            .ConfigureAwait(false);

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
            SeriesTaskState.Completed => $"全部 {report.SucceededCount} 集完成，{report.TotalBytes / 1024.0 / 1024.0:0.0} MB",
            SeriesTaskState.PartiallyCompleted =>
                $"完成 {report.SucceededCount} 集，失败 {report.FailedCount} 集（暂存目录已保留，可重试）",
            SeriesTaskState.Canceled => $"已取消（完成 {report.SucceededCount} 集）",
            _ => $"失败 {report.FailedCount} 集",
        };

        task.RaiseTexts();
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
        lock (_gate) _disposed = true;

        foreach (var t in Tasks.Where(t => t.CanCancel))
        {
            try { t.Cts.Cancel(); } catch { }
        }

        _downloader.Dispose();
    }
}
