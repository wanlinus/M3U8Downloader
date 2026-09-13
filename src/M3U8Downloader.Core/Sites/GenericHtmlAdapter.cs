using System.Text.RegularExpressions;

namespace M3U8Downloader.Core.Sites;

/// <summary>
/// 通用兜底适配器：没有专用适配器时，直接从页面里找 m3u8 地址与剧集链接。
///
/// 它能覆盖相当一批小站：页面（或页内 JS）里直接写着 m3u8 地址，
/// 剧集列表就是一串「第N集」链接。这类站点以前只能报「暂不支持」。
///
/// 边界要说清楚：
/// - **动态接口型站点认不了** —— 直链要另外调 XHR 接口、地址还是拼出来的
///   （努努影院就是这样），那必须写专用适配器；
/// - 找不到 m3u8 时**明确报错**，而不是糊一个空列表出来 ——
///   否则用户会以为「解析成功了但一集都没有」。
/// </summary>
public sealed class GenericHtmlAdapter : ISiteAdapter
{
    public SiteKind Kind => SiteKind.Generic;
    public string Name => "通用解析（兜底）";

    /// <summary>永远排最后：只有专用适配器都不认这个 URL 时才轮到它</summary>
    public int Priority => -100;

    public bool NeedsProxy => false;

    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    /// <summary>页面里的 m3u8 地址（绝对、协议相对、根相对、纯相对都吃）</summary>
    private static readonly Regex PlaylistUrl = new(
        @"(?<url>(?:https?://[^\s""'<>\\]+?|//[^\s""'<>\\]+?|/[^\s""'<>\\]+?|[A-Za-z0-9_\-][^\s""'<>\\:/]*?)\.m3u8(?:\?[^\s""'<>\\]*)?)",
        Opts);

    private static readonly Regex Anchor = new(
        @"<a\b[^>]*href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex EpisodeText = new(@"第\s*(?<n>\d{1,4})\s*[集話话期]", Opts);

    private static readonly Regex TagStrip = new(@"<[^>]+>", RegexOptions.Compiled);

    /// <summary>最多探测几个候选直链</summary>
    private const int MaxProbe = 3;

    /// <summary>兜底：http(s) 页面都接，能不能解析交给 <see cref="ParseAsync"/></summary>
    public bool CanHandle(Uri url) => url.Scheme is "http" or "https";

    public async Task<SiteSeries> ParseAsync(string html, Uri pageUrl, SiteContext ctx, CancellationToken ct = default)
    {
        var siteName = ExtractSiteName(html, pageUrl);
        var title = ExtractTitle(html) ?? pageUrl.Host;

        var series = new SiteSeries
        {
            Kind = Kind,
            SiteName = siteName,
            PageUrl = pageUrl.ToString(),
            SeriesId = pageUrl.AbsolutePath,
            Title = title,
        };
        series.Headers["Referer"] = pageUrl.ToString();

        var episodeLinks = ParseEpisodeLinks(html, pageUrl);
        var playlists = ExtractPlaylistUrls(html, pageUrl);

        var source = new SitePlaySource { Id = 1, Name = $"{siteName} 通用解析" };

        if (episodeLinks.Count > 1)
        {
            // 有分集链接：每集单独一页，直链下载前再逐集解析
            foreach (var (number, url, text) in episodeLinks)
            {
                source.Episodes.Add(new SiteEpisode
                {
                    Number = number,
                    SourceId = source.Id,
                    PageUrl = url,
                    Title = text,
                });
            }

            series.Log.Add($"通用解析：识别到 {episodeLinks.Count} 个分集链接，直链在下载每集时逐个解析。");
        }
        else if (playlists.Count > 0)
        {
            // 页面里直接有 m3u8：当成单集
            source.Episodes.Add(new SiteEpisode
            {
                Number = 1,
                SourceId = source.Id,
                PageUrl = pageUrl.ToString(),
                Title = title,
                PlaylistUrl = playlists[0],
            });

            series.Log.Add($"通用解析：页面里直接找到 m3u8 地址（共 {playlists.Count} 个候选，已选第一个）。");
        }
        else
        {
            throw new NotSupportedException(
                "这个页面里既没有 m3u8 地址、也没有分集链接 —— 多半是「地址由接口动态返回」的站点，" +
                "通用解析处理不了，需要专门为它写一个适配器。");
        }

        series.Sources.Add(source);
        series.PreferredSourceId = source.Id;

        if (playlists.Count > 1 && episodeLinks.Count <= 1)
            series.Log.Add($"（另有 {playlists.Count - 1} 个 m3u8 候选未使用，通常是同一集的不同清晰度）");

        return series;
    }

    public async Task<string> ResolvePlaylistUrlAsync(SiteEpisode episode, SiteContext ctx, CancellationToken ct = default)
    {
        var pageUri = new Uri(episode.PageUrl);
        var html = await ctx.GetHtmlAsync(episode.PageUrl, this, ct).ConfigureAwait(false);

        var candidates = ExtractPlaylistUrls(html, pageUri);
        if (candidates.Count == 0)
            throw new InvalidOperationException($"这一集的页面里没有 m3u8 地址：{episode.PageUrl}");

        foreach (var candidate in candidates.Take(MaxProbe))
        {
            if (await ctx.LooksLikePlaylistAsync(candidate, ct).ConfigureAwait(false))
                return candidate;
        }

        return candidates[0];
    }

    // ---------------- 解析辅助 ----------------

    /// <summary>
    /// 抽出页面里的 m3u8 地址。
    /// 先把 JS 里的转义斜杠还原（<c>https:\/\/…</c> 很常见），否则一条都匹配不到。
    /// </summary>
    internal static List<string> ExtractPlaylistUrls(string html, Uri pageUrl)
    {
        var normalized = html
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
            .Replace("\\u002f", "/", StringComparison.Ordinal);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in PlaylistUrl.Matches(normalized))
        {
            var raw = m.Groups["url"].Value.Trim();
            if (raw.Length == 0) continue;

            string absolute;
            try
            {
                absolute = raw.StartsWith("//", StringComparison.Ordinal)
                    ? $"{pageUrl.Scheme}:{raw}"
                    : new Uri(pageUrl, raw).ToString();
            }
            catch
            {
                continue;
            }

            if (seen.Add(absolute)) result.Add(absolute);
        }

        return result;
    }

