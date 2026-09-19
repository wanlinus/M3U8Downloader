using System.Net;
using System.Text.Json;
using M3U8Downloader.Core.Net;

namespace M3U8Downloader.Core.Update;

/// <summary>GitHub Release 上发布的一个版本</summary>
public sealed record ReleaseInfo {
    /// <summary>tag，形如 v1.4.0</summary>
    public required string Tag { get; init; }

    /// <summary>去掉 v 的版本号，形如 1.4.0</summary>
    public required string Version { get; init; }

    /// <summary>Release 标题</summary>
    public string Name { get; init; } = "";

    /// <summary>Release 说明正文（Markdown）</summary>
    public string Body { get; init; } = "";

    /// <summary>Release 页面地址（浏览器打开用）</summary>
    public string HtmlUrl { get; init; } = "";

    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>界面版 zip 的直链（找不到就是 null）</summary>
    public string? DownloadUrl { get; init; }

    public long DownloadSize { get; init; }

    public string DownloadSizeText => DownloadSize > 0
        ? $"{DownloadSize / 1024.0 / 1024.0:0.0} MB"
        : "";
}

/// <summary>检查更新的结果</summary>
public sealed record UpdateCheckResult(
    bool HasUpdate,
    string CurrentVersion,
    ReleaseInfo? Latest,
    string? Error) {
    /// <summary>检查本身失败了（网络不通、被限流等）—— 这不等于"已是最新"</summary>
    public bool Failed => Error is not null;
}

/// <summary>
/// 检查 GitHub Release 上有没有新版本。
///
/// 几个刻意的选择：
/// · 用 <c>releases/latest</c> 而不是列全部再挑 —— 一次请求、且 GitHub 已经帮我们
///   排除了草稿与预发布版；
/// · 走 GitHub API 就必须带 User-Agent（不带直接 403），这点和下载 FFmpeg 时踩过的坑一样；
/// · **代理只在这里用**：GitHub 在国内常常连不上，但用户要是没配代理，
///   我们也只是查不到更新而已，不该因此弹一堆错。
/// </summary>
public static class UpdateChecker {
    private const string Owner = "wanlinus";
    private const string Repository = "M3U8Downloader";

    private static string ApiUrl => $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>项目主页，检查失败时给用户一个能自己去看的地方</summary>
    public static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repository}/releases";

    /// <summary>
    /// 查最新版本并和 <paramref name="currentVersion"/> 比。
    /// 任何异常都收敛成 <see cref="UpdateCheckResult.Error"/>，不往外抛 ——
    /// 调用方多半在启动流程里，不该因为查不了更新就把程序带崩。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckAsync(
        string currentVersion, string? proxyUrl = null, CancellationToken ct = default) {
        try {
            var latest = await FetchLatestAsync(proxyUrl, ct).ConfigureAwait(false);
            if (latest is null)
                return new UpdateCheckResult(false, currentVersion, null, "GitHub 上没有找到已发布的版本");

            var hasUpdate = CompareVersions(latest.Version, currentVersion) > 0;
            return new UpdateCheckResult(hasUpdate, currentVersion, latest, null);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            return new UpdateCheckResult(false, currentVersion, null, ex.Message);
        }
    }

    /// <summary>
    /// 直连优先，失败再走代理。
    ///
    /// 不能"配了代理就直接用代理"：实测 GitHub API **在国内经常能直连**
    /// （0.8 秒就回来了），而用户的代理软件未必开着 —— 那种情况下硬走代理
    /// 只会得到一句"目标计算机积极拒绝"，明明直连能成的事却查不到更新。
    /// 这与站点解析那边是同一套策略。
    /// </summary>
    private static async Task<ReleaseInfo?> FetchLatestAsync(string? proxyUrl, CancellationToken ct) {
        try {
            return await FetchWithAsync(null, ct).ConfigureAwait(false);
        } catch (Exception) when (!ct.IsCancellationRequested) {
            var normalized = ProxyHelper.Normalize(proxyUrl);
            if (normalized is null) throw;   // 没配代理，直连失败就是失败
            return await FetchWithAsync(normalized, ct).ConfigureAwait(false);
        }
    }

    private static async Task<ReleaseInfo?> FetchWithAsync(string? proxyUrl, CancellationToken ct) {
        using var handler = new SocketsHttpHandler {
            ConnectTimeout = TimeSpan.FromSeconds(8),
            // 显式关掉：不关会退到系统代理（用户的 Clash），见 AGENTS.md 的硬约定
            UseProxy = false,
        };

        if (proxyUrl is not null) {
            handler.Proxy = new WebProxy(new Uri(proxyUrl)) { BypassProxyOnLocal = true };
            handler.UseProxy = true;
        }

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // GitHub API 不带 UA 会被直接拒掉，这个坑在下载 FFmpeg 时踩过一次
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "M3U8Downloader-UpdateCheck");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var resp = await client.GetAsync(ApiUrl, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var tag = GetString(root, "tag_name");
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var (downloadUrl, size) = FindAppAsset(root);

        return new ReleaseInfo {
            Tag = tag!,
            Version = tag!.TrimStart('v', 'V'),
            Name = GetString(root, "name") ?? tag!,
            Body = GetString(root, "body") ?? "",
            HtmlUrl = GetString(root, "html_url") ?? ReleasesPageUrl,
            PublishedAt = root.TryGetProperty("published_at", out var p) &&
                          p.ValueKind == JsonValueKind.String &&
                          DateTimeOffset.TryParse(p.GetString(), out var t)
                ? t
                : null,
            DownloadUrl = downloadUrl,
            DownloadSize = size,
        };
    }

    /// <summary>在 assets 里找主程序那个 zip</summary>
    private static (string? Url, long Size) FindAppAsset(JsonElement root) {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return (null, 0);

        foreach (var asset in assets.EnumerateArray()) {
            var name = GetString(asset, "name") ?? "";
            if (!name.StartsWith("M3U8Downloader-", StringComparison.OrdinalIgnoreCase)) continue;
            if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

            var url = GetString(asset, "browser_download_url");
            var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var l) ? l : 0;
            return (url, size);
        }

        return (null, 0);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    /// <summary>
    /// 比较两个版本号（忽略 v 前缀）。
    /// 解析不了的一律当 0.0 —— 宁可漏报一次更新，也不要因为 tag 写得不规范就乱提示。
    /// </summary>
    public static int CompareVersions(string left, string right) =>
        ParseVersion(left).CompareTo(ParseVersion(right));

    private static Version ParseVersion(string raw) =>
        Version.TryParse(raw.Trim().TrimStart('v', 'V'), out var v) ? v : new Version(0, 0);
}
