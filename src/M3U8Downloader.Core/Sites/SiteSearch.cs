using System.Text.RegularExpressions;

namespace M3U8Downloader.Core.Sites;

/// <summary>
/// 搜索命中的一部剧。
///
/// <see cref="PosterUrl"/> / <see cref="Badge"/> / <see cref="Note"/> / <see cref="Intro"/>
/// 是给搜索弹窗里的卡片用的 —— 搜"交锋"会出来一堆同名的剧，光有剧名根本分不清哪部是哪部。
/// 认不出这些特征（宽松解析）时它们都是 null，卡片退化成纯文字行。
/// </summary>
public sealed record SiteSearchHit(
    string Title,
    string PageUrl,
    string? PosterUrl = null,
    string? Badge = null,
    string? Note = null,
    string? Intro = null);

/// <summary>
/// 一次搜索的结果。<c>Unsupported</c> = 这个站压根没有搜索入口（首页找不到搜索表单），
/// 调用方据此提示"这个站搜不了"，而不是显示成"没有搜到结果"。
/// </summary>
public sealed record SiteSearchResult(IReadOnlyList<SiteSearchHit> Hits, bool Unsupported) {
    public static readonly SiteSearchResult UnsupportedSite = new(Array.Empty<SiteSearchHit>(), true);
}

/// <summary>
/// 在**指定站点**里按关键词搜剧。
///
/// **搜索入口不靠猜，靠读首页的搜索表单。** 苹果 CMS 各家的搜索路径五花八门 ——
/// 实测青苹果影院在 <c>/vodsearch/-------------.html</c>、影迷界影院在 <c>/search.html</c>；
/// 我一开始挨个试了六种路径，其中五种都错。而首页的 <c>&lt;form&gt;</c> 里明明白白写着
/// 正确的 action 和关键词字段名。所以这里先抓首页、把表单读出来，再拿它去搜：
/// 换模板不用改代码，加新站也不用一个个试路径。
///
/// 网络与编码全部复用 <see cref="SiteContext"/> —— 直连不通自动改走代理、
/// 国内站的 GBK/GB2312 也能正确解码（它静态构造里注册了代码页）。
/// 站点没有搜索表单时**返回空列表**（调用方据此提示"这个站搜不了"），而不是抛异常。
/// </summary>
public static class SiteSearch {
    /// <summary>表单里可能用的关键词字段名（苹果 CMS 用 wd；别家模板可能不同）</summary>
    private static readonly string[] KeywordFields = { "wd", "keyword", "searchword", "q", "s" };

    /// <summary>一次搜索最多取多少条 —— 搜索页往往还有分页，先只取第一页</summary>
    private const int MaxHits = 50;

    /// <summary>
    /// 一条结果最多往回看多少字符来抓海报/角标/简介。
    /// 真实卡片在 1000~1900 字符之间（青苹果一张卡含海报、章节数、剧名、导演、标签、简介），
    /// 这个上限只是防止"最后一条"一路看到页脚去。
    /// </summary>
    private const int MaxEntryChars = 2400;

    public static async Task<SiteSearchResult> SearchAsync(
        SiteContext context, Uri siteRoot, string keyword, CancellationToken ct = default) {
        if (string.IsNullOrWhiteSpace(keyword))
            return new SiteSearchResult(Array.Empty<SiteSearchHit>(), Unsupported: false);

        // 1) 首页 → 搜索表单
        var home = await context.GetHtmlAsync(siteRoot.ToString(), needsProxyFirst: false, ct)
            .ConfigureAwait(false);

        var form = FindSearchForm(home);
        if (form is null) return SiteSearchResult.UnsupportedSite;

        // 2) 按表单拼出搜索地址
        var searchUrl = BuildSearchUrl(siteRoot, form.Value.Action, form.Value.Field, keyword);

        // 3) 抓搜索页并解析
        var html = await context.GetHtmlAsync(searchUrl, needsProxyFirst: false, ct).ConfigureAwait(false);
        return new SiteSearchResult(ParseHits(html, siteRoot), Unsupported: false);
    }

