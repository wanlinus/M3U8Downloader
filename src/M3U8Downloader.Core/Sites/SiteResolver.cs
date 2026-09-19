using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using M3U8Downloader.Core.Net;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Sites;

/// <summary>
/// 抓取站点页面所需的上下文（HttpClient + 默认请求头 + 解码工具）。
///
/// 有两套客户端：
/// - <see cref="Direct"/>：直连，默认都用它。站点大多在国内，绕代理更慢，
///   视频分片与站点解析**默认都不走代理**；
/// - <see cref="Proxied"/>：配了代理地址才有。只给「声明自己需要代理」的适配器用
///   （典型情况：站点挂在 Cloudflare 后面，国内直连被 RST —— 实测努努影院就是这样，
///   但它的分片 CDN 直连正常，所以代理只用在页面解析这一步）。
/// </summary>
public sealed class SiteContext : IDisposable {
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private readonly bool _ownsDirect;
    private readonly bool _ownsProxy;

    /// <summary>
    /// 连接超时放宽到 30 秒的直连客户端，只在「直连超时 → 代理也不行」这条链路上兜底。
    /// 没有配代理时不会创建（没有回退链，直连超时该直接报错）。
    /// </summary>
    private readonly HttpClient? _directPatient;

    /// <summary>直连客户端</summary>
    public HttpClient Direct { get; }

    /// <summary>代理客户端；没配代理时为 null</summary>
    public HttpClient? Proxied { get; }

    public string UserAgent { get; }

    /// <summary>生效的代理地址（规范化后）；没配或格式不对为 null</summary>
    public string? ProxyUrl { get; }

    public bool HasProxy => Proxied is not null;

    /// <summary>本实例上「直连失败 → 改用代理」发生了多少次（写进解析日志用）</summary>
    public int ProxyFallbackCount { get; private set; }

    /// <summary>最后一次触发代理回退的主机名</summary>
    public string? LastProxyFallbackHost { get; private set; }

    /// <summary>
    /// 已确认「直连不通、必须走代理」的主机 —— 与下载引擎同一套做法。
    ///
    /// 这一条对速度影响极大：识别一个站点往往要抓好几次页面
    /// （详情页 → 播放页 → playerconfig.js → 逐集解析），
    /// 不记的话**每一次**都要先白等一遍连接超时。
    /// 实测欧乐影院：详情页识别 36 秒、播放页 27 秒，绝大部分时间都耗在重复的超时上。
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _proxyRequiredHosts = new(StringComparer.OrdinalIgnoreCase);

