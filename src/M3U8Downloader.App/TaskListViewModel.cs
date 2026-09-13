using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using M3U8Downloader.Core.Tasks;

namespace M3U8Downloader;

/// <summary>
/// 「下载任务」面板的视图模型。
/// 直接暴露 <see cref="DownloadTaskManager.Tasks"/>，列表项绑定 <see cref="SeriesTask"/>。
/// </summary>
public sealed class TaskListViewModel : INotifyPropertyChanged
{
    public DownloadTaskManager Manager { get; }

    public TaskListViewModel(DownloadTaskManager manager)
    {
        Manager = manager;
        Manager.Tasks.CollectionChanged += OnTasksChanged;
        Manager.Changed += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<SeriesTask> Tasks => Manager.Tasks;

    private void OnTasksChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (SeriesTask t in e.NewItems) t.PropertyChanged += OnTaskPropertyChanged;
        }
        if (e.OldItems is not null)
        {
            foreach (SeriesTask t in e.OldItems) t.PropertyChanged -= OnTaskPropertyChanged;
        }
        Refresh();
    }

    private void OnTaskPropertyChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(HasTasks));
        OnPropertyChanged(nameof(HasNoTasks));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(UploadSpeedText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(ShowUploadSpeed));
        OnPropertyChanged(nameof(HasNotice));
    }

    private string? _notice;

    /// <summary>一次性提示（例如「已恢复上次的任务」）。留空表示不显示。</summary>
    public string? Notice
    {
        get => _notice;
        private set
        {
            if (_notice == value) return;
            _notice = value;
            OnPropertyChanged(nameof(Notice));
            OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => !string.IsNullOrWhiteSpace(_notice);

    /// <summary>在任务面板上提示一句（留空则清除）</summary>
    public void SetNotice(string? text) => Notice = text;

    public bool HasTasks => Tasks.Count > 0;
    public bool HasNoTasks => !HasTasks;

    public string SummaryText
    {
        get
        {
            if (Tasks.Count == 0) return "";

            var running = Tasks.Count(t => t.State == SeriesTaskState.Running);
            var queued = Tasks.Count(t => t.State == SeriesTaskState.Queued);
            var paused = Tasks.Count(t => t.State == SeriesTaskState.Paused);
            var done = Tasks.Count(t => t.State == SeriesTaskState.Completed);
            var failed = Tasks.Count(t => t.State is SeriesTaskState.Failed or SeriesTaskState.PartiallyCompleted);

            var parts = new List<string> { $"共 {Tasks.Count} 个任务" };
            if (running > 0) parts.Add($"下载中 {running}");
            if (queued > 0) parts.Add($"排队 {queued}");
            if (paused > 0) parts.Add($"已暂停 {paused}");
            if (done > 0) parts.Add($"已完成 {done}");
            if (failed > 0) parts.Add($"有失败 {failed}");
            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>形如「↓ 12.34 MB/s」</summary>
    public string SpeedText => $"↓ {SeriesTask.FormatSpeed(Manager.TotalDownloadSpeed)}";

    /// <summary>
    /// 上传速度。本程序只下载、不上传任何数据，因此恒为 0；
    /// 保留这一项是为了让状态栏信息结构与常见下载工具一致。
    /// 界面上只在真的有任务在跑时才显示它（见 <see cref="ShowUploadSpeed"/>）——
    /// 空闲时挂一个「↑ 0 B/s」纯属占地方。
    /// </summary>
    public string UploadSpeedText => $"↑ {SeriesTask.FormatSpeed(Manager.TotalUploadSpeed)}";

    /// <summary>是否在下载中（用于决定要不要显示上传速度）</summary>
    public bool ShowUploadSpeed => IsBusy;

    /// <summary>是否有任务在跑</summary>
    public bool IsBusy => Manager.RunningCount > 0;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
