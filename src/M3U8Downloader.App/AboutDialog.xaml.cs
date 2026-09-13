using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Settings;
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

    public AboutDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        PrimaryButtonClick += OnCopyDiagnostics;
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
            $"FFmpeg 目录：{FfmpegLocator.PreferredInstallDirectory}";
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
}