    /// <summary>抽出「第N集」这类分集链接（集号必须在文本里，避免把导航链接当剧集）</summary>
    internal static List<(int Number, string Url, string Text)> ParseEpisodeLinks(string html, Uri pageUrl)
    {
        var found = new Dictionary<int, (string Url, string Text)>();

        foreach (Match m in Anchor.Matches(html))
        {
            var href = m.Groups["href"].Value.Trim();
            if (href.Length == 0 ||
                href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith('#')) continue;

            var text = CleanText(m.Groups["text"].Value);
            var ep = EpisodeText.Match(text);
            if (!ep.Success || !int.TryParse(ep.Groups["n"].Value, out var number)) continue;
            if (number is <= 0 or > 5000) continue;

            string absolute;
            try { absolute = new Uri(pageUrl, href).ToString(); }
            catch { continue; }

            // 同一个集号出现多次时保留第一次（页面里常有重复的选集区）
            found.TryAdd(number, (absolute, string.IsNullOrWhiteSpace(text) ? $"第{number}集" : text));
        }

        return found
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Key, kv.Value.Url, kv.Value.Text))
            .ToList();
    }

    private static string? ExtractTitle(string html)
    {
        var m = Regex.Match(html,
            @"<meta[^>]*(?:property|name)\s*=\s*[""']og:title[""'][^>]*content\s*=\s*[""'](?<t>[^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (!m.Success)
        {
            m = Regex.Match(html,
                @"<meta[^>]*content\s*=\s*[""'](?<t>[^""']+)[""'][^>]*(?:property|name)\s*=\s*[""']og:title[""']",
                RegexOptions.IgnoreCase);
        }

        if (m.Success) return CleanText(m.Groups["t"].Value);

        m = Regex.Match(html, @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            var text = CleanText(m.Groups["t"].Value);
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        m = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;

        var title = CleanText(m.Groups["t"].Value);
        return string.IsNullOrWhiteSpace(title) ? null : title;
    }

    private static string ExtractSiteName(string html, Uri pageUrl)
    {
        var m = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            var parts = CleanText(m.Groups["t"].Value)
                .Split(['-', '|', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length > 0 && parts[^1].Length is > 0 and <= 12) return parts[^1];
        }

        return pageUrl.Host;
    }

    private static string CleanText(string html) =>
        Regex.Replace(TagStrip.Replace(html, ""), @"\s+", " ").Trim();
}
