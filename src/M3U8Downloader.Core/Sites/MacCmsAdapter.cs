using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace M3U8Downloader.Core.Sites;

/// <summary>
/// 苹果 CMS（MacCMS v10）及衍生模板适配器。
///
/// 识别与解析要点（均经过实测）：
/// 1. 播放页固定形态：<c>/vodplay/{剧ID}-{源sid}-{集nid}.html</c>；
/// 2. 页内 <c>var player_aaaa={...}</c> 是一段合法 JSON，含本集 m3u8 直链字段 <c>url</c>，
///    以及 <c>url_next</c>（下一集直链）、<c>from</c>（播放源标识）、<c>encrypt</c>（是否加密）；
/// 3. 剧集列表**不依赖模板 CSS 类名**：直接把页内所有指向同一剧 ID 的
///    <c>/vodplay/{id}-{sid}-{nid}.html</c> 链接收集起来按 nid 排序即可，
///    这样换模板也不用改代码（分页标签「1-10 / 11-16」的链接本来就都在同一份 HTML 里）；
/// 4. 取流通常需要带 Referer/Origin，因此解析结果里直接给出建议请求头。
/// </summary>
public sealed class MacCmsAdapter : ISiteAdapter
{
    public SiteKind Kind => SiteKind.MacCms;
    public string Name => "苹果CMS(MacCMS)";

    private static readonly RegexOptions Opts =
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

    // 播放页路径。不同站的伪静态前缀各不相同，实测已见：
    //   /vodplay/30450-1-1.html     (七猫短剧)
    //   /v/7005-1-3.html            (kktvs)
    // 因此这里**不写死前缀**，只要求「若干路径段 + {id}-{sid}-{nid}.html」。
    private static readonly Regex PlayPath = new(
        @"^/(?<prefix>(?:[A-Za-z0-9_\-]+/)*)(?<id>\d+)-(?<sid>\d+)-(?<nid>\d+)\.html$", Opts);

    private static readonly Regex PlayPathAlt = new(
        @"^/index\.php/vod/play/id/(?<id>\d+)/sid/(?<sid>\d+)/nid/(?<nid>\d+)\.html$", Opts);

    // 详情页同理，支持 /voddetail/7005.html、/v/7005.html 等
    private static readonly Regex DetailPath = new(
        @"^/(?<prefix>(?:[A-Za-z0-9_\-]+/)*)(?<id>\d+)\.html$", Opts);

    private static readonly Regex DetailPathAlt = new(
        @"^/index\.php/vod/detail/id/(?<id>\d+)\.html$", Opts);

    /// <summary>
    /// 任意 href 中的播放页路径（允许带域名前缀，也允许任意路径段前缀）。
    /// 靠「同一剧集 id」过滤 + 集号范围校验，避免误吃日期型链接（如 /news/2026-09-13.html）。
    /// </summary>
    private static readonly Regex AnyPlayHref = new(
        @"(?:https?://[^/""'\s]+)?/(?<prefix>(?:[A-Za-z0-9_\-]+/)*)(?<id>\d+)-(?<sid>\d+)-(?<nid>\d+)\.html", Opts);

    /// <summary>
    /// 原生形态的播放页：<c>/index.php/vod/play/id/{id}/sid/{sid}/nid/{nid}.html</c>。
    /// 欧乐影院（olevod.com）整站的剧集链接都是这个形状 —— 只认伪静态那条规则的话，
    /// 它的详情页会被判成「不是苹果 CMS 站点」。
    /// </summary>
    private static readonly Regex AnyPlayHrefNative = new(
        @"(?:https?://[^/""'\s]+)?/(?<prefix>(?:[A-Za-z0-9_\-]+/)*)index\.php/vod/play/id/(?<id>\d+)/sid/(?<sid>\d+)/nid/(?<nid>\d+)\.html",
        Opts);