    /// <summary>
    /// 从首页里找搜索表单：返回 action 与关键词字段名。
    /// 找不到返回 null —— 那就说明这个站没有可用的搜索入口。
    /// </summary>
    public static (string Action, string Field)? FindSearchForm(string html) {
        foreach (Match form in Regex.Matches(html, @"<form\b[^>]*>(.*?)</form>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase)) {
            var body = form.Value;

            // 关键词字段：优先 wd，其次别家模板可能用的名字
            string? field = null;
            foreach (var candidate in KeywordFields) {
                if (Regex.IsMatch(body, $@"<input\b[^>]*name\s*=\s*[""']{candidate}[""']",
                        RegexOptions.IgnoreCase)) {
                    field = candidate;
                    break;
                }
            }
            if (field is null) continue;

            var action = Regex.Match(body, @"action\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
            return (action.Success ? action.Groups[1].Value : "", field);
        }

        return null;
    }

    /// <summary>把表单的 action + 字段名 + 关键词拼成一个可请求的绝对地址</summary>
    public static string BuildSearchUrl(Uri siteRoot, string action, string field, string keyword) {
        Uri target;
        if (string.IsNullOrWhiteSpace(action)) {
            target = siteRoot;                                   // action 为空 = 提交回当前页
        } else if (Uri.TryCreate(action, UriKind.Absolute, out var absolute)) {
            target = absolute;
        } else {
            target = new Uri(siteRoot, action);                  // 相对路径（常见是 /search.html）
        }

        // 原地址自己带查询串的话要用 & 接（比如 action 已经是 /vod/search.html?x=1）
        var separator = string.IsNullOrEmpty(target.Query) ? "?" : "&";
        return $"{target}{separator}{field}={Uri.EscapeDataString(keyword)}";
    }

    /// <summary>
    /// 从搜索页里挑出「剧」的链接。
    ///
    /// 只认**详情页**（<c>/voddetail/</c>、<c>/vod/detail/</c>、<c>/t/</c> 这类），
    /// 不认播放页 —— 详情页才带整部剧的分集清单，后面的流程正好接得上。
    /// 标题优先取 <c>&lt;a title="…"&gt;</c>，没有就退回去取链接里的文字。
    /// </summary>
    public static IReadOnlyList<SiteSearchHit> ParseHits(string html, Uri siteRoot) {
        // 先**严格**解析：只认"结果条目"才有的特征（海报 alt / 标题 div）。
        // "推荐标签"那种纯文本链接区因此天然被排除 —— 第一版用宽松解析时，
        // 影迷界影院搜出 30 条，前 8 条全是推荐位的剧。
        var strict = ParseStrict(html, siteRoot);
        if (strict.Count > 0) return strict;

        // 严格模式一条都没认出（模板结构不一样）：放宽重来，
        // 别因为认不出结构就让人什么都搜不到。
        return ParseLoose(html, siteRoot);
    }

    /// <summary>严格解析：只认"结果条目"才有的特征（海报图 alt、标题 div）</summary>
    public static IReadOnlyList<SiteSearchHit> ParseStrict(string html, Uri siteRoot) =>
        Parse(html, siteRoot, requireEntryMark: true);

    /// <summary>宽松解析：认不出结果条目的模板退回 title 属性 / 链接文字</summary>
    public static IReadOnlyList<SiteSearchHit> ParseLoose(string html, Uri siteRoot) =>
        Parse(html, siteRoot, requireEntryMark: false);

    private static List<SiteSearchHit> Parse(string html, Uri siteRoot, bool requireEntryMark) {
        // 先把候选连同"它在页面里的位置"收下来 —— 位置用来划分"这一条结果"的范围。
        var candidates = new List<(int Index, string Title, string Url)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match link in Regex.Matches(html, @"<a\b([^>]*)>(.*?)</a>",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase)) {
            var attributes = link.Groups[1].Value;
            var hrefMatch = Regex.Match(attributes, @"href\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            if (!hrefMatch.Success) continue;

            var href = hrefMatch.Groups[1].Value;
            if (!LooksLikeDetailPage(href)) continue;

            var title = ExtractTitle(attributes, link.Groups[2].Value, requireEntryMark);
            if (string.IsNullOrWhiteSpace(title)) continue;

            string url;
            try {
                url = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                    ? absolute.ToString()
                    : new Uri(siteRoot, href).ToString();
            } catch {
                continue;
            }

            // 同一部剧在结果页里常常出现好几次（海报、标题、更多按钮各一个），去重
            if (!seen.Add(url)) continue;

            candidates.Add((link.Index, title, url));
            if (candidates.Count >= MaxHits) break;
        }

        // 一条结果的"范围"＝它到下一条**不同**结果之间。
        //
        // 这里必须**先去重再划分**：青苹果影院的一张卡片里有 5 个指向同一个详情页的链接
        // （海报、更新至xx集、剧名、导演、简介），拿"下一个链接"当边界的话，
        // 第一条的范围会在第二个同 URL 的链接处被截断 —— 海报、标签、简介一个都取不到。
        var hits = new List<SiteSearchHit>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++) {
            var start = candidates[i].Index;
            var end = i + 1 < candidates.Count ? candidates[i + 1].Index : html.Length;
            var block = html[start..Math.Min(end, start + MaxEntryChars)];

            // 额外信息只在严格模式下取：宽松模式连"这是不是结果条目"都不确定，
            // 从一段不认识的 HTML 里抓图，抓到的多半是别的东西。
            var extra = requireEntryMark ? ExtractEntry(block, siteRoot) : default;

            hits.Add(new SiteSearchHit(
                candidates[i].Title, candidates[i].Url,
                extra.Poster, extra.Badge, extra.Note, extra.Intro));
        }

        return hits;
    }

    /// <summary>结果条目里除剧名之外的信息（海报 / 角标 / 类型 / 简介），认不出就是 null</summary>
    private static (string? Poster, string? Badge, string? Note, string? Intro) ExtractEntry(
        string block, Uri siteRoot) {
        var poster = Absolute(siteRoot, Attr(block, @"<img\b[^>]*\bsrc\s*=\s*[""']([^""']+)[""']"));

        // 角标：「全9集」「HD中字」「已完结」这类挂在海报角上，扫一眼就知道是电影还是剧
        var badge = Text(block, $@"<a\b[^>]*{ClassAttr("totalChapterNum")}[^>]*>(.*?)</a>")
                    ?? Text(block, $@"<span\b[^>]*{ClassAttr("ep")}[^>]*>(.*?)</span>");
        if (badge is not null && IsUpdatingBadge(badge)) badge = null;

        // 类型/年份：影迷界写在 <div class="m">当代国安剧 · 2026</div>，
        // 青苹果写成一组标签链接（国产 / 美剧 …），另外挂着一个评分角标
        var note = Text(block, $@"<div\b[^>]*{ClassAttr("m")}[^>]*>(.*?)</div>")
                   ?? Text(block, $@"<div\b[^>]*{ClassAttr("tagsBox")}[^>]*>(.*?)</div>");
        var rating = Text(block, $@"<span\b[^>]*{ClassAttr("sc")}[^>]*>(.*?)</span>");
        if (!string.IsNullOrEmpty(rating)) note = note is null ? rating : $"{note} · {rating}";

        // 简介：同名剧里最有区分度的就是它，卡片上截两行
        var intro = Text(block, $@"<a\b[^>]*{ClassAttr("intro")}[^>]*>(.*?)</a>")
                    ?? Text(block, $@"<div\b[^>]*{ClassAttr("intro")}[^>]*>(.*?)</div>");

        return (poster, Clip(badge, 24), Clip(note, 48), Clip(intro, 120));
    }

    /// <summary>
    /// 匹配 class 属性里的一个**完整**类名。
    ///
    /// 不能图省事写 <c>\bintro\b</c>：CSS 类名里的 <c>-</c> 和 <c>_</c> 都算单词字符，
    /// 所以 <c>TagBookList_intro</c> 里的 <c>intro</c> 前面**没有**单词边界，一条都匹配不上
    /// （第一版简介全空就是这么来的）。这里要求类名两侧是分隔符或引号边界，
    /// 于是 <c>ep</c> 能匹配 <c>class="ep"</c> / <c>class="pic ep"</c>，但不会匹配 <c>class="episode"</c>。
    /// </summary>
    private static string ClassAttr(string token) =>
        $@"class\s*=\s*[""'](?:[^""']*[\s\-_])?{Regex.Escape(token)}(?:[\s\-_][^""']*)?[""']";

    /// <summary>
    /// 角标里那些「还在更新」的状态，**不能当集数用，一律丢掉**。
    ///
    /// 这个角标就是 MacCMS 的 <c>vod_remarks</c> 字段：**由上传者手填、经常不更新**。
    /// 实测青苹果影院搜索页给《交锋》写的是「更新至04集」，而同一个
    /// <c>/voddetail/30450.html</c> 详情页列出第01集…第28集、我们的解析器也确认是 28 集 ——
    /// 站点自己前后不一致（换 UA、走代理四种抓法抓到的都是同一份 HTML，不是我们取错了元素）。
    /// 把这种数字摆在卡片上只会误导人选剧，宁可不显示。
    ///
    /// 「全9集」「HD中字」「已完结」这类**结论性**标记则留着 —— 它们说的是"就这样了"，
    /// 不随时间漂移，正是用来分辨"这是电影还是连续剧、完结没有"的关键信息。
    /// </summary>
    private static bool IsUpdatingBadge(string badge) =>
        badge.StartsWith("更新", StringComparison.Ordinal);

    /// <summary>取第一个匹配组的原始文本（不去标签），没有就返回 null</summary>
    private static string? Attr(string html, string pattern) {
        var m = Regex.Match(html, pattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>取第一个匹配组、去标签、解码实体、压空白；空的算没取到</summary>
    private static string? Text(string html, string pattern) {
        var raw = Attr(html, pattern);
        if (raw is null) return null;

        var text = Clean(StripTags(raw));
        return text.Length == 0 ? null : text;
    }

    /// <summary>相对地址补成绝对地址（海报大多写作 /upload/…，也有直接写完整域名的）</summary>
    private static string? Absolute(Uri siteRoot, string? url) {
        if (string.IsNullOrWhiteSpace(url)) return null;

        var text = url.Trim();
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        try {
            return Uri.TryCreate(text, UriKind.Absolute, out var absolute)
                ? absolute.ToString()
                : new Uri(siteRoot, text).ToString();
        } catch {
            return null;
        }
    }

    private static string? Clip(string? text, int max) {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : text[..max] + "…";
    }

    /// <summary>像不像"剧详情页"的地址（播放页不算：详情页才带完整分集）</summary>
    private static bool LooksLikeDetailPage(string href) {
        if (string.IsNullOrWhiteSpace(href)) return false;
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return false;
        if (href.StartsWith('#')) return false;

        if (href.Contains("vodplay", StringComparison.OrdinalIgnoreCase)) return false;
        if (href.Contains("/play/", StringComparison.OrdinalIgnoreCase)) return false;

        // 斜杠形态的播放页（/{id}/{sid}/{nid}.html）前缀不一定是 /play/，按形状再兜一层。
        // 影迷界影院的播放页就是 /play/2337178967/7/1.html，而它的详情页是 /video/2337178967.html。
        if (SlashPlayPage.IsMatch(href)) return false;

        return href.Contains("voddetail", StringComparison.OrdinalIgnoreCase)
               || href.Contains("/vod/detail/", StringComparison.OrdinalIgnoreCase)
               || href.Contains("/detail/", StringComparison.OrdinalIgnoreCase)
               || href.Contains("/t/", StringComparison.OrdinalIgnoreCase)
               || href.Contains("/show/", StringComparison.OrdinalIgnoreCase)
               || href.Contains("/vod/", StringComparison.OrdinalIgnoreCase)
               // 影迷界影院的搜索结果条目是 /video/{id}.html，而页脚推荐位是 /t/{id}.html。
               // 少了这一条，真正搜到的那部剧会被整个过滤掉，只剩推荐位（实测就是这样）。
               || href.Contains("/video/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>斜杠形态的播放页路径：<c>/{id}/{sid}/{nid}.html</c>（末尾可以是查询串）</summary>
    private static readonly Regex SlashPlayPage = new(
        @"/\d+/\d+/\d+\.html(?:[?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 从一条链接里取剧名。**优先认"结果条目"才有的特征**，认不出才退回通用写法：
    ///
    /// 1. 海报图的 <c>alt</c> —— 青苹果影院这类模板把剧名写在这儿（<c>&lt;img alt="交锋"&gt;</c>）；
    /// 2. <c>&lt;div class="nm"&gt;</c> —— 影迷界影院那套模板；
    /// 3. 最后才用 <c>title</c> 属性或链接文字，**并且要排除状态文本** ——
    ///    结果条目里还挂着"更新至04集""全9集""★7.0"这类小字链接，
    ///    不排掉的话剧名就成了"更新至04集"（第一版就是这么错的）。
    /// </summary>
    private static string? ExtractTitle(string attributes, string innerHtml, bool requireEntryMark) {
        // 1) 海报图的 alt
        var alt = Regex.Match(innerHtml, @"<img\b[^>]*alt\s*=\s*[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (alt.Success) {
            var fromAlt = Clean(alt.Groups[1].Value);
            // alt 也可能挂在角标图上（"更新至04集"），那就不是剧名，往后让给 div.nm
            if (fromAlt.Length > 0 && !LooksLikeStatusText(fromAlt)) return fromAlt;
        }

        // 2) <div class="nm">剧名</div>
        var nm = Regex.Match(innerHtml,
            @"<div\b[^>]*class\s*=\s*[""'][^""']*\bnm\b[^""']*[""'][^>]*>(.*?)</div>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (nm.Success) {
            var fromNm = Clean(StripTags(nm.Groups[1].Value));
            if (fromNm.Length > 0 && !LooksLikeStatusText(fromNm)) return fromNm;
        }

        // 严格模式：上面两个特征都没有，说明这条不是"结果条目"，不放行
        if (requireEntryMark) return null;

        // 3) 退回 title 属性 / 链接文字
        var title = Regex.Match(attributes, @"title\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
        var candidate = Clean(title.Success ? title.Groups[1].Value : StripTags(innerHtml));
        return candidate.Length == 0 || LooksLikeStatusText(candidate) ? null : candidate;
    }

    /// <summary>"更新至04集""全9集""★7.0"这类不是剧名的小字</summary>
    private static bool LooksLikeStatusText(string text) {
        if (text.Contains('★')) return true;

        return Regex.IsMatch(text,
            @"^(更新至|更新|全\s*\d+\s*集|第\s*\d+\s*集|已完结|完结|正片|抢先|超清|高清|HD|BD|TS|TC)",
            RegexOptions.IgnoreCase)
            || Regex.IsMatch(text, @"(集|HD|BD)$", RegexOptions.IgnoreCase);
    }

    private static string StripTags(string html) => Regex.Replace(html, "<[^>]+>", " ");

    /// <summary>
    /// 去标签 + 解实体 + 压空白。
    /// **先解实体再压空白**：<c>&amp;nbsp;</c> 解出来是不换行空格（U+00A0），
    /// 顺序反了的话它落在"压空白"之后，就从 trim 底下溜过去 —— 类型会变成「国产␣」这种带尾巴的。
    /// </summary>
    private static string Clean(string text) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
}
