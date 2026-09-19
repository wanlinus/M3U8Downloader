using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Downloads;

namespace M3U8Downloader;

public sealed class MainViewModel : INotifyPropertyChanged {
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    public MainViewModel(DispatcherQueue dispatcher) {
        _dispatcher = dispatcher;
        OutputDirectory = KnownFolders.Downloads;   // 系统「下载」目录（可能已被用户搬到别的盘）
    }

    // ---------------- 输入项 ----------------

    private string _url = "";
    public string Url { get => _url; set => Set(ref _url, value); }

    private string _referer = "";
    public string Referer { get => _referer; set => Set(ref _referer, value); }

    private string _origin = "";
    public string Origin { get => _origin; set => Set(ref _origin, value); }

    private string _userAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";
    public string UserAgent { get => _userAgent; set => Set(ref _userAgent, value); }

    private string _outputDirectory = "";
    public string OutputDirectory { get => _outputDirectory; set => Set(ref _outputDirectory, value); }

    private string _fileName = "video";
    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    private int _concurrency = 16;
    public int Concurrency { get => _concurrency; set => Set(ref _concurrency, value); }

    private int _maxRetries = 3;
    public int MaxRetries { get => _maxRetries; set => Set(ref _maxRetries, value); }

    private bool _autoSkipAds = true;
    public bool AutoSkipAds { get => _autoSkipAds; set => Set(ref _autoSkipAds, value); }

    /// <summary>ffmpeg 路径（由窗口从设置里注入）；为空则保留 TS，不转 MP4</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>是否对产物做全量解码检查（由窗口从设置里注入）</summary>
    public bool FullDecodeCheck { get; set; } = true;

    // ---------------- 状态 ----------------

    private bool _isBusy;
    public bool IsBusy {
        get => _isBusy;
        set {
            if (Set(ref _isBusy, value)) {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanStart));
            }
        }
    }

    public bool IsIdle => !IsBusy;
    public bool CanStart => !IsBusy && !string.IsNullOrWhiteSpace(Url);

    private double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    private string _statusText = "就绪。粘贴 m3u8 地址后点击开始下载。";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _detailText = "";
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }

    public ObservableCollection<string> Logs { get; } = new();

    // ---------------- 模式切换（由 MainWindow 控制） ----------------

    private Microsoft.UI.Xaml.Visibility _singleModeVisibility = Microsoft.UI.Xaml.Visibility.Visible;
    public Microsoft.UI.Xaml.Visibility SingleModeVisibility {
        get => _singleModeVisibility;
        set => Set(ref _singleModeVisibility, value);
    }

    private Microsoft.UI.Xaml.Visibility _batchModeVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility BatchModeVisibility {
        get => _batchModeVisibility;
        set => Set(ref _batchModeVisibility, value);
    }

    // ---------------- 操作 ----------------

    public async Task StartAsync() {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Url)) {
            StatusText = "请先填写 m3u8 地址。";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Logs.Clear();
        DetailText = "";
        _cts = new CancellationTokenSource();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Referer)) headers["Referer"] = Referer.Trim();
        if (!string.IsNullOrWhiteSpace(Origin)) headers["Origin"] = Origin.Trim();
        if (!string.IsNullOrWhiteSpace(UserAgent)) headers["User-Agent"] = UserAgent.Trim();

        try {
            // 下载流程整体在 Core 的 SingleFileDownloadService 里（与站点批量模式共用同一条
            // 流水线：暂存目录 + 清单指纹续传 + 转 MP4 + 产物体检）。
            // 界面在这里只做两件事：把参数递进去、把日志与进度搬上来。
            var service = new SingleFileDownloadService(headers);

            var progress = new Progress<SingleFileProgress>(p => {
                Progress = p.Percent;
                StatusText = p.Phase;
                DetailText = p.TotalSegments == 0
                    ? p.Phase
                    : $"{p.CompletedSegments + p.FailedSegments + p.SkippedSegments}/{p.TotalSegments} 分片  ·  " +
                      $"{p.SizeText}  ·  {p.SpeedText}  ·  已用 {p.Elapsed:hh\\:mm\\:ss}" +
                      (p.Eta.HasValue ? $"  ·  剩余约 {p.Eta.Value:hh\\:mm\\:ss}" : "");
            });

            var report = await service.DownloadAsync(new SingleFileDownloadOptions {
                Url = Url.Trim(),
                Headers = headers,
                OutputDirectory = OutputDirectory,
                FileName = FileName,
                SegmentConcurrency = Concurrency,
                MaxRetries = MaxRetries,
                AutoSkipInvalidSegments = AutoSkipAds,
                FfmpegPath = FfmpegPath,
                FullDecodeCheck = FullDecodeCheck,
            }, progress, Log, _cts.Token);

            switch (report.Outcome?.Status) {
                case EpisodeOutcomeStatus.Completed:
                    Progress = 100;
                    StatusText = $"下载完成：{report.OutputPath}";
                    break;

                case EpisodeOutcomeStatus.Canceled:
                    StatusText = "已取消。已下载的分片已保留，可重新点击开始以续传。";
                    break;

                default:
                    StatusText = report.Error ?? "下载未完成。";
                    break;
            }
        } catch (Exception ex) {
            StatusText = "出错：" + ex.Message;
            Log("✘ 异常：" + ex);
        } finally {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    // ---------------- 辅助 ----------------

    /// <summary>给窗口/托盘这些外部代码写日志用（内部会封送回 UI 线程）</summary>
    public void AppendLog(string message) => Log(message);

    private void Log(string message) {
        _dispatcher.TryEnqueue(() => {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Url)) OnPropertyChanged(nameof(CanStart));
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