    /// <summary>
    /// 第三种形态：把 <c>{id}-{sid}-{nid}</c> 里的短横线换成斜杠。
    /// 影迷界影院（wakuredo.com）整站都是这个形状 ——
    /// 详情页 <c>/t/62329.html</c>、播放页 <c>/play/2337178967/7/1.html</c>。
    ///
    /// 这条比短横线那条**更容易误匹配**（<c>/news/2026/09/13.html</c> 长得一模一样），
    /// 所以它从不单独作为判据：挡误匹配的是 <see cref="PickPlayId"/> 的多数投票。
    /// </summary>
    private static readonly Regex AnyPlayHrefSlash = new(
        @"(?:https?://[^/""'\s]+)?/(?<prefix>(?:[A-Za-z0-9_\-]+/)*)(?<id>\d+)/(?<sid>\d+)/(?<nid>\d+)\.html",
        Opts);

    /// <summary>斜杠形态的播放页路径（用户直接把播放页地址丢进来时靠它认出来）</summary>
    private static readonly Regex PlayPathSlash = new(
        @"^/(?<prefix>(?:[A-Za-z0-9_\-]+/)*)(?<id>\d+)/(?<sid>\d+)/(?<nid>\d+)\.html$", Opts);

    /// <summary>播放页路径的前缀，用于在没抓到链接时合成同款地址</summary>
    private static readonly Regex PathPrefix = new(@"^/(?<prefix>[A-Za-z0-9_\-]+/)", Opts);

    /// <summary>集号上限，超出基本可判定是日期之类的误匹配</summary>
    private const int MaxEpisodeNumber = 5000;

    /// <summary>探测各播放源名称时的并发上限</summary>
    private const int SourceProbeConcurrency = 4;

    /// <summary>探测各播放源名称的总时间预算，超时即放弃（退回「源N」）</summary>
    private static readonly TimeSpan SourceProbeBudget = TimeSpan.FromSeconds(12);

