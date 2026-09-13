using System.Runtime.InteropServices;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace M3U8Downloader;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    /// <summary>托盘图标（在 TrayIconResources.xaml 里声明，这里取出来创建）</summary>
    public TaskbarIcon? TrayIcon { get; private set; }

    /// <summary>
    /// true = 点窗口右上角的关闭只「隐藏到托盘」，下载继续在后台跑。
    /// 托盘菜单里的「退出」会先把它置成 false，窗口才能真正关掉。
    /// （名字与 H.NotifyIcon 官方示例一致）
    /// </summary>
    public bool HandleClosedEvents { get; set; } = true;

    /// <summary>
    /// 第二个实例用它唤醒已有实例（见 <see cref="Main"/>）。
    /// 只有第一个实例持有，拿不到就退化成"重复启动没反应"。
    /// </summary>
    private static EventWaitHandle? _activateSignal;

    /// <summary>「已最小化到托盘」的气泡只弹一次，别每次点关闭都弹</summary>
    private bool _trayHintShown;

    /// <summary>
    /// 启动命令行参数。
    /// 用 Win32 GetCommandLineW 读取而不是 Environment.GetCommandLineArgs()：
    /// 后者在 WinUI 的启动路径下不保证可靠。
    /// </summary>
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetCommandLineW();

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var window = new MainWindow();
            MainWindow = window;

            // 点关闭 → 只隐藏到托盘（真正退出走托盘菜单，那时 HandleClosedEvents 已经是 false）
            window.AppWindow.Closing += (_, closingArgs) =>
            {
                if (!HandleClosedEvents || !window.MinimizeToTrayOnClose) return;

                closingArgs.Cancel = true;
                HideToTray(window);
            };

            // 窗口真的关掉了（托盘菜单退出 / 系统注销）：把托盘图标摘掉，
            // 否则通知区会留下一个点不动的"幽灵图标"
            window.Closed += (_, __) =>
            {
                TrayIcon?.Dispose();
                TrayIcon = null;
            };

            window.Activate();

            InitializeTrayIcon(window);
            StartActivationListener();
        }
        catch (Exception ex)
        {
            LogFatal("OnLaunched 失败", ex);
            throw;
        }
    }

    /// <summary>
    /// 创建托盘图标。流程照 H.NotifyIcon 官方 WinUI 示例（Windowless 版）：
    /// 先从 Resources 取出 XamlUICommand 挂命令处理器，再取出 TaskbarIcon 调 ForceCreate。
    /// </summary>
    private void InitializeTrayIcon(MainWindow window)
    {
        var showWindowCommand = (XamlUICommand)Resources["ShowWindowCommand"];
        showWindowCommand.ExecuteRequested += (_, __) => ShowMainWindow();

        var exitCommand = (XamlUICommand)Resources["ExitApplicationCommand"];
        exitCommand.ExecuteRequested += (_, __) => ExitApplication();

        try
        {
            TrayIcon = (TaskbarIcon)Resources["TrayIcon"];

            // 参数 false：不启用效率模式。窗口藏起来后 AES 解密、TS 合并、ffmpeg 校验还在跑，
            // 效率模式会把这类 CPU 密集步骤一起降频（库的默认值是 true，这里显式关掉）。
            TrayIcon.ForceCreate(enablesEfficiencyMode: false);
        }
        catch (Exception ex)
        {
            // 托盘起不来（极罕见）：绝不能把窗口藏成一个找不回来的状态
            TrayIcon = null;
            window.MinimizeToTrayOnClose = false;
            window.LogFromTray("托盘图标不可用，已退回：点关闭即退出程序");
            LogFatal("托盘图标创建失败", ex);
        }
    }

    /// <summary>把主窗口从托盘叫回来（单击托盘图标 / 菜单第一项 / 再次启动程序）</summary>
    public void ShowMainWindow()
    {
        if (MainWindow is M3U8Downloader.MainWindow window)
        {
            window.ShowFromTray();
        }
        else
        {
            MainWindow?.Activate();
        }
    }

    /// <summary>隐藏到托盘：窗口不关，进程和下载都继续活着</summary>
    private void HideToTray(MainWindow window)
    {
        window.HideToTray();

        if (_trayHintShown) return;
        _trayHintShown = true;

        try
        {
            TrayIcon?.ShowNotification("M3U8 视频下载器",
                "已最小化到托盘，下载在后台继续；右键托盘图标可退出。");
        }
        catch
        {
            // 气泡弹不出来不影响隐藏本身
        }
    }

    /// <summary>真正退出：先放行关闭，再摘掉托盘图标，最后关窗口</summary>
    private void ExitApplication()
    {
        HandleClosedEvents = false;
        TrayIcon?.Dispose();
        TrayIcon = null;

        MainWindow?.Close();

        // 没有窗口时关无可关，只能直接结束进程
        // （见 https://github.com/HavenDV/H.NotifyIcon/issues/66）
        if (MainWindow == null)
        {
            Exit();
        }
    }

    /// <summary>
    /// 监听"程序又被启动了一次"的信号，把已有窗口显示出来。
    /// 不做这件事的话，窗口缩在托盘里时用户再点开始菜单会看起来"没反应"。
    /// </summary>
    private static void StartActivationListener()
    {
        var signal = _activateSignal;
        if (signal is null) return;

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    if (!signal.WaitOne()) return;
                }
                catch
                {
                    return;
                }

                // 信号来自别的进程，窗口只能在 UI 线程动
                dispatcher.TryEnqueue(() => (Current as App)?.ShowMainWindow());
            }
        })
        {
            IsBackground = true,
            Name = "M3U8Downloader.激活监听",
        };
        thread.Start();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogFatal("未处理异常", e.Exception);
    }

    /// <summary>
    /// 自定义入口：把启动阶段的异常落盘，避免静默崩溃（0xC000027B）无法定位。
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        // 单实例保护：重复双击图标时，第二个实例把已有实例的窗口叫出来，然后自己退出，
        // 避免两个界面同时下载同一批分片互相抢带宽、争用临时目录。
        Mutex? singleInstance = null;
        try
        {
            singleInstance = new Mutex(initiallyOwned: true,
                name: @"Local\M3U8Downloader.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                // 已有实例在运行：让它把窗口（可能正缩在托盘里）显示出来
                if (EventWaitHandle.TryOpenExisting(@"Local\M3U8Downloader.Activate", out var signal))
                {
                    signal.Set();
                    signal.Dispose();
                }

                return 0;
            }

            // 第一个实例：建好事件，供后来者唤醒
            _activateSignal = new EventWaitHandle(
                initialState: false, EventResetMode.AutoReset, name: @"Local\M3U8Downloader.Activate");
        }
        catch
        {
            // 拿不到互斥体/事件时照常启动，不要因为保护逻辑反而起不来
        }

        try
        {
            // 两种来源都试，取到更完整的那个
            var fromWin32 = SplitCommandLine(Marshal.PtrToStringUni(GetCommandLineW()) ?? "");
            StartupArgs = fromWin32.Length > args.Length ? fromWin32 : args;

            try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }

            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(OnApplicationStart);
            return 0;
        }
        catch (Exception ex)
        {
            LogFatal("Main 启动失败", ex);
            return 1;
        }
        finally
        {
            try { singleInstance?.ReleaseMutex(); } catch { }
            singleInstance?.Dispose();
            try { _activateSignal?.Dispose(); } catch { }
        }
    }

    /// <summary>解析命令行（支持引号包裹的参数，返回结果含程序名本身）</summary>
    private static string[] SplitCommandLine(string commandLine)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0) { parts.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) parts.Add(current.ToString());
        return parts.ToArray();
    }

    private static void OnApplicationStart(ApplicationInitializationCallbackParams p)
    {
        try
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        }
        catch (Exception ex)
        {
            LogFatal("应用初始化失败", ex);
            throw;
        }
    }

    /// <summary>把致命异常写到 %TEMP%\M3U8Downloader-crash.log</summary>
    internal static void LogFatal(string stage, Exception? ex)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "M3U8Downloader-crash.log");
            var text = $"""

                ===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====
                阶段: {stage}
                类型: {ex?.GetType().FullName}
                HRESULT: 0x{ex?.HResult:X8}
                消息: {ex?.Message}
                堆栈:
                {ex}

                """;
            File.AppendAllText(path, text, System.Text.Encoding.UTF8);
        }
        catch { }
    }
}
