using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core.Tasks;

namespace M3U8Downloader;

/// <summary>
/// 「续下更新」的勾选框。
///
/// 为什么不让程序直接把新集全下了：站点更新了几集、用户今天想要哪几集，
/// 只有用户自己知道（热播剧一天补两集，他可能只想先看更新的那集）。
/// 所以先把候选列出来让他勾，勾了哪些就下哪些。
/// </summary>
public sealed partial class FetchNewDialog : ContentDialog {
    public FetchNewDialog(string seriesTitle, FetchNewPreview preview) {
        InitializeComponent();

        foreach (var candidate in preview.Candidates) {
            var item = new FetchNewItem(candidate);
            item.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(FetchNewItem.IsSelected)) UpdateSelection();
            };
            Items.Add(item);
        }

        // 默认只勾「新更新」的那些集：点这个按钮的人要的就是它们。
        // 上次失败/取消的老集默认不勾 —— 想一起补的话自己勾上或点「全选待补」。
        // 已下载的勾不了（FetchNewItem 会自己挡住），它们只是用来"看见"的。
        foreach (var item in Items) item.IsSelected = item.IsNew && item.IsSelectable;

        Title = $"续下更新 · {seriesTitle}";

        var downloaded = preview.DownloadedCount;
        var pending = preview.PendingCount;
        var parts = new List<string> { $"共 {preview.Candidates.Count} 集" };
        if (downloaded > 0) parts.Add($"已下载 {downloaded} 集（置灰）");
        if (preview.NewCount > 0) parts.Add($"新更新 {preview.NewCount} 集");
        var rest = pending - preview.NewCount;
        if (rest > 0) parts.Add($"待补 {rest} 集");

        SummaryText.Text = pending == 0
            ? $"{string.Join("，", parts)} —— 站点上的集都已经在磁盘上了。"
            : $"{string.Join("，", parts)}。勾选要补下的集（已下载的不可勾选）：";

        // 列表是本地快照时先把"数据有多旧"说清楚，好让用户知道后台正在刷新
        if (!preview.Refreshed && preview.SnapshotAt is { } at)
            SetRefreshStatus($"列表来自本地保存的集数据（{DescribeAge(at)}），正在联网刷新…");

        UpdateSelection();
    }

    /// <summary>把"多久以前"说成人话</summary>
    private static string DescribeAge(DateTimeOffset at) {
        var span = DateTimeOffset.Now - at;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;

        return span.TotalMinutes switch {
            < 2 => "刚刚",
            < 60 => $"{(int)span.TotalMinutes} 分钟前",
            < 24 * 60 => $"{(int)span.TotalHours} 小时前",
            _ => $"{(int)span.TotalDays} 天前",
        };
    }

    /// <summary>候选集（界面绑定用）</summary>
    public ObservableCollection<FetchNewItem> Items { get; } = new();

    /// <summary>用户勾选的集号。取消（CloseButton）时调用方不看这个。</summary>
    public IReadOnlySet<int> SelectedNumbers =>
        Items.Where(i => i.IsSelected).Select(i => i.Number).ToHashSet();

    /// <summary>
    /// 后台刷新发现新集时补进列表（调用方负责封送到 UI 线程）。
    /// 新集默认勾上 —— 点「续下更新」的人要的就是它们。
    /// </summary>
    public void AppendEpisodes(IReadOnlyList<FetchNewCandidate> candidates, string status) {
        foreach (var candidate in candidates) {
            var item = new FetchNewItem(candidate);
            item.PropertyChanged += (_, e) => {
                if (e.PropertyName == nameof(FetchNewItem.IsSelected)) UpdateSelection();
            };
            item.IsSelected = item.IsNew && item.IsSelectable;
            Items.Add(item);
        }

        SetRefreshStatus(status);
        UpdateSelection();
    }

    /// <summary>显示后台刷新到了什么（"已是最新" / "连不上站点，用的是本地数据"）</summary>
    public void SetRefreshStatus(string text) {
        RefreshStatusText.Text = text;
        RefreshStatusText.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    /// <summary>用户点了「刷新集列表」。由调用方去真的拉一次（它才知道怎么拉）。</summary>
    public event EventHandler? RefreshRequested;

    /// <summary>刷新期间把按钮禁掉并改文案，避免连点</summary>
    public void SetRefreshing(bool refreshing) {
        _refreshing = refreshing;
        RefreshButton.IsEnabled = !refreshing;
        RefreshButton.Content = refreshing ? "刷新中…" : "刷新集列表";
    }

    private bool _refreshing;

    private void OnRefresh(object sender, RoutedEventArgs e) {
        if (_refreshing) return;

        SetRefreshing(true);
        SetRefreshStatus("正在从站点拉取集列表…");
        RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateSelection() {
        var selected = Items.Count(i => i.IsSelected);
        var selectable = Items.Count(i => i.IsSelectable);
        SelectionText.Text = selectable > 0
            ? $"已选 {selected} / 可补 {selectable} 集"
            : "没有需要补下的集";

        // 一集都没勾就别让他点「下载」，否则点了等于什么都没发生
        IsPrimaryButtonEnabled = selected > 0;
    }

    private void OnSelectNewOnly(object sender, RoutedEventArgs e) {
        foreach (var item in Items) item.IsSelected = item.IsNew && item.IsSelectable;
        UpdateSelection();
    }

    private void OnSelectAll(object sender, RoutedEventArgs e) {
        // 「全选」只选能选的（没下载过的）—— 已下载的有文件在，重下没有意义
        foreach (var item in Items) item.IsSelected = item.IsSelectable;
        UpdateSelection();
    }

    private void OnSelectNone(object sender, RoutedEventArgs e) {
        foreach (var item in Items) item.IsSelected = false;
        UpdateSelection();
    }
}

/// <summary>勾选框里的一行。Core 的候选记录是纯数据，这里补一层可通知的勾选状态。</summary>
public sealed class FetchNewItem : INotifyPropertyChanged {
    public FetchNewItem(FetchNewCandidate candidate) {
        Number = candidate.Number;
        Title = candidate.Title;
        IsNew = candidate.IsNew;
        Note = candidate.Note;
        IsDownloaded = candidate.IsDownloaded;
        FilePath = candidate.FilePath;
    }

    public int Number { get; }
    public string Title { get; }

    /// <summary>是不是站点刚更新出来的集</summary>
    public bool IsNew { get; }

    /// <summary>短标签：「已下载」「新增」「上次失败」「曾下载过，文件已不在」…</summary>
    public string Note { get; }

    /// <summary>产物文件已经在磁盘上（判据是文件本身，不是记录）</summary>
    public bool IsDownloaded { get; }

    /// <summary>已下载时的产物路径</summary>
    public string? FilePath { get; }

    /// <summary>能不能勾：磁盘上已经有文件的那些不允许勾（重下没有意义）</summary>
    public bool IsSelectable => !IsDownloaded;

    /// <summary>已下载的整行压暗，一眼能看出"这些已经有了"</summary>
    public double RowOpacity => IsDownloaded ? 0.55 : 1.0;

    private bool _isSelected;

    /// <summary>双向绑到 CheckBox —— 没有 INotifyPropertyChanged 的话勾选状态不会回写</summary>
    public bool IsSelected {
        get => _isSelected;
        set {
            // 已下载的集永远勾不上：界面已禁用，这里再挡一道，
            // 免得「全选」之类的批量操作把它勾上
            if (IsDownloaded) value = false;
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
