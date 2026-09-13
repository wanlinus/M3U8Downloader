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
    }

    public bool HasTasks => Tasks.Count > 0;
    public bool HasNoTasks => !HasTasks;

    public string SummaryText
    {
        get
        {
            if (Tasks.Count == 0) return "";

            var running = Tasks.Count(t => t.State == SeriesTaskState.Running);
            var queued = Tasks.Count(t => t.State == SeriesTaskState.Queued);
            var done = Tasks.Count(t => t.State == SeriesTaskState.Completed);
            var failed = Tasks.Count(t => t.State is SeriesTaskState.Failed or SeriesTaskState.PartiallyCompleted);

            var parts = new List<string> { $"共 {Tasks.Count} 个任务" };
            if (running > 0) parts.Add($"下载中 {running}");
            if (queued > 0) parts.Add($"排队 {queued}");
            if (done > 0) parts.Add($"已完成 {done}");
            if (failed > 0) parts.Add($"有失败 {failed}");
            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
