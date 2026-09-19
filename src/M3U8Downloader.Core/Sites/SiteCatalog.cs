namespace M3U8Downloader.Core.Sites;

/// <summary>
/// 界面「站点」下拉框里的一项：一个可以拿来搜的站。
///
/// 域名是会失效的（这类站在换域名上很勤快），所以清单**不是写死在代码里的常量**，
/// 而是存在统一库里、由用户自己维护 —— 内置的那几个只是"第一次跑的时候别是空的"。
/// </summary>
public sealed record SearchSite(string Name, string Url) {
    /// <summary>站点根地址。填得不对（不是 http/https、或者压根不是地址）就是 null</summary>
    public Uri? Root => TryResolve(Url, out var uri) ? uri : null;

    /// <summary>界面上显示的文字：没填名字就退而显示域名</summary>
    public string Display => Name.Trim().Length > 0 ? Name.Trim() : Url.Trim();

    /// <summary>
    /// 能不能当站点根用。允许用户只敲域名（自动补 <c>https://</c>）——
    /// 让人手输 "www.qmao.net" 比逼他补全协议自然。
    /// </summary>
    public static bool TryResolve(string? url, out Uri uri) {
        uri = null!;

        var text = (url ?? "").Trim();
        if (text.Length == 0) return false;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme is not ("http" or "https")) return false;
        if (parsed.Host.Length == 0) return false;

        uri = parsed;
        return true;
    }
}

/// <summary>
/// 站点清单的读写。实现在 <c>Core/Storage/SqliteSiteStore</c>（统一库里的 <c>sites</c> 表）。
/// 跟设置/任务/历史一样，"接口在 Core、实现在 Storage"，自检直接测真实现。
/// </summary>
public interface ISiteCatalogStore {
    /// <summary>
    /// 读站点清单。**库读不出来、或者里面一条都没有，都退回内置清单** ——
    /// 下拉框空着等于这个功能没法用，那还不如给几个内置的让用户去改。
    /// </summary>
    IReadOnlyList<SearchSite> Load();

    /// <summary>整表覆盖写。返回是否成功，失败不抛（调用方决定怎么提示）。</summary>
    bool Save(IReadOnlyList<SearchSite> sites);
}

/// <summary>内置站点：只在库里一条都没有的时候兜底用</summary>
public static class SiteCatalog {
    /// <summary>
    /// 内置的三个站（2026-09 实测可搜）。
    /// **它们的搜索入口不写在这里** —— <see cref="SiteSearch"/> 是去首页读 &lt;form&gt; 的，
    /// 所以换模板、换域名都不用改代码，只要地址还对。
    /// </summary>
    public static readonly IReadOnlyList<SearchSite> BuiltIn = new[] {
        new SearchSite("青苹果影院", "https://www.qmao.net"),
        new SearchSite("影迷界影院", "https://www.wakuredo.com"),
        new SearchSite("努努影院", "https://www.nnyy.in"),
    };
}
