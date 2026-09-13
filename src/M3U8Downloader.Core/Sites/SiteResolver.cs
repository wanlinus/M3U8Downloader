using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using M3U8Downloader.Core.Net;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Sites;

/// <summary>抓取站点页面所需的上下文（HttpClient + 默认请求头 + 解码工具）</summary>
public sealed class SiteContext : IDisposable
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly bool _ownsHttp;

    public HttpClient Http { get; }
    public string UserAgent { get; }

    static SiteContext()
    {
        // 国内不少影视站是 GBK/GB2312，.NET Core 默认不带这些代码页，需要显式注册。
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 已注册或不可用 */ }
    }

    /// <param name="http">外部传入的 HttpClient；传 null 则内部按 <paramref name="settings"/> 自建</param>
    /// <param name="userAgent">自定义 UA；留空用内置浏览器 UA</param>
    /// <param name="settings">应用设置（用于取代理配置）；留空则读取已保存的设置</param>
    public SiteContext(HttpClient? http = null, string? userAgent = null, AppSettings? settings = null)
    {
        _ownsHttp = http is null;

        if (http is null && settings is null)
        {
            try { settings = AppSettingsStore.Load(); } catch { /* 设置不可用就用默认 */ }
        }
        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent!;

        if (http is null)
        {
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
            };

            // 代理：部分站点在海外，或用户所处的网络需要代理才能访问
            var proxy = ProxyHelper.Create(settings);
            if (proxy is not null) handler.Proxy = proxy;

            Http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }
        else
        {
            Http = http;
        }

        Http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        Http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
    }

    /// <summary>GET 一个 HTML 页面并按正确编码解码</summary>
    public async Task<string> GetHtmlAsync(string url, CancellationToken ct = default)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return DecodeHtml(bytes, resp.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>按响应头 charset → &lt;meta charset&gt; → UTF-8 的顺序解码</summary>
    public static string DecodeHtml(byte[] bytes, string? charsetHeader)
    {
        if (!string.IsNullOrWhiteSpace(charsetHeader))
        {
            var text = TryDecode(bytes, charsetHeader!);
            if (text is not null) return text;
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*(?<cs>[A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var text = TryDecode(bytes, m.Groups["cs"].Value);
            if (text is not null) return text;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? TryDecode(byte[] bytes, string charset)
    {
        try { return Encoding.GetEncoding(charset.Trim().Trim('"', '\'').Trim()).GetString(bytes); }
        catch { return null; }
    }

    public void Dispose()
    {
        if (_ownsHttp) Http.Dispose();
    }
}

/// <summary>
/// 站点适配器。新增一个站点 = 新增一个实现类，不改动下载引擎。
/// </summary>
public interface ISiteAdapter
{
    SiteKind Kind { get; }

    string Name { get; }

    /// <summary>只根据 URL 形态判断能否处理</summary>
    bool CanHandle(Uri url);

    /// <summary>解析页面，得到剧集列表（尽量顺带把当前集的 m3u8 也解析出来）</summary>
    Task<SiteSeries> ParseAsync(string html, Uri pageUrl, SiteContext ctx, CancellationToken ct = default);

    /// <summary>逐集补全 m3u8 直链（列表页没给出的集走这里）</summary>
    Task<string> ResolvePlaylistUrlAsync(SiteEpisode episode, SiteContext ctx, CancellationToken ct = default);
}

/// <summary>
/// 站点识别入口。按注册顺序匹配，先命中先用。
/// 加新站点只需在 <see cref="CreateDefault"/> 里追加。
/// </summary>
public sealed class SiteResolver
{
    private readonly List<ISiteAdapter> _adapters = new();

    public IReadOnlyList<ISiteAdapter> Adapters => _adapters;

    public SiteResolver Register(ISiteAdapter adapter)
    {
        _adapters.Add(adapter);
        return this;
    }

    public static SiteResolver CreateDefault() => new SiteResolver().Register(new MacCmsAdapter());

    /// <summary>按 URL 找适配器；找不到返回 null（调用方据此提示"不支持的站点"）</summary>
    public ISiteAdapter? Resolve(Uri url) => _adapters.FirstOrDefault(a => a.CanHandle(url));

    /// <summary>
    /// 一步到位：识别站点 → 抓页面 → 解析剧集。
    /// </summary>
    public async Task<SiteSeries> ParseAsync(string url, SiteContext ctx, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"不是合法的绝对地址：{url}", nameof(url));

        var adapter = Resolve(uri)
            ?? throw new NotSupportedException(
                $"暂不支持该站点（{uri.Host}）。当前已登记：{string.Join("、", _adapters.Select(a => a.Name))}");

        var html = await ctx.GetHtmlAsync(uri.ToString(), ct).ConfigureAwait(false);
        var series = await adapter.ParseAsync(html, uri, ctx, ct).ConfigureAwait(false);

        // 列表页通常只有当前集的 m3u8，这里顺手补一集，失败不影响列表展示
        var first = series.SelectedEpisodes.FirstOrDefault();
        if (first is not null && string.IsNullOrEmpty(first.PlaylistUrl))
        {
            try { first.PlaylistUrl = await adapter.ResolvePlaylistUrlAsync(first, ctx, ct).ConfigureAwait(false); }
            catch (Exception ex) { series.Log.Add($"当前集直链解析失败：{ex.Message}"); }
        }

        return series;
    }

    /// <summary>把一集补全为 m3u8 直链</summary>
    public async Task<string> ResolvePlaylistUrlAsync(SiteSeries series, SiteEpisode episode,
        SiteContext ctx, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(episode.PlaylistUrl)) return episode.PlaylistUrl!;

        var uri = new Uri(episode.PageUrl);
        var adapter = Resolve(uri) ?? throw new NotSupportedException($"暂不支持该站点（{uri.Host}）");
        episode.PlaylistUrl = await adapter.ResolvePlaylistUrlAsync(episode, ctx, ct).ConfigureAwait(false);
        return episode.PlaylistUrl!;
    }
}