    private static readonly Regex Anchor = new(
        @"<a\b[^>]*?href\s*=\s*[""'](?<href>[^""']+)[""'][^>]*?>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex TagStrip = new(@"<[^>]+>", RegexOptions.Compiled);

    public bool CanHandle(Uri url)
    {
        var p = url.AbsolutePath;
        return PlayPath.IsMatch(p) || PlayPathAlt.IsMatch(p) || PlayPathSlash.IsMatch(p)
            || DetailPath.IsMatch(p) || DetailPathAlt.IsMatch(p);
    }

    public async Task<SiteSeries> ParseAsync(string html, Uri pageUrl, SiteContext ctx, CancellationToken ct = default)
    {
        var log = new List<string>();
        var siteName = ExtractSiteName(html, pageUrl);
        var headers = BuildHeaders(pageUrl, ctx.UserAgent);

        // ---- 1. 定位播放页 & 读取 player_aaaa ----
        var player = TryParsePlayer(html, out var playerRaw);
        if (player is not null) log.Add($"已解析 player_aaaa：源={player.From} sid={player.SourceId} nid={player.EpisodeNumber} encrypt={player.Encrypt}");

        string seriesId;
        var currentSourceId = 1;
        var currentEpisode = 1;
        var pathPrefix = "/vodplay/";
        var slashStyle = false;

        var playMatch = PlayPath.Match(pageUrl.AbsolutePath);
        if (!playMatch.Success) playMatch = PlayPathAlt.Match(pageUrl.AbsolutePath);
        if (!playMatch.Success)
        {
            playMatch = PlayPathSlash.Match(pageUrl.AbsolutePath);
            slashStyle = playMatch.Success;
        }

        if (playMatch.Success)
        {
            seriesId = playMatch.Groups["id"].Value;
            currentSourceId = int.Parse(playMatch.Groups["sid"].Value);
            currentEpisode = int.Parse(playMatch.Groups["nid"].Value);
            var p = playMatch.Groups["prefix"].Value;
            if (p.Length > 0) pathPrefix = "/" + p;
        }
        else
        {
            // 用户给的是详情页：id 从详情页拿，播放页地址后面合成
            var dm = DetailPath.Match(pageUrl.AbsolutePath);
            if (!dm.Success) dm = DetailPathAlt.Match(pageUrl.AbsolutePath);
            seriesId = player?.SeriesId ?? dm.Groups["id"].Value;
            if (player is not null) { currentSourceId = player.SourceId; currentEpisode = player.EpisodeNumber; }

            // 详情页的 URL 前缀通常和播放页不同（/voddetail/ vs /vodplay/），
            // 但同族站点可以借用；借不到就用最常见的 /vodplay/。
            var pp = PathPrefix.Match(pageUrl.AbsolutePath);
            if (pp.Success && !dm.Success) pathPrefix = pp.Groups["prefix"].Value;
        }

        // ---- 2. 收集全剧集链接（模板无关）----
        // 先把页内所有形如播放页的链接都收下来，**暂不按剧 ID 过滤**。
        // 原因：少数站点（影迷界影院 wakuredo.com）的详情页 ID 与播放页 ID 是两套编号 ——
        // 详情页是 /t/62329.html，播放页却是 /play/2337178967/{sid}/{nid}.html，
        // 一上来就按详情页 ID 过滤会把本剧的选集链接全部滤掉，最后误报「没有剧集链接」。
        // 改成：先收集 → 投票选出本剧的播放 ID（PickPlayId）→ 再按它过滤。
        var candidates = new List<PlayLink>();
        foreach (Match a in Anchor.Matches(html))
        {
            var href = WebUtility.HtmlDecode(a.Groups["href"].Value.Trim());

            // 三种形态都认（组名一致，后面的取值代码不用分叉）
            var m = AnyPlayHref.Match(href);
            var slash = false;
            if (!m.Success)
            {
                m = AnyPlayHrefNative.Match(href);
                if (!m.Success)
                {
                    m = AnyPlayHrefSlash.Match(href);
                    slash = m.Success;
                }
            }

            if (!m.Success) continue;

            var sid = int.Parse(m.Groups["sid"].Value);
            var nid = int.Parse(m.Groups["nid"].Value);
            if (sid <= 0 || nid <= 0 || nid > MaxEpisodeNumber) continue;

            candidates.Add(new PlayLink(
                m.Groups["id"].Value, sid, nid,
                ToAbsolute(pageUrl, href), CleanText(a.Groups["text"].Value), a.Index, slash));
        }

        var playId = PickPlayId(candidates, seriesId);
        var found = new Dictionary<(int Sid, int Nid), (string PageUrl, string Text)>();
        foreach (var c in candidates)
        {
            if (c.PlayId != playId) continue;   // 滤掉侧边栏「猜你喜欢」里别的剧

            // 同一集出现多次时保留靠后的：详情页顶部的「▶ 立即播放」在选集区之前，
            // 后面那条「第01集」才是更好的标题
            found[(c.Sid, c.Nid)] = (c.PageUrl, c.Text);
        }

        if (playId is not null) slashStyle = candidates.Any(c => c.Slash && c.PlayId == playId);

        // 详情页里没有 player_aaaa，当前源无从得知；而默认值 1 遇上「源从 3 起编号」的站
        // （wakuredo 是 3/7/8/11）会让所有集都处于未勾选状态，用户看到的是「一集都没有」。
        // 这里按「模板标记为选中的那条线路 → 编号最小的源」兜底。
        if (player is null && found.Count > 0 && !found.Keys.Any(k => k.Sid == currentSourceId))
        {
            currentSourceId = FindSelectedSourceId(html, candidates) ?? found.Keys.Min(k => k.Sid);
            log.Add($"详情页未给出当前播放源，默认选中 sid={currentSourceId}");
        }

        // 防误判：CanHandle 为了兼容各种伪静态前缀放得比较宽，
        // 像 /news/2026-09-13.html 这种日期路径也会命中。苹果 CMS 的**播放页必然带
        // player_aaaa**，所以这里必须兜住，否则会把日期当剧集产出垃圾结果。
        if (player is null && playMatch.Success)
        {
            throw new NotSupportedException(
                $"该地址形如播放页，但页面里没有 player_aaaa，可能不是苹果CMS(MacCMS)站点：{pageUrl}");
        }

        // 既没有播放器配置、也没有任何剧集链接 → 基本可以判定不是苹果 CMS 的播放/详情页
        if (player is null && found.Count == 0)
        {
            throw new NotSupportedException(
                $"页面里既没有 player_aaaa 也没有剧集链接，可能不是苹果CMS(MacCMS)站点：{pageUrl}");
        }

        // 详情页里若没有剧集链接，退化为合成播放页地址（按本页用的是哪种 URL 形态）
        if (found.Count == 0)
        {
            var nativeStyle = DetailPathAlt.IsMatch(pageUrl.AbsolutePath);
            var synthesized = nativeStyle
                ? new Uri(pageUrl, $"/index.php/vod/play/id/{seriesId}/sid/{currentSourceId}/nid/{currentEpisode}.html").ToString()
                : slashStyle
                    ? new Uri(pageUrl, $"{pathPrefix}{seriesId}/{currentSourceId}/{currentEpisode}.html").ToString()
                    : new Uri(pageUrl, $"{pathPrefix}{seriesId}-{currentSourceId}-{currentEpisode}.html").ToString();

            var styleName = nativeStyle ? "原生" : slashStyle ? "斜杠" : "伪静态";
            log.Add($"页面内未找到剧集链接，按{styleName}形态合成播放页地址：{synthesized}");
            found[(currentSourceId, currentEpisode)] = (synthesized, "");
        }

        var series = new SiteSeries
        {
            Kind = SiteKind.MacCms,
            SiteName = siteName,
            PageUrl = pageUrl.ToString(),
            SeriesId = seriesId,
            Title = player?.VodName ?? ExtractTitle(html) ?? seriesId,
            Category = player?.VodClass,
            CoverUrl = ExtractMeta(html, "og:image"),
        };
        foreach (var kv in headers) series.Headers[kv.Key] = kv.Value;

        foreach (var group in found.GroupBy(k => k.Key.Sid).OrderBy(g => g.Key))
        {
            var source = new SitePlaySource { Id = group.Key };
            foreach (var item in group.OrderBy(g => g.Key.Nid))
            {
                var nid = item.Key.Nid;
                source.Episodes.Add(new SiteEpisode
                {
                    Number = nid,
                    SourceId = group.Key,
                    PageUrl = item.Value.PageUrl,
                    Title = NormalizeEpisodeTitle(item.Value.Text, nid),
                    // 只有当前播放源默认勾选，避免多源重复下载同一集
                    IsSelected = group.Key == currentSourceId,
                });
            }
            series.Sources.Add(source);
        }

        // ---- 3. 播放源：标记当前源 + 取名（多源弹窗要用）----
        series.PreferredSourceId = currentSourceId;
        await ResolveSourceNamesAsync(series, player?.From, html, pageUrl, ctx, log, ct).ConfigureAwait(false);

        // 当前集直链（列表页一般只给当前集）
        var current = series.AllEpisodes.FirstOrDefault(e => e.SourceId == currentSourceId && e.Number == currentEpisode);
        if (current is not null && player?.Url is { Length: > 0 } u)
        {
            current.PlaylistUrl = WebUtility.HtmlDecode(u);
            log.Add($"当前集直链已就绪（{player.From}）");
        }

        log.Add($"共解析 {series.Sources.Count} 个播放源 / {series.TotalEpisodes} 集");
        if (series.Sources.Count > 1)
            log.Add($"检测到多个播放源，默认只勾选 sid={currentSourceId}，其余源可在确认列表里切换");
        if (player is { Encrypt: not 0 })
            log.Add($"⚠ 该源 encrypt={player.Encrypt}，直链为加密串，需要额外解密（当前实现仅支持 encrypt=0）");

        series.Log.AddRange(log);
        return series;
    }

    public async Task<string> ResolvePlaylistUrlAsync(SiteEpisode episode, SiteContext ctx, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(episode.PlaylistUrl)) return episode.PlaylistUrl!;

        var html = await ctx.GetHtmlAsync(episode.PageUrl, this, ct).ConfigureAwait(false);
        var player = TryParsePlayer(html, out _)
            ?? throw new InvalidOperationException($"播放页未找到 player_aaaa：{episode.PageUrl}");

        if (player.Encrypt != 0)
            throw new NotSupportedException(
                $"该集直链被加密（encrypt={player.Encrypt}），需要先实现对应解密：{episode.PageUrl}");

        if (string.IsNullOrWhiteSpace(player.Url))
            throw new InvalidOperationException($"player_aaaa 缺少 url 字段：{episode.PageUrl}");

        var url = WebUtility.HtmlDecode(player.Url!);

        // 有些站点把 url 指向"解析接口"而不是直链，这里给出明确提示而不是让引擎报莫名的错
        if (!url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) &&
            !url.Contains(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            // 仍然返回，交由下载引擎尝试；调用方可据日志判断
            episode.Title = string.IsNullOrWhiteSpace(episode.Title) ? episode.DisplayTitle : episode.Title;
        }

        episode.PlaylistUrl = url;
        return url;
    }