    static SiteContext() {
        // 国内不少影视站是 GBK/GB2312，.NET Core 默认不带这些代码页，需要显式注册。
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* 已注册或不可用 */ }
    }

    public SiteContext(HttpClient? http = null, string? userAgent = null, string? proxyUrl = null) {
        UserAgent = string.IsNullOrWhiteSpace(userAgent) ? DefaultUserAgent : userAgent!;

        if (http is not null) {
            // 外部传入的客户端由调用方负责释放（自检里就是这么用的）
            Direct = http;
        } else {
            Direct = CreateClient(null, UserAgent);
            _ownsDirect = true;
        }

        // 代理地址来源：显式传入 → 设置里填的地址。
        // 刻意**不看 ProxyEnabled**：那个开关管的是「下载 FFmpeg 时走代理」，
        // 而这里要解决的是「被墙站点的页面根本打不开」——
        // 用户既然填了代理地址，就说明这台机器上有代理可用，不必再开一个开关。
        var normalized = ProxyHelper.Normalize(proxyUrl ?? TryReadProxyFromSettings());
        if (normalized is not null) {
            try {
                Proxied = CreateClient(normalized, UserAgent);
                ProxyUrl = normalized;
                _ownsProxy = true;

                // 配了代理才需要它：直连超时 → 换代理 → 代理也被拒时，才有「再宽容地直连一次」的余地
                _directPatient = CreateClient(null, UserAgent, TimeSpan.FromSeconds(30));
            } catch {
                // 代理客户端建不起来就只直连，不影响其它站点
                Proxied = null;
                ProxyUrl = null;
            }
        }
    }

    private static string? TryReadProxyFromSettings() {
        try { return AppSettingsStore.Load().ProxyUrl; } catch { return null; }
    }

    private static HttpClient CreateClient(string? proxyUrl, string userAgent, TimeSpan? connectTimeout = null) {
        // 用 SocketsHttpHandler 而不是 HttpClientHandler：需要它的 ConnectTimeout
        var handler = new SocketsHttpHandler {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            // 连不上就早点认输：被墙的站点靠「直连失败 → 换代理」兜底。
            // 5 秒是权衡后的值 —— 正常站点建立 TCP 连远远用不到 5 秒，
            // 而 8 秒的旧值会让每次首探都多等 3 秒（同一台主机只探一次，见 _proxyRequiredHosts）
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(5),

            // ★ 这一行不能少：SocketsHttpHandler.UseProxy 默认是 **true**，
            //   而 Proxy 为 null 时它会退到 HttpClient.DefaultProxy ——
            //   Windows 上那就是系统的 WinINET 代理设置。
            //   用户机器上开着 Clash 之类的工具时，「直连」客户端会**悄悄走代理**：
            //   既白耗流量（用户明确在意），又会被按代理 IP 拒绝。
            //   实测影迷界影院：真直连 200 / 走系统代理 403，而解析失败的根因正是后者。
            //   只有显式给了代理地址的那份 handler 才该打开代理。
            UseProxy = proxyUrl is not null,
        };

        if (proxyUrl is not null)
            handler.Proxy = new WebProxy(new Uri(proxyUrl)) { BypassProxyOnLocal = true };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
        return client;
    }

    /// <summary>该适配器应该用哪个客户端（声明 NeedsProxy 且配了代理才走代理）</summary>
    public HttpClient For(ISiteAdapter adapter) =>
        adapter.NeedsProxy && Proxied is not null ? Proxied : Direct;

    /// <summary>GET 一个 HTML 页面并按正确编码解码（直连）</summary>
    public Task<string> GetHtmlAsync(string url, CancellationToken ct = default) =>
        GetHtmlAsync(url, Direct, ct);

    /// <summary>
    /// 按适配器的需求取页面，**直连不通时自动改走代理重试一次**。
    ///
    /// 这一层回退是必要的：绝大多数影视站在国内、直连又快又不耗代理，
    /// 但确实有一部分（欧乐影院 olevod.com 实测就是这样）挂在外面，国内直连直接超时。
    /// 让每个适配器自己声明 NeedsProxy 既容易漏，又会让国内站点白白绕一圈代理；
    /// 靠"失败了再回退"就两全了 —— 通畅时零代理开销，不通时自动兜底。
    /// </summary>
    public async Task<string> GetHtmlAsync(string url, ISiteAdapter adapter, CancellationToken ct = default) {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : null;
        var canFallback = host is not null && Proxied is not null;

        // 适配器明确要求代理，或这台主机已经证明直连不通 → 直接用代理，不再白等一次超时
        if (canFallback && (adapter.NeedsProxy || _proxyRequiredHosts.ContainsKey(host!)))
            return await GetHtmlAsync(url, Proxied!, ct).ConfigureAwait(false);

        try {
            return await GetHtmlAsync(url, Direct, ct).ConfigureAwait(false);
        } catch (Exception ex) when (canFallback && IsConnectivityFailure(ex, ct)) {
            try {
                var html = await GetHtmlAsync(url, Proxied!, ct).ConfigureAwait(false);
                NoteProxyFallback(url, host!);
                return html;
            } catch (Exception) when (!ct.IsCancellationRequested && _directPatient is not null) {
                // 代理也拿不到。但要分清：「直连 5 秒内没连上」并不等于站点不可达 ——
                // 实测影迷界影院直连耗时在 0.9s~30s 之间剧烈波动，而它的代理 IP 被站点 403。
                // 把「慢」当成「不通」，就会把本来能成功的一次请求判死（还会顺带污染
                // _proxyRequiredHosts 的判断），所以给直连一次宽容的重试。
                return await GetHtmlAsync(url, _directPatient, ct).ConfigureAwait(false);
            }
        } catch (HttpRequestException ex) when (canFallback && ShouldRetryViaProxy(ex.StatusCode)) {
            // 连上了但被「按 IP 拒绝」（403/451）：同样换代理再试一次
            var html = await GetHtmlAsync(url, Proxied!, ct).ConfigureAwait(false);
            NoteProxyFallback(url, host!);
            return html;
        }
    }

    private void NoteProxyFallback(string url, string host) {
        ProxyFallbackCount++;
        LastProxyFallbackHost = host;
        _proxyRequiredHosts.TryAdd(host, true);
    }

    /// <summary>403/451/429 这类「按 IP 拒绝」值得换代理再试；404/410 是资源真没了，换也没用</summary>
    private static bool ShouldRetryViaProxy(HttpStatusCode? status) =>
        status is HttpStatusCode.Forbidden
            or HttpStatusCode.UnavailableForLegalReasons
            or HttpStatusCode.TooManyRequests;

    /// <summary>
    /// 是不是「网络根本不通」（而不是站点返回了 4xx/5xx）。
    /// 只有这一类才值得换代理重试 —— 站点自己报错时换代理纯属白费。
    /// </summary>
    private static bool IsConnectivityFailure(Exception ex, CancellationToken ct) {
        // 用户按了取消：这是取消，不是网络问题
        if (ct.IsCancellationRequested) return false;

        for (var e = ex; e is not null; e = e.InnerException) {
            if (e is HttpRequestException or SocketException) return true;
            if (e is TaskCanceledException) return true;   // HttpClient.Timeout 超时
        }

        return false;
    }

    /// <summary>用指定客户端 GET 一个页面</summary>
    public async Task<string> GetHtmlAsync(string url, HttpClient client, CancellationToken ct = default) {
        using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return DecodeHtml(bytes, resp.Content.Headers.ContentType?.CharSet);
    }

    /// <summary>
    /// 这个地址是不是一个真的 m3u8 清单（只读开头几百字节）。
    ///
    /// 用途：同一个剧往往挂着好几个源，其中一部分早已失效（返回 404 或干脆是 HTML）。
    /// 挨个探测一遍比"直接拿第一个、失败了再报错"体验好得多，代价又很小。
    /// 先直连试，直连不通再走代理（分片 CDN 通常直连即可）。
    /// </summary>
    public async Task<bool> LooksLikePlaylistAsync(string url, CancellationToken ct = default) {
        foreach (var client in EnumerateClients()) {
            try {
                using var resp = await client
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode) continue;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[512];
                var read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read <= 0) continue;

                return Encoding.UTF8.GetString(buffer, 0, read).Contains("#EXTM3U", StringComparison.Ordinal);
            } catch {
                // 这个客户端不行就换下一个
            }
        }

        return false;
    }

    private IEnumerable<HttpClient> EnumerateClients() {
        yield return Direct;
        if (Proxied is not null) yield return Proxied;
    }

    /// <summary>按响应头 charset → &lt;meta charset&gt; → UTF-8 的顺序解码</summary>
    public static string DecodeHtml(byte[] bytes, string? charsetHeader) {
        if (!string.IsNullOrWhiteSpace(charsetHeader)) {
            var text = TryDecode(bytes, charsetHeader!);
            if (text is not null) return text;
        }

        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = Regex.Match(head, @"charset\s*=\s*[""']?\s*(?<cs>[A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase);
        if (m.Success) {
            var text = TryDecode(bytes, m.Groups["cs"].Value);
            if (text is not null) return text;
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? TryDecode(byte[] bytes, string charset) {
        try { return Encoding.GetEncoding(charset.Trim().Trim('"', '\'').Trim()).GetString(bytes); } catch { return null; }
    }

    public void Dispose() {
        if (_ownsDirect) Direct.Dispose();
        if (_ownsProxy) Proxied?.Dispose();
        _directPatient?.Dispose();
    }
}

/// <summary>
/// 站点适配器。**新增一个站点 = 在 Sites 目录下新增一个实现类**，
/// 不需要改任何注册代码：<see cref="SiteResolver.CreateDefault"/> 会反射扫描本程序集。
/// </summary>
public interface ISiteAdapter {
    SiteKind Kind { get; }

    string Name { get; }

    /// <summary>
    /// 匹配优先级，大的先匹配（默认 0）。
    /// 认域名的专用适配器给正值；什么都接的通用兜底适配器给负值，永远排在最后。
    /// </summary>
    int Priority => 0;

    /// <summary>
    /// 抓这个站点的**页面**是否需要代理。
    /// 只影响页面解析；分片下载始终直连 —— 国内 CDN 基本都能直连，
    /// 绕代理既慢又容易触发防盗链。
    /// </summary>
    bool NeedsProxy => false;

    /// <summary>只根据 URL 形态判断能否处理</summary>
    bool CanHandle(Uri url);

    /// <summary>解析页面，得到剧集列表（尽量顺带把当前集的 m3u8 也解析出来）</summary>
    Task<SiteSeries> ParseAsync(string html, Uri pageUrl, SiteContext ctx, CancellationToken ct = default);

    /// <summary>逐集补全 m3u8 直链（列表页没给出的集走这里）</summary>
    Task<string> ResolvePlaylistUrlAsync(SiteEpisode episode, SiteContext ctx, CancellationToken ct = default);
}

/// <summary>
/// 站点识别入口。按优先级匹配，先命中先用。
///
/// **新增站点不用改这个文件**：在 <c>Sites/</c> 下写一个 <see cref="ISiteAdapter"/> 实现，
/// 它会被自动登记。通用兜底适配器（<see cref="GenericHtmlAdapter"/>）优先级最低，
/// 只在前面的专用适配器都不认的时候出手。
/// </summary>
public sealed class SiteResolver {
    private readonly List<ISiteAdapter> _adapters = new();

    public IReadOnlyList<ISiteAdapter> Adapters => _adapters;

    public SiteResolver Register(ISiteAdapter adapter) {
        _adapters.Add(adapter);
        _adapters.Sort((a, b) => {
            var byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0 ? byPriority : string.CompareOrdinal(a.Name, b.Name);
        });
        return this;
    }

    /// <summary>扫描本程序集里所有适配器实现并登记</summary>
    public static SiteResolver CreateDefault() {
        var resolver = new SiteResolver();
        foreach (var adapter in Discover()) resolver.Register(adapter);
        return resolver;
    }

    private static IEnumerable<ISiteAdapter> Discover() {
        var types = typeof(SiteResolver).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(ISiteAdapter).IsAssignableFrom(t))
            .ToList();

        var built = new List<ISiteAdapter>();
        foreach (var type in types) {
            try {
                if (Activator.CreateInstance(type) is ISiteAdapter adapter) built.Add(adapter);
            } catch {
                // 某个适配器构造失败不能拖垮整个程序：跳过它，其余站点照常可用
            }
        }

        return built.OrderByDescending(a => a.Priority).ThenBy(a => a.Name, StringComparer.Ordinal);
    }

    /// <summary>按 URL 找适配器；找不到返回 null（调用方据此提示"不支持的站点"）</summary>
    public ISiteAdapter? Resolve(Uri url) => _adapters.FirstOrDefault(a => a.CanHandle(url));

    /// <summary>
    /// 一步到位：识别站点 → 抓页面 → 解析剧集。
    /// </summary>
    public async Task<SiteSeries> ParseAsync(string url, SiteContext ctx, CancellationToken ct = default) {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"不是合法的绝对地址：{url}", nameof(url));

        var adapter = Resolve(uri)
            ?? throw new NotSupportedException(
                $"暂不支持该站点（{uri.Host}）。当前已登记：{string.Join("、", _adapters.Select(a => a.Name))}");

        string html;
        try {
            html = await ctx.GetHtmlAsync(uri.ToString(), adapter, ct).ConfigureAwait(false);
        } catch (Exception ex) when (adapter.NeedsProxy && !ct.IsCancellationRequested) {
            throw await BuildProxyErrorAsync(adapter, ctx, ex, ct).ConfigureAwait(false);
        }

        var series = await adapter.ParseAsync(html, uri, ctx, ct).ConfigureAwait(false);

        if (adapter.NeedsProxy && ctx.HasProxy)
            series.Log.Add($"{adapter.Name}：页面经代理访问（{ctx.ProxyUrl}），分片仍直连下载。");
        else if (ctx.ProxyFallbackCount > 0)
            series.Log.Add($"直连 {ctx.LastProxyFallbackHost} 不通，已自动改用代理（{ctx.ProxyUrl}）；分片下载仍然直连。");

        // 列表页通常只有当前集的 m3u8，这里顺手补一集，失败不影响列表展示
        var first = series.SelectedEpisodes.FirstOrDefault();
        if (first is not null && string.IsNullOrEmpty(first.PlaylistUrl)) {
            try { first.PlaylistUrl = await adapter.ResolvePlaylistUrlAsync(first, ctx, ct).ConfigureAwait(false); } catch (Exception ex) { series.Log.Add($"当前集直链解析失败：{ex.Message}"); }
        }

        return series;
    }

    /// <summary>
    /// 把「需要代理的站点抓不到页面」翻译成一句能让人立刻知道该干什么的话。
    ///
    /// 为什么要专门做这件事：用户最常遇到的**不是**"没填代理"，而是
    /// "填了、但代理软件没开"（Clash 关掉了 / 换了端口）。这时原始异常只有一句
    /// 「由于目标计算机积极拒绝，无法连接」，从里面根本看不出跟代理有关 ——
    /// 用户会以为"这软件坏了"，而不是"我代理没开"。
    ///
    /// 所以这里顺手测一次代理：测通了说明问题在站点那边，测不通就把代理这条线索点破。
    /// 代价是一次几十字节的请求，换来的是用户不用为此来问我们。
    /// </summary>
    private static async Task<SiteProxyRequiredException> BuildProxyErrorAsync(
        ISiteAdapter adapter, SiteContext ctx, Exception error, CancellationToken ct) {
        const string tail = "视频分片本身是直连下载的，不受影响。";

        if (!ctx.HasProxy) {
            return new SiteProxyRequiredException(
                $"{adapter.Name} 必须通过代理才能访问，但还没有配置代理。\n\n" +
                $"请到「设置 → 代理」填好地址（例如 http://127.0.0.1:7897）后重试。{tail}",
                error) { ProxyMissing = true };
        }

        var test = await ProxyHelper.TestAsync(ctx.ProxyUrl, ct).ConfigureAwait(false);
        if (test.Success) {
            // 代理是通的 → 问题在站点那边（改版 / 临时故障 / 需要人机验证）
            return new SiteProxyRequiredException(
                $"{adapter.Name} 的页面抓取失败，但代理 {ctx.ProxyUrl} 是通的（{test.Message}）。\n\n" +
                $"可能是站点临时不可用或改版了。\n\n原始错误：{error.Message}",
                error);
        }

        return new SiteProxyRequiredException(
            $"{adapter.Name} 必须通过代理才能访问，但代理 {ctx.ProxyUrl} 连不上：{test.Message}\n\n" +
            $"请确认代理软件（Clash / v2ray 等）正在运行，且端口与「设置 → 代理」里填的一致。{tail}",
            error);
    }

    /// <summary>把一集补全为 m3u8 直链</summary>
    public async Task<string> ResolvePlaylistUrlAsync(SiteSeries series, SiteEpisode episode,
        SiteContext ctx, CancellationToken ct = default) {
        if (!string.IsNullOrWhiteSpace(episode.PlaylistUrl)) return episode.PlaylistUrl!;

        var uri = new Uri(episode.PageUrl);
        var adapter = Resolve(uri) ?? throw new NotSupportedException($"暂不支持该站点（{uri.Host}）");
        episode.PlaylistUrl = await adapter.ResolvePlaylistUrlAsync(episode, ctx, ct).ConfigureAwait(false);
        return episode.PlaylistUrl!;
    }
}

/// <summary>
/// 站点必须过代理才能打开，而当前代理不可用：要么压根没配，要么配了但连不上。
///
/// 单独建一个类型（而不是继续用 <see cref="NotSupportedException"/>）是为了让界面
/// 能据此把「打开设置」按钮摆出来 —— 靠匹配异常消息的文本来猜意图太脆，
/// 改一个错别字就失效了。继承 <see cref="NotSupportedException"/> 是为了兼容
/// 已有的捕获逻辑（调用方仍然可以只 catch NotSupportedException）。
/// </summary>
public sealed class SiteProxyRequiredException : NotSupportedException {
    public SiteProxyRequiredException(string message, Exception? inner = null)
        : base(message, inner) {
    }

    /// <summary>true = 压根没配代理；false = 配了但连不上（或代理通、站点本身有问题）</summary>
    public bool ProxyMissing { get; init; }
}
