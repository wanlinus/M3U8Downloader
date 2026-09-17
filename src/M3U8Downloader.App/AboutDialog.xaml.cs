using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Settings;
using M3U8Downloader.Core.Sites;
using M3U8Downloader.Core.Update;
using Windows.ApplicationModel.DataTransfer;

namespace M3U8Downloader;

/// <summary>
/// 「关于」对话框：软件名称、版本、版权、许可证与第三方组件声明。
///
/// 之所以必须包含第三方声明：程序会调用 FFmpeg，也支持在应用内下载 FFmpeg，
/// 而 FFmpeg 适用其自身的 LGPL/GPL 条款 —— 分发时需要在用户可见处给出声明。
/// </summary>
public sealed partial class AboutDialog : ContentDialog
{
    private string _ffmpegStatusText = "";
    private ReleaseInfo? _latestRelease;

    /// <summary>
    /// 用户在「关于」里点了「立即更新」。
    /// 由主窗口接过去执行 —— 进度弹窗、收尾脚本、退出程序都归它管，
    /// 这个对话框自己只负责把新版本信息递出去。
    /// </summary>
    public event EventHandler<ReleaseInfo>? AutoUpdateRequested;

    public AboutDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PrimaryButtonClick += OnCopyDiagnostics;
        SecondaryButtonClick += OnCheckUpdate;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AppNameText.Text = AppInfo.ProductName;
        VersionText.Text = $"版本 {AppInfo.Version}";
        CopyrightText.Text = $"{AppInfo.Author}  ·  {AppInfo.Copyright}";
        LicenseText.Text = AppInfo.LicenseName;
        RuntimeText.Text = AppInfo.RuntimeDescription;
        PathsText.Text =
            $"设置文件：{AppSettingsStore.SettingsFilePath}\n" +
            $"FFmpeg 目录：{FfmpegLocator.PreferredInstallDirectory}\n" +
            $"已适配站点：{string.Join("、", SiteResolver.CreateDefault().Adapters.Select(a => a.Name))}";
        ThirdPartyText.Text = AppInfo.ThirdPartyNotices;

        if (!string.IsNullOrWhiteSpace(AppInfo.ProjectUrl))
        {
            ProjectUrlLink.Content = AppInfo.ProjectUrl;
            ProjectUrlLink.NavigateUri = new Uri(AppInfo.ProjectUrl);
            ProjectUrlLink.Visibility = Visibility.Visible;
        }

        // 顺便把 FFmpeg 的检测结果也显示出来，报问题时一眼就能看到
        try
        {
            var status = await FfmpegLocator.DetectAsync();
            _ffmpegStatusText = status.IsInstalled
                ? $"可用（{status.Ffmpeg!.Version}，来源：{status.Source}）"
                : "未安装/未找到";
        }
        catch
        {
            _ffmpegStatusText = "检测失败";
        }
    }

    private void OnCopyDiagnostics(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 保持对话框开着，方便用户看到"已复制"
        args.Cancel = true;

        try
        {
            var package = new DataPackage();
            package.SetText(AppInfo.BuildDiagnosticText(_ffmpegStatusText));
            Clipboard.SetContent(package);
            CopyHintText.Visibility = Visibility.Visible;
        }
        catch
        {
            CopyHintText.Text = "复制失败，可手动截图";
            CopyHintText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// 「检查更新」：查 GitHub Release 上有没有比当前更新的版本。
    ///
    /// 结果**就地**显示在版本号下面，不再开第二个 ContentDialog ——
    /// 「关于」本身就是一个 ContentDialog，而 WinUI 同一时刻只允许一个。
    /// </summary>
    private async void OnCheckUpdate(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // 保持对话框开着，让用户看到结果
        args.Cancel = true;

        UpdateLink.Visibility = Visibility.Collapsed;
        UpdateNowButton.Visibility = Visibility.Collapsed;
        UpdateStatusText.Text = "正在检查…";
        UpdateStatusText.Visibility = Visibility.Visible;

        try
        {
            // 代理只在这里用：GitHub 在国内常常连不上；没配代理也只是查不到，不是错误
            var proxy = AppSettingsStore.Load().ProxyUrl;
            var result = await UpdateChecker.CheckAsync(AppInfo.Version, proxy);

            if (result.Failed)
            {
                UpdateStatusText.Text = $"检查失败：{result.Error}";
                return;
            }

            if (!result.HasUpdate || result.Latest is null)
            {
                UpdateStatusText.Text = $"已是最新版本（{result.CurrentVersion}）。";
                return;
            }

            var latest = result.Latest;
            _latestRelease = latest;

            UpdateStatusText.Text = $"发现新版本 {latest.Tag}（当前 {result.CurrentVersion}）" +
                                    (latest.DownloadSizeText.Length > 0 ? $"，约 {latest.DownloadSizeText}" : "") +
                                    "。";

            // 主推「立即更新」；链接留给想自己去发布页看的人
            UpdateNowButton.Visibility = Visibility.Visible;
            UpdateLink.Content = "或到发布页面下载 →";
            UpdateLink.NavigateUri = new Uri(latest.HtmlUrl);
            UpdateLink.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "检查失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 点「立即更新」：关掉自己，把新版本信息交给主窗口去装。
    ///
    /// 必须先 Hide 再用 DispatcherQueue 排队触发 —— 主窗口的进度弹窗有自己的
    /// 「同时只能有一个 ContentDialog」守卫，得等这个对话框彻底关掉、
    /// 那边把 _openDialog 清空之后才轮得到它。
    /// </summary>
    private void OnUpdateNow(object sender, RoutedEventArgs e)
    {
        if (_latestRelease is not { } latest)
        {
            UpdateInstaller.Trace("点了「立即更新」，但没有版本信息，忽略");
            return;
        }

        UpdateInstaller.Trace($"点了「立即更新」→ {latest.Tag}，关闭「关于」");
        Hide();

        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateInstaller.Trace("派发 AutoUpdateRequested 事件");
            AutoUpdateRequested?.Invoke(this, latest);
        });
    }
}
