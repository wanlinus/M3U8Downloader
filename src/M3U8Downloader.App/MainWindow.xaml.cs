using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
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

    /// <summary>
    /// 点窗口右上角的关闭时是否只是隐藏到托盘（来自设置）。
    /// 托盘图标起不来时 App 会把它置成 false —— 免得窗口藏起来又找不回来。
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    public MainWindow()
    {
        InitializeComponent();

        // 标题栏和界面顶部都带上版本号：反馈问题时用户一眼就能报出用的是哪一版
        AppTitleText.Text = AppInfo.ProductName;
        AppVersionText.Text = $"v{AppInfo.Version}";
        Title = $"{AppInfo.ProductName} v{AppInfo.Version}";

        // 设置一个合适的初始窗口尺寸
        try { AppWindow.Resize(new SizeInt32(1180, 840)); } catch { }

        // 任务状态是从后台线程改的，必须封送回 UI 线程，否则界面不会刷新（表现为「卡在下载中」）
        // store：任务列表落盘到 %APPDATA%\M3U8Downloader\tasks.json，重开程序能接着下
        // 诊断日志：把「哪一轮领到了哪几集、何时结束」写进 %APPDATA%\M3U8Downloader\logs\，
        //           排查「暂停了还在下」「继续下载停不下来」这类问题时就靠它
        _taskManager = new DownloadTaskManager(null, a => DispatcherQueue.TryEnqueue(() => a()), new TaskStore())
        {
            Diagnostics = TaskDiagnostics.Create("tasks"),
        };

        // 跳过广告要在下载完成时主动说一声 —— 理由见 CheckSkippedAdsNotice
        _taskManager.Changed += (_, _) => DispatcherQueue.TryEnqueue(CheckSkippedAdsNotice);

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
            // 关窗前把当前进度写进盘：下次打开才能接着下，而不是从头再来
            _taskManager.SaveNow();
            _batch.Dispose();
            _taskManager.Dispose();
        };

        // 应用已保存的设置（输出目录、并发、跳过广告等）
        ApplySettings(AppSettingsStore.Load());

        // 支持 --mode=batch 直接以批量模式启动
        ApplyStartupMode();

        // 恢复上次没下完的任务（关掉程序再打开会接着下）
        RestorePreviousTasks();
    }

    /// <summary>
    /// 启动时把上次的任务列表读回来，未完成的自动排队续传。
    /// 已完成的任务只恢复显示，不会重新下载。
    /// </summary>
    private async void RestorePreviousTasks()
    {
        try
        {
            var count = await _taskManager.RestoreAsync();
            if (count == 0) return;

            _tasks.SetNotice($"已恢复上次的 {count} 个任务；没下完的会接着下（已完成的集不会重下）");
            ShowTasksPanel();
        }
        catch (Exception ex)
        {
            _tasks.SetNotice("恢复上次任务失败：" + ex.Message);
        }
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
        _single.FullDecodeCheck = settings.FullDecodeCheck;

        MinimizeToTrayOnClose = settings.MinimizeToTrayOnClose;

        _batch.OutputDirectory = outputDir;
        _batch.EpisodeConcurrency = settings.EpisodeConcurrency;
        _batch.SegmentConcurrency = settings.SegmentConcurrency;
        _batch.AutoSkipAds = settings.AutoSkipInvalidSegments;
        _batch.SeriesSubdirectory = settings.SeriesSubdirectory;
        _batch.FullDecodeCheck = settings.FullDecodeCheck;

        // 有 ffmpeg 才能把产物转成 MP4；没有就保留 TS（报告里会说明）。
        // 单文件模式与站点批量模式走同一条流水线，所以两边都要拿到这个路径。
        try
        {
            var status = await FfmpegLocator.DetectAsync(settings);
            _batch.FfmpegPath = status.FfmpegPath;
            _single.FfmpegPath = status.FfmpegPath;
        }
        catch
        {
            _batch.FfmpegPath = settings.FfmpegPath;
            _single.FfmpegPath = settings.FfmpegPath;
        }
    }

    /// <summary>
    /// 当前打开的模态对话框。
    /// WinUI 同时只允许一个 ContentDialog，第二次 ShowAsync 会抛
    /// 「Only a single ContentDialog can be open at any time」——
    /// 而 OnOpenSettings 是 async void，异常没人接，直接崩掉整个程序。
    /// 双击「设置」就能触发，所以这里挡一道。
    /// </summary>
    private ContentDialog? _openDialog;

    private async void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if (_openDialog is not null) return;

        var dialog = new SettingsDialog(WindowNative.GetWindowHandle(this), AppSettingsStore.Load())
        {
            XamlRoot = Content.XamlRoot,
        };

        _openDialog = dialog;
        try
        {
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
        catch (Exception ex)
        {
            // 对话框本身出问题也不该把程序带走
            _single.StatusText = "打开设置失败：" + ex.Message;
        }
        finally
        {
            _openDialog = null;
        }
    }

    private async void OnOpenAbout(object sender, RoutedEventArgs e)
    {
        if (_openDialog is not null) return;

        var dialog = new AboutDialog { XamlRoot = Content.XamlRoot };
        _openDialog = dialog;
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _single.StatusText = "打开关于失败：" + ex.Message;
        }
        finally
        {
            _openDialog = null;
        }
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

    private void OnModeTasks(object sender, RoutedEventArgs e) => ShowTasksPanel();

    private void ShowTasksPanel()
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

    /// <summary>打开诊断日志目录（排查"暂停了还在下""继续下载停不下来"这类问题时用）</summary>
    private async void OnOpenTaskLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _taskManager.Diagnostics.FilePath;
            var dir = path is { Length: > 0 }
                ? Path.GetDirectoryName(path)!
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "M3U8Downloader", "logs");

            Directory.CreateDirectory(dir);
            await Windows.System.Launcher.LaunchFolderPathAsync(dir);
        }
        catch (Exception ex)
        {
            _tasks.SetNotice("打开日志目录失败：" + ex.Message);
        }
    }

    private static SeriesTask? TaskOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as SeriesTask;

    private void OnTaskCancel(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) _taskManager.Cancel(task);
    }

    /// <summary>
    /// 暂停：停下这一轮下载，已下载的分片留在暂存目录里。
    /// 之后点「继续下载」会重新解析站点、只补没下完的集。
    /// </summary>
    private void OnTaskPause(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) _taskManager.Pause(task);
    }

    /// <summary>继续下载：重新解析站点，只补下没完成的集</summary>
    private async void OnTaskResume(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is not { } task) return;

        try
        {
            if (!await _taskManager.ResumeAsync(task))
                task.Message ??= "没有需要继续下载的分集。";
        }
        catch (Exception ex)
        {
            task.Message = "继续下载失败：" + ex.Message;
        }
    }

    private void OnTaskRemove(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) _taskManager.Remove(task);
    }

    /// <summary>
    /// 重试失败的分集：**在原任务上重试**，不再新建一个任务
    /// （早先的做法会往列表里插入一条重复的任务，同一个任务看起来出现两份）。
    /// </summary>
    private async void OnTaskRetryFailed(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is not { } task) return;

        try
        {
            var retried = await _taskManager.RetryFailedAsync(task);
            if (retried is null)
            {
                task.Message = "没有需要重试的分集。";
                return;
            }

            OnModeTasks(sender, e);
        }
        catch (Exception ex)
        {
            task.Message = "重试失败：" + ex.Message;
        }
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
            // 打开的是**实际产物目录**（…\交锋 - 努努影院），而不是用户填的根目录 ——
            // 点「打开目录」就是想看下好的视频在哪
            var dir = task.DisplayDirectory;
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
                task.Message = $"目录不存在：{task.DisplayDirectory}";
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

        if (_batch.HasSeries)
        {
            // 识别成功后让结果区淡入：列表是整批出现的，没有过渡会显得很突兀
            PlayResultsEntrance();
            return;
        }

        // 识别失败必须弹窗 —— 理由见 ShowParseFailureAsync
        if (_batch.LastError is { Length: > 0 } error)
            await ShowParseFailureAsync(error, _batch.LastErrorNeedsProxy);
    }

    /// <summary>
    /// 已经为哪些任务弹过「跳过广告」的提示（任务 Id）。
    /// 用界面侧的 HashSet 而不是给任务加标记位：这是纯粹的界面行为，不该污染 Core 的模型。
    /// </summary>
    private readonly HashSet<string> _adNoticeShown = new();

    /// <summary>
    /// 任务下载完成、且跳过过插播广告时，主动弹一次提示。
    ///
    /// 为什么非弹不可：用户**察觉不到**"少了几十秒"—— 不做声的话这个功能做了也等于没做，
    /// 用户只会觉得"怎么比别人短一截"，甚至怀疑下载丢了内容。
    /// 这是本工具少数几个"替用户省了事"的功能，得让他看见。
    /// </summary>
    private void CheckSkippedAdsNotice()
    {
        if (_openDialog is not null) return;   // 正开着一个弹窗就别插队

        foreach (var task in _taskManager.Tasks)
        {
            if (!task.HasSkippedAds || !task.IsFinished) continue;
            if (!_adNoticeShown.Add(task.Id)) continue;   // 这个任务已经提示过

            _ = ShowInfoAsync("已自动跳过插播广告",
                $"《{task.Title}》本次下载识别并跳过了 {task.SkippedAdSegments} 个插播广告分片。\n\n" +
                "识别依据：这些分片的编号跳出了正片的连续编号带，且两侧都有编码断层标记。\n\n" +
                "产物里不会出现这些内容，所以时长会比源站标称的短一点 —— 这是正常的。",
                offerSettings: false);
            return;   // 一次只提示一个，其余的下次 Changed 再说
        }
    }

    /// <summary>
    /// 识别失败的提示框。
    ///
    /// 为什么非弹不可：用户贴完地址点「识别」，视线多半还停在输入框那一片，
    /// 只在下面写一行状态文字的话，"没反应"和"失败了"他分不出来。
    /// 尤其是失败原因还需要他动手改设置的时候（典型：站点要过代理、而代理没开）。
    ///
    /// 代理类失败多给一个「打开设置」按钮：那种情况下用户要做的就是去改代理，
    /// 省得他再自己找一遍入口。
    /// </summary>
    private Task ShowParseFailureAsync(string message, bool needsProxy) =>
        ShowInfoAsync("识别失败", message, offerSettings: needsProxy);

    /// <summary>
    /// 通用提示框。<paramref name="offerSettings"/> 为真时把主按钮换成「打开设置」——
    /// 用在"用户下一步动作就是去改设置"的场合（代理没开、FFmpeg 没装）。
    ///
    /// 全程受 <see cref="_openDialog"/> 保护：WinUI 同时只允许一个 ContentDialog，
    /// 第二次 ShowAsync 会直接抛异常，而调用方多半是 async void，没人接就崩。
    /// </summary>
    private async Task ShowInfoAsync(string title, string message, bool offerSettings)
    {
        if (_openDialog is not null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = offerSettings ? "打开设置" : "知道了",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (offerSettings) dialog.CloseButtonText = "关闭";

        _openDialog = dialog;
        var openSettings = false;
        try
        {
            var result = await dialog.ShowAsync();
            openSettings = offerSettings && result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // 弹窗自身出问题也不该把程序带走；状态栏里已经写了原因
            _single.StatusText = "提示框打开失败：" + ex.Message;
        }
        finally
        {
            // 必须先释放再开设置，否则 OnOpenSettings 会被自己的守卫挡掉
            _openDialog = null;
        }

        if (openSettings) OnOpenSettings(this, new RoutedEventArgs());
    }

    /// <summary>
    /// 把已经下好的任务补转成 MP4。
    ///
    /// 存在的理由：下载那会儿还没装 FFmpeg 的话，产物是按 TS 留下的；
    /// 事后装好 FFmpeg 再想转，原来的流程里没有任何补救手段（只能整部重下）。
    /// 这个按钮就是那条补救路径 —— 不重下，只是就地换个封装。
    /// </summary>
    private async void OnTaskRemuxMp4(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SeriesTask task }) return;
        if (sender is not Button button) return;

        // 没装 FFmpeg 就先把他引到设置里，而不是点了一下什么都没发生
        var ffmpegPath = _batch.FfmpegPath ?? _single.FfmpegPath;
        if (string.IsNullOrWhiteSpace(ffmpegPath) || !File.Exists(ffmpegPath))
        {
            await ShowInfoAsync("需要 FFmpeg",
                "转 MP4 需要 FFmpeg，但当前没有检测到。\n\n" +
                "请到「设置 → FFmpeg」点一次「自动下载 FFmpeg」，" +
                "或手动指定 ffmpeg.exe 的位置，然后再回来点这个按钮。",
                offerSettings: true);
            return;
        }

        button.IsEnabled = false;
        try
        {
            // Progress 在主线程创建，回调会自动封送回 UI 线程
            var progress = new Progress<string>(m => task.Message = m);
            using var cts = new CancellationTokenSource();
            var outcome = await _taskManager.RemuxToMp4Async(task, ffmpegPath!, progress, cts.Token);

            task.Message = outcome.Describe();
            var detail = outcome.Messages.Count > 0
                ? "\n\n" + string.Join("\n", outcome.Messages.Take(10)) +
                  (outcome.Messages.Count > 10 ? $"\n…另有 {outcome.Messages.Count - 10} 条" : "")
                : "";

            await ShowInfoAsync(
                outcome.Failed > 0 ? "转 MP4 完成（有失败）" : "转 MP4 完成",
                outcome.Describe() + detail,
                offerSettings: false);
        }
        catch (OperationCanceledException)
        {
            task.Message = "转 MP4 已取消。";
        }
        catch (Exception ex)
        {
            task.Message = "转 MP4 失败：" + ex.Message;
            await ShowInfoAsync("转 MP4 失败", ex.Message, offerSettings: false);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    /// <summary>
    /// 识别成功时结果区淡入。
    ///
    /// 用代码构造 Storyboard 而不是 XAML 资源：省掉 NameScope 解析那一层不确定性
    /// （动画找不到目标时是静默不播放，很难查）。Opacity 是合成动画，不占 UI 线程。
    /// </summary>
    private void PlayResultsEntrance()
    {
        try
        {
            var animation = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromMilliseconds(350)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            Storyboard.SetTarget(animation, EpisodeListBorder);
            Storyboard.SetTargetProperty(animation, "Opacity");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }
        catch
        {
            // 动画播不出来不影响功能
        }
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

    // ==================== 托盘 ====================

    /// <summary>
    /// 隐藏到托盘：窗口、进程、下载任务全都留着，只是窗口看不见了。
    /// 「点关闭只隐藏」的拦截在 App 里（AppWindow.Closing），这里只负责藏起来。
    /// </summary>
    public void HideToTray()
    {
        try
        {
            // 先把进度写盘：窗口看不见了，用户随时可能直接关机
            _taskManager.SaveNow();

            // H.NotifyIcon 的 WindowExtensions：显式关掉效率模式 ——
            // 后台还在跑 AES 解密、TS 合并、ffmpeg 校验，降频会拖慢这些步骤
            this.Hide(enableEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            LogFromTray("隐藏到托盘失败：" + ex.Message);
        }
    }

    /// <summary>从托盘把窗口叫回来并置前</summary>
    public void ShowFromTray()
    {
        try
        {
            this.Show(disableEfficiencyMode: false);
            Activate();
        }
        catch (Exception ex)
        {
            LogFromTray("恢复窗口失败：" + ex.Message);
        }
    }

    /// <summary>把托盘相关消息写进窗口里的运行日志</summary>
    public void LogFromTray(string message) => _single.AppendLog("[托盘] " + message);

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
