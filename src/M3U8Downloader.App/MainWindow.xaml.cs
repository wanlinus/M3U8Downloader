using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Settings;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace M3U8Downloader;

public sealed partial class MainWindow : Window
{
    private readonly MainViewModel _single;
    private readonly SeriesBatchViewModel _batch;

    public MainWindow()
    {
        InitializeComponent();

        Title = "M3U8 视频下载器";

        // 设置一个合适的初始窗口尺寸
        try { AppWindow.Resize(new SizeInt32(1180, 840)); } catch { }

        _single = new MainViewModel(DispatcherQueue);
        _batch = new SeriesBatchViewModel(DispatcherQueue);

        // 两个面板各自绑定自己的 ViewModel，互不干扰
        SinglePanel.DataContext = _single;
        BatchPanel.DataContext = _batch;

        // 日志追加后自动滚到底部
        _single.Logs.CollectionChanged += (_, __) => ScrollLogToEnd();
        Closed += (_, __) => _batch.Dispose();

        // 应用已保存的设置（输出目录、并发、跳过广告等）
        ApplySettings(AppSettingsStore.Load());

        // 支持 --mode=batch 直接以批量模式启动
        ApplyStartupMode();
    }

    // ==================== 设置 / 关于 ====================

    private AppSettings _settings = AppSettings.Default;

    /// <summary>把设置套用到两个面板的默认值上</summary>
    private void ApplySettings(AppSettings settings)
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
                SingleModeButton.IsChecked = false;
                BatchModeButton.IsChecked = true;
                SinglePanel.Visibility = Visibility.Collapsed;
                BatchPanel.Visibility = Visibility.Visible;
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
        SinglePanel.Visibility = Visibility.Visible;
        BatchPanel.Visibility = Visibility.Collapsed;
    }

    private void OnModeBatch(object sender, RoutedEventArgs e)
    {
        SingleModeButton.IsChecked = false;
        BatchModeButton.IsChecked = true;
        SinglePanel.Visibility = Visibility.Collapsed;
        BatchPanel.Visibility = Visibility.Visible;
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

    private async void OnBatchStart(object sender, RoutedEventArgs e)
    {
        BatchStartButton.IsEnabled = false;
        try
        {
            await _batch.StartDownloadAsync();
        }
        finally
        {
            BatchStartButton.IsEnabled = _batch.CanDownload;
        }
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
