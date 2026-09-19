using Microsoft.UI.Xaml;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace M3U8Downloader;

/// <summary>
/// 内置播放器：用系统自带的解码器播下载好的产物，底部可以切上一集 / 下一集。
///
/// **不用 ffmpeg**：走的是 Windows 的 Media Foundation（<see cref="MediaPlayerElement"/> 的底层），
/// 实测本项目的产物（.mp4 / H.264 + AAC）直接就能解，时长与画面尺寸都读得出来。
/// 播放列表由调用方传进来 —— 取的是同一个任务里"产物还在磁盘上"的那些集，
/// 所以切集不用回主界面重新点。
/// </summary>
public sealed partial class PlayerWindow : Window {
    /// <summary>播放列表的一项</summary>
    public sealed record Item(int Number, string Title, string FilePath);

    private readonly string _seriesTitle;
    private readonly IReadOnlyList<Item> _playlist;
    private readonly MediaPlayer _player;
    private int _index;

    public PlayerWindow(string seriesTitle, IReadOnlyList<Item> playlist, int startIndex) {
        InitializeComponent();

        _seriesTitle = seriesTitle;
        _playlist = playlist;
        _index = startIndex;

        // MediaPlayerElement.MediaPlayer 第一次访问时会自己建一个播放器。
        // **谁创建谁释放**：它建的由它自己收拾，我们只挂事件、**不要 Dispose** ——
        // 在窗口关闭途中释放它，Window.Close() 本身就会抛 E_ABORT(0x80004004)，
        // 异常没人接，整个进程跟着崩（实机表现："关视频把软件也关了"）。
        _player = Player.MediaPlayer;
        _player.MediaOpened += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
        _player.MediaFailed += (_, e) => ShowError(
            $"这一集放不出来：{e.ErrorMessage}（0x{e.ExtendedErrorCode?.HResult:X8}）");

        // "不 Dispose"不等于"什么都不做"：元素什么时候回收它的播放器是它的事，
        // 窗口关掉之后声音可能还留在后台继续响（实机反馈）。
        // 所以在窗口关闭前、以及真的关掉之后，各主动停一次。
        AppWindow.Closing += (_, _) => StopPlayback();
        Closed += (_, _) => StopPlayback();

        Show(startIndex);
    }

    /// <summary>
    /// 停掉播放。**只停、不 Dispose**：这个播放器归 <see cref="MediaPlayerElement"/> 所有
    /// （见构造函数里的说明），我们把它暂停并松开媒体就够 —— 剩下的交给元素自己收拾。
    /// 注意别改成 Dispose：那会让关闭动作抛异常并把整个进程崩掉。
    /// </summary>
    private void StopPlayback() {
        try {
            _player.Pause();
            _player.Source = null;
        } catch {
            // 已经跟窗口一起没了就算了
        }
    }

    /// <summary>切到播放列表的第 <paramref name="index"/> 项并开始播</summary>
    private void Show(int index) {
        _index = index;
        var item = _playlist[index];

        Title = $"{_seriesTitle} · {item.Title}";
        TitleText.Text = Title;
        CounterText.Text = $"第 {index + 1} / {_playlist.Count} 集";
        PrevButton.IsEnabled = index > 0;
        NextButton.IsEnabled = index < _playlist.Count - 1;

        try {
            Player.Source = MediaSource.CreateFromUri(new Uri(item.FilePath));
        } catch (Exception ex) {
            ShowError("打不开这个文件：" + ex.Message);
        }
    }

    private void ShowError(string message) {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void OnPrevious(object sender, RoutedEventArgs e) {
        if (_index > 0) Show(_index - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e) {
        if (_index < _playlist.Count - 1) Show(_index + 1);
    }
}
