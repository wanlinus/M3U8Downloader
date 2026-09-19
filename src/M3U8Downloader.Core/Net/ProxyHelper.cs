using System.Net;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Net;

/// <summary>代理相关的公共处理</summary>
public static class ProxyHelper {
    /// <summary>
    /// 把用户填的代理地址补全成合法 URI。
    /// 允许只填 <c>127.0.0.1:7897</c>（很多人习惯这么写），会自动补上 http://。
    /// </summary>
    public static string? Normalize(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var text = raw.Trim();

        if (!text.Contains("://", StringComparison.Ordinal))
            text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https" or "socks4" or "socks5")) return null;
        if (string.IsNullOrWhiteSpace(uri.Host)) return null;
        if (!IsPlausibleHost(uri.Host)) return null;

        return uri.ToString();
    }

    /// <summary>
    /// 主机名是否像话。不校验的话，随便一句中文会被 .NET 转成 punycode 当成域名，
    /// 用户看到的错误就变成"不知道这样的主机 (xn--…)"，完全看不懂。
    /// </summary>
    private static bool IsPlausibleHost(string host) {
        if (IPAddress.TryParse(host, out _)) return true;                    // 1.2.3.4 / ::1
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return true;

        // 域名至少得有个点，且只能是字母数字点横线
        if (!host.Contains('.')) return false;
        foreach (var c in host) {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-')) return false;
        }
        return true;
    }

    /// <summary>
    /// 按设置构造代理。返回 null 表示"不显式指定代理"（走系统默认）。
    /// </summary>
    public static IWebProxy? Create(AppSettings? settings) {
        if (settings is null || !settings.ProxyEnabled) return null;

        var normalized = Normalize(settings.ProxyUrl);
        if (normalized is null) return null;

        var proxy = new WebProxy(new Uri(normalized)) {
            // 本地地址不走代理：站点大多在国内，绕一圈反而更慢甚至失败
            BypassProxyOnLocal = true,
        };

        try {
            proxy.BypassList = new[]
            {
                @"^https?://(localhost|127\.0\.0\.1|\[::1\])(:\d+)?(/|$)",
                @"^https?://10\.",
                @"^https?://192\.168\.",
                @"^https?://172\.(1[6-9]|2\d|3[01])\.",
            };
        } catch {
            // BypassList 赋值失败不影响主功能
        }

        return proxy;
    }

    /// <summary>设置里代理是否可用（UI 用来提示"已填但格式不对"）</summary>
    public static bool IsConfiguredButInvalid(AppSettings? settings) =>
        settings is { ProxyEnabled: true } &&
        !string.IsNullOrWhiteSpace(settings.ProxyUrl) &&
        Normalize(settings.ProxyUrl) is null;

    /// <summary>代理连通性测试结果</summary>
    public sealed record ProxyTestResult(bool Success, string Message);

    /// <summary>测试目标：体积最小的官方端点，只回一句话</summary>
    private const string TestUrl = "https://api.github.com/zen";

    /// <summary>GitHub API 不带 UA 会返回 403，必须显式带上</summary>
    private const string UserAgent = "M3U8Downloader/1.0 (proxy-check)";

    /// <summary>
    /// 代理连通性测试。CLI 与界面共用这一份实现，
    /// 避免出现"界面点不动、命令行却正常"这种两边不一致的问题。
    ///
    /// 这里刻意**不套用 BypassList**：要测的就是「这个代理能不能带我们出去」。
    /// </summary>
    public static async Task<ProxyTestResult> TestAsync(string? proxyUrl, CancellationToken ct = default) {
        var normalized = Normalize(proxyUrl);
        if (normalized is null)
            return new ProxyTestResult(false, "✘ 代理地址格式不正确。示例：http://127.0.0.1:7897");

        try {
            using var handler = new HttpClientHandler {
                Proxy = new WebProxy(new Uri(normalized)),
                UseProxy = true,
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

            // 关键：GitHub API 强制要求 User-Agent，不带会被直接回 403，
            // 那样代理明明是通的却永远测不出"可用"。
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

            using var response = await client
                .GetAsync(TestUrl, HttpCompletionOption.ResponseContentRead, ct)
                .ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? new ProxyTestResult(true, $"✔ 代理可用（HTTP {(int)response.StatusCode}）")
                : new ProxyTestResult(false, $"⚠ 代理有响应，但目标返回 HTTP {(int)response.StatusCode}");
        } catch (OperationCanceledException) {
            return new ProxyTestResult(false, "✘ 测试超时（12 秒），代理可能没启动或端口不对");
        } catch (Exception ex) {
            return new ProxyTestResult(false, "✘ 测试失败：" + ex.Message);
        }
    }
}
