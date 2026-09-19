using System.ComponentModel;
using System.Runtime.CompilerServices;
using M3U8Downloader.Core.Sites;

namespace M3U8Downloader;

/// <summary>
/// 剧集列表项的界面包装。
///
/// 存在的理由：Core 的 <see cref="SiteEpisode.IsSelected"/> 是普通属性，
/// WinUI 的 CheckBox 双向绑定需要 INotifyPropertyChanged 才会把勾选状态写回，
/// 否则界面上勾了、内存里还是旧值。这里做一层薄包装，保持 Core 模型干净。
/// </summary>
public sealed class EpisodeItemViewModel : INotifyPropertyChanged {
    public EpisodeItemViewModel(SiteEpisode episode) {
        Episode = episode;
        _isSelected = episode.IsSelected;
        _title = episode.DisplayTitle;
    }

    public SiteEpisode Episode { get; }

    private bool _isSelected;
    public bool IsSelected {
        get => _isSelected;
        set {
            if (_isSelected == value) return;
            _isSelected = value;
            Episode.IsSelected = value;   // 回写到 Core 模型
            OnPropertyChanged();
        }
    }

    private string _title;
    public string Title {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    private string _status = "待下载";
    public string Status {
        get => _status;
        set { if (_status != value) { _status = value; OnPropertyChanged(); } }
    }

    private double _percent;
    public double Percent {
        get => _percent;
        set { if (Math.Abs(_percent - value) > 0.01) { _percent = value; OnPropertyChanged(); } }
    }

    private bool _isDownloading;
    public bool IsDownloading {
        get => _isDownloading;
        set {
            if (_isDownloading == value) return;
            _isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProgressVisibility));
        }
    }

    private bool _isDone;
    public bool IsDone {
        get => _isDone;
        set { if (_isDone != value) { _isDone = value; OnPropertyChanged(); } }
    }

    private bool _isFailed;
    public bool IsFailed {
        get => _isFailed;
        set { if (_isFailed != value) { _isFailed = value; OnPropertyChanged(); } }
    }

    public string SourceLabel => $"源{Episode.SourceId}";

    /// <summary>下载中才显示进度条（绑定友好，避免用转换器）</summary>
    public Microsoft.UI.Xaml.Visibility ProgressVisibility =>
        _isDownloading ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
