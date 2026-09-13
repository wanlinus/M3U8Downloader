using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace M3U8Downloader;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

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
            MainWindow = new MainWindow();
            MainWindow.Activate();
        }
        catch (Exception ex)
        {
            LogFatal("OnLaunched 失败", ex);
            throw;
        }
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
        // 单实例保护：重复双击图标时，第二个实例直接退出，
        // 避免两个界面同时下载同一批分片互相抢带宽、争用临时目录。
        Mutex? singleInstance = null;
        try
        {
            singleInstance = new Mutex(initiallyOwned: true,
                name: @"Local\M3U8Downloader.SingleInstance", out var isFirstInstance);
            if (!isFirstInstance)
            {
                // 已有实例在运行：直接退出（不弹窗打扰）
                return 0;
            }
        }
        catch
        {
            // 拿不到互斥体时照常启动，不要因为保护逻辑反而起不来
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