    // ---------------- 内部工具 ----------------

    /// <summary>
    /// 页内一条「播放页链接」候选。
    /// 单独建这个类型，是因为判定要**跨链接统计**（见 <see cref="PickPlayId"/>），
    /// 还要记住它在 HTML 里的位置（判「当前线路」用，见 <see cref="FindSelectedSourceId"/>）。
    /// </summary>
    private sealed record PlayLink(string PlayId, int Sid, int Nid, string PageUrl, string Text, int Pos, bool Slash);

    /// <summary>
    /// 判定「本页这部剧的播放 ID」。
    ///
    /// 多数苹果 CMS 站的详情页 ID 与播放页 ID 相同，第一个条件就命中；
    /// 但影迷界影院（wakuredo.com）是两套编号 —— 详情页 <c>/t/62329.html</c>、
    /// 播放页 <c>/play/2337178967/…</c>，永远命中不了，于是退到多数投票：
    /// 本剧的选集链接动辄十几条，而侧边栏推荐、日期型误匹配最多一两条，足以分开。
    ///
    /// 「最少 2 次」是刻意保守：宁可判定失败（上层会合成播放页地址或明确报错），
    /// 也不要凭一条孤零零的 <c>/a/b/c.html</c> 解析出一堆垃圾集号。
    /// </summary>
    private static string? PickPlayId(IReadOnlyList<PlayLink> candidates, string seriesId)
    {
        if (candidates.Count == 0) return null;
        if (candidates.Any(c => string.Equals(c.PlayId, seriesId, StringComparison.Ordinal))) return seriesId;

        var best = candidates
            .GroupBy(c => c.PlayId, StringComparer.Ordinal)
            .Select(g => (Id: g.Key, Count: g.Count()))
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.Id, StringComparer.Ordinal)
            .First();

