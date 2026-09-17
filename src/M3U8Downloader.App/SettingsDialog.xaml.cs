using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Ffmpeg;
using M3U8Downloader.Core.Net;
using M3U8Downloader.Core.Settings;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace M3U8Downloader;

/// <summary>
/// 设置对话框：FFmpeg（手动指定 / 自动下载）、代理、下载默认值。
///
/// 这里的所有路径都来自 Core 的按用户目录推导，不含任何与本机相关的硬编码 ——
/// 装到别人机器上同样能跑。
/// </summary>
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly IntPtr _hwnd;
    private readonly AppSettings _working;

    /// <summary>用户点「保存」后为 true；调用方据此决定是否回写设置与刷新界面</summary>
    public bool Saved { get; private set; }

    /// <summary>保存后的设置</summary>
    public AppSettings Result => _working;

    private CancellationTokenSource? _downloadCts;

    public SettingsDialog(IntPtr hwnd, AppSettings settings)
    {
        _hwnd = hwnd;
        _working = settings;

        InitializeComponent();

        PrimaryButtonClick += OnSaveClicked;
        Closed += OnClosed;

        LoadFromSettings();
        _ = RefreshStatusAsync();
    }

    private void LoadFromSettings()
    {
        FfmpegPathBox.Text = _working.FfmpegPath ?? "";
        FfmpegUrlBox.Text = _working.FfmpegDownloadUrl ?? "";
        ProxyEnabledCheck.IsChecked = _working.ProxyEnabled;
        ProxyUrlBox.Text = _working.ProxyUrl ?? "";
        OutputDirBox.Text = _working.DefaultOutputDirectory ?? "";
        EpisodeConcurrencyBox.Value = _working.EpisodeConcurrency;
        SegmentConcurrencyBox.Value = _working.SegmentConcurrency;
        SkipAdsCheck.IsChecked = _working.AutoSkipInvalidSegments;
        SeriesSubdirCheck.IsChecked = _working.SeriesSubdirectory;
        FullDecodeCheckBox.IsChecked = _working.FullDecodeCheck;
        MinimizeToTrayCheck.IsChecked = _working.MinimizeToTrayOnClose;
        UpdateCheckBox.IsChecked = _working.CheckUpdateOnStartup;
    }

    private void CollectBack()
    {
        _working.FfmpegPath = NullIfBlank(FfmpegPathBox.Text);
        _working.FfmpegDownloadUrl = NullIfBlank(FfmpegUrlBox.Text);
        _working.ProxyEnabled = ProxyEnabledCheck.IsChecked == true;
        _working.ProxyUrl = NullIfBlank(ProxyUrlBox.Text);
        _working.DefaultOutputDirectory = NullIfBlank(OutputDirBox.Text);
        _working.EpisodeConcurrency = (int)Math.Round(double.IsNaN(EpisodeConcurrencyBox.Value) ? 2 : EpisodeConcurrencyBox.Value);
        _working.SegmentConcurrency = (int)Math.Round(double.IsNaN(SegmentConcurrencyBox.Value) ? 16 : SegmentConcurrencyBox.Value);
        _working.AutoSkipInvalidSegments = SkipAdsCheck.IsChecked == true;
        _working.SeriesSubdirectory = SeriesSubdirCheck.IsChecked == true;
        _working.FullDecodeCheck = FullDecodeCheckBox.IsChecked == true;
        _working.MinimizeToTrayOnClose = MinimizeToTrayCheck.IsChecked == true;
        _working.CheckUpdateOnStartup = UpdateCheckBox.IsChecked == true;
        _working.Normalize();
    }

    private static string? NullIfBlank(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void OnSaveClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        CollectBack();
        Saved = true;
    }

    private void OnClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    // ==================== FFmpeg ====================

    private async void OnDetectFfmpeg(object sender, RoutedEventArgs e) => await RefreshStatusAsync();

    private async Task RefreshStatusAsync()
    {
        CollectBack();
        FfmpegStatusText.Text = "正在检测…";
        try
        {
            var status = await FfmpegLocator.DetectAsync(_working);
            FfmpegStatusText.Text = status.Describe();
        }
        catch (Exception ex)
        {
            FfmpegStatusText.Text = "检测失败：" + ex.Message;
        }
    }

    private async void OnDownloadFfmpeg(object sender, RoutedEventArgs e)
    {
        CollectBack();

        if (ProxyHelper.IsConfiguredButInvalid(_working))
        {
            FfmpegStatusText.Text = "代理地址格式不正确，请检查后再下载。";
            return;
        }

        DownloadFfmpegButton.IsEnabled = false;
        DownloadProgressPanel.Visibility = Visibility.Visible;
        DownloadProgressBar.Value = 0;
        DownloadProgressText.Text = "准备下载…";

        _downloadCts?.Dispose();
        _downloadCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<FfmpegInstallProgress>(p =>
            {
                DownloadProgressBar.Value = p.TotalBytes > 0 ? p.Percent : 0;
                DownloadProgressText.Text = p.Describe();
            });

            var result = await FfmpegInstaller.InstallAsync(
                _working.FfmpegDownloadUrl,
                _working.ProxyEnabled ? _working.ProxyUrl : null,
                progress,
                _downloadCts.Token);

            if (result.Success)
            {
                // 装好后把"手动指定"清掉，让它走应用管理副本
                _working.FfmpegPath = null;
                FfmpegPathBox.Text = "";

                var where = result.UsedFallbackDirectory
                    ? $"\n（程序目录不可写，已装到用户目录：{result.Directory}）"
                    : $"\n安装位置：{result.Directory}";
                DownloadProgressText.Text = $"✔ 安装完成：ffmpeg {result.Version}{where}";

                await RefreshStatusAsync();
            }
            else
            {
                DownloadProgressText.Text = "✘ " + result.Error;
                if (result.Error?.Contains("下载失败") == true)
                {
                    DownloadProgressText.Text +=
                        "\n提示：如果直连不通，请在上面填写代理（例如 http://127.0.0.1:7897），或改用镜像下载源。";
                }
            }
        }
        catch (Exception ex)
        {
            DownloadProgressText.Text = "✘ 安装失败：" + ex.Message;
        }
        finally
        {
            DownloadFfmpegButton.IsEnabled = true;
        }
    }

    private async void OnRemoveManagedFfmpeg(object sender, RoutedEventArgs e)
    {
        var existing = FfmpegLocator.ManagedDirectories.Where(Directory.Exists).ToList();
        if (existing.Count == 0)
        {
            FfmpegStatusText.Text = "没有找到应用下载的 FFmpeg（可能用的是手动指定或系统 PATH 里的）。";
            return;
        }

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "清理已下载的 FFmpeg",
            Content = "将删除：\n" + string.Join("\n", existing) +
                      "\n\n删除后如需转码/校验要重新下载。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var failed = new List<string>();
        foreach (var dir in existing)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex) { failed.Add($"{dir}：{ex.Message}"); }
        }

        await RefreshStatusAsync();
        if (failed.Count > 0) FfmpegStatusText.Text += "\n删除失败：" + string.Join("；", failed);
    }

    private async void OnPickFfmpeg(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        FfmpegPathBox.Text = file.Path;
        await RefreshStatusAsync();
    }

    // ==================== 代理 ====================

    private async void OnTestProxy(object sender, RoutedEventArgs e)
    {
        ProxyTestText.Visibility = Visibility.Visible;
        ProxyTestText.Text = "正在测试…（最多 12 秒）";
        ProxyTestText.StartBringIntoView();   // 结果在滚动区里，保证用户一定看得到

        try
        {
            // 直接用输入框里的地址：即使「启用代理」没勾，也应该能测这个地址通不通
            var result = await ProxyHelper.TestAsync(ProxyUrlBox.Text);
            ProxyTestText.Text = result.Message;
        }
        catch (Exception ex)
        {
            ProxyTestText.Text = "✘ 测试失败：" + ex.Message;
        }
        finally
        {
            ProxyTestText.StartBringIntoView();
        }
    }

    // ==================== 目录 ====================

    private async void OnPickOutputDir(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedStartLocation = PickerLocationId.Downloads;
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) OutputDirBox.Text = folder.Path;
    }
}
