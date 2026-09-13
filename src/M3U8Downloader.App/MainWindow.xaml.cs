using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Settings;
using M3U8Downloader.Core.Sites;
using M3U8Downloader.Core.Tasks;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace M3U8Downloader;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _single;
    private readonly SeriesBatchViewModel _batch;

    /// <summary>下载任务队列（串行执行，加入后立刻返回）</summary>
    private readonly DownloadTaskManager _taskManager;
    private readonly TaskListViewModel _tasks;

    public MainWindow()
    {
        InitializeComponent();

        Title = "M3U8 视频下载器";

        // 设置一个合适的初始窗口尺寸
        try { AppWindow.Resize(new SizeInt32(1180, 840)); } catch { }

        // 任务状态是从后台线程改的，必须封送回 UI 线程，否则界面不会刷新（表现为「卡在下载中」）
        _taskManager = new DownloadTaskManager(null, a => DispatcherQueue.TryEnqueue(() => a()));

        _single = new MainViewModel(DispatcherQueue);
        _batch = new SeriesBatchViewModel(DispatcherQueue);
        _tasks = new TaskListViewModel(_taskManager);

        // 三个面板各自绑定自己的 ViewModel，互不干扰
        SinglePanel.DataContext = _single;
        BatchPanel.DataContext = _batch;
        TaskPanel.DataContext = _tasks;

        // 日志追加后自动滚到底部
        _single.Logs.CollectionChanged += (_, __) => ScrollLogToEnd();
        Closed += (_, __) =>
        {
            _batch.Dispose();
            _taskManager.Dispose();
        };

        // 应用已保存的设置（输出目录、并发、跳过广告等）
        ApplySettings(AppSettingsStore.Load());

        // 支持 --mode=batch 直接以批量模式启动
        ApplyStartupMode();
    }

    // ==================== 设置 / 关于 ====================

    private AppSettings _settings = AppSettings.Default;

    /// <summary>把设置套用到各面板的默认值上</summary>
    private async void ApplySettings(AppSettings settings)
    {
        _settings = settings;

        var outputDir = string.IsNullOrWhiteSpace(settings.DefaultOutputDirectory)
            ? KnownFolders.Downloads
            : settings.DefaultOutputDirectory!;

        _single.OutputDirectory = outputDir;
        _single.Concurrency = settings.SegmentConcurrency;

        _batch.OutputDirectory = outputDir;
        _batch.EpisodeConcurrency = settings.EpisodeConcurrency;
        _batch.SegmentConcurrency = settings.SegmentConcurrency;
        _batch.AutoSkipAds = settings.AutoSkipInvalidSegments;
        _batch.SeriesSubdirectory = settings.SeriesSubdirectory;

        // 有 ffmpeg 才能把产物转成 MP4；没有就保留 TS（报告里会说明）
        try
        {
            var status = await FfmpegLocator.DetectAsync(settings);
            _batch.FfmpegPath = status.FfmpegPath;
        }
        catch
        {
            _batch.FfmpegPath = settings.FfmpegPath;
        }
    }

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsDialog(WindowNative.GetWindowHandle(this), AppSettingsStore.Load())
        {
            XamlRoot = Content.XamlRoot,
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary || !dialog.Saved) return;

        var settings = dialog.Result;
        if (!AppSettingsStore.Save(settings))
        {
            _single.StatusText = "设置保存失败（无法写入 " + AppSettingsStore.SettingsFilePath + "）";
            return;
        }

        ApplySettings(settings);
        _single.StatusText = $"设置已保存：{AppSettingsStore.SettingsFilePath}";
    }

    private async void OnOpenAbout(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }

    /// <summary>根据命令行参数决定初始模式</summary>
    private void ApplyStartupMode()
    {
        try
        {
            var args = App.StartupArgs;
            var wantsBatch = args.Any(a => a.Equals("--batch", StringComparison.OrdinalIgnoreCase)
                                        || a.Equals("--mode=batch", StringComparison.OrdinalIgnoreCase));
            if (wantsBatch)
            {
                OnModeBatch(this, new RoutedEventArgs());
            }
        }
        catch { }
    }

    private void ScrollLogToEnd()
    {
        if (LogScroll is null) return;
        LogScroll.UpdateLayout();
        LogScroll.ChangeView(null, LogScroll.ScrollableHeight, null, true);
    }

    // ==================== 模式切换 ====================

    private void OnModeSingle(object sender, RoutedEventArgs e)
    {
        SingleModeButton.IsChecked = true;
        BatchModeButton.IsChecked = false;
        TaskModeButton.IsChecked = false;
        SinglePanel.Visibility = Visibility.Visible;
        BatchPanel.Visibility = Visibility.Collapsed;
        TaskPanel.Visibility = Visibility.Collapsed;
    }

    private void OnModeBatch(object sender, RoutedEventArgs e)
    {
        SingleModeButton.IsChecked = false;
        BatchModeButton.IsChecked = true;
        TaskModeButton.IsChecked = false;
        SinglePanel.Visibility = Visibility.Collapsed;
        BatchPanel.Visibility = Visibility.Visible;
        TaskPanel.Visibility = Visibility.Collapsed;
    }

    private void OnModeTasks(object sender, RoutedEventArgs e)
    {
        SingleModeButton.IsChecked = false;
        BatchModeButton.IsChecked = false;
        TaskModeButton.IsChecked = true;
        SinglePanel.Visibility = Visibility.Collapsed;
        BatchPanel.Visibility = Visibility.Collapsed;
        TaskPanel.Visibility = Visibility.Visible;
    }

    // ==================== 下载任务 ====================

    private void OnClearFinishedTasks(object sender, RoutedEventArgs e) => _taskManager.ClearFinished();

    private static SeriesTask? TaskOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SeriesTask;

    private void OnTaskCancel(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) _taskManager.Cancel(task);
    }

    private void OnTaskRemove(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) _taskManager.Remove(task);
    }

    private void OnTaskRetryFailed(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is not { } task) return;

        var retry = _taskManager.RetryFailed(task);
        if (retry is null)
        {
            task.Message = "没有需要重试的分集。";
            return;
        }

        task.Message = $"已把失败的 {retry.TotalEpisodes} 集重新加入队列。";
        OnModeTasks(sender, e);
    }

    private async void OnTaskOpenReport(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is not { ReportPath: { Length: > 0 } path } task) return;

        try
        {
            if (File.Exists(path))
            {
                // 报告是 Markdown，交给系统默认关联程序打开
                await Windows.System.Launcher.LaunchFileAsync(
                    await Windows.Storage.StorageFile.GetFileFromPathAsync(path));
                return;
            }

            task.Message = $"报告文件不存在：{path}";
        }
        catch (Exception ex)
        {
            task.Message = "打开报告失败：" + ex.Message;
        }
    }

    private async void OnTaskOpenFolder(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is not { } task) return;

        try
        {
            var dir = task.OutputDirectory;
            if (!Directory.Exists(dir))
            {
                // 目录还没建出来（任务还在排队），退而打开上级
                dir = Path.GetDirectoryName(dir) ?? dir;
            }
            if (Directory.Exists(dir))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(dir);
            }
            else
            {
                task.Message = $"目录不存在：{task.OutputDirectory}";
            }
        }
        catch (Exception ex)
        {
            task.Message = "打开目录失败：" + ex.Message;
        }
    }

    // ==================== 单文件下载 ====================

    private async void OnSingleStart(object sender, RoutedEventArgs e)
    {
        SingleStartButton.IsEnabled = false;
        try
        {
            await _single.StartAsync();
        }
        finally
        {
            SingleStartButton.IsEnabled = _single.CanStart;
        }
    }

    private void OnSingleCancel(object sender, RoutedEventArgs e) => _single.Cancel();

    private async void OnPickSingleFolder(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (path != null) _single.OutputDirectory = path;
    }

    // ==================== 站点批量下载 ====================

    private async void OnParseSeries(object sender, RoutedEventArgs e)
    {
        // 不再弹窗打断：多个播放源时，上方工具栏的「视频源」下拉里直接选
        await _batch.ParseAsync();
    }

    /// <summary>
    /// 「加入下载队列」：把当前这部剧交给任务队列，然后**清空当前页面**并跳到「下载任务」。
    /// 队列在后台串行执行，所以这里立刻返回，用户可以马上贴下一个地址。
    /// </summary>
    private void OnBatchStart(object sender, RoutedEventArgs e)
    {
        var task = _batch.EnqueueTo(_taskManager);
        if (task is null) return;

        _batch.ClearAfterEnqueue();
        OnModeTasks(sender, e);
    }

    private void OnBatchCancel(object sender, RoutedEventArgs e) => _batch.Cancel();

    private void OnSelectAll(object sender, RoutedEventArgs e) => _batch.SelectAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => _batch.SelectAll(false);

    private void OnApplySelection(object sender, RoutedEventArgs e)
        => _batch.ApplySelectionSpec(SelectionSpecBox.Text);

    private async void OnPickBatchFolder(object sender, RoutedEventArgs e)
    {
        var path = await PickFolderAsync();
        if (path != null) _batch.OutputDirectory = path;
    }

    // ==================== 公共 ====================

    private async Task<string?> PickFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.SuggestedStartLocation = PickerLocationId.Downloads;
            picker.FileTypeFilter.Add("*");

            var hwnd = WindowNative.GetWindowHandle(this);
            InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
        catch (Exception ex)
        {
            _single.StatusText = "选择目录失败：" + ex.Message;
            return null;
        }
    }
}
