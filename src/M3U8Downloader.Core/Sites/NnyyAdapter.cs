using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace M3U8Downloader.Core.Sites;

/// <summary>
/// 努努影院（nnyy.in）适配器。
///
/// 这个站点**不是**苹果 CMS，页面与取流接口都是自研的，实测形态：
/// 1. 详情页 <c>/{栏目}/{剧ID}.html</c>（如 <c>/dianshiju/20267897.html</c>），页内没有播放链接；
/// 2. 集列表靠 <c>&lt;li class="play-btn" ep_slug="ep12"&gt;</c> —— 集号在 <c>ep_slug</c> 里，
///    链接是 <c>javascript:;</c>（点击走 JS），所以**不能**按 href 抓；
/// 3. 直链要调 <c>GET /_gp/{剧ID}/{ep_slug}</c>，返回 JSON：
///    <c>{"video_plays":[{"play_data":"…index.m3u8","src_site":"bfzy"}, …],
///       "html_content":"&lt;button&gt;BF 第1集&lt;/button&gt;…"}</c>；
///    同一次返回里既给了该集的**全部可用源**，也给了它们在页面上的显示名；
/// 4. 站点挂在 Cloudflare 后面，国内直连会被重置（<c>curl: (35) Recv failure</c>），
///    因此页面解析声明 <see cref="NeedsProxy"/>；但分片 CDN（fengbao12、bfikuncdn 等）
///    实测直连正常，所以下载那一步不受影响。
///
/// 关于「多源」：同一个剧**每一集可用的源并不相同**（《交锋》第 1 集有 9 个源、
/// 第 17 集只剩 1 个），所以识别时会把每一集都问一遍，把结果汇总成
/// 「BF（bfzy）17 集 / IK（ikzy）17 集 / LZ（lzzy）16 集…」这样的源列表交给用户选，
/// 与苹果 CMS 站点的多源体验保持一致。
/// </summary>
public sealed class NnyyAdapter : ISiteAdapter
{
    public SiteKind Kind => SiteKind.Nnyy;
    public string Name => "努努影院";

    /// <summary>认域名的专用适配器，排在通用兜底之前</summary>
    public int Priority => 20;

    public bool NeedsProxy => true;

    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>详情页 <c>/{栏目}/{剧ID}.html</c>；栏目前缀是拼音（dianshiju / dianying / dongman…），不写死。</summary>
    private static readonly Regex DetailPath = new(@"^/(?<cat>[A-Za-z][A-Za-z0-9_\-]*)/(?<id>\d{4,})\.html$", Opts);

    /// <summary>努努换过不少域名（nnyy.in / nnyy.me / …），按主机名里的标签认，比写死列表耐用</summary>
    private static readonly Regex HostPattern = new(@"(^|\.)nnyy[a-z0-9\-]*\.", Opts);

