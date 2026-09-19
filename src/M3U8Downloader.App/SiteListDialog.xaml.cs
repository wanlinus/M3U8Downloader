using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using M3U8Downloader.Core.Sites;

namespace M3U8Downloader;

/// <summary>
/// 「站点管理」：编辑搜索结果用的站点清单。
///
/// 为什么要给用户一个编辑入口，而不是把站点写死在代码里：这类影视站换域名换得很勤，
/// 写死的清单过一阵就是一堆死链。清单存在统一库里（<c>sites</c> 表），
/// 用户改完保存，下次搜索直接生效 —— 不用等程序更新。
///
/// 地址只校验"能不能当站点根用"（补上 https:// 后是不是个 http(s) 绝对地址）。
/// **不做联网探测**：一是慢，二是"这个站现在打不开"不代表它是错的
/// （可能是临时故障、也可能需要代理）。
/// </summary>
public sealed partial class SiteListDialog : ContentDialog {
    public SiteListDialog(IReadOnlyList<SearchSite> sites) {
        InitializeComponent();

        foreach (var site in sites) Items.Add(new SiteEditItem(site));
    }

    /// <summary>界面上的行（可增可删，绑定是双向的）</summary>
    public ObservableCollection<SiteEditItem> Items { get; } = new();

    /// <summary>点「保存」之后的清单。校验通过才有意义</summary>
    public IReadOnlyList<SearchSite> Sites { get; private set; } = Array.Empty<SearchSite>();

    private void OnAdd(object sender, RoutedEventArgs e) {
        Items.Add(new SiteEditItem(new SearchSite("", "")));
        HideError();
    }

    /// <summary>把内置站点加回来。**只补不删** —— 用户自己加的站不该被这个按钮冲掉</summary>
    private void OnRestoreBuiltIn(object sender, RoutedEventArgs e) {
        foreach (var builtIn in SiteCatalog.BuiltIn) {
            if (Items.Any(i => string.Equals(i.Url.Trim(), builtIn.Url, StringComparison.OrdinalIgnoreCase)))
                continue;

            Items.Add(new SiteEditItem(builtIn));
        }

        HideError();
    }

    private void OnRemove(object sender, RoutedEventArgs e) {
        if (sender is Button { Tag: SiteEditItem item }) Items.Remove(item);
        HideError();
    }

    /// <summary>
    /// 保存前校验。**有问题就不让关**（<c>args.Cancel = true</c>）：
    /// 关掉再弹一次比"悄没声地丢掉几行"好得多。
    /// </summary>
    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args) {
        var sites = new List<SearchSite>();

        foreach (var item in Items) {
            var name = item.Name.Trim();
            var url = item.Url.Trim();

            // 整行空白＝用户加了一行又没填，直接忽略，不算错
            if (name.Length == 0 && url.Length == 0) continue;

            if (!SearchSite.TryResolve(url, out _)) {
                ShowError(url.Length == 0
                    ? $"「{name}」这行还没填地址。"
                    : $"「{url}」不像一个站点地址 —— 填站点首页，例如 https://www.example.com。");
                args.Cancel = true;
                return;
            }

            sites.Add(new SearchSite(name, url));
        }

        if (sites.Count == 0) {
            ShowError("至少要留一个站点，否则搜索时下拉框是空的。或者点「取消」不改动。");
            args.Cancel = true;
            return;
        }

        Sites = sites;
    }

    private void ShowError(string message) {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorText.Visibility = Visibility.Collapsed;
}

/// <summary>站点管理里的一行（两个输入框都要双向绑定，所以得能通知变化）</summary>
public sealed class SiteEditItem : INotifyPropertyChanged {
    public SiteEditItem(SearchSite site) {
        _name = site.Name;
        _url = site.Url;
    }

    private string _name;
    public string Name {
        get => _name;
        set { if (_name == value) return; _name = value; OnPropertyChanged(); }
    }

    private string _url;
    public string Url {
        get => _url;
        set { if (_url == value) return; _url = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
