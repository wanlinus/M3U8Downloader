using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Settings;
using M3U8Downloader.Core.Sites;
using M3U8Downloader.Core.Tasks;

namespace M3U8Downloader;

/// <summary>
/// 「站点批量下载」模式的视图模型：
/// 粘贴播放页地址 → 识别站点 → 列出剧集 → 勾选 → 批量下载。
/// </summary>
public sealed class SeriesBatchViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly SeriesDownloader _downloader;
    private CancellationTokenSource? _cts;
    private SiteSeries? _series;

    public SeriesBatchViewModel(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _downloader = new SeriesDownloader();
        OutputDirectory = KnownFolders.Downloads;   // 系统「下载」目录，实际文件会再套一层「剧名」目录
    }

    // ---------------- 输入 ----------------

    private string _pageUrl = "";
    public string PageUrl
    {
        get => _pageUrl;
        set { if (Set(ref _pageUrl, value)) OnPropertyChanged(nameof(CanParse)); }
    }

    private string _outputDirectory = "";
    public string OutputDirectory
    {
        get => _outputDirectory;
        set
        {
            if (!Set(ref _outputDirectory, value)) return;

            // 手动改（输入框 / 「选择…」/ 设置同步）时，它就是新的根目录；
            // 识别后自动接上「剧名 - 站点」的那次赋值不算（见 AppendSeriesFolder）
            if (!_appendingSeriesFolder) _outputRoot = value;
        }
    }

    /// <summary>
    /// 用户真正选的根目录。识别时自动接上去的「剧名 - 站点」不算在里面 ——
    /// 否则换一部剧再识别就会套成「交锋 - 努努影院\另一部剧 - 站点」。
    /// </summary>
    private string _outputRoot = "";

    /// <summary>true = 这次赋值是程序在自动接目录，别把它当成新的根</summary>
    private bool _appendingSeriesFolder;

    private int _episodeConcurrency = 2;
    public int EpisodeConcurrency { get => _episodeConcurrency; set => Set(ref _episodeConcurrency, value); }

    private int _segmentConcurrency = 16;
    public int SegmentConcurrency { get => _segmentConcurrency; set => Set(ref _segmentConcurrency, value); }

    private bool _autoSkipAds = true;
    public bool AutoSkipAds { get => _autoSkipAds; set => Set(ref _autoSkipAds, value); }

    /// <summary>按网站下载时是否自动建「剧名」子目录（来自设置）</summary>
    private bool _seriesSubdirectory = true;
    public bool SeriesSubdirectory { get => _seriesSubdirectory; set => Set(ref _seriesSubdirectory, value); }

    /// <summary>下载完成后是否对每集做全量解码检查（来自设置，耗时但能发现源站坏包）</summary>
    private bool _fullDecodeCheck = true;
    public bool FullDecodeCheck { get => _fullDecodeCheck; set => Set(ref _fullDecodeCheck, value); }

    // ---------------- 解析结果 ----------------

    /// <summary>全部播放源的剧集（供下载状态回填与映射，界面不直接绑定）</summary>
    public ObservableCollection<EpisodeItemViewModel> Episodes { get; } = new();

    /// <summary>界面实际显示的剧集：只含当前选中的那个播放源</summary>
    public ObservableCollection<EpisodeItemViewModel> VisibleEpisodes { get; } = new();

    /// <summary>播放源下拉框的数据源（界面「视频源」选项）</summary>
    public ObservableCollection<SourceOption> SourceOptions { get; } = new();

    private SourceOption? _selectedSource;

    private int _selectedSourceId;
    public int SelectedSourceId
    {
        get => _selectedSourceId;
        private set
        {
            if (Set(ref _selectedSourceId, value))
            {
                OnPropertyChanged(nameof(SourceSummary));
                OnPropertyChanged(nameof(HasMultipleSources));
                OnPropertyChanged(nameof(MultipleSourcesVisibility));
            }
        }
    }

    /// <summary>是否需要在解析完成后弹窗让用户挑播放源</summary>
    public bool HasMultipleSources => SourceOptions.Count > 1;

    public Microsoft.UI.Xaml.Visibility MultipleSourcesVisibility =>
        HasMultipleSources ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>
    /// 界面上「视频源」下拉框的当前选项（双向绑定）。
    /// 选中即切换：只勾选该源的剧集，并把列表换成该源。
    /// </summary>
    public SourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (ReferenceEquals(_selectedSource, value)) return;
            _selectedSource = value;
            OnPropertyChanged(nameof(SelectedSource));
            if (value is not null) ApplySource(value.Id);
        }
    }

    public string SourceSummary
    {
        get
        {
            var current = SourceOptions.FirstOrDefault(o => o.Id == _selectedSourceId);
            if (current is null) return "";
            return HasMultipleSources
                ? $"播放源：{current.Display}   ·   共 {SourceOptions.Count} 个源"
                : $"播放源：{current.Display}";
        }
    }

    /// <summary>
    /// 切换播放源：只勾选该源的剧集，并把列表切换成该源。
    /// 下拉框与解析后的弹窗都走这里。
    /// </summary>
    public void ApplySource(int sourceId)
    {
        if (_series == null) return;

        SeriesDownloader.SelectSource(_series, sourceId);
        SelectedSourceId = sourceId;

        // 让下拉框跟着走（直接改字段，避免再触发一次 ApplySource）
        var option = SourceOptions.FirstOrDefault(o => o.Id == sourceId);
        if (!ReferenceEquals(_selectedSource, option))
        {
            _selectedSource = option;
            OnPropertyChanged(nameof(SelectedSource));
        }

        foreach (var vm in Episodes)
            vm.IsSelected = vm.Episode.IsSelected;   // 回写 Core 并同步界面

        VisibleEpisodes.Clear();
        foreach (var vm in Episodes.Where(v => v.Episode.SourceId == sourceId).OrderBy(v => v.Episode.Number))
            VisibleEpisodes.Add(vm);

        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(HasMultipleSources));
        OnPropertyChanged(nameof(MultipleSourcesVisibility));

        if (VisibleEpisodes.Count > 0)
            StatusText = $"已切换到「{option?.Display}」，共 {VisibleEpisodes.Count} 集。";
    }

    private string _seriesTitle = "";
    public string SeriesTitle { get => _seriesTitle; set => Set(ref _seriesTitle, value); }

    private string _siteInfo = "";
    public string SiteInfo { get => _siteInfo; set => Set(ref _siteInfo, value); }

    private bool _hasSeries;
    public bool HasSeries
    {
        get => _hasSeries;
        set
        {
            if (Set(ref _hasSeries, value))
            {
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(SelectionSummary));
                OnPropertyChanged(nameof(HasSeriesVisibility));
            }
        }
    }

    /// <summary>解析出剧集后才显示信息条与选集工具栏</summary>
    public Microsoft.UI.Xaml.Visibility HasSeriesVisibility =>
        _hasSeries ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

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
                OnPropertyChanged(nameof(CanParse));
                OnPropertyChanged(nameof(CanDownload));
                OnPropertyChanged(nameof(ParseButtonText));
            }
        }
    }

    public bool IsIdle => !IsBusy;
    public bool CanParse => !IsBusy && !string.IsNullOrWhiteSpace(PageUrl);
    public bool CanDownload => !IsBusy && _series != null && Episodes.Any(e => e.IsSelected);

    /// <summary>
    /// 识别按钮上的文字。识别期间换成进行时 —— 按钮里还会转一个 ProgressRing，
    /// 否则点了按钮要等好几秒（要抓页面、可能还要过代理），界面看起来像卡死了。
    /// </summary>
    public string ParseButtonText => IsBusy ? "正在识别…" : "识别并列出剧集";

    private string _statusText = "粘贴视频网站的播放页或详情页地址，点击「识别并列出剧集」。";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _detailText = "";
    public string DetailText { get => _detailText; set => Set(ref _detailText, value); }

    private double _totalProgress;
    public double TotalProgress { get => _totalProgress; set => Set(ref _totalProgress, value); }

    public ObservableCollection<string> Logs { get; } = new();

    public string SelectionSummary
    {
        get
        {
            var total = VisibleEpisodes.Count;
            var selected = VisibleEpisodes.Count(e => e.IsSelected);
            return total == 0 ? "" : $"已选 {selected} / 共 {total} 集";
        }
    }

    // ---------------- 操作 ----------------

    /// <summary>识别站点并列出剧集</summary>
    public async Task ParseAsync()
    {
        if (IsBusy) return;
        if (string.IsNullOrWhiteSpace(PageUrl))
        {
            StatusText = "请先填写视频页面地址。";
            return;
        }

        IsBusy = true;
        ResetResults();
        _cts = new CancellationTokenSource();

        try
        {
            StatusText = "正在识别站点并解析剧集列表…";
            Log($"开始解析：{PageUrl}");

            var series = await _downloader.ParseAsync(PageUrl.Trim(), _cts.Token);
            _series = series;

            foreach (var line in series.Log) Log(line);

            SeriesTitle = string.IsNullOrWhiteSpace(series.Title) ? "(未取到剧名)" : series.Title;
            SiteInfo = $"{series.SiteName} · {series.Kind} · 剧集ID {series.SeriesId}" +
                       $" · {series.Sources.Count} 个播放源 · 共 {series.TotalEpisodes} 集";

            foreach (var ep in series.AllEpisodes.OrderBy(e => e.SourceId).ThenBy(e => e.Number))
            {
                var vm = new EpisodeItemViewModel(ep);
                vm.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(EpisodeItemViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(SelectionSummary));
                        OnPropertyChanged(nameof(CanDownload));
                    }
                };
                Episodes.Add(vm);
            }

            // 播放源：填充弹窗数据源，并默认选中「用户所给页面所在的那个源」
            SourceOptions.Clear();
            foreach (var s in series.Sources)
                SourceOptions.Add(new SourceOption { Id = s.Id, Display = s.ToString() });

            var preferred = series.PreferredSourceId
                            ?? series.Sources.FirstOrDefault(s => s.Episodes.Any(e => e.IsSelected))?.Id
                            ?? series.Sources.FirstOrDefault()?.Id
                            ?? 0;
            ApplySource(preferred);

            HasSeries = VisibleEpisodes.Count > 0;
            if (HasSeries) AppendSeriesFolder(series);

            StatusText = HasSeries
                ? $"已识别：{SeriesTitle}，共 {VisibleEpisodes.Count} 集" +
                  (HasMultipleSources
                      ? $"（共 {SourceOptions.Count} 个播放源，可在上方「视频源」下拉里切换）"
                      : "，请勾选后开始下载。")
                : "未解析到任何剧集，请确认该页面是播放页或详情页。";

            if (HasSeries)
                Log($"✔ 解析完成，共 {Episodes.Count} 集 / {SourceOptions.Count} 个播放源。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "解析已取消。";
        }
        catch (Exception ex)
        {
            StatusText = "解析失败：" + ex.Message;
            Log("✘ " + ex);
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 清空上一次的解析/下载结果。
    ///
    /// 必须在**每次开始解析前**调用，而且要清干净：只清 Episodes 是不够的，
    /// 因为界面绑定的是 VisibleEpisodes —— 如果新地址解析失败（比如换了不被支持的站点），
    /// 旧剧的剧集列表和「已完成」状态会继续留在界面上，看起来像"什么都没发生"。
    /// </summary>
    /// <summary>
    /// 任务已加入下载队列：清空当前页面，让用户可以立刻去贴下一个地址。
    /// 连输入框一起清掉 —— 否则很容易手滑对同一部剧重复建任务。
    /// </summary>
    public void ClearAfterEnqueue()
    {
        ResetResults();
        PageUrl = "";
        StatusText = "任务已加入「下载任务」，可继续识别下一部剧。";
    }

    /// <summary>
    /// 识别成功后，把「剧名 - 站点」接到保存目录后面。
    ///
    /// 目的是让用户**点完识别就能看到东西会下到哪**，而不是等任务跑起来才在卡片上看。
    /// 两个已覆盖的情况：
    /// - 重复识别同一部剧不会叠加（<see cref="SeriesDownloader.ResolveSeriesDirectory"/>
    ///   里对「目录名已经等于这个名字」有判断）；
    /// - 换一部剧再识别会整段替换，而不是套在上一次的路径下面（靠 <see cref="_outputRoot"/>）。
    ///
    /// 目录本身不在这里创建：真正落盘时 <c>SeriesDownloader.DownloadAsync</c> 会
    /// <c>Directory.CreateDirectory</c>，所以「点了下载但文件夹不存在」不会失败。
    /// </summary>
    private void AppendSeriesFolder(SiteSeries series)
    {
        var root = string.IsNullOrWhiteSpace(_outputRoot) ? OutputDirectory : _outputRoot;

        var full = SeriesDownloader.ResolveSeriesDirectory(series, new SeriesDownloadOptions
        {
            // 留空时交给 ResolveSeriesDirectory 回落到系统「下载」目录
            OutputDirectory = root,
            SeriesSubdirectory = SeriesSubdirectory,
        });

        _appendingSeriesFolder = true;
        try { OutputDirectory = full; }
        finally { _appendingSeriesFolder = false; }
    }

    private void ResetResults()
    {
        Episodes.Clear();
        VisibleEpisodes.Clear();
        SourceOptions.Clear();
        Logs.Clear();

        _series = null;
        SelectedSourceId = 0;
        _selectedSource = null;
        OnPropertyChanged(nameof(SelectedSource));
        HasSeries = false;

        SeriesTitle = "";
        SiteInfo = "";
        TotalProgress = 0;
        DetailText = "";

        OnPropertyChanged(nameof(SourceSummary));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanDownload));
        OnPropertyChanged(nameof(HasMultipleSources));
        OnPropertyChanged(nameof(MultipleSourcesVisibility));
    }

    /// <summary>
    /// 把当前已勾选的剧集加入下载队列（立即返回，不阻塞界面）。
    /// 之后由「下载任务」面板负责展示进度与结果。
    /// </summary>
    public SeriesTask? EnqueueTo(DownloadTaskManager manager)
    {
        if (IsBusy) return null;
        if (_series is null)
        {
            StatusText = "请先识别剧集。";
            return null;
        }
        if (!Episodes.Any(e => e.IsSelected))
        {
            StatusText = "请至少勾选一集。";
            return null;
        }

        try
        {
            var options = BuildOptions(_series);
            var task = manager.Enqueue(_series, options);

            Log(new string('-', 60));
            Log($"已加入下载队列：{_series.Title}");
            Log($"并发：{options.EpisodeConcurrency} 集 × {options.SegmentConcurrency} 分片");
            Log($"输出目录：{task.OutputDirectory}");
            Log($"产物格式：{(string.IsNullOrWhiteSpace(options.FfmpegPath) ? "TS（未配置 FFmpeg）" : "MP4")}");

            return task;
        }
        catch (Exception ex)
        {
            StatusText = "加入队列失败：" + ex.Message;
            Log("✘ " + ex);
            return null;
        }
    }

    /// <summary>构建下载参数（输出目录、并发、请求头、ffmpeg 路径等）</summary>
    public SeriesDownloadOptions BuildOptions(SiteSeries series)
    {
        var options = new SeriesDownloadOptions
        {
            OutputDirectory = string.IsNullOrWhiteSpace(OutputDirectory) ? "." : OutputDirectory,
            EpisodeConcurrency = Math.Clamp(EpisodeConcurrency, 1, 8),
            SegmentConcurrency = Math.Clamp(SegmentConcurrency, 1, 64),
            AutoSkipInvalidSegments = AutoSkipAds,
            SeriesSubdirectory = SeriesSubdirectory,
            FullDecodeCheck = FullDecodeCheck,
            // 暂存目录留空 = 用下载目录下的 .m3u8tmp（不散落到系统临时目录）
            FfmpegPath = FfmpegPath,
        };

        // 站点建议的请求头（Referer / Origin / User-Agent）透传给下载引擎
        foreach (var kv in series.Headers) options.ExtraHeaders[kv.Key] = kv.Value;
        return options;
    }

    /// <summary>ffmpeg 路径（来自设置）；为空则产物保留 TS 格式</summary>
    public string? FfmpegPath { get; set; }

    public void Cancel()
    {
        // 队列化之后，"取消"由「下载任务」面板里的按钮负责；这里只清掉解析中的请求
        _cts?.Cancel();
        StatusText = "已取消当前解析。";
    }

    public void SelectAll(bool selected)
    {
        foreach (var vm in VisibleEpisodes) vm.IsSelected = selected;
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanDownload));
    }

    /// <summary>按区间勾选，如 "1-8" 或 "1,3,5"（只在当前播放源内生效）</summary>
    public void ApplySelectionSpec(string spec)
    {
        if (_series == null || string.IsNullOrWhiteSpace(spec)) return;
        try
        {
            SeriesDownloader.ApplySelection(_series, spec);
            // 选集规则只在当前源内生效，避免把其它播放源的同名集也勾上
            foreach (var ep in _series.AllEpisodes)
                ep.IsSelected = ep.IsSelected && ep.SourceId == SelectedSourceId;
            foreach (var vm in Episodes) vm.IsSelected = vm.Episode.IsSelected;
            StatusText = $"已按「{spec}」勾选：{SelectionSummary}";
        }
        catch (Exception ex)
        {
            StatusText = "选集表达式无效：" + ex.Message;
        }
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(CanDownload));
    }

    private void Log(string message)
    {
        _dispatcher.TryEnqueue(() =>
        {
            Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
            while (Logs.Count > 500) Logs.RemoveAt(0);
        });
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _downloader.Dispose();
    }
}

/// <summary>
/// 播放源选择项（多播放源弹窗用）。
/// Display 形如「360播放器（16集）」，名字来自站点 playerconfig.js 的 player_list。
/// </summary>
public sealed class SourceOption
{
    public required int Id { get; init; }
    public required string Display { get; init; }
    public override string ToString() => Display;
}