    /// <summary>集按钮：集号在 ep_slug 属性里，文本在内部的 &lt;a&gt; 里</summary>
    private static readonly Regex PlayButton = new(
        @"<li\b[^>]*ep_slug\s*=\s*[""'](?<slug>[^""']+)[""'][^>]*>(?<inner>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AnchorText = new(
        @"<a\b[^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary> /_gp/ 返回的 html_content 里，每个源一个按钮（"BF 第1集"），顺序与 video_plays 一一对应 </summary>
    private static readonly Regex SourceButton = new(
        @"<button\b[^>]*>(?<text>[^<]*)</button>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagStrip = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>逐集取源时的并发。每集一次小请求（约 2 KB），但都走代理，别开太大</summary>
    private const int PlayFetchConcurrency = 5;

    public bool CanHandle(Uri url) =>
        HostPattern.IsMatch(url.Host) && DetailPath.IsMatch(url.AbsolutePath);

    public async Task<SiteSeries> ParseAsync(string html, Uri pageUrl, SiteContext ctx, CancellationToken ct = default)
    {
        var match = DetailPath.Match(pageUrl.AbsolutePath);
        var seriesId = match.Groups["id"].Value;
        var siteName = ExtractSiteName(html, pageUrl);

        var series = new SiteSeries
        {
            Kind = Kind,
            SiteName = siteName,
            PageUrl = pageUrl.ToString(),
            SeriesId = seriesId,
            Title = ExtractTitle(html) ?? seriesId,
            CoverUrl = TryExtractCover(html, pageUrl),
            Category = match.Groups["cat"].Value,
        };

        // 取流与分片都要带 Referer（CDN 会校验防盗链）
        series.Headers["Referer"] = pageUrl.ToString();

        var episodes = ParseEpisodes(html);
        if (episodes.Count == 0)
        {
            throw new NotSupportedException(
                "页面里没有找到剧集列表（ep_slug）。努努影院可能改版了，" +
                "把页面地址反馈一下就能补上。");
        }

        series.Log.Add($"{siteName}：{series.Title}，共 {episodes.Count} 集（剧 ID {seriesId}）");

        // ---- 逐集取源 ----
        // 每一集的可用源都不一样，只有挨个问一遍才能列出「哪个源有哪些集」。
        // 每集一次小请求（约 2 KB，走代理），并发跑。
        var perEpisode = new EpisodePlays[episodes.Count];
        using (var gate = new SemaphoreSlim(PlayFetchConcurrency))
        {
            var tasks = episodes.Select(async (episode, index) =>
            {
                try
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                try
                {
                    var plays = await FetchPlaysAsync(pageUrl, seriesId, episode.Slug, ctx, ct).ConfigureAwait(false);
                    perEpisode[index] = new EpisodePlays(episode, plays);
                }
                catch
                {
                    // 单集取源失败不能拖垮整次识别：那一集就是「没有可用源」，
                    // 其余集照常列出 —— 至少能把能下的先下了。
                    perEpisode[index] = new EpisodePlays(episode, new List<PlayInfo>());
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        BuildSources(series, perEpisode, pageUrl);
        return series;
    }

    public async Task<string> ResolvePlaylistUrlAsync(SiteEpisode episode, SiteContext ctx, CancellationToken ct = default)
    {
        var pageUri = new Uri(episode.PageUrl);
        var seriesId = DetailPath.Match(pageUri.AbsolutePath).Groups["id"].Value;
        if (string.IsNullOrEmpty(seriesId))
            throw new InvalidOperationException($"无法从页面地址里取到剧 ID：{episode.PageUrl}");

        var slug = string.IsNullOrWhiteSpace(episode.Key) ? $"ep{episode.Number}" : episode.Key!;
        var plays = await FetchPlaysAsync(pageUri, seriesId, slug, ctx, ct).ConfigureAwait(false);
        if (plays.Count == 0)
            throw new InvalidOperationException($"第 {episode.Number} 集没有可用的播放源（{ApiUrl(pageUri, seriesId, slug)}）。");

        return plays[0].Url;
    }

    // ---------------- 取源与汇总 ----------------

    private async Task<List<PlayInfo>> FetchPlaysAsync(
        Uri pageUri, string seriesId, string slug, SiteContext ctx, CancellationToken ct)
    {
        // 页面接口与详情页同源，同样需要代理
        var json = await ctx.GetHtmlAsync(ApiUrl(pageUri, seriesId, slug), this, ct).ConfigureAwait(false);
        return ParsePlays(json);
    }

    private static string ApiUrl(Uri pageUri, string seriesId, string slug) =>
        $"{pageUri.Scheme}://{pageUri.Authority}/_gp/{seriesId}/{slug}";

    /// <summary>
    /// 把「源 → 集」汇总成 <see cref="SiteSeries.Sources"/>。
    /// 源的先后顺序沿用第一集里的排列（站点自己的推荐顺序）。
    /// </summary>
    private static void BuildSources(SiteSeries series, EpisodePlays[] perEpisode, Uri pageUrl)
    {
        var order = new List<string>();
        var map = new Dictionary<string, SitePlaySource>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in perEpisode)
        {
            if (item is null) continue;

            foreach (var play in item.Plays)
            {
                var name = Describe(play);
                if (!map.TryGetValue(name, out var source))
                {
                    source = new SitePlaySource { Id = map.Count + 1, Name = name };
                    map[name] = source;
                    order.Add(name);
                }

                source.Episodes.Add(new SiteEpisode
                {
                    Number = item.Episode.Number,
                    SourceId = source.Id,
                    PageUrl = pageUrl.ToString(),
                    Title = item.Episode.Title,
                    Key = item.Episode.Slug,
                    // 直链在识别阶段就拿到了，下载时不必再请求接口
                    PlaylistUrl = play.Url,
                });
            }
        }

        if (order.Count == 0)
        {
            throw new NotSupportedException(
                "没能从 /_gp/ 接口取到任何一集的播放源。可能是站点改版，或者网络/代理不通" +
                "（该站点的页面需要代理，分片下载才直连）。");
        }

        foreach (var name in order) series.Sources.Add(map[name]);
        series.PreferredSourceId = map[order[0]].Id;

        series.Log.Add($"共 {order.Count} 个播放源：" +
                       string.Join(" / ", order.Select(n => $"{n} {map[n].Episodes.Count} 集")));

        var empty = perEpisode.Count(x => x is null || x.Plays.Count == 0);
        if (empty > 0)
            series.Log.Add($"另有 {empty} 集没有任何可用源（多为源站已下架该集）。");
    }

    /// <summary>源名：站点在按钮上显示什么就用什么（"BF"），顺带带上它的资源站代号（"bfzy"）</summary>
    private static string Describe(PlayInfo play)
    {
        var shortName = play.ShortName?.Trim() ?? "";
        var site = play.Site?.Trim() ?? "";

        if (shortName.Length == 0) return site.Length == 0 ? "未知源" : site;
        if (site.Length == 0 || site.Equals(shortName, StringComparison.OrdinalIgnoreCase)) return shortName;
        return $"{shortName}（{site}）";
    }

    /// <summary>从 /_gp/ 的 JSON 里取出所有候选源（保持站点给的顺序）</summary>
    private static List<PlayInfo> ParsePlays(string json)
    {
        var list = new List<PlayInfo>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("video_plays", out var plays) ||
                plays.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            // 源简称在 html_content 的按钮文本里（"BF 第1集"），顺序与 video_plays 对应
            var names = new List<string>();
            if (doc.RootElement.TryGetProperty("html_content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                foreach (Match m in SourceButton.Matches(content.GetString() ?? ""))
                    names.Add(ShortNameFrom(CleanText(m.Groups["text"].Value)));
            }

            var index = 0;
            foreach (var item in plays.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("play_data", out var url)) continue;

                var text = url.GetString();
                if (string.IsNullOrWhiteSpace(text)) continue;

                var site = item.TryGetProperty("src_site", out var s) ? s.GetString() ?? "" : "";
                list.Add(new PlayInfo(text.Trim(), site, index < names.Count ? names[index] : ""));
                index++;
            }
        }
        catch (JsonException)
        {
            // 接口返回的不是 JSON（被拦截或改版）：返回空表，由调用方报错
        }

        return list;
    }

    /// <summary>
    /// 「BF 第1集」→「BF」，「SD HD」→「SD」。
    /// 站点把源名和集数/画质写在同一行按钮上，这里只取源名那一段。
    /// </summary>
    private static string ShortNameFrom(string text)
    {
        var stripped = Regex.Replace(text, @"第\s*\d+\s*[集话期]", "").Trim();
        var first = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return string.IsNullOrWhiteSpace(first) ? stripped : first;
    }

    // ---------------- 页面解析 ----------------

    private static List<(string Slug, int Number, string Title)> ParseEpisodes(string html)
    {
        var list = new List<(string, int, string)>();
        var seenNumbers = new HashSet<int>();
        var seenSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fallback = 0;

        foreach (Match m in PlayButton.Matches(html))
        {
            var slug = m.Groups["slug"].Value.Trim();
            if (slug.Length == 0 || !seenSlugs.Add(slug)) continue;

            var anchor = AnchorText.Match(m.Groups["inner"].Value);
            var title = anchor.Success ? CleanText(anchor.Groups["text"].Value) : "";

            var number = ParseEpisodeNumber(slug);
            if (number <= 0)
            {
                // 电影页的 slug 是 "hd" / "other" 这种，**根本不含集号** ——
                // 这时按出现顺序编号。少了这一步，整页一集都解析不出来
                // （报「页面里没有找到剧集列表」，努努的电影页就是这样）。
                do { number = ++fallback; } while (!seenNumbers.Add(number));
            }
            else if (!seenNumbers.Add(number))
            {
                // 同一个集号出现多次（页面里常有重复的选集区），保留第一次
                continue;
            }

            list.Add((slug, number, string.IsNullOrWhiteSpace(title) ? $"第{number}集" : title));
        }

        return list.OrderBy(e => e.Item2).ToList();
    }

    /// <summary>ep12 → 12；也吃纯数字（有的模板直接写 12）</summary>
    private static int ParseEpisodeNumber(string slug)
    {
        var m = Regex.Match(slug, @"(\d+)");
        if (!m.Success || !int.TryParse(m.Groups[1].Value, out var number)) return 0;
        return number is > 0 and < 100000 ? number : 0;
    }

    private static string? ExtractTitle(string html)
    {
        var m = Regex.Match(html,
            @"<h1[^>]*class\s*=\s*[""'][^""']*product-title[^""']*[""'][^>]*>(?<t>.*?)</h1>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success)
            m = Regex.Match(html, @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;

        var text = CleanText(m.Groups["t"].Value);
        // 去掉标题尾部的年份，如「交锋 (2026)」
        text = Regex.Replace(text, @"\s*[（(]\s*(?:19|20)\d{2}\s*[)）]\s*$", "");
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>站点名取 &lt;title&gt; 的最后一段：「《交锋》全集在线观看 - 电视剧 - 努努影院」→「努努影院」</summary>
    private static string ExtractSiteName(string html, Uri pageUrl)
    {
        var m = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            var title = CleanText(m.Groups["t"].Value);
            var parts = title.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0)
            {
                var last = parts[^1];
                if (last.Length is > 0 and <= 12) return last;
            }
        }

        return pageUrl.Host;
    }

    private static string? TryExtractCover(string html, Uri pageUrl)
    {
        // class 与 src 的先后顺序不固定，两种都试
        var m = Regex.Match(html,
            @"<img\b[^>]*class\s*=\s*[""'][^""']*detail-img[^""']*[""'][^>]*src\s*=\s*[""'](?<src>[^""']+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success)
        {
            m = Regex.Match(html,
                @"<img\b[^>]*src\s*=\s*[""'](?<src>[^""']+)[""'][^>]*class\s*=\s*[""'][^""']*detail-img",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        if (!m.Success) return null;
        try { return new Uri(pageUrl, m.Groups["src"].Value).ToString(); }
        catch { return null; }
    }

    /// <summary>去标签 + 解码 HTML 实体（剧名里的 <c>&amp;#39;</c> 要还原成撇号）</summary>
    private static string CleanText(string html) =>
        WebUtility.HtmlDecode(Regex.Replace(TagStrip.Replace(html, ""), @"\s+", " ")).Trim();

    // ---------------- 内部类型 ----------------

    /// <summary>一个候选源：直链 + 资源站代号（bfzy）+ 页面上的显示名（BF）</summary>
    private sealed record PlayInfo(string Url, string Site, string ShortName);

    /// <summary>某一集取到的全部候选源</summary>
    private sealed record EpisodePlays((string Slug, int Number, string Title) Episode, List<PlayInfo> Plays);
}
