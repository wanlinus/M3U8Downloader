using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using M3U8Downloader.Core;

namespace M3U8Downloader;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DispatcherQueue _dispatcher;
    private CancellationTokenSource? _cts;

    public MainViewModel(DispatcherQueue dispatcher)
    {
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

    // ---------------- 状态 ----------------

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (Set(ref _isBusy, value))
            {
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
    public Microsoft.UI.Xaml.Visibility SingleModeVisibility
    {
        get => _singleModeVisibility;
        set => Set(ref _singleModeVisibility, value);
    }

    private Microsoft.UI.Xaml.Visibility _batchModeVisibility = Microsoft.UI.Xaml.Visibility.Collapsed;
    public Microsoft.UI.Xaml.Visibility BatchModeVisibility
    {
        get => _batchModeVisibility;
        set => Set(ref _batchModeVisibility, value);
    }

    // ---------------- 操作 ----------------

    public async Task StartAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(Url))
        {
            StatusText = "请先填写 m3u8 地址。";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Logs.Clear();
        DetailText = "";
        _cts = new CancellationTokenSource();

        var headers = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(Referer)) headers["Referer"] = Referer.Trim();
        if (!string.IsNullOrWhiteSpace(Origin)) headers["Origin"] = Origin.Trim();
        if (!string.IsNullOrWhiteSpace(UserAgent)) headers["User-Agent"] = UserAgent.Trim();

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(FileName) ? "video" : FileName);
        var outDir = string.IsNullOrWhiteSpace(OutputDirectory) ? "." : OutputDirectory;
        var outputPath = Path.Combine(outDir, safeName + ".ts");
        var tempDir = Path.Combine(Path.GetTempPath(), "M3U8Downloader", SanitizeFileName(safeName) + "_" + Math.Abs(Url.GetHashCode()));

        try
        {
            using var downloader = new HlsDownloader(headers);
            var options = new DownloadOptions
            {
                Concurrency = Math.Clamp(Concurrency, 1, 64),
                MaxRetries = Math.Clamp(MaxRetries, 0, 10),
                Headers = headers,
                AutoSkipInvalidSegments = AutoSkipAds,
                TempDirectory = tempDir,
                OutputPath = outputPath,
            };

            Log($"开始解析：{Url}");
            Log($"附加请求头：{(headers.Count == 0 ? "无" : string.Join(", ", headers.Keys))}");

            StatusText = "正在解析播放列表…";
            var (media, logs) = await downloader.ResolveMediaPlaylistAsync(Url, null, _cts.Token);
            foreach (var l in logs) Log(l);
            Log($"分片 {media.Segments.Count} 个，总时长 {TimeSpan.FromSeconds(media.TotalDuration):hh\\:mm\\:ss}，" +
                $"加密方式 {DescribeEncryption(media)}");

            // 下载前先把广告/无效分片分析结果展示出来
            var report = SegmentInspector.Inspect(media);
            if (report.Suspects.Count > 0)
            {
                Log(new string('-', 60));
                Log($"⚠ 检测到 {report.Suspects.Count} 个疑似插播广告/无效分片：");
                foreach (var r in report.Reasons) Log("  · " + r);
                foreach (var s in report.Suspects.Take(12))
                    Log($"  [{s.Index}] {s.FileName}  {s.Duration:0.##}s  {s.ValidityNote}");
                if (report.Suspects.Count > 12)
                    Log($"  …另有 {report.Suspects.Count - 12} 个同类分片");
                Log(AutoSkipAds
                    ? "→ 已启用自动跳过，这些分片不会导致任务失败。"
                    : "→ 未启用自动跳过，任务可能因这些分片失败。");
                Log(new string('-', 60));
            }
            else
            {
                Log("未发现异目录/重复分片，播放列表看起来是干净的。");
            }

            var progress = new Progress<DownloadProgress>(p =>
            {
                Progress = p.Percent;
                DetailText = $"{p.CompletedSegments + p.FailedSegments + p.SkippedSegments}/{p.TotalSegments} 分片  ·  " +
                             $"{p.SizeText}  ·  {p.SpeedText}  ·  已用 {p.Elapsed:hh\\:mm\\:ss}" +
                             (p.Eta.HasValue ? $"  ·  剩余约 {p.Eta.Value:hh\\:mm\\:ss}" : "");
            });

            StatusText = "正在下载分片…";
            var result = await downloader.DownloadAsync(media, options, progress, _cts.Token);

            Log(new string('-', 60));
            foreach (var m in result.Messages) Log(m);
            Log($"分片：成功 {result.CompletedSegments} / 跳过 {result.SkippedSegments} / 失败 {result.FailedSegments} / 共 {result.TotalSegments}");
            Log($"耗时：{result.Elapsed:hh\\:mm\\:ss}");

            if (result.Success)
            {
                Progress = 100;
                StatusText = $"下载完成：{result.OutputPath}";
                Log($"✔ 输出文件：{result.OutputPath}");
                Log($"  大小：{result.OutputBytes / 1024.0 / 1024.0:0.0} MB");
            }
            else
            {
                StatusText = result.Error ?? "下载未完成。";
                Log("✘ " + (result.Error ?? "下载未完成。"));
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消。已下载的分片已保留，可重新点击开始以续传。";
            Log("已取消。");
        }
        catch (Exception ex)
        {
            StatusText = "出错：" + ex.Message;
            Log("✘ 异常：" + ex);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel()
    {
        _cts?.Cancel();
        StatusText = "正在取消…";
    }

    // ---------------- 辅助 ----------------

    /// <summary>给窗口/托盘这些外部代码写日志用（内部会封送回 UI 线程）</summary>
    public void AppendLog(string message) => Log(message);

    private void Log(string message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        });
    }

    private static string DescribeEncryption(HlsMediaPlaylist media)
    {
        var keys = media.Segments.Select(s => s.Key.Method).Distinct().ToList();
        if (keys.Count == 1) return keys[0] == HlsEncryptionMethod.None ? "无加密" : keys[0].ToString();
        return string.Join(" + ", keys);
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "video" : name.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        if (name == nameof(Url)) OnPropertyChanged(nameof(CanStart));
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
