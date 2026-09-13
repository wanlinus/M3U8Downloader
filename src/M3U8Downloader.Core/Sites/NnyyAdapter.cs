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
///    <c>{"video_plays":[{"play_data":"…index.m3u8","src_site":"bfzy"}, …]}</c>；
///    同一个剧的不同集**可用源并不相同**（实测《交锋》第 1 集有 9 个源、第 17 集只剩 1 个），
///    所以直链必须逐集现取，不能一次解析全集；
/// 4. 站点挂在 Cloudflare 后面，国内直连会被重置（<c>curl: (35) Recv failure</c>），
///    因此页面解析声明 <see cref="NeedsProxy"/>；但分片 CDN（fengbao12、bfikuncdn 等）
///    实测直连正常，所以下载那一步不受影响。
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

    private static readonly Regex TagStrip = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>最多探测几个源：站点的顺序就是它的推荐顺序，通常第一个就能用</summary>
    private const int MaxSourceProbe = 4;

    public bool CanHandle(Uri url) =>
        HostPattern.IsMatch(url.Host) && DetailPath.IsMatch(url.AbsolutePath);

    public Task<SiteSeries> ParseAsync(string html, Uri pageUrl, SiteContext ctx, CancellationToken ct = default)
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

        var source = new SitePlaySource { Id = 1, Name = $"{siteName} 线路" };
        foreach (var (slug, number, title) in episodes)
        {
            source.Episodes.Add(new SiteEpisode
            {
                Number = number,
                SourceId = source.Id,
                PageUrl = pageUrl.ToString(),
                Title = title,
                Key = slug,
            });
        }

        series.Sources.Add(source);
        series.PreferredSourceId = source.Id;

        series.Log.Add($"{siteName}：{series.Title}，共 {episodes.Count} 集（剧 ID {seriesId}）");
        series.Log.Add("每集的播放源在下载时按 /_gp/ 接口现取：不同集可用源不同，会自动挑一个能用的。");
        return Task.FromResult(series);
    }

    public async Task<string> ResolvePlaylistUrlAsync(SiteEpisode episode, SiteContext ctx, CancellationToken ct = default)
    {
        var pageUri = new Uri(episode.PageUrl);
        var seriesId = DetailPath.Match(pageUri.AbsolutePath).Groups["id"].Value;
        if (string.IsNullOrEmpty(seriesId))
            throw new InvalidOperationException($"无法从页面地址里取到剧 ID：{episode.PageUrl}");

        var slug = string.IsNullOrWhiteSpace(episode.Key) ? $"ep{episode.Number}" : episode.Key!;
        var api = $"{pageUri.Scheme}://{pageUri.Authority}/_gp/{seriesId}/{slug}";

        // 页面接口与详情页同源，同样需要代理
        var json = await ctx.GetHtmlAsync(api, this, ct).ConfigureAwait(false);

        var plays = ParseVideoPlays(json);
        if (plays.Count == 0)
            throw new InvalidOperationException($"第 {episode.Number} 集没有可用的播放源（{api}）。");

        // 挨个探测前几个源，挑一个真能取到清单的；都探不出来就把站点的首选交回去，
        // 让下载引擎去报具体错误 —— 比这里吞掉更有助于排查。
        var candidates = plays.Take(MaxSourceProbe).ToList();
        foreach (var candidate in candidates)
        {
            if (await ctx.LooksLikePlaylistAsync(candidate, ct).ConfigureAwait(false))
                return candidate;
        }

        return candidates[0];
    }

    // ---------------- 解析辅助 ----------------

    /// <summary>从 /_gp/ 的 JSON 里取出所有候选直链（保持站点给的顺序）</summary>
    private static List<string> ParseVideoPlays(string json)
    {
        var list = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("video_plays", out var plays) ||
                plays.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in plays.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("play_data", out var url)) continue;

                var text = url.GetString();
                if (!string.IsNullOrWhiteSpace(text)) list.Add(text.Trim());
            }
        }
        catch (JsonException)
        {
            // 接口返回的不是 JSON（被拦截或改版）：返回空表，由调用方报错
        }

        return list;
    }

    private static List<(string Slug, int Number, string Title)> ParseEpisodes(string html)
    {
        var list = new List<(string, int, string)>();
        var seen = new HashSet<int>();

        foreach (Match m in PlayButton.Matches(html))
        {
            var slug = m.Groups["slug"].Value.Trim();
            var number = ParseEpisodeNumber(slug);
            if (number <= 0 || !seen.Add(number)) continue;

            var anchor = AnchorText.Match(m.Groups["inner"].Value);
            var title = anchor.Success ? CleanText(anchor.Groups["text"].Value) : "";
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

    private static string CleanText(string html) =>
        Regex.Replace(TagStrip.Replace(html, ""), @"\s+", " ").Trim();
}