        return best.Count >= 2 ? best.Id : null;
    }

    /// <summary>
    /// 详情页里「当前线路」的 sid。
    /// 苹果 CMS 模板会给选中的线路名加一个 on 类
    /// （<c>&lt;div class="line-name on"&gt;线路10&lt;/div&gt;</c>），
    /// 紧随其后的选集区就是这条线路的链接，取其中第一条的 sid 即可。
    /// 找不到返回 null，由调用方退回「编号最小的源」。
    /// </summary>
    private static int? FindSelectedSourceId(string html, IReadOnlyList<PlayLink> candidates)
    {
        foreach (Match cls in Regex.Matches(html, @"class\s*=\s*[""'](?<cls>[^""']*)[""']", RegexOptions.IgnoreCase))
        {
            var tokens = cls.Groups["cls"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!tokens.Contains("line-name", StringComparer.OrdinalIgnoreCase)) continue;
            if (!tokens.Contains("on", StringComparer.OrdinalIgnoreCase)) continue;

            return candidates
                .Where(c => c.Pos > cls.Index)
                .OrderBy(c => c.Pos)
                .Select(c => (int?)c.Sid)
                .FirstOrDefault();
        }

        return null;
    }

    /// <summary>播放页里的播放器配置</summary>
    private sealed record PlayerConfig(
        string? Url, string? UrlNext, string? From, string? SeriesId,
        int SourceId, int EpisodeNumber, int Encrypt, string? VodName, string? VodClass);

    private static PlayerConfig? TryParsePlayer(string html, out string raw)
    {
        raw = "";
        if (!TryExtractJsonObject(html, "player_aaaa", out var json)) return null;
        raw = json;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? vodName = null, vodClass = null;
            if (root.TryGetProperty("vod_data", out var vd) && vd.ValueKind == JsonValueKind.Object)
            {
                vodName = GetString(vd, "vod_name");
                vodClass = GetString(vd, "vod_class");
            }

            return new PlayerConfig(
                Url: GetString(root, "url"),
                UrlNext: GetString(root, "url_next"),
                From: GetString(root, "from"),
                SeriesId: GetString(root, "id"),
                SourceId: GetInt(root, "sid", 1),
                EpisodeNumber: GetInt(root, "nid", 1),
                Encrypt: GetInt(root, "encrypt", 0),
                VodName: vodName,
                VodClass: vodClass);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 用花括号配对法从 JS 里抠出 JSON 对象。
    /// 比正则可靠：JSON 内含转义斜杠（\/）、嵌套对象，且结尾未必紧跟分号。
    /// </summary>
    internal static bool TryExtractJsonObject(string html, string marker, out string json)
    {
        json = "";
        var i = html.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return false;

        var start = html.IndexOf('{', i);
        if (start < 0) return false;

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var k = start; k < html.Length; k++)
        {
            var c = html[k];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }

            if (c == '"') { inString = true; continue; }
            if (c == '{') depth++;
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = html.Substring(start, k - start + 1);
                    return true;
                }
            }
        }

        return false;
    }

    // ---------------- 播放源命名 ----------------

    /// <summary>host → （源标识 from → 显示名）。playerconfig.js 一个站点只需拉一次。</summary>
    private static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> PlayerListCache = new();

    /// <summary>
    /// 解析 playerconfig.js 里的 <c>MacPlayerConfig.player_list={...}</c>，
    /// 得到「源标识 → 显示名」映射，例如 360zy → 360播放器。
    /// </summary>
    public static bool TryParsePlayerList(string js, out Dictionary<string, string> map)
    {
        map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!TryExtractJsonObject(js, "player_list", out var json)) return false;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                if (!entry.Value.TryGetProperty("show", out var show)) continue;
                if (show.ValueKind != JsonValueKind.String) continue;

                var name = show.GetString();
                if (!string.IsNullOrWhiteSpace(name)) map[entry.Name] = name!.Trim();
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return map.Count > 0;
    }

    private async Task<IReadOnlyDictionary<string, string>> GetPlayerListAsync(
        SiteContext ctx, Uri pageUrl, string html, CancellationToken ct)
    {
        var key = pageUrl.Authority;
        if (PlayerListCache.TryGetValue(key, out var cached)) return cached;

        IReadOnlyDictionary<string, string> result =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 各站放 playerconfig.js 的路径不统一，先从页面的 <script src> 里找，
            // 找不到再退回最常见的 /static/js/playerconfig.js。
            var candidate = FindPlayerConfigUrl(html, pageUrl)
                            ?? new Uri(pageUrl, "/static/js/playerconfig.js").ToString();

            var js = await ctx.GetHtmlAsync(candidate, this, ct).ConfigureAwait(false);
            if (TryParsePlayerList(js, out var map)) result = map;
        }
        catch
        {
            // 拿不到就退回「源N」，不影响主流程
        }

        PlayerListCache[key] = result;
        return result;
    }

    /// <summary>从页面里找出播放器配置脚本的地址（形如 playerconfig.js?t=...）</summary>
    private static string? FindPlayerConfigUrl(string html, Uri pageUrl)
    {
        foreach (Match m in Regex.Matches(html, @"<script[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""']",
                     RegexOptions.IgnoreCase))
        {
            var src = m.Groups["src"].Value;
            if (src.IndexOf("player", StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (!src.Contains(".js", StringComparison.OrdinalIgnoreCase)) continue;

            // player.js 是播放逻辑，playerconfig.js 才是配置；优先配置
            var url = ToAbsolute(pageUrl, WebUtility.HtmlDecode(src));
            if (url.Contains("playerconfig", StringComparison.OrdinalIgnoreCase)) return url;
        }

        // 退一步：任何含 player 的 js 都试一次（有些站把它合并进 player.js）
        foreach (Match m in Regex.Matches(html, @"<script[^>]*\bsrc\s*=\s*[""'](?<src>[^""']+)[""']",
                     RegexOptions.IgnoreCase))
        {
            var src = m.Groups["src"].Value;
            if (src.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0 &&
                src.Contains(".js", StringComparison.OrdinalIgnoreCase))
            {
                var url = ToAbsolute(pageUrl, WebUtility.HtmlDecode(src));
                if (url.Contains("player.js", StringComparison.OrdinalIgnoreCase)) continue;
                return url;
            }
        }

        return null;
    }

    private static string LookupSourceName(IReadOnlyDictionary<string, string> playerList, string? flag)
    {
        if (string.IsNullOrWhiteSpace(flag)) return "";
        return playerList.TryGetValue(flag!, out var name) ? name : "";
    }

    /// <summary>
    /// 给每个播放源取一个能看懂的名字：
    /// 当前源用本页 player_aaaa.from 查 playerconfig.js；
    /// 其余源各探测一次它的第 1 集（并发上限 3，失败就退回「源N」）。
    /// </summary>
    private async Task ResolveSourceNamesAsync(
        SiteSeries series, string? currentSourceFlag, string html, Uri pageUrl,
        SiteContext ctx, List<string> log, CancellationToken ct)
    {
        if (series.Sources.Count == 0) return;

        var playerList = await GetPlayerListAsync(ctx, pageUrl, html, ct).ConfigureAwait(false);

        var current = series.Sources.FirstOrDefault(s => s.Id == series.PreferredSourceId);
        if (current is not null)
        {
            var name = LookupSourceName(playerList, currentSourceFlag);
            current.Name = string.IsNullOrWhiteSpace(name) ? (currentSourceFlag ?? "") : name;
        }

        var pending = series.Sources.Where(s => string.IsNullOrWhiteSpace(s.Name)).ToList();
        if (pending.Count > 0)
        {
            // 探测每个源的第 1 集只为拿到它的 from 标识。
            // 有的站有几十个源（实测 kktvs 有 21 个），所以要有并发上限 + 总时间预算，
            // 否则解析会明显变慢；超时的一律退回「源N」，不影响下载。
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(SourceProbeBudget);

            using var gate = new SemaphoreSlim(SourceProbeConcurrency);
            var tasks = pending.Select(async source =>
            {
                try
                {
                    await gate.WaitAsync(budget.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;   // 预算用尽，不再探测
                }

                try
                {
                    var probe = source.Episodes.OrderBy(e => e.Number).FirstOrDefault();
                    if (probe is null) return;

                    var pageHtml = await ctx.GetHtmlAsync(probe.PageUrl, this, budget.Token).ConfigureAwait(false);
                    var player = TryParsePlayer(pageHtml, out _);
                    if (player?.From is not { Length: > 0 } flag) return;

                    var name = LookupSourceName(playerList, flag);
                    source.Name = string.IsNullOrWhiteSpace(name) ? flag : name;
                }
                catch
                {
                    // 探测失败无所谓，最后统一兜底
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            try { await Task.WhenAll(tasks).ConfigureAwait(false); } catch { /* 已在内部吞掉 */ }
        }

        var unnamed = 0;
        foreach (var s in series.Sources)
        {
            if (string.IsNullOrWhiteSpace(s.Name))
            {
                s.Name = $"源{s.Id}";
                unnamed++;
            }
        }

        log.Add($"播放源：{string.Join("、", series.Sources.Take(6).Select(s => s.ToString()))}" +
                (series.Sources.Count > 6 ? $" …等 {series.Sources.Count} 个" : ""));
        if (unnamed > 0 && series.Sources.Count > 1)
            log.Add($"{unnamed} 个播放源未能识别名称（已按「源N」显示），不影响下载");
    }

    private static string? GetString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>sid/nid 在部分模板里是字符串、部分是数字，两种都要吃</summary>
    private static int GetInt(JsonElement obj, string name, int fallback)
    {
        if (!obj.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var n) ? n : fallback,
            JsonValueKind.String => int.TryParse(v.GetString(), out var n) ? n : fallback,
            _ => fallback,
        };
    }

    private static string ToAbsolute(Uri baseUri, string href)
    {
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.ToString();
        // 注意：不要对路径做 "//" 归一化，某些 CDN 的路径里确实含双斜杠
        return new Uri(baseUri, href).ToString();
    }

    private static string CleanText(string anchorInnerHtml)
    {
        var text = TagStrip.Replace(anchorInnerHtml, " ");
        text = WebUtility.HtmlDecode(text);
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text.Length > 40 ? text[..40] : text;
    }

    /// <summary>
    /// 多数模板的选集按钮只有裸数字（"1"、"2"…），直接拿来当标题在确认列表里很难看，
    /// 这里统一补成「第NN集」；已经是「第01集」这类文本的原样保留。
    /// </summary>
    private static string NormalizeEpisodeTitle(string? text, int nid)
    {
        var t = text?.Trim() ?? "";
        if (t.Length == 0) return $"第{nid:00}集";
        if (Regex.IsMatch(t, @"^\d+$")) return $"第{nid:00}集";
        return t;
    }

    /// <summary>
    /// 剧名。优先 &lt;h1&gt;，其次 og:title，最后退回 &lt;title&gt; 的第一段。
    ///
    /// 最后这条回退是必需的：欧乐影院（olevod.com）的详情页**没有 h1**，
    /// 剧名只出现在「交锋_更新至第18集_欧乐影院 - 站点标语」这样的 title 里，
    /// 少了它剧名会退化成剧集 ID（界面上显示成一串数字）。
    /// </summary>
    private static string? ExtractTitle(string html)
    {
        var m = Regex.Match(html, @"<h1[^>]*>(?<t>.*?)</h1>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (m.Success)
        {
            var t = CleanText(m.Groups["t"].Value);
            if (!string.IsNullOrWhiteSpace(t)) return t;
        }

        var og = ExtractMeta(html, "og:title");
        if (!string.IsNullOrWhiteSpace(og)) return og!.Trim();

        var titleTag = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!titleTag.Success) return null;

        var title = CleanText(titleTag.Groups["t"].Value);

        // 「交锋_更新至第18集_欧乐影院 - …」：下划线分隔时第一段就是剧名
        var parts = title.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var first = parts.Length > 0 ? parts[0] : title;

        // 没有下划线时（「交锋 - 欧乐影院」）再用「 - 」切一刀
        if (parts.Length <= 1)
        {
            first = title
                .Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault() ?? title;
        }

        // 兜底：把粘在一起的更新进度去掉（「交锋更新至第18集」）
        first = Regex.Replace(first, @"[（(]?\s*(?:更新至|更新到|已更新|全)\s*第?\s*\d+\s*[集话期]\s*[)）]?", "");
        first = Regex.Replace(first, @"\s*第\s*\d+\s*[集话期]\s*$", "");

        return string.IsNullOrWhiteSpace(first) ? null : first.Trim();
    }

    private static string? ExtractMeta(string html, string property)
    {
        var m = Regex.Match(html,
            $@"<meta[^>]*(?:property|name)\s*=\s*[""']{Regex.Escape(property)}[""'][^>]*content\s*=\s*[""'](?<c>[^""']+)[""']",
            RegexOptions.IgnoreCase);
        return m.Success ? WebUtility.HtmlDecode(m.Groups["c"].Value) : null;
    }

    private static string ExtractSiteName(string html, Uri pageUrl)
    {
        var og = ExtractMeta(html, "og:site_name");
        if (!string.IsNullOrWhiteSpace(og)) return og!;

        var t = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (t.Success)
        {
            var title = WebUtility.HtmlDecode(t.Groups["t"].Value);
            var parts = title.Split(new[] { '-', '_', '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0)
            {
                var last = parts[^1].Trim();
                if (last.Length is > 1 and < 20) return last;
            }
        }

        return pageUrl.Host;
    }

    private static Dictionary<string, string> BuildHeaders(Uri pageUrl, string userAgent) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Referer"] = pageUrl.ToString(),
        ["Origin"] = $"{pageUrl.Scheme}://{pageUrl.Authority}",
        ["User-Agent"] = userAgent,
    };
}
