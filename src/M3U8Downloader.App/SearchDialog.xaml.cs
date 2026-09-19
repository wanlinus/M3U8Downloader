using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace M3U8Downloader;

/// <summary>
/// 「搜索视频」弹窗：选站 → 输剧名 → 搜 → 点一部剧列出它的集数。
///
/// **为什么搜索结果要弹窗、而不是铺在主界面上**：搜「交锋」出来的是一堆同名的剧
/// （交锋 / 权力交锋 / 宿敌交锋 / 圣诞交锋 / 骇客交锋 …），主界面那一条窄窄的结果列表
/// 只能显示剧名和地址，根本分不清哪部是哪部。弹窗里能放下海报、角标、类型和简介 ——
/// 和青苹果影院搜索页一个排法。
///
/// 弹窗**只负责选**：选完就关，真正"识别 → 列集数"由主窗口接手。
/// 解析要好几秒、失败时还要弹提示，那些留在主界面上比压在弹窗里清楚。
/// </summary>
public sealed partial class SearchDialog : ContentDialog {
    private readonly SeriesBatchViewModel _batch;

    public SearchDialog(SeriesBatchViewModel batch) {
        InitializeComponent();

        _batch = batch;
        DataContext = batch;

        // 打开就把光标放进关键词框：点开这个弹窗十有八九就是来打字的
        Opened += (_, _) => KeywordBox.Focus(FocusState.Programmatic);
    }

    /// <summary>用户点中的那部剧；直接关掉（没点）= null</summary>
    public SearchHitViewModel? Selected { get; private set; }

    private async void OnSearch(object sender, RoutedEventArgs e) => await _batch.SearchAsync();

    /// <summary>关键词框里按回车＝点搜索</summary>
    private async void OnKeywordKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e) {
        if (e.Key != Windows.System.VirtualKey.Enter) return;

        e.Handled = true;
        await _batch.SearchAsync();
    }

    private void OnHitClick(object sender, ItemClickEventArgs e) {
        if (e.ClickedItem is not SearchHitViewModel hit) return;

        Selected = hit;
        Hide();
    }
}
