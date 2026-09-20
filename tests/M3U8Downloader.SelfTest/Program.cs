using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Downloads;
using M3U8Downloader.Core.Settings;
using M3U8Downloader.Core.Sites;
using M3U8Downloader.Core.Storage;
using M3U8Downloader.Core.Tasks;

// ============================================================================
// 下载任务自检（无界面，不依赖外网）
//
// 分阶段验证「整部剧下载」的进度与状态是否真的对：
//   阶段 A ：SeriesDownloader（整部剧协调器）—— 上报频率、每集进度、总速度
//   阶段 A2：HlsDownloader（分片引擎）—— 分片级进度
//   阶段 B ：DownloadTaskManager（任务队列）—— 入队即可见分集清单、状态流转，
//            以及"绑定属性一律经 UI 线程封送"这一约定（用假 Dispatcher 模拟）
//   阶段 C ：断点续传（存盘 → 关程序 → 恢复）
//   阶段 D ：暂停 → 继续下载（必须停在「已暂停」，续传复用已下载的分片）
//   阶段 E ：重试失败集（**在原任务上重试**，任务数不能变多 —— 防重复集的回归测试）
//
// 做法：本地起一个极简 HLS 服务器（20 个分片，3 集共用同一份播放列表），
// 分片按块慢慢发以制造真实的中间进度。
//
// 运行： dotnet run --project tests/M3U8Downloader.SelfTest
// 退出码 0 = 全部通过。
//
// ---------------------------------------------------------------------------
// 临时目录：**整个自检只用下面这一个 runRoot**，各阶段在它下面建自己的子目录。
// 所以本文件里不该再出现 `Path.GetTempPath()` —— 要临时目录就用 runRoot。
//
// 退出时按结果决定去留：
//   · 全部通过   → 整个根删掉，`%TEMP%` 不留东西；
//   · 有阶段失败 → **保留**并把路径打印出来。暂存目录、合并产物、SQLite 库都是排查证据，
//     stdout 有时给不出来（比如"产物字节数不对"得看实际文件）。
//     保留是有界的 —— 下一次运行的启动兜底清理会把它扫掉，最多只留一轮。
//
// 为什么用退出钩子而不是 try/finally：本文件是顶层语句、主体 2500 多行，包一层 try
// 就得把每一行重新缩进（diff 会盖住全部内容、还容易误伤断言）。顶层语句的局部变量
// 能被 lambda 捕获，所以钩子里读得到 runRoot / runOk。
//
// 已确认：阶段 C 的「关程序」是**进程内模拟**（Dispose 掉 manager，再用同一个 SQLite
// 库文件新建一个恢复），**不是**真的拉起子进程 —— 所以单一钩子就够，不存在
// "把子进程还在用的目录删掉"的问题。哪天阶段 C 改成真起子进程了，这里要跟着改。
// ============================================================================

// ---- 临时根：先建自己的，再清别人留下的 ----

var runRoot = Path.Combine(Path.GetTempPath(), "m3u8-selftest-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(runRoot);

// 记下"谁在用这个目录"：兜底清理靠它判断主人还在不在（并发跑多个自检时不会互删）
File.WriteAllText(Path.Combine(runRoot, "owner.pid"), Environment.ProcessId.ToString());

// 上一次被强杀 / 断电 / CI 取消作业留下的（进程没走到退出钩子）在这里清掉
CleanStaleSelfTestTemp();

var tempSettled = false;

/// <summary>
/// 收尾：通过就删掉整个临时根，失败就留证据。两个退出钩子都会调它，所以只能跑一次。
/// </summary>
void SettleSelfTestTemp(bool passed) {
    if (tempSettled) return;
    tempSettled = true;

    if (passed) {
        TryDeleteDirectory(runRoot);
        return;
    }

    // 失败时把路径打出来 —— 不然"保留供排查"等于没保留
    Console.WriteLine();
    Console.WriteLine($"⚠ 自检没有全过，临时目录保留下来供排查：{runRoot}");
    Console.WriteLine("  （下次运行自检会自动清掉它；也可以现在手动删。）");
}

var runOk = false;

// 正常退出（包括结尾的 return ok ? 0 : 1）走 ProcessExit；
// 未捕获异常再单独挂一个 —— 异常路径下 ProcessExit 不保证跑得到。
AppDomain.CurrentDomain.ProcessExit += (_, _) => SettleSelfTestTemp(runOk);
AppDomain.CurrentDomain.UnhandledException += (_, _) => SettleSelfTestTemp(passed: false);

const int Port = 18742;
const int SegmentCount = 20;

var playlistBuilder = new StringBuilder();
playlistBuilder.AppendLine("#EXTM3U");
playlistBuilder.AppendLine("#EXT-X-VERSION:3");
playlistBuilder.AppendLine("#EXT-X-TARGETDURATION:4");
playlistBuilder.AppendLine("#EXT-X-MEDIA-SEQUENCE:1");
for (var i = 0; i < SegmentCount; i++) {
    playlistBuilder.AppendLine("#EXTINF:4.000,");
    playlistBuilder.AppendLine($"seg{i}.ts");
}
playlistBuilder.AppendLine("#EXT-X-ENDLIST");
var playlist = playlistBuilder.ToString();

// 合法 TS：每 188 字节一个包，包首字节 0x47（引擎会逐包校验）。
// 每个分片的长度与内容都必须不同 —— 否则会被「重复分片 = 广告」的识别逻辑跳过。
var segments = new byte[SegmentCount][];
for (var i = 0; i < SegmentCount; i++)
    segments[i] = BuildFakeTs(188 * (2000 + i * 7), (byte)(i + 1));

// ---------------------------------------------------------------- 加密流用例数据
//
// 专门复现一个真实踩过的坑：**加密分片的响应是密文，密文首字节约 1/16 概率是 '<'（0x3C）**，
// 被"假 200 / HTML 嗅探"误判成错误页 → 该片全网重试都失败。
// 实测某源站第 11 集 1400 片里正好 87 片（≈1/16）栽在这里，表现为"每 16 片失败 1 片"。
// 这里构造一条加密清单，并特意让 3 个分片的**密文首字节都等于 0x3C**。
var aesKey = new byte[16] { 0x37, 0x33, 0x63, 0x30, 0x62, 0x65, 0x62, 0x31,
                            0x65, 0x61, 0x65, 0x64, 0x63, 0x39, 0x64, 0x66 };
var encryptedSegments = new byte[3][];

for (var i = 0; i < encryptedSegments.Length; i++) {
    var plain = BuildFakeTs(188 * (300 + i * 20), (byte)(0xA0 + i));

    // 第一块密文的第一个字节只取决于明文前 16 字节，所以随机化首块里除同步字节外的内容，
    // 直到 PKCS7 加密后的首个字节正好是 0x3C（'<'）。命中概率约 1/16。
    var found = false;
    var rng = new Random(1234 + i);
    for (var attempt = 0; attempt < 4000 && !found; attempt++) {
        for (var k = 1; k < 16; k++) plain[k] = (byte)rng.Next(256);

        var cipher = AesEncryptPkcs7(plain, aesKey);
        if (cipher.Length > 0 && cipher[0] == 0x3C) {
            encryptedSegments[i] = cipher;
            found = true;
        }
    }

    if (!found) throw new InvalidOperationException("构造密文首字节 0x3C 失败");

    // 构造出来的密文解密回去必须仍是合法 TS，否则这条用例本身就不成立
    var back = AesDecryptPkcs7(encryptedSegments[i], aesKey);
    if (back is null || back[0] != 0x47 || back.Length % 188 != 0)
        throw new InvalidOperationException("构造出的密文解密后不是合法 TS");
}

var encPlaylistBuilder = new StringBuilder();
encPlaylistBuilder.AppendLine("#EXTM3U");
encPlaylistBuilder.AppendLine("#EXT-X-VERSION:3");
encPlaylistBuilder.AppendLine("#EXT-X-TARGETDURATION:4");
encPlaylistBuilder.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
encPlaylistBuilder.AppendLine($"#EXT-X-KEY:METHOD=AES-128,URI=\"/enc/key.key\",IV=0x{new string('0', 32)}");
for (var i = 0; i < encryptedSegments.Length; i++) {
    encPlaylistBuilder.AppendLine("#EXTINF:4.000,");
    encPlaylistBuilder.AppendLine($"/enc/seg{i}.ts");
}
encPlaylistBuilder.AppendLine("#EXT-X-ENDLIST");
var encPlaylist = encPlaylistBuilder.ToString();

// 每个分片被请求了多少次 —— 续传自检靠它判断「已完成的集有没有被重下」
var requestCounts = new ConcurrentDictionary<string, int>();

// 打开后分片请求一律 404 —— 阶段 E 用它真实制造「失败的分集」
var failSegments = false;

// ---- 适配层用例的页面：真实结构的手工精简版，保证自检不依赖外网 ----

// 努努影院详情页：集号在 ep_slug 属性里，链接是 javascript:;（真实页面的形态）
const string NnyyDetailHtml = """
<!DOCTYPE html>
<html><head><meta charset="UTF-8">
<title>《交锋》全集在线观看 - 电视剧 - 努努影院</title>
</head>
<body>
<header class="product-header">
  <h1 class="product-title" style="display: inline-block;">
      交锋
      <span style="font-size:15px;">(2026)</span>
  </h1>
  <img src="/nnimg/20267897.jpg" class="thumb detail-img" alt="交锋">
</header>
<div class="playlists" id="slider">
  <ul id="eps-ul">
    <li class="play-btn" onclick="on_play_btn(this)" ep_slug="ep3"><a href="javascript:;" >第3集</a></li>
    <li class="play-btn" onclick="on_play_btn(this)" ep_slug="ep2"><a href="javascript:;" >第02集</a></li>
    <li class="play-btn" onclick="on_play_btn(this)" ep_slug="ep1"><a href="javascript:;" >第1集</a></li>
  </ul>
</div>
<script>
  function on_ep(ep_slug) {
    var url = '/_gp/{0}/{1}'.replace('{0}', '20267897').replace('{1}', ep_slug);
  }
  on_ep('ep3');
</script>
</body></html>
""";

// /_gp/ 接口的返回：两个源，第一个可用，第二个已失效（返回 HTML 而不是清单）。
// html_content 里的按钮文本决定源在界面上的显示名（BF / DE）。
var nnyyPlaysJson = $$"""
{
  "video_plays": [
    { "play_data": "http://localhost:{{Port}}/index.m3u8", "src_site": "bfzy" },
    { "play_data": "http://localhost:{{Port}}/not-a-playlist.html", "src_site": "dead" }
  ],
  "html_content": "<button onclick=\"play_changed(0)\">BF 第1集</button><button onclick=\"play_changed(1)\">DE 第1集</button>"
}
""";

// 电影页：slug 是 "hd" / "other"，**根本不含集号**。
// 少了「按出现顺序编号」这一步，整页会报「没有找到剧集列表」。
const string NnyyMovieHtml = """
<!DOCTYPE html>
<html><head><meta charset="UTF-8">
<title>《玛丽·雪莱的怪物》全集在线观看 - 电影 - 努努影院</title>
</head>
<body>
<header class="product-header">
  <h1 class="product-title" style="display: inline-block;">
      玛丽·雪莱的怪物 Mary&#39;s Monster
      <span style="font-size:15px;">(2030)</span>
  </h1>
  <img src="/nnimg/20304951.jpg" class="thumb detail-img" alt="玛丽·雪莱的怪物">
</header>
<div class="playlists" id="slider">
  <ul id="eps-ul">
    <li class="play-btn" onclick="on_play_btn(this)" ep_slug="hd"><a href="javascript:;" >HD中字</a></li>
    <li class="play-btn" onclick="on_play_btn(this)" ep_slug="other"><a href="javascript:;" >其他</a></li>
  </ul>
</div>
<script>
  function on_ep(ep_slug) {
    var url = '/_gp/{0}/{1}'.replace('{0}', '20304951').replace('{1}', ep_slug);
  }
  on_ep('hd');
</script>
</body></html>
""";

// 电影页的取源返回：按钮上写的是「SD HD」，源名应该只取到「SD」
var nnyyMoviePlaysJson = $$"""
{
  "video_plays": [
    { "play_data": "http://localhost:{{Port}}/index.m3u8", "src_site": "sdzy2" }
  ],
  "html_content": "<button onclick=\"play_changed(0)\">SD HD</button>"
}
""";

// 通用兜底用例：页面里只有一段 JS，地址还是转义斜杠写法
var genericHtml = $$"""
<!DOCTYPE html>
<html><head><meta charset="UTF-8"><title>某个小站 - 在线观看</title></head>
<body>
<h1>测试影片</h1>
<script>
  var player = new Player();
  player.setup({ url: "http:\/\/localhost:{{Port}}\/index.m3u8" });
</script>
</body></html>
""";

const string EmptyHtml = """
<!DOCTYPE html>
<html><head><meta charset="UTF-8"><title>空页面 - 某站</title></head>
<body><p>这里什么都没有。</p></body></html>
""";

// 影迷界影院（wakuredo.com）详情页：真实页面的精简版。
// 这个站点有两处打破了「苹果 CMS 只有一种 URL 形态」的假设：
//   1. 播放页是**斜杠**分隔 —— /play/2337178967/{sid}/{nid}.html，
//      而不是常见的 /vodplay/{id}-{sid}-{nid}.html；
//   2. 详情页 ID（62329）与播放页 ID（2337178967）**是两套编号**，
//      按详情页 ID 过滤会把本剧的选集链接全部滤掉。
// 页面末尾那条「另一部剧」的链接用来验证多数投票能把侧边栏噪声挡在外面。
const string WakuredoDetailHtml = """
<!DOCTYPE html>
<html><head><meta charset="UTF-8">
<title>王凯《交锋》全集免费 - 影迷界影院</title>
<meta property="og:title" content="王凯《交锋》全集免费">
<meta property="og:site_name" content="影迷界影院">
</head>
<body>
<div class="panel"><p>交锋电视剧全集播放、交锋在线观看完整版电视剧免费</p></div>
<a class="btn" href="/play/2337178967/3/1.html">▶ 立即播放</a>
<div class="panel"><h2 style="font-size:16px;margin-bottom:10px">播放列表</h2>
  <div class="line-name">线路6</div>
  <div class="eps mac-eps-panel">
    <a class="" href="/play/2337178967/7/1.html" title="第01集">第01集</a>
    <a class="" href="/play/2337178967/7/2.html" title="第02集">第02集</a>
    <a class="" href="/play/2337178967/7/3.html" title="第03集">第03集</a>
  </div>
  <div class="line-name on">线路10</div>
  <div class="eps mac-eps-panel mac-on">
    <a class="" href="/play/2337178967/3/1.html" title="第01集">第01集</a>
    <a class="" href="/play/2337178967/3/2.html" title="第02集">第02集</a>
    <a class="" href="/play/2337178967/3/3.html" title="第03集">第03集</a>
  </div>
  <div class="line-name">线路9</div>
  <div class="eps mac-eps-panel">
    <a class="" href="/play/2337178967/8/1.html" title="第01集">第01集</a>
    <a class="" href="/play/2337178967/8/2.html" title="第02集">第02集</a>
    <a class="" href="/play/2337178967/8/3.html" title="第03集">第03集</a>
  </div>
</div>
<div class="side"><h3>猜你喜欢</h3>
  <a href="/play/8888888888/1/1.html">另一部剧 第01集</a>
</div>
</body></html>
""";

// 播放页的 player_aaaa 取自真实页面（只把直链换成自检用的本地地址）。
// 不同 sid 给不同的 from，用来验证「当前源名取自本页、其余源靠探测」这条逻辑。
// 真实播放页同样带完整选集区，且用 line-name on 标出正在播的那条线路。
string WakuredoPlayHtml(int sid, int nid) {
    var from = sid switch { 3 => "lzm3u8", 7 => "bfzym3u8", 8 => "wjm3u8", _ => $"unknown{sid}" };

    var panels = new StringBuilder();
    foreach (var (line, s) in new[] { ("线路10", 3), ("线路6", 7), ("线路9", 8) }) {
        var on = s == sid ? " on" : "";
        panels.Append($"<div class=\"line-name{on}\">{line}</div>");
        panels.Append($"<div class=\"eps mac-eps-panel{on}\">");
        for (var i = 1; i <= 3; i++) {
            var cur = s == sid && i == nid ? " on" : "";
            panels.Append($"<a class=\"{cur}\" href=\"/play/2337178967/{s}/{i}.html\" title=\"交锋 第{i:00}集\">第{i:00}集</a>");
        }
        panels.Append("</div>");
    }

    return $$"""
<!DOCTYPE html>
<html><head><meta charset="UTF-8"><title>交锋_第{{nid}}集 - 影迷界影院</title></head>
<body>
<div class="panel"><h2 style="font-size:16px;margin-bottom:10px">播放列表</h2>{{panels}}</div>
<script type="text/javascript">
var player_aaaa={"flag":"play","encrypt":0,"trysee":0,"points":0,"link":"\/play\/2337178967\/{{sid}}\/{{nid}}.html","link_next":"","link_pre":"","url":"http:\/\/localhost:{{Port}}\/index.m3u8","url_next":"","from":"{{from}}","server":"no","note":"","id":"2337178967","sid":{{sid}},"nid":{{nid}}}
</script>
</body></html>
""";
}

// 影迷界影院的播放页：斜杠分隔的 /play/{剧ID}/{sid}/{nid}.html
var wakuredoPlayPath = new Regex(
    @"^/play/\d+/(?<sid>\d+)/(?<nid>\d+)\.html$", RegexOptions.IgnoreCase);

var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{Port}/");
listener.Start(); Console.WriteLine($"本地测试服务器: http://localhost:{Port}/  " +
                  $"{SegmentCount} 个分片，每片约 {segments[0].Length / 1024.0:0} KB");

_ = Task.Run(async () => {
    while (listener.IsListening) {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); } catch { break; }

        _ = Task.Run(async () => {
            try {
                var path = ctx.Request.Url!.AbsolutePath;

                // ---- 加密流用例：/enc/index.m3u8 + /enc/key.key + /enc/segN.ts（AES-128 密文）----
                if (path.StartsWith("/enc/", StringComparison.OrdinalIgnoreCase)) {
                    if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) {
                        var body = Encoding.UTF8.GetBytes(encPlaylist);
                        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    } else if (path.EndsWith("key.key", StringComparison.OrdinalIgnoreCase)) {
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = aesKey.Length;
                        await ctx.Response.OutputStream.WriteAsync(aesKey);
                    } else {
                        var name = Path.GetFileNameWithoutExtension(path);
                        var idx = int.TryParse(name.AsSpan(3), out var en) ? en : 0;
                        var body = encryptedSegments[Math.Clamp(idx, 0, encryptedSegments.Length - 1)];

                        ctx.Response.ContentType = "video/mp2t";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                }
                // ---- 适配层用例（放在 failSegments 之前：这些用例不受「源站抽风」开关影响）----
                else if (path.Equals("/dianshiju/20267897.html", StringComparison.OrdinalIgnoreCase)) {
                    var body = Encoding.UTF8.GetBytes(NnyyDetailHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (path.StartsWith("/_gp/", StringComparison.Ordinal)) {
                    // 电影页用另一份取源结果（按钮上写的是「SD HD」这种）
                    var payload = path.Contains("20304951", StringComparison.Ordinal)
                        ? nnyyMoviePlaysJson
                        : nnyyPlaysJson;

                    var body = Encoding.UTF8.GetBytes(payload);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (path.Equals("/dianying/20304951.html", StringComparison.OrdinalIgnoreCase)) {
                    var body = Encoding.UTF8.GetBytes(NnyyMovieHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (path.Equals("/generic.html", StringComparison.OrdinalIgnoreCase)) {
                    var body = Encoding.UTF8.GetBytes(genericHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (path.Equals("/empty.html", StringComparison.OrdinalIgnoreCase)) {
                    var body = Encoding.UTF8.GetBytes(EmptyHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                  // ---- 影迷界影院用例：斜杠形态的详情页 / 播放页 ----
                  else if (path.Equals("/t/62329.html", StringComparison.OrdinalIgnoreCase)) {
                    var body = Encoding.UTF8.GetBytes(WakuredoDetailHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (wakuredoPlayPath.Match(path) is { Success: true } wm) {
                    var body = Encoding.UTF8.GetBytes(WakuredoPlayHtml(
                        int.Parse(wm.Groups["sid"].Value), int.Parse(wm.Groups["nid"].Value)));
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (path.Equals("/not-a-playlist.html", StringComparison.OrdinalIgnoreCase)) {
                    // 「失效源」：返回 HTML 而不是 m3u8，用来验证多源里会跳过它挑下一个
                    var body = Encoding.UTF8.GetBytes("<html><body>404 not found</body></html>");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else if (failSegments && !path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) {
                    // 源站抽风：分片一直 404，重试也救不回来
                    ctx.Response.StatusCode = 404;
                    ctx.Response.ContentLength64 = 0;
                } else if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)) {
                    var body = Encoding.UTF8.GetBytes(playlist);
                    ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                } else {
                    var name = Path.GetFileNameWithoutExtension(path);          // seg12
                    var index = int.TryParse(name.AsSpan(3), out var n) ? n : 0;
                    var body = segments[Math.Clamp(index, 0, segments.Length - 1)];

                    requestCounts.AddOrUpdate(name, 1, (_, v) => v + 1);

                    ctx.Response.ContentType = "video/mp2t";
                    ctx.Response.ContentLength64 = body.Length;

                    const int chunk = 100 * 1024;
                    for (var off = 0; off < body.Length; off += chunk) {
                        var count = Math.Min(chunk, body.Length - off);
                        await ctx.Response.OutputStream.WriteAsync(body.AsMemory(off, count));
                        await ctx.Response.OutputStream.FlushAsync();
                        await Task.Delay(60);   // 慢一点，便于观察进度中间态与速度
                    }
                }
            } catch { } finally { try { ctx.Response.Close(); } catch { } }
        });
    }
});

// ---------------------------------------------------------------- 剧集数据

var outputDir = Path.Combine(runRoot, "out");
Directory.CreateDirectory(outputDir);

SiteSeries BuildSeries(int episodeCount = 3) {
    var s = new SiteSeries {
        Kind = SiteKind.MacCms,
        SiteName = "本地测试站",
        PageUrl = $"http://localhost:{Port}/vodplay/1-1-1.html",
        SeriesId = "1",
        Title = "自检剧集",
    };

    var playSource = new SitePlaySource { Id = 1, Name = "本地源" };
    s.Sources.Add(playSource);

    for (var n = 1; n <= episodeCount; n++) {
        playSource.Episodes.Add(new SiteEpisode {
            Number = n,
            SourceId = 1,
            PageUrl = $"http://localhost:{Port}/vodplay/1-1-{n}.html",
            Title = $"第{n:00}集",
            PlaylistUrl = $"http://localhost:{Port}/index.m3u8",
            IsSelected = true,
        });
    }

    return s;
}

/// <summary>造一部剧，但只勾选其中某几集（模拟"用户只挑了一集下载"）</summary>
SiteSeries BuildSeriesSelected(int episodeCount, params int[] selected) {
    var s = BuildSeries(episodeCount);
    foreach (var e in s.AllEpisodes) e.IsSelected = selected.Contains(e.Number);
    return s;
}

/// <summary>追加拿第二个播放源（同集号会重复），并把勾选切到新源上</summary>
SiteSeries AddSecondSource(SiteSeries series, int episodeCount, params int[] selected) {
    var second = new SitePlaySource { Id = 2, Name = "备用源" };
    for (var n = 1; n <= episodeCount; n++) {
        second.Episodes.Add(new SiteEpisode {
            Number = n,
            SourceId = 2,
            PageUrl = $"http://localhost:{Port}/vodplay/1-2-{n}.html",
            Title = $"第{n:00}集",
            PlaylistUrl = $"http://localhost:{Port}/index.m3u8",
            IsSelected = selected.Contains(n),
        });
    }

    series.Sources.Add(second);
    foreach (var e in series.AllEpisodes) e.IsSelected = e.SourceId == 2 && selected.Contains(e.Number);
    return series;
}

var options = new SeriesDownloadOptions {
    OutputDirectory = outputDir,
    EpisodeConcurrency = 3,
    SegmentConcurrency = 2,
    FfmpegPath = null,          // 不转 MP4，保留 .ts —— 本次自检不关心封装
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
};

// ---------------------------------------------------------------- 阶段 A：引擎上报频率

Console.WriteLine();
Console.WriteLine("阶段 A：直接调用 SeriesDownloader（不经任务队列），看引擎到底上报了多少次");

var directReports = 0;
var directTrace = new List<string>();

using (var direct = new SeriesDownloader()) {
    var directProgress = new Progress<SeriesDownloadProgress>(p => {
        directReports++;
        var line = $"{p.OverallPercent,5:0.0}%  [{string.Join(",", p.Episodes.Select(e => $"{e.Percent:0}"))}]  " +
                   $"{p.SpeedText,-10}  {p.DownloadedBytes / 1024.0 / 1024.0:0.00} MB";
        if (directTrace.Count == 0 || directTrace[^1] != line) directTrace.Add(line);
    });

    await direct.DownloadAsync(BuildSeries(), options, directProgress);
}

Console.WriteLine($"上报次数: {directReports}");
foreach (var line in directTrace.Take(30)) Console.WriteLine("  " + line);
if (directTrace.Count > 30) Console.WriteLine($"  …（共 {directTrace.Count} 个不同值）");

// ---------------------------------------------------------------- 阶段 A2：引擎最底层

Console.WriteLine();
Console.WriteLine("阶段 A2：直接用 HlsDownloader，看分片级上报频率");

{
    var hlsDir = Path.Combine(runRoot, "hls");
    Directory.CreateDirectory(hlsDir);

    using var hls = new HlsDownloader();
    var (media, _) = await hls.ResolveMediaPlaylistAsync($"http://localhost:{Port}/index.m3u8");
    Console.WriteLine($"播放列表解析：{media.Segments.Count} 个分片，总时长 {media.TotalDuration:0.0}s");

    var hlsReports = 0;
    var hlsTrace = new List<string>();

    var hlsProgress = new Progress<DownloadProgress>(d => {
        hlsReports++;
        var line = $"{d.Percent,5:0.0}%  片 {d.CompletedSegments}/{d.TotalSegments}  " +
                   $"{d.DownloadedBytes / 1024.0 / 1024.0:0.00} MB  {d.SpeedText}";
        if (hlsTrace.Count == 0 || hlsTrace[^1] != line) hlsTrace.Add(line);
    });

    var hlsResult = await hls.DownloadAsync(media, new DownloadOptions {
        TempDirectory = hlsDir,
        OutputPath = Path.Combine(hlsDir, "out.ts"),
        Concurrency = 2,
        MaxRetries = 1,
        DeleteTempOnSuccess = false,
    }, hlsProgress);

    Console.WriteLine($"上报次数: {hlsReports}   成功: {hlsResult.Success}   产物 {hlsResult.OutputBytes / 1024.0:0.0} KB");
    foreach (var line in hlsTrace.Take(30)) Console.WriteLine("  " + line);
    if (hlsTrace.Count > 30) Console.WriteLine($"  …（共 {hlsTrace.Count} 个不同值）");
}

// ---------------------------------------------------------------- 阶段 B：走任务队列

var queueDir = Path.Combine(runRoot, "queue");
Directory.CreateDirectory(queueDir);
var queueOptions = new SeriesDownloadOptions {
    OutputDirectory = queueDir,
    EpisodeConcurrency = 3,
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
};

Console.WriteLine();
Console.WriteLine("阶段 B：走 DownloadTaskManager（模拟界面异步封送）");

var dispatcher = new FakeDispatcher();
using var manager = new DownloadTaskManager(null, dispatcher.Post);

var task = manager.Enqueue(BuildSeries(), queueOptions);
await dispatcher.InvokeAsync(() => { });        // 等入队动作在"UI 线程"跑完

var midSamples = new ConcurrentDictionary<int, int>();
var stateSequence = new List<string>();
var finished = new TaskCompletionSource();
var speedSamples = 0;
double peakSpeed = 0;
long peakBytes = 0;
foreach (var item in task.Episodes) {
    var ep = item;
    ep.PropertyChanged += (_, e) => {
        if (e.PropertyName == nameof(TaskEpisodeItem.Percent) && ep.Percent > 0 && ep.Percent < 100)
            midSamples.AddOrUpdate(ep.Number, 1, (_, v) => v + 1);
    };
}

task.PropertyChanged += (_, e) => {
    switch (e.PropertyName) {
        case nameof(SeriesTask.SpeedBytesPerSecond):
            if (task.SpeedBytesPerSecond > 0) {
                speedSamples++;
                peakSpeed = Math.Max(peakSpeed, task.SpeedBytesPerSecond);
            }
            break;

        case nameof(SeriesTask.DownloadedBytes):
            peakBytes = Math.Max(peakBytes, task.DownloadedBytes);
            break;

        case nameof(SeriesTask.State):
            stateSequence.Add(task.StateText);
            if (task.IsFinished) finished.TrySetResult();
            break;
    }
};

// 状态与进度序列另外用轮询记录：入队后「下载中」那次写入可能早于上面的订阅；
// 而且共享的进度对象会让事件采样被合并，轮询能看到界面真正拿到的值。
var observedStates = new List<string>();
var progressTrace = new List<string>();
var poller = Task.Run(async () => {
    while (!finished.Task.IsCompleted) {
        try {
            var (state, percent, perEpisode, speed, bytes) = await dispatcher.InvokeAsync(() =>
                (task.StateText,
                 task.Percent,
                 string.Join(",", task.Episodes.Select(e => $"{e.Percent:0}")),
                 task.SpeedBytesPerSecond,
                 task.DownloadedBytes));

            if (observedStates.Count == 0 || observedStates[^1] != state) observedStates.Add(state);

            var line = $"{percent:0.0}% [{perEpisode}] {SeriesTask.FormatSpeed(speed)} {bytes / 1024.0 / 1024.0:0.00}MB";
            if (progressTrace.Count == 0 || progressTrace[^1] != line) progressTrace.Add(line);
        } catch { }
        try { await Task.Delay(100); } catch { }
    }
});

Console.WriteLine("已入队，等待下载完成…");
await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
await poller;
await dispatcher.InvokeAsync(() => { });        // flush 最后一次状态写入

double totalSpeed = await dispatcher.InvokeAsync(() => manager.TotalDownloadSpeed);

// ---------------------------------------------------------------- 结果

Console.WriteLine();
Console.WriteLine($"任务标题       : {task.Title}");
Console.WriteLine($"状态           : {task.StateText}");
Console.WriteLine($"整体进度       : {task.Percent:0.0}%   进度文本: {task.ProgressText}");
Console.WriteLine($"分集清单标题   : {task.EpisodeHeaderText}");
Console.WriteLine($"状态流转(轮询) : {string.Join(" → ", observedStates)}");
Console.WriteLine($"状态流转(事件) : {string.Join(" → ", stateSequence)}");
Console.WriteLine();
Console.WriteLine("界面看到的进度（去重后）：");
foreach (var line in progressTrace.Take(40)) Console.WriteLine("  " + line);
if (progressTrace.Count > 40) Console.WriteLine($"  …（共 {progressTrace.Count} 个采样点）"); Console.WriteLine($"UI 线程执行次数: {dispatcher.Executed}");
Console.WriteLine($"速度采样(>0)   : {speedSamples} 次，峰值 {SeriesTask.FormatSpeed(peakSpeed)}");
Console.WriteLine($"已下载峰值     : {peakBytes / 1024.0 / 1024.0:0.00} MB");
Console.WriteLine($"结束态总速度   : {SeriesTask.FormatSpeed(totalSpeed)}");
Console.WriteLine($"上传速度       : {SeriesTask.FormatSpeed(manager.TotalUploadSpeed)}（本程序不上传数据）");
Console.WriteLine();
Console.WriteLine("每一集：");
foreach (var ep in task.Episodes) {
    var mids = midSamples.TryGetValue(ep.Number, out var c) ? c : 0;
    Console.WriteLine($"  {ep.Title,-8} {ep.StatusText,-6} {ep.Percent,5:0.0}%  {ep.SizeText,-9} 中间态采样 {mids}");
}

Console.WriteLine();
Console.WriteLine($"输出目录       : {outputDir}");
foreach (var f in Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories))
    Console.WriteLine($"  {Path.GetRelativePath(outputDir, f)}  ({new FileInfo(f).Length / 1024.0:0.0} KB)");
Console.WriteLine($"报告文件       : {task.ReportPath ?? "(未生成)"}");

var okB = task.State == SeriesTaskState.Completed
          && task.Episodes.Count == 3
          && task.Episodes.All(e => e.Percent >= 100 && e.Bytes > 0)
          && midSamples.Count == 3
          && speedSamples > 0
          && peakBytes > 0
          && stateSequence.Contains("已完成")
          && observedStates.Contains("下载中")
          && dispatcher.Executed > 0;

// ---------------------------------------------------------------- 阶段 C：断点续传

Console.WriteLine();
Console.WriteLine("阶段 C：断点续传（保存任务 → 关程序 → 重开恢复）");

var resumeRoot = Path.Combine(runRoot, "resume");
var resumeOut = Path.Combine(resumeRoot, "out");
Directory.CreateDirectory(resumeOut);
var store = new TaskStore(Path.Combine(resumeRoot, "m3u8.db"));

var resumeOptions = new SeriesDownloadOptions {
    OutputDirectory = resumeOut,
    EpisodeConcurrency = 1,      // 串行下：保证第 1 集先完成，才能模拟「下到一半关掉程序」
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
};

var dispatcher1 = new FakeDispatcher();
var manager1 = new DownloadTaskManager(null, dispatcher1.Post, store) {
    // 恢复时要重新解析站点；自检不去访问真实网站，直接给本地剧集数据
    SeriesParser = (_, _) => Task.FromResult(BuildSeries()),
};

var task1 = manager1.Enqueue(BuildSeries(), resumeOptions);
await dispatcher1.InvokeAsync(() => { });

// 等「第 1 集已完成、第 2 集正在进行」—— 这就是用户关掉程序的那一刻
var halfDone = new TaskCompletionSource();
var halfPercent = 0.0;
var watcher = Task.Run(async () => {
    while (true) {
        var (firstDone, secondPercent) = await dispatcher1.InvokeAsync(() =>
            (task1.Episodes[0].State == EpisodeDownloadStatus.Completed, task1.Episodes[1].Percent));

        if (firstDone && secondPercent >= 15 && secondPercent < 100) {
            halfPercent = secondPercent;
            halfDone.TrySetResult();
            return;
        }

        if (halfDone.Task.IsCompleted) return;
        await Task.Delay(50);
    }
});
await halfDone.Task.WaitAsync(TimeSpan.FromMinutes(2));

var firstEpisodeFile = await dispatcher1.InvokeAsync(() => task1.Episodes[0].OutputPath);
var firstWriteTime = firstEpisodeFile is not null && File.Exists(firstEpisodeFile)
    ? File.GetLastWriteTimeUtc(firstEpisodeFile)
    : DateTime.MinValue;
var requestsBefore = requestCounts.Values.Sum();

Console.WriteLine($"  关程序前：第01集已完成（{firstEpisodeFile}），" +
                  $"第02集 {halfPercent:0}%，第03集还在排队");
Console.WriteLine($"  未完成的暂存目录：{Directory.GetDirectories(resumeOut, ".m3u8tmp-*", SearchOption.AllDirectories).Length} 个");

// 「关掉程序」：保存进度并释放（与窗口关闭时做的事一致）
await dispatcher1.InvokeAsync(() => manager1.SaveNow());
manager1.Dispose();
await Task.WhenAny(watcher, Task.Delay(TimeSpan.FromSeconds(2)));

var savedRecords = store.Load();
var stagingKept = Directory.GetDirectories(resumeOut, ".m3u8tmp-*", SearchOption.AllDirectories).Length;

// 「重新打开程序」：同一个库文件，新建 manager 恢复
var dispatcher2 = new FakeDispatcher();
using var manager2 = new DownloadTaskManager(null, dispatcher2.Post, store) {
    SeriesParser = (_, _) => Task.FromResult(BuildSeries()),
};

var restoredCount = await manager2.RestoreAsync();
var restoredTask = await dispatcher2.InvokeAsync(() => manager2.Tasks.FirstOrDefault());

var deadline = DateTime.UtcNow.AddMinutes(2);
while (restoredTask is not null && DateTime.UtcNow < deadline) {
    var state = await dispatcher2.InvokeAsync(() => restoredTask.State);
    if (state is SeriesTaskState.Completed or SeriesTaskState.PartiallyCompleted
        or SeriesTaskState.Failed or SeriesTaskState.Canceled or SeriesTaskState.Paused) break;
    await Task.Delay(200);
}

await dispatcher2.InvokeAsync(() => { });

var finalEpisodes = restoredTask is null
    ? new List<(int Number, string Status, long Bytes)>()
    : await dispatcher2.InvokeAsync(() => restoredTask.Episodes
        .Select(e => (e.Number, Status: e.StatusText, e.Bytes)).ToList());

var reportEpisodeCount = restoredTask?.Report?.Episodes.Count ?? 0;
var finalStateText = restoredTask?.StateText ?? "(无)";
var finalWriteTime = firstEpisodeFile is not null && File.Exists(firstEpisodeFile)
    ? File.GetLastWriteTimeUtc(firstEpisodeFile)
    : DateTime.MinValue;
var requestsAfter = requestCounts.Values.Sum();

Console.WriteLine($"  落盘任务数：{savedRecords.Count}（关程序后暂存目录保留 {stagingKept} 个）");
Console.WriteLine($"  恢复任务数：{restoredCount}   最终状态：{finalStateText}");
foreach (var ep in finalEpisodes)
    Console.WriteLine($"    第{ep.Number:00}集 {ep.Status}  {ep.Bytes / 1024.0 / 1024.0:0.0} MB");
Console.WriteLine($"  报告里的集数：{reportEpisodeCount}（含上次已完成的集）");
Console.WriteLine($"  第01集文件时间未变：{firstWriteTime == finalWriteTime}（说明没有重下）");
Console.WriteLine($"  恢复期间新增分片请求：{requestsAfter - requestsBefore} 个" +
                  $"（{SegmentCount * 2} = 两集全部重下；更少说明复用了已下载的分片）");

var okC = restoredCount == 1
          && savedRecords.Count == 1
          && stagingKept >= 1
          && firstEpisodeFile is not null
          && firstWriteTime == finalWriteTime
          && finalStateText == "已完成"
          && finalEpisodes.Count == 3
          && finalEpisodes.All(e => e.Status == "已完成")
          && reportEpisodeCount == 3
          && requestsAfter - requestsBefore > 0
          && requestsAfter - requestsBefore < SegmentCount * 2;

// ---------------------------------------------------------------- 阶段 D：暂停 / 继续
//
// 用户点「暂停」后任务要停在「已暂停」，已下载的分片保留；点「继续下载」接着下完。

Console.WriteLine();
Console.WriteLine("阶段 D：暂停 → 继续下载");

var pauseDir = Path.Combine(runRoot, "pause");
Directory.CreateDirectory(pauseDir);

var dispatcher3 = new FakeDispatcher();
using var manager3 = new DownloadTaskManager(null, dispatcher3.Post) {
    // 「继续下载」会重新解析站点；自检不去访问真实网站，直接给本地剧集数据
    SeriesParser = (_, _) => Task.FromResult(BuildSeries()),
};
var task3 = manager3.Enqueue(BuildSeries(), new SeriesDownloadOptions {
    OutputDirectory = pauseDir,
    EpisodeConcurrency = 1,      // 串行：第 1 集下到一半时才有机会按暂停
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
});
await dispatcher3.InvokeAsync(() => { });

// 等某一集下到一半 → 暂停 → 等它停下来
// （EpisodeConcurrency=1，所以只有一集在跑，其余在排队等名额 ——
//   这正好覆盖「暂停时还有集没轮到」这条路径）
await WaitUntilAsync(async () => await dispatcher3.InvokeAsync(() =>
    task3.Episodes.Any(e => e.Percent >= 5 && e.Percent < 100)), TimeSpan.FromMinutes(1),
    "有分集下到 5% 以上");
manager3.Pause(task3);
await WaitUntilAsync(async () => await dispatcher3.InvokeAsync(() => task3.IsFinished),
    TimeSpan.FromMinutes(1), "任务进入停止态");

// 立刻读一次（不排空 UI 队列）：此刻若已不是收尾写进去的值，说明有别的地方在写进度
Console.WriteLine($"  暂停刚返回时 : 进度 {await dispatcher3.InvokeAsync(() => task3.Percent):0.0}%" +
                  $"，状态 {await dispatcher3.InvokeAsync(() => task3.StateText)}");

// 停止之后不许自己再跑起来：等"所有集都到了终态且任务不在运行中"，再观察几秒。
// （注意不能只看 IsFinished —— 多集并发时它会短暂变 true，然后下一个集开跑又变回去）
try {
    await WaitUntilAsync(async () => await dispatcher3.InvokeAsync(() =>
            !task3.IsRunning
            && task3.Episodes.All(e => e.State is EpisodeDownloadStatus.Completed
                or EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled
                or EpisodeDownloadStatus.Pending)),
        TimeSpan.FromSeconds(20), "暂停后各集都停下");
} catch (TimeoutException) {
    var dump = await dispatcher3.InvokeAsync(() => string.Join("、",
        task3.Episodes.Select(e => $"第{e.Number:00}集={e.State}/{e.StatusText}/{e.Percent:0}%")));
    Console.WriteLine($"  ⚠ 暂停后仍有集没停下：{await dispatcher3.InvokeAsync(() => task3.StateText)} / " +
                      $"仍在运行={await dispatcher3.InvokeAsync(() => task3.IsRunning)}");
    Console.WriteLine($"    {dump}");
    throw;
}

var pausedPercentAtRest = await dispatcher3.InvokeAsync(() => task3.Percent);
await Task.Delay(6000);                       // 观察窗口：真"停不下来"的话这里就会看到它在动
var stillState = await dispatcher3.InvokeAsync(() => task3.State);
var stillPercent = await dispatcher3.InvokeAsync(() => task3.Percent);
var stillRunning = await dispatcher3.InvokeAsync(() => task3.IsRunning);

Console.WriteLine($"  暂停后 6 秒   : 状态 {await dispatcher3.InvokeAsync(() => task3.StateText)}" +
                  $"，进度 {pausedPercentAtRest:0.0}% → {stillPercent:0.0}%，仍在运行={stillRunning}");

var okStopped = !stillRunning
                && stillState == SeriesTaskState.Paused
                && Math.Abs(stillPercent - pausedPercentAtRest) < 0.01;

// 一集都没下完就被暂停：整体进度不该显示成 100%（看起来像"下完了"），
// 也不该被拍成 0%（用户明明已经下了一部分）
var pausedPercentAfterPause = await dispatcher3.InvokeAsync(() => task3.Percent);
Console.WriteLine($"  一集未完成时暂停的整体进度：{pausedPercentAfterPause:0.0}%（应在 0 与 100 之间）");
okStopped = okStopped && pausedPercentAfterPause is > 0 and < 100;

await dispatcher3.InvokeAsync(() => { });      // 把界面上排队的进度回填排空
await Task.Delay(200);
await dispatcher3.InvokeAsync(() => { });      // 收尾的那次回填可能刚刚才排队

var pausedState = await dispatcher3.InvokeAsync(() => task3.State);
var pausedFinished = await dispatcher3.InvokeAsync(() => task3.FinishedEpisodes);
var pausedTotal = task3.TotalEpisodes;
var pausedStillTodo = await dispatcher3.InvokeAsync(() =>
    task3.Episodes.Count(e => e.State != EpisodeDownloadStatus.Completed));
var pausedSucceeded = await dispatcher3.InvokeAsync(() => task3.SucceededEpisodes);
var pausedProgress = await dispatcher3.InvokeAsync(() => task3.ProgressText);
var pausedMessage = await dispatcher3.InvokeAsync(() => task3.MessageText);
var pausedCanResume = await dispatcher3.InvokeAsync(() => task3.CanResume);
var pausedCanPause = await dispatcher3.InvokeAsync(() => task3.CanPause);
var pausedNeedsRetry = await dispatcher3.InvokeAsync(() => task3.NeedsRetry);
var pausedFailed = await dispatcher3.InvokeAsync(() => task3.FailedEpisodes);
var pausedRows = await dispatcher3.InvokeAsync(() => task3.Episodes
    .Select(e => $"第{e.Number:00}集 {e.StatusText} {e.Percent:0}%").ToList());
var pausedRowList = await dispatcher3.InvokeAsync(() => task3.Episodes
    .Select(e => (e.Number, e.State, e.StatusText)).ToList());

Console.WriteLine($"  暂停后状态   : {await dispatcher3.InvokeAsync(() => task3.StateText)}" +
                  $"（已完成 {pausedSucceeded} 集，未完成 {pausedStillTodo} 集，已结束 {pausedFinished}/{pausedTotal}）");
Console.WriteLine($"  进度文本     : {pausedProgress}");
Console.WriteLine($"  可继续       : {pausedCanResume}");
Console.WriteLine($"  界面提示     : {pausedMessage}");
foreach (var line in pausedRows) Console.WriteLine($"    {line}");
Console.WriteLine($"  报告明细     : 成功 {task3.Report?.SucceededCount} / 失败 {task3.Report?.FailedCount}" +
                  $" / 取消 {task3.Report?.CanceledCount}，" +
                  $"{string.Join("、", task3.Report?.Episodes.Select(e => $"第{e.Episode.Number:00}集 {e.Status} {e.Percent:0}%") ?? new List<string>())}");

// 暂停必须被当作「暂停」而不是「失败」：被打断的集显示「已暂停」，
// 但**真的**失败（暂停那一刻分片恰好挂了）的集要如实保留，继续下载时会自动重下
//
// 另外：暂停不算"跑完"（IsRoundFinished = false）—— 完成类提示（"已自动过滤 N 个广告"）
// 用的是这个判据，用错就会在用户点暂停时弹窗。
var okPaused = pausedState == SeriesTaskState.Paused
               && pausedStillTodo > 0
               && pausedCanResume
               && !pausedCanPause
               && !pausedNeedsRetry
               && pausedFailed == 0
               && pausedMessage.Contains("已暂停")
               && await dispatcher3.InvokeAsync(() => task3.IsFinished && !task3.IsRoundFinished)
               && pausedRowList.All(e => e.State != EpisodeDownloadStatus.Canceled
                                         && e.StatusText is "已完成" or "失败" or "已暂停");

var requestsBeforeResume = requestCounts.Values.Sum();
var resumeStarted = await manager3.ResumeAsync(task3);
Console.WriteLine($"  调用继续下载 : 返回 {resumeStarted}，界面提示 {await dispatcher3.InvokeAsync(() => task3.MessageText)}");
await WaitUntilAsync(async () => await dispatcher3.InvokeAsync(() => task3.IsFinished),
    TimeSpan.FromMinutes(3), "继续下载跑完");

// 跑完之后**不许再有动静** —— 「继续下载之后就停不下来」就是这一条
await Task.Delay(800);
await dispatcher3.InvokeAsync(() => { });
var resumeRequestsSettled = requestCounts.Values.Sum() - requestsBeforeResume;
await Task.Delay(5000);
var resumeRequestsAfterSettle = requestCounts.Values.Sum() - requestsBeforeResume;
var resumeStateAfterSettle = await dispatcher3.InvokeAsync(() => task3.State);
var resumeRunningAfterSettle = await dispatcher3.InvokeAsync(() => task3.IsRunning);

Console.WriteLine($"  跑完后再等 5 秒：状态 {await dispatcher3.InvokeAsync(() => task3.StateText)}" +
                  $"，仍在运行={resumeRunningAfterSettle}，" +
                  $"分片请求 {resumeRequestsSettled} → {resumeRequestsAfterSettle}（应相等）");

var okSettled = !resumeRunningAfterSettle
                && resumeStateAfterSettle == SeriesTaskState.Completed
                && resumeRequestsAfterSettle == resumeRequestsSettled;

var resumeRequests = resumeRequestsAfterSettle;
var resumedEpisodes = await dispatcher3.InvokeAsync(() => task3.Episodes
    .Select(e => (e.Number, e.StatusText, e.Percent)).ToList());

Console.WriteLine($"  续传后状态   : {await dispatcher3.InvokeAsync(() => task3.StateText)}" +
                  $"（界面提示 {await dispatcher3.InvokeAsync(() => task3.MessageText)}）");
foreach (var ep in resumedEpisodes)
    Console.WriteLine($"    第{ep.Number:00}集 {ep.StatusText} {ep.Percent:0.0}%");
Console.WriteLine($"  续传期间新增分片请求：{resumeRequests} 个" +
                  $"（全重下最多 {SegmentCount * 3} 个；复用已下载的分片会更少）");

var okResume = await dispatcher3.InvokeAsync(() => task3.State) == SeriesTaskState.Completed
               && resumedEpisodes.Count == 3
               && resumedEpisodes.All(e => e.StatusText == "已完成")
               && resumeRequests > 0
               && resumeRequests < SegmentCount * 3;
// ---------------------------------------------------------------- 阶段 E：重试失败集不再重复建任务
//
// 曾经的 bug：RetryFailed 走 Enqueue 新建一个任务，同一个任务在列表里出现两份
// （一份「已取消/失败」+ 一份从头再列的），点几次就冒出几批重复的集。
// 现在必须：任务数不变，失败的那几集就地回到「等待中」并重下。

Console.WriteLine();
Console.WriteLine("阶段 E：重试失败集（任务数不能变多）");

var retryDir = Path.Combine(runRoot, "retry");
Directory.CreateDirectory(retryDir);

var dispatcher4 = new FakeDispatcher();
using var manager4 = new DownloadTaskManager(null, dispatcher4.Post);
var task4 = manager4.Enqueue(BuildSeries(), new SeriesDownloadOptions {
    OutputDirectory = retryDir,
    EpisodeConcurrency = 1,      // 串行：失败的分片只影响当前这一集
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
});
await dispatcher4.InvokeAsync(() => { });

// 先让第 1 集下完，再让分片请求开始 404（等价于源站抽风），
// 这样失败的是后面两集，而第 1 集是「已经下好的、重试时不该重下」的那一集
await WaitUntilAsync(async () => await dispatcher4.InvokeAsync(() =>
    task4.Episodes[0].State == EpisodeDownloadStatus.Completed), TimeSpan.FromMinutes(2), "第 1 集下载完成");
failSegments = true;
await WaitUntilAsync(async () => await dispatcher4.InvokeAsync(() => task4.IsFinished),
    TimeSpan.FromMinutes(3), "首次下载跑完（含失败集）");

var taskCountBefore = manager4.Tasks.Count;
var rowsBefore = await dispatcher4.InvokeAsync(() => task4.Episodes.Count);
var canRetry = await dispatcher4.InvokeAsync(() => task4.NeedsRetry);
var firstRunEpisodes = await dispatcher4.InvokeAsync(() => task4.Episodes
    .Select(e => (e.Number, e.StatusText)).ToList());
var firstRunState = await dispatcher4.InvokeAsync(() => task4.StateText);

// 第 1 集在「重试」时不能被重下 —— 用产物文件的写入时间来证明
var firstEpisodePath = await dispatcher4.InvokeAsync(() =>
    task4.Episodes.First(e => e.Number == 1).OutputPath);
var firstEpisodeWrite = firstEpisodePath is not null && File.Exists(firstEpisodePath)
    ? File.GetLastWriteTimeUtc(firstEpisodePath)
    : DateTime.MinValue;

// 源站恢复正常，再点「重试失败集」
failSegments = false;
var requestsBeforeRetry = requestCounts.Values.Sum();
var retried = await manager4.RetryFailedAsync(task4);

// 重试**立刻**就要检查一次：老实现是 Enqueue 新建任务，这里会当场变成 2
var taskCountRightAfter = manager4.Tasks.Count;
var rowsRightAfter = await dispatcher4.InvokeAsync(() => task4.Episodes.Count);

await WaitUntilAsync(async () => await dispatcher4.InvokeAsync(() => task4.IsFinished),
    TimeSpan.FromMinutes(3), "重试跑完");
await dispatcher4.InvokeAsync(() => { });

var taskCountAfter = manager4.Tasks.Count;
var rowsAfter = await dispatcher4.InvokeAsync(() => task4.Episodes.Count);
var retryRequests = requestCounts.Values.Sum() - requestsBeforeRetry;
var finalEpisodesD = await dispatcher4.InvokeAsync(() => task4.Episodes
    .Select(e => (e.Number, e.StatusText, HasError: e.HasError)).ToList());
var finalWrite = firstEpisodePath is not null && File.Exists(firstEpisodePath)
    ? File.GetLastWriteTimeUtc(firstEpisodePath)
    : DateTime.MinValue;

Console.WriteLine($"  首次下载     : {firstRunState}（" +
                  $"{string.Join("、", firstRunEpisodes.Select(e => $"第{e.Number:00}集{e.StatusText}"))}）");
Console.WriteLine($"  任务数       : 重试前 {taskCountBefore} → 调用后立刻 {taskCountRightAfter}" +
                  $" → 跑完 {taskCountAfter}（同一个任务对象：{ReferenceEquals(retried, task4)}）");
Console.WriteLine($"  分集行数     : 重试前 {rowsBefore} → 调用后立刻 {rowsRightAfter} → 跑完 {rowsAfter}");
Console.WriteLine($"  重试后状态   : {await dispatcher4.InvokeAsync(() => task4.StateText)}");
foreach (var ep in finalEpisodesD)
    Console.WriteLine($"    第{ep.Number:00}集 {ep.StatusText}{(ep.HasError ? "（有错误信息）" : "")}");
Console.WriteLine($"  第01集产物未被动过：{firstEpisodeWrite == finalWrite}（说明已完成的集没重下）");
Console.WriteLine($"  重试期间新增分片请求：{retryRequests} 个" +
                  $"（失败的 2 集全重下应为 {SegmentCount * 2} 个；三集全重下会是 {SegmentCount * 3} 个）");

var okRetry = canRetry
              && firstRunState == "部分完成"
              && retried is not null
              && ReferenceEquals(retried, task4)
              && taskCountRightAfter == taskCountBefore
              && taskCountAfter == taskCountBefore
              && rowsRightAfter == rowsBefore
              && rowsAfter == rowsBefore
              && finalEpisodesD.Count == 3
              && finalEpisodesD.Single(e => e.Number == 1).StatusText == "已完成"
              && firstEpisodeWrite == finalWrite
              && retryRequests > 0
              && retryRequests <= SegmentCount * 3;

// ---------------------------------------------------------------- 阶段 F：续传只能下"选中的那一集"
//
// 真实事故：用户在剧集列表里只勾了第 12 集，下载后暂停，点「继续下载」——
// 结果 21 集全被排进队列，开始整部剧重下（诊断日志里是 `开始下载：21 集（1）`）。
// 这条断言就是钉住"续传只下原本选中的集"。

Console.WriteLine();
Console.WriteLine("阶段 F：续传只下选中的那一集（21 集里只勾了第 12 集）");

const int FullEpisodeCount = 21;
const int PickedEpisode = 12;

var subsetDir = Path.Combine(runRoot, "subset");
Directory.CreateDirectory(subsetDir);

var dispatcher5 = new FakeDispatcher();
using var manager5 = new DownloadTaskManager(null, dispatcher5.Post) {
    // 站点上仍然是完整的 21 集 —— 续传要能从中只挑回第 12 集
    SeriesParser = (_, _) => Task.FromResult(BuildSeries(FullEpisodeCount)),
};

var task5 = manager5.Enqueue(BuildSeriesSelected(FullEpisodeCount, PickedEpisode),
    new SeriesDownloadOptions {
        OutputDirectory = subsetDir,
        EpisodeConcurrency = 1,
        SegmentConcurrency = 2,
        FfmpegPath = null,
        WriteReport = true,
        SeriesSubdirectory = true,
        MaxRetries = 1,
        RetryBaseDelayMs = 200,
    });
await dispatcher5.InvokeAsync(() => { });

var subsetStarted = new List<int>();
var subsetDone = new TaskCompletionSource();
task5.PropertyChanged += (_, e) => {
    if (e.PropertyName is not (nameof(SeriesTask.CurrentEpisode) or nameof(SeriesTask.State))) return;

    var current = task5.CurrentEpisode;
    if (!string.IsNullOrWhiteSpace(current) && !subsetStarted.Contains(PickedEpisode))
        subsetStarted.Add(PickedEpisode);

    if (task5.IsFinished) subsetDone.TrySetResult();
};

// 一开始就暂停：这一轮一集都下不完
await WaitUntilAsync(async () => await dispatcher5.InvokeAsync(() => task5.IsRunning),
    TimeSpan.FromSeconds(30), "任务开始下载");
manager5.Pause(task5);
await subsetDone.Task.WaitAsync(TimeSpan.FromSeconds(60));
await dispatcher5.InvokeAsync(() => { });

var requestsBeforeSubsetResume = requestCounts.Values.Sum();
var subsetResumed = await manager5.ResumeAsync(task5);
await WaitUntilAsync(async () => await dispatcher5.InvokeAsync(() => task5.IsFinished),
    TimeSpan.FromMinutes(3), "续传跑完");
await dispatcher5.InvokeAsync(() => { });

var subsetRequests = requestCounts.Values.Sum() - requestsBeforeSubsetResume;
var subsetTaskEpisodes = await dispatcher5.InvokeAsync(() => task5.Episodes
    .Select(e => (e.Number, e.StatusText)).ToList());
var subsetPlanned = await dispatcher5.InvokeAsync(() => task5.PlannedEpisodeNumbers.ToList());

Console.WriteLine($"  首次入队     : {FullEpisodeCount} 集里只勾了第 {PickedEpisode} 集 → " +
                  $"任务里 {await dispatcher5.InvokeAsync(() => task5.Episodes.Count)} 行");
Console.WriteLine($"  续传调用     : 返回 {subsetResumed}，这一轮计划要下 {subsetPlanned.Count} 集" +
                  $"（{string.Join(",", subsetPlanned)}，应为 1 集：{PickedEpisode}）");
Console.WriteLine($"  续传期间分片请求：{subsetRequests} 个（只下 1 集约 {SegmentCount} 个；" +
                  $"{FullEpisodeCount} 集全下会是 {SegmentCount * FullEpisodeCount} 个）");
foreach (var ep in subsetTaskEpisodes)
    Console.WriteLine($"    第{ep.Number:00}集 {ep.StatusText}");

var okResumeSubset = subsetResumed
                     && subsetPlanned.Count == 1
                     && subsetPlanned[0] == PickedEpisode
                     && subsetRequests > 0
                     && subsetRequests < SegmentCount * 2
                     && subsetTaskEpisodes.Count == 1
                     && subsetTaskEpisodes[0].Number == PickedEpisode
                     && subsetTaskEpisodes[0].StatusText == "已完成";

// ---- 阶段 F2：多播放源 + 源 id 失效，兜底也绝不能整源全下 ----
//
// 这是「继续下载变成整部剧重下」最容易复发的分支：
// 首选源在重新解析后对不上（源改版/重新编号），只能退到"按集号找"。

Console.WriteLine();
Console.WriteLine("阶段 F2：首选源对不上时的兜底（21 集 × 2 个源，只有备源有第 12 集）");

var fallbackDir = Path.Combine(runRoot, "fallback");
Directory.CreateDirectory(fallbackDir);

var dispatcher6 = new FakeDispatcher();
using var manager6 = new DownloadTaskManager(null, dispatcher6.Post) {
    SeriesParser = (_, _) => Task.FromResult(AddSecondSource(BuildSeries(FullEpisodeCount), FullEpisodeCount, PickedEpisode)),
};

var task6 = manager6.Enqueue(AddSecondSource(BuildSeries(FullEpisodeCount), FullEpisodeCount, PickedEpisode),
    new SeriesDownloadOptions {
        OutputDirectory = fallbackDir,
        EpisodeConcurrency = 1,
        SegmentConcurrency = 2,
        FfmpegPath = null,
        WriteReport = true,
        SeriesSubdirectory = true,
        MaxRetries = 1,
        RetryBaseDelayMs = 200,
    });
task6.PreferredSourceId = 99;      // 故意指向一个重新解析后不存在的源
await dispatcher6.InvokeAsync(() => { });

var fallbackDone = new TaskCompletionSource();
task6.PropertyChanged += (_, e) => {
    if (e.PropertyName == nameof(SeriesTask.State) && task6.IsFinished) fallbackDone.TrySetResult();
};

await WaitUntilAsync(async () => await dispatcher6.InvokeAsync(() => task6.IsRunning),
    TimeSpan.FromSeconds(30), "任务开始下载");
manager6.Pause(task6);
await fallbackDone.Task.WaitAsync(TimeSpan.FromSeconds(60));
await dispatcher6.InvokeAsync(() => { });

var requestsBeforeFallback = requestCounts.Values.Sum();
Console.WriteLine($"  入队后（尚未续传）：{await dispatcher6.InvokeAsync(() => task6.PlannedSeriesSummary)}");
var fallbackResumed = await manager6.ResumeAsync(task6);
Console.WriteLine($"  续传返回后立刻  ：{await dispatcher6.InvokeAsync(() => task6.PlannedSeriesSummary)}");
await WaitUntilAsync(async () => await dispatcher6.InvokeAsync(() => task6.IsFinished),
    TimeSpan.FromMinutes(3), "兜底续传跑完");
await dispatcher6.InvokeAsync(() => { });

var fallbackRequests = requestCounts.Values.Sum() - requestsBeforeFallback;
var fallbackPlanned = await dispatcher6.InvokeAsync(() => task6.PlannedEpisodeNumbers.ToList());
var fallbackSelection = await dispatcher6.InvokeAsync(() => task6.PlannedSelection.ToList());

Console.WriteLine($"  续传调用     : 返回 {fallbackResumed}，计划要下 {fallbackPlanned.Count} 集" +
                  $"（{string.Join(",", fallbackPlanned)}，应为 1 集：{PickedEpisode}）");
Console.WriteLine($"  选中明细     : {string.Join(",", fallbackSelection)}（应为 1 项）");
Console.WriteLine($"  续传期间分片请求：{fallbackRequests} 个（整源 21 集全下会是 {SegmentCount * FullEpisodeCount} 个）");

var okFallback = fallbackResumed
                 && fallbackPlanned.Count == 1
                 && fallbackPlanned[0] == PickedEpisode
                 && fallbackRequests > 0
                 && fallbackRequests < SegmentCount * 2;

// ---------------------------------------------------------------- 阶段 G：带 ffmpeg 的转封装 + 时长核对
//
// 这条路径（转 MP4）在自检里一直没被走过，而"下载完有没有做完整校验"的最大缺口
// 恰好在这里：ffmpeg 退出码 0 只说明封装成功，**说明不了没缺片**。
// 这里用真实 ffmpeg 跑一遍，要求核对结果里出现"时长核对通过"。

Console.WriteLine();
Console.WriteLine("阶段 G：转 MP4 后的时长核对（用真实 ffmpeg）");

// G1：先单独验证"内置时长探测"本身（不依赖任何外部工具）
var pcrDir = Path.Combine(runRoot, "pcr");
Directory.CreateDirectory(pcrDir);
var pcrPath = Path.Combine(pcrDir, "pcr-42s.ts");
File.WriteAllBytes(pcrPath, BuildTsWithPcr(42));

var probedSeconds = MediaDurationProbe.TryProbeSeconds(pcrPath);
var probeOk = probedSeconds is > 41.0 and < 43.0;
Console.WriteLine($"  内置探测（PCR）: 构造 42.0s 的 TS，读出 {probedSeconds?.ToString("0.00") ?? "null"}s " +
                  $"→ {(probeOk ? "通过 ✔" : "不通过 ✘")}");

var ffmpegPath = new[]
{
    @"D:\wanli\Downloads\视频\M3U8Downloader\publish\app-win-x64\ffmpeg\ffmpeg.exe",
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "M3U8Downloader", "ffmpeg", "ffmpeg.exe"),
}.FirstOrDefault(File.Exists);

// G2：完整下载（自检的假分片没有真实音视频，ffmpeg 认不出，所以这里只验证
//     "核对逻辑不会误报通过"：要么读不出来并如实报告，要么读出来且与 80s 相符）
var okDuration = probeOk;
{
    var mp4Dir = Path.Combine(runRoot, "mp4");
    Directory.CreateDirectory(mp4Dir);

    using var mp4Downloader = new SeriesDownloader();
    var mp4Report = await mp4Downloader.DownloadAsync(BuildSeries(), new SeriesDownloadOptions {
        OutputDirectory = mp4Dir,
        EpisodeConcurrency = 3,
        SegmentConcurrency = 2,
        FfmpegPath = ffmpegPath,
        WriteReport = true,
        SeriesSubdirectory = true,
        MaxRetries = 1,
        RetryBaseDelayMs = 200,
    });

    var durationLines = mp4Report.Log.Where(l => l.Contains("时长核对")).ToList();
    var verdictOk = durationLines.Count == 3 && durationLines.All(l =>
        l.Contains("时长核对通过") || l.Contains("时长核对未完成") || l.Contains("时长核对不通过"));

    Console.WriteLine($"  ffmpeg       : {(ffmpegPath is null ? "未找到（跳过转 MP4）" : "已找到")}");
    foreach (var line in durationLines) Console.WriteLine("    " + line);

    okDuration = okDuration && verdictOk;
}

// ---------------------------------------------------------------- 阶段 H：密文首字节是 '<' 的分片必须能下下来
//
// 这是"第 11 集每 16 片失败 1 片"那个真实故障的回归测试：
// 加密分片的响应是密文，密文首字节有 1/16 概率是 0x3C（'<'），
// 被"假 200 / HTML 嗅探"误判成错误页后，**重试多少次都没用**（内容一直是那个密文）。

Console.WriteLine();
Console.WriteLine("阶段 H：密文首字节为 '<'（0x3C）的加密分片必须下载成功");

var encDir = Path.Combine(runRoot, "enc");
Directory.CreateDirectory(encDir);

var encOk = false;
{
    using var encDownloader = new HlsDownloader();
    var (encMedia, encLogs) = await encDownloader.ResolveMediaPlaylistAsync(
        $"http://localhost:{Port}/enc/index.m3u8");
    foreach (var l in encLogs) Console.WriteLine("  [解析] " + l);

    Console.WriteLine($"  分片 {encMedia.Segments.Count} 个；" +
                      $"密文首字节: {string.Join(", ", encryptedSegments.Select(s => $"0x{s[0]:X2}"))}");

    var encResult = await encDownloader.DownloadAsync(encMedia, new DownloadOptions {
        Concurrency = 3,
        MaxRetries = 1,
        TempDirectory = Path.Combine(encDir, "staging"),
        OutputPath = Path.Combine(encDir, "out.ts"),
        DeleteTempOnSuccess = false,
    });

    Console.WriteLine($"  结果: Success={encResult.Success} 成功 {encResult.CompletedSegments} / " +
                      $"失败 {encResult.FailedSegments} / 共 {encResult.TotalSegments}，输出 " +
                      $"{encResult.OutputBytes / 1024.0 / 1024.0:0.00} MB");
    if (encResult.Error is not null) Console.WriteLine($"  Error: {encResult.Error}");
    foreach (var r in encResult.FailureReasons) Console.WriteLine("  失败原因: " + r);

    // 产物应等于 3 个明文分片之和（密文比明文多 16 字节填充）
    var expectedBytes = (long)encryptedSegments.Sum(s => s.Length - 16);
    encOk = encResult.Success
            && encResult.CompletedSegments == 3
            && encResult.FailedSegments == 0
            && encResult.OutputBytes == expectedBytes;
    Console.WriteLine($"  产物应等于三个明文分片之和：{encResult.OutputBytes} vs {expectedBytes} → " +
                      $"{(encResult.OutputBytes == expectedBytes ? "一致 ✔" : "不一致 ✘")}");
}

// ---------------------------------------------------------------- 阶段 I：单文件服务
//
// 「单文件模式」（界面第一个面板、命令行默认模式）现在也走 Core 的下载服务，
// 与站点批量模式共用同一条流水线。这个阶段守住三件事：
//   1. 它能独立跑通（不依赖任何界面代码）；
//   2. 校验强度与站点模式一致 —— 转封装、时长核对、报告都要有；
//   3. 成功后暂存目录必须被清掉（不留垃圾）。

Console.WriteLine();
Console.WriteLine("阶段 I：单文件下载服务（SingleFileDownloadService）");

var singleDir = Path.Combine(runRoot, "single");
Directory.CreateDirectory(singleDir);

var okSingle = false;
{
    var service = new SingleFileDownloadService();
    var report = await service.DownloadAsync(new SingleFileDownloadOptions {
        Url = $"http://localhost:{Port}/index.m3u8",
        OutputDirectory = singleDir,
        FileName = "single",
        SegmentConcurrency = 4,
        MaxRetries = 1,
        FfmpegPath = ffmpegPath,
        FullDecodeCheck = false,   // 自检里不跑全量解码（那是几十秒的整条解码）
        WriteReport = true,
    });

    Console.WriteLine($"  结果: Success={report.Success}，" +
                      $"分片 {report.Outcome?.CompletedSegments}/{report.Outcome?.TotalSegments}，" +
                      $"产物 {report.OutputPath}（{report.Format}，{report.OutputBytes / 1024.0 / 1024.0:0.00} MB）");

    var producedExists = report.OutputPath is not null && File.Exists(report.OutputPath);
    var stagingCleaned = !Directory.EnumerateDirectories(singleDir, ".m3u8tmp-single-*").Any();
    var reportExists = report.ReportPath is not null && File.Exists(report.ReportPath);
    var verdictOk = !string.IsNullOrWhiteSpace(report.Outcome?.DurationVerdict);
    var formatOk = report.Format is "mp4" or "ts";

    Console.WriteLine($"  产物存在={producedExists} 暂存目录已清理={stagingCleaned} 报告已写出={reportExists}");
    Console.WriteLine($"  时长核对={report.Outcome?.DurationVerdict}");
    foreach (var line in report.Log.Where(l => l.Contains("容器信息") || l.Contains("全量解码")).Take(3))
        Console.WriteLine("    " + line);

    okSingle = report.Success && producedExists && stagingCleaned && reportExists && verdictOk && formatOk;
}

// ---------------------------------------------------------------- 阶段 J：站点适配层
//
// 「适配更多网站」的能力本身也要有回归测试守着。用本地页面 fixture 覆盖四件事：
//   1. 适配器**自动登记**且按优先级排序（新增站点不用改任何注册代码）；
//   2. 努努影院这类自研站点：集号在 ep_slug、直链要调 /_gp/ 接口、
//      同一集里失效的源要能跳过；
//   3. 通用兜底：页面里只有一段转义写法的 JS，也要能把 m3u8 抠出来；
//   4. 什么都没有的页面必须明确报错，而不是给个空列表（那样用户只会更困惑）。

Console.WriteLine();
Console.WriteLine("阶段 J：站点适配层（自动登记 / 努努影院 / 通用兜底）");

var okRegistry = false;
var okNnyy = false;
var okNnyyMovie = false;
var okGeneric = false;
var okEmpty = false;
var okWakuredo = false;

{
    using var registryCtx = new SiteContext();
    var resolver = SiteResolver.CreateDefault();
    var adapters = resolver.Adapters.ToList();

    var nnyyIndex = adapters.FindIndex(a => a.Kind == SiteKind.Nnyy);
    var macIndex = adapters.FindIndex(a => a.Kind == SiteKind.MacCms);
    var genericIndex = adapters.FindIndex(a => a.Kind == SiteKind.Generic);

    Console.WriteLine($"  自动登记: {string.Join(" / ", adapters.Select(a => $"{a.Name}(P{a.Priority})"))}");
    okRegistry = nnyyIndex >= 0 && macIndex >= 0 && genericIndex >= 0
                 && nnyyIndex < macIndex && macIndex < genericIndex;
    Console.WriteLine($"  优先级顺序 努努 < 苹果CMS < 通用: {(okRegistry ? "✔" : "✘")}");
}

{
    using var nnyyCtx = new SiteContext();
    var adapter = new NnyyAdapter();
    var pageUrl = new Uri($"http://localhost:{Port}/dianshiju/20267897.html");

    var handlesNnyy = adapter.CanHandle(new Uri("https://nnyy.in/dianshiju/20267897.html"));
    var handlesOther = adapter.CanHandle(new Uri("https://example.com/dianshiju/20267897.html"));

    var parsed = await adapter.ParseAsync(NnyyDetailHtml, pageUrl, nnyyCtx);
    Console.WriteLine($"  努努解析: {parsed.Title} · {parsed.SiteName} · " +
                      $"{parsed.Sources.FirstOrDefault()?.Episodes.Count ?? 0} 集 / {parsed.Sources.Count} 个源" +
                      $"（CanHandle: nnyy={handlesNnyy} 其它站={handlesOther}）");
    foreach (var source in parsed.Sources)
        Console.WriteLine($"    {source.Name} → {source.Episodes.Count} 集");

    var firstSource = parsed.Sources.FirstOrDefault();
    var second = parsed.AllEpisodes.First(e => e.Number == 2);

    // 识别阶段就该把每一集的直链都拿到（下载时不必再请求 /_gp/，也就不用再过代理）
    var allHaveUrl = parsed.AllEpisodes.All(e => !string.IsNullOrWhiteSpace(e.PlaylistUrl));

    okNnyy = handlesNnyy && !handlesOther
             && parsed.Title == "交锋"
             && parsed.Sources.Count == 2
             && parsed.Sources.All(s => s.Episodes.Count == 3)
             // 多源站点每源各持一份集，总数是「集数 × 源数」（与苹果 CMS 的口径一致）
             && parsed.TotalEpisodes == 6
             && firstSource?.Name == "BF（bfzy）"
             && second.Key == "ep2" && second.DisplayTitle == "第02集"
             && allHaveUrl
             && parsed.AllEpisodes
                 .Where(e => e.SourceId == firstSource!.Id)
                 .All(e => e.PlaylistUrl!.EndsWith("/index.m3u8", StringComparison.Ordinal));

    // 下载目录要带上站点标识：同一部剧在不同站点往往是不同版本，不能混进同一个目录
    var folder = SeriesDownloader.ResolveSeriesDirectory(parsed, new SeriesDownloadOptions {
        OutputDirectory = Path.Combine(runRoot, "batch-out"),
        SeriesSubdirectory = true,
    });
    Console.WriteLine($"  下载目录: {folder}");
    okNnyy = okNnyy && folder.EndsWith("交锋 - 努努影院", StringComparison.Ordinal);
}

// 电影页：slug 是 "hd"/"other"，**没有集号** —— 从前会把整页过滤空，
// 报「页面里没有找到剧集列表」。这条守着「按出现顺序编号」的兜底。
{
    using var movieCtx = new SiteContext();
    var parsed = await new NnyyAdapter().ParseAsync(NnyyMovieHtml,
        new Uri($"http://localhost:{Port}/dianying/20304951.html"), movieCtx);

    Console.WriteLine($"  努努电影页: {parsed.Title} · " +
                      $"{parsed.Sources.FirstOrDefault()?.Episodes.Count ?? 0} 集 / {parsed.Sources.Count} 个源");
    foreach (var source in parsed.Sources)
        foreach (var ep in source.Episodes)
            Console.WriteLine($"    {source.Name} → #{ep.Number} {ep.DisplayTitle}（key={ep.Key}）");

    var first = parsed.AllEpisodes.FirstOrDefault();
    okNnyyMovie = parsed.Sources.Count == 1
                  && parsed.Sources[0].Episodes.Count == 2
                  && parsed.Sources[0].Name == "SD（sdzy2）"      // 按钮上写的是「SD HD」，只取源名
                  && parsed.Title == "玛丽·雪莱的怪物 Mary's Monster"   // HTML 实体要解码
                  && first is { Number: 1, Key: "hd" }
                  && first.DisplayTitle == "HD中字"
                  && first.PlaylistUrl?.EndsWith("/index.m3u8", StringComparison.Ordinal) == true;
}

{
    using var genericCtx = new SiteContext();
    var resolver = SiteResolver.CreateDefault();
    var parsed = await resolver.ParseAsync($"http://localhost:{Port}/generic.html", genericCtx);
    var episode = parsed.AllEpisodes.FirstOrDefault();

    Console.WriteLine($"  通用兜底: {parsed.SiteName} · {parsed.Title} · {parsed.TotalEpisodes} 集 · 直链={episode?.PlaylistUrl}");
    okGeneric = parsed.Kind == SiteKind.Generic && parsed.TotalEpisodes == 1
                && (episode?.PlaylistUrl?.EndsWith("/index.m3u8", StringComparison.Ordinal) ?? false);
}

{
    using var emptyCtx = new SiteContext();
    var resolver = SiteResolver.CreateDefault();
    try {
        var parsed = await resolver.ParseAsync($"http://localhost:{Port}/empty.html", emptyCtx);
        Console.WriteLine($"  ✘ 空页面居然解析出了 {parsed.TotalEpisodes} 集，应当明确报错");
    } catch (NotSupportedException ex) {
        okEmpty = true;
        Console.WriteLine($"  空页面明确报错 ✔：{ex.Message}");
    }
}

// 影迷界影院（wakuredo.com）：播放页是**斜杠**形态，而且详情页 ID（62329）与
// 播放页 ID（2337178967）是两套编号。从前这两条会一起把 MacCmsAdapter 逼进
// 「页面里既没有 player_aaaa 也没有剧集链接」的报错分支，用户看到的就是「解析不出来」。
{
    using var wxCtx = new SiteContext();
    var resolver = SiteResolver.CreateDefault();
    var adapter = resolver.Resolve(new Uri($"http://localhost:{Port}/t/62329.html"));

    var canDetail = adapter?.CanHandle(new Uri("https://www.wakuredo.com/t/62329.html")) == true;
    var canPlay = adapter?.CanHandle(new Uri("https://www.wakuredo.com/play/2337178967/7/1.html")) == true;
    Console.WriteLine($"  影迷界影院 CanHandle：详情页={canDetail} 斜杠播放页={canPlay}（{adapter?.Name}）");

    var parsed = await resolver.ParseAsync($"http://localhost:{Port}/t/62329.html", wxCtx);
    Console.WriteLine($"  详情页解析: {parsed.Title} · {parsed.SiteName} · " +
                      $"{parsed.Sources.Count} 个源 / {parsed.TotalEpisodes} 集 · 当前源 sid={parsed.PreferredSourceId}");
    foreach (var source in parsed.Sources)
        Console.WriteLine($"    {source.Name} → {source.Episodes.Count} 集，勾选 {source.Episodes.Count(e => e.IsSelected)}");

    var current = parsed.Sources.FirstOrDefault(s => s.Id == parsed.PreferredSourceId);
    var selected = parsed.SelectedEpisodes.ToList();

    okWakuredo = canDetail && canPlay
                 && adapter!.Kind == SiteKind.MacCms
                 && parsed.Title == "王凯《交锋》全集免费"
                 && parsed.SiteName == "影迷界影院"
                 // 3 个源各 3 集；侧边栏那部「另一部剧」（id 8888888888）必须被多数投票滤掉
                 && parsed.Sources.Count == 3
                 && parsed.Sources.All(s => s.Episodes.Count == 3)
                 && parsed.TotalEpisodes == 9
                 // 默认选中模板标记为 on 的那条线路（sid=3），且只有它被勾选
                 && parsed.PreferredSourceId == 3
                 && current is not null
                 && selected.Count == 3 && selected.All(e => e.SourceId == 3)
                 && parsed.AllEpisodes.Where(e => e.SourceId != 3).All(e => !e.IsSelected)
                 // 同一集的「▶ 立即播放」在选集区之前，标题要取后面那条「第01集」
                 && current.Episodes.First(e => e.Number == 1).DisplayTitle == "第01集"
                 // 源名要靠探测各源播放页的 player_aaaa 拿到（playerconfig.js 取不到时的兜底）
                 && parsed.Sources.Select(s => s.Name).SequenceEqual(["lzm3u8", "bfzym3u8", "wjm3u8"])
                 // 选中集的第一集直链由 resolver 在识别阶段就补上
                 && selected[0].PlaylistUrl?.EndsWith("/index.m3u8", StringComparison.Ordinal) == true;

    // 用户直接把播放页地址粘进来也要能解析（同一个站点的另一种入口）
    var fromPlay = await resolver.ParseAsync($"http://localhost:{Port}/play/2337178967/7/2.html", wxCtx);
    Console.WriteLine($"  播放页解析: {fromPlay.Title} · {fromPlay.Sources.Count} 个源 / {fromPlay.TotalEpisodes} 集" +
                      $" · 当前源 sid={fromPlay.PreferredSourceId}");
    okWakuredo = okWakuredo
                 && fromPlay.Sources.Count == 3
                 && fromPlay.PreferredSourceId == 7
                 && fromPlay.SelectedEpisodes.All(e => e.SourceId == 7)
                 && fromPlay.SelectedEpisodes.Count() == 3;
}

// ---------------------------------------------------------------- 阶段 K：直连不得走系统代理
//
// SocketsHttpHandler.UseProxy 默认是 **true**，Proxy 为 null 时它会退到
// HttpClient.DefaultProxy —— Windows 上那就是系统的 WinINET 代理设置。
// 用户机器上开着 Clash 之类的工具时，「直连」客户端会**全程走代理**：
// 白耗代理流量（用户明确在意），还会被站点按代理 IP 拒绝。
// 实测影迷界影院：真直连 200 / 走系统代理 403 —— wakuredo 解析失败正是这个原因。
//
// 做法：把进程默认代理指到一个连不上的黑洞，站点解析与分片下载都必须照常工作。

Console.WriteLine();
Console.WriteLine("阶段 K：直连不得走系统代理（UseProxy 必须显式关闭）");

var okNoSystemProxy = false;
{
    var originalProxy = HttpClient.DefaultProxy;
    HttpClient.DefaultProxy = new WebProxy("http://127.0.0.1:1");   // 黑洞：连上就说明走了代理

    try {
        using var directCtx = new SiteContext();
        var html = await directCtx.Direct.GetStringAsync($"http://localhost:{Port}/generic.html");
        var siteOk = html.Contains("测试影片", StringComparison.Ordinal);

        using var directHls = new HlsDownloader();
        var (media, _) = await directHls.ResolveMediaPlaylistAsync($"http://localhost:{Port}/index.m3u8");
        var hlsOk = media.Segments.Count == SegmentCount;

        okNoSystemProxy = siteOk && hlsOk;
        Console.WriteLine($"  黑洞代理下的站点解析: {(siteOk ? "✔ 直连正常" : "✘ 走了代理")}");
        Console.WriteLine($"  黑洞代理下的分片清单: {(hlsOk ? $"✔ 直连正常（{media.Segments.Count} 片）" : "✘ 走了代理")}");
    } catch (Exception ex) {
        Console.WriteLine($"  ✘ 直连走了系统代理：{ex.GetType().Name}: {ex.Message}");
    } finally {
        HttpClient.DefaultProxy = originalProxy;
    }
}

// ---------------------------------------------------------------- 阶段 L：插播广告识别
//
// 实测场景：影迷界影院《交锋》第 25 集，源站在正片里插了两段各 7 片的赌博广告。
// 它们**同目录、不重复、密钥也正常**，原有三条规则一条都盖不住，于是被完整下载；
// 而广告是 1920x1080、正片 1080x460，同一个视频轨道里分辨率中途突变，
// 转成 MP4 后多数播放器解不出来 —— 表现就是「广告有声音、没画面」。
//
// 新规则要两个信号同时命中才动手：编号脱离正片的连续编号带 + 两侧有 DISCONTINUITY。

Console.WriteLine();
Console.WriteLine("阶段 L：插播广告识别（编号带断裂）");

var okInserted = false;
var okInsertedGuard = false;
{
    // 用例 1：正片 30 片（编号 0-29），中间插 5 片编号 359691+ 的广告，两侧有断层标记
    var sb = new StringBuilder();
    sb.AppendLine("#EXTM3U");
    sb.AppendLine("#EXT-X-VERSION:3");
    sb.AppendLine("#EXT-X-TARGETDURATION:8");
    sb.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
    sb.AppendLine("#EXT-X-DISCONTINUITY");
    for (var i = 0; i < 10; i++) sb.AppendLine($"#EXTINF:4.000,\nseg{i:000000}.ts");
    sb.AppendLine("#EXT-X-DISCONTINUITY");
    for (var i = 0; i < 5; i++) sb.AppendLine($"#EXTINF:4.000,\nseg{359691 + i}.ts");
    sb.AppendLine("#EXT-X-DISCONTINUITY");
    for (var i = 10; i < 30; i++) sb.AppendLine($"#EXTINF:4.000,\nseg{i:000000}.ts");
    sb.AppendLine("#EXT-X-ENDLIST");

    var parsed = M3U8Parser.Parse(sb.ToString(), "https://example.com/hls/index.m3u8");
    var report = SegmentInspector.Inspect(parsed.Media!);
    var marked = report.Suspects.Select(s => s.Index).OrderBy(x => x).ToList();

    Console.WriteLine($"  插播用例: 共 {report.TotalSegments} 片，标记 {marked.Count} 片 → {string.Join(",", marked)}");
    foreach (var r in report.Reasons) Console.WriteLine($"    · {r}");

    okInserted = marked.SequenceEqual(new[] { 10, 11, 12, 13, 14 });
    Console.WriteLine($"  只标记中间那 5 片（10-14）: {(okInserted ? "✔" : "✘")}");

    // 用例 2（反例）：编号同样跳变，但**没有** DISCONTINUITY 夹住 → 必须一片都不标记。
    // 这条守着"宁可留着广告，也不能删正片"：编号乱也可能只是源站删过号。
    var sb2 = new StringBuilder();
    sb2.AppendLine("#EXTM3U");
    sb2.AppendLine("#EXT-X-VERSION:3");
    sb2.AppendLine("#EXT-X-TARGETDURATION:8");
    sb2.AppendLine("#EXT-X-MEDIA-SEQUENCE:0");
    for (var i = 0; i < 10; i++) sb2.AppendLine($"#EXTINF:4.000,\nseg{i:000000}.ts");
    for (var i = 0; i < 5; i++) sb2.AppendLine($"#EXTINF:4.000,\nseg{359691 + i}.ts");
    for (var i = 10; i < 30; i++) sb2.AppendLine($"#EXTINF:4.000,\nseg{i:000000}.ts");
    sb2.AppendLine("#EXT-X-ENDLIST");

    var parsed2 = M3U8Parser.Parse(sb2.ToString(), "https://example.com/hls/index.m3u8");
    var report2 = SegmentInspector.Inspect(parsed2.Media!);
    okInsertedGuard = report2.Suspects.Count == 0;
    Console.WriteLine($"  反例（无断层标记）: 标记 {report2.Suspects.Count} 片，应为 0 → {(okInsertedGuard ? "✔" : "✘")}");
}

// ---------------------------------------------------------------- 阶段 M：更新检测
//
// 只测纯逻辑（版本比较）—— 网络那部分刻意不测：自检的约定是"不依赖外网"。
// 端到端在本地手动验过：GitHub API 直连 0.8 秒返回，代理没开也照样查到。

Console.WriteLine();
Console.WriteLine("阶段 M：更新检测（版本比较）");

var okVersionCompare = false;
{
    var cases = new (string Left, string Right, int Expected)[]
    {
        ("1.4.0", "1.3.0", 1),
        ("v1.3.0", "1.3.0", 0),      // tag 的 v 前缀要能剥掉
        ("1.3.0", "1.4.0", -1),
        ("1.3.1", "1.3.0", 1),       // 补丁号也比
        ("2.0.0", "10.0.0", -1),     // 数值比较，不是字符串比较（"2" > "10" 那种错法）
        ("1.10.0", "1.9.0", 1),
    };

    var bad = new List<string>();
    foreach (var (l, r, expected) in cases) {
        var actual = M3U8Downloader.Core.Update.UpdateChecker.CompareVersions(l, r);
        if (actual != expected) bad.Add($"{l} vs {r} 期望 {expected} 实得 {actual}");
    }

    okVersionCompare = bad.Count == 0;
    Console.WriteLine($"  版本比较 {cases.Length} 组: {(okVersionCompare ? "✔" : "✘ " + string.Join("；", bad))}");
}

// ---------------------------------------------------------------- 阶段 N：续下更新
//
// 场景：热播剧每天更新几集 —— 今天下完 2 集，明天站点多了第 3、4 集。
// 点「续下更新」要先**预检**列出候选，由用户勾选，最后只下勾中的那几集：
// 勾了第 4 集就只下第 4 集，没勾的第 3 集绝不能自己跑进来。
// 反例：候选都下完之后再预检，候选应为空。

Console.WriteLine();
Console.WriteLine("阶段 N：续下更新（预检列候选 → 用户勾选 → 只下勾中的）");

var fetchNewDir = Path.Combine(runRoot, "fetchnew");
Directory.CreateDirectory(fetchNewDir);

// 站点"当前"的集数 —— 首轮 2 集，之后模拟隔天更新成 4 集
var siteEpisodeCount = 2;
var parseCount = 0;   // 预检解析了几次；续下带着预检结果就不该再抓页面

var dispatcher7 = new FakeDispatcher();
using var manager7 = new DownloadTaskManager(null, dispatcher7.Post) {
    SeriesParser = (_, _) => {
        Interlocked.Increment(ref parseCount);
        return Task.FromResult(BuildSeries(siteEpisodeCount));
    },
};

var task7 = manager7.Enqueue(BuildSeries(siteEpisodeCount), new SeriesDownloadOptions {
    OutputDirectory = fetchNewDir,
    EpisodeConcurrency = 2,
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
});
await dispatcher7.InvokeAsync(() => { });

var fetchFirstDone = new TaskCompletionSource();
task7.PropertyChanged += (_, e) => {
    if (e.PropertyName == nameof(SeriesTask.State) && task7.IsFinished) fetchFirstDone.TrySetResult();
};

await WaitUntilAsync(async () => await dispatcher7.InvokeAsync(() => task7.IsRunning),
    TimeSpan.FromSeconds(30), "任务开始下载");
await fetchFirstDone.Task.WaitAsync(TimeSpan.FromMinutes(2));
await dispatcher7.InvokeAsync(() => { });

// 模拟隔天：站点更新出第 3、4 集
siteEpisodeCount = 4;

var preview1 = await manager7.PreviewFetchNewAsync(task7);
// 预检返回的是**站点上的全部集**：已下载的要能看出来（界面里置灰），待补的才可勾
var preview1Grey = preview1?.Candidates.Where(c => c.IsDownloaded).Select(c => c.Number).ToList()
                   ?? new List<int>();
var preview1Pending = preview1?.Candidates.Where(c => !c.IsDownloaded)
    .Select(c => (c.Number, c.IsNew)).ToList() ?? new List<(int, bool)>();
var parseAfterPreview = Interlocked.CompareExchange(ref parseCount, 0, 0);

// 用户只勾了第 4 集：第 3 集不许跟着进来
var fetched = await manager7.ResumeAsync(task7, includeNewEpisodes: true,
    preview: preview1, onlyNumbers: new HashSet<int> { 4 });
await WaitUntilAsync(async () => await dispatcher7.InvokeAsync(() => task7.IsFinished),
    TimeSpan.FromMinutes(2), "续下跑完");
await dispatcher7.InvokeAsync(() => { });

var parseAfterResume = Interlocked.CompareExchange(ref parseCount, 0, 0);
var fetchRows = await dispatcher7.InvokeAsync(() => task7.Episodes
    .Select(e => (e.Number, e.StatusText)).ToList());
var fetchTotal = await dispatcher7.InvokeAsync(() => task7.TotalEpisodes);
var fetchState = await dispatcher7.InvokeAsync(() => task7.State);

// 新集的产物必须真的在磁盘上 —— "续下"不是只改了行上的字
var ep4Output = await dispatcher7.InvokeAsync(() =>
    task7.Episodes.FirstOrDefault(e => e.Number == 4)?.OutputPath);
var ep4OnDisk = ep4Output is not null && File.Exists(ep4Output);
var ep3InTask = fetchRows.Any(r => r.Number == 3);

// 再预检：用户没勾的第 3 集应该还在候选里（说明它确实没被下）
var preview2 = await manager7.PreviewFetchNewAsync(task7);
var preview2Pending = preview2?.Candidates.Where(c => !c.IsDownloaded).Select(c => c.Number).ToList()
                      ?? new List<int>();

// 这次补勾第 3 集
var fetched2 = await manager7.ResumeAsync(task7, includeNewEpisodes: true,
    preview: preview2, onlyNumbers: new HashSet<int> { 3 });
await WaitUntilAsync(async () => await dispatcher7.InvokeAsync(() => task7.IsFinished),
    TimeSpan.FromMinutes(2), "补下第 3 集跑完");
await dispatcher7.InvokeAsync(() => { });

var finalRows = await dispatcher7.InvokeAsync(() => task7.Episodes
    .Select(e => (e.Number, e.StatusText)).ToList());
var finalTotal = await dispatcher7.InvokeAsync(() => task7.TotalEpisodes);
var preview3 = await manager7.PreviewFetchNewAsync(task7);

Console.WriteLine($"  预检: 已下载置灰 {string.Join("、", preview1Grey)}；待补 " +
                  $"{string.Join("、", preview1Pending.Select(c => $"第{c.Item1}集({(c.Item2 ? "新增" : "未下完")})"))}");
Console.WriteLine($"  只勾第 4 集续下 → 任务 {fetchRows.Count} 行 / 总集数 {fetchTotal}：{string.Join("、", fetchRows.Select(r => $"第{r.Number}集"))}");
Console.WriteLine($"    第 4 集产物在磁盘: {ep4OnDisk}；第 3 集被下进来了吗（应为 False）: {ep3InTask}");
Console.WriteLine($"  页面解析次数: 预检后 {parseAfterPreview}、续下后 {parseAfterResume}（续下复用预检结果，不该再解析）");
Console.WriteLine($"  再预检待补: {string.Join("、", preview2Pending)}（第 3 集应仍在）");
Console.WriteLine($"  补勾第 3 集后续下 → {finalRows.Count} 行 / {finalTotal} 集，待补剩 {preview3?.PendingCount} 项");

var okFetchNew = preview1?.NewCount == 2
                 && preview1Grey.OrderBy(n => n).SequenceEqual(new[] { 1, 2 })
                 && preview1Pending.Select(c => c.Item1).OrderBy(n => n).SequenceEqual(new[] { 3, 4 })
                 && fetched
                 && fetchRows.Count == 3
                 && fetchRows.All(e => e.StatusText == "已完成")
                 && fetchTotal == 3
                 && fetchState == SeriesTaskState.Completed
                 && ep4OnDisk
                 && !ep3InTask
                 && parseAfterPreview == 1
                 && parseAfterResume == 1        // 复用预检结果，没有再抓页面
                 && preview2Pending.SequenceEqual(new[] { 3 })
                 && fetched2
                 && finalRows.Count == 4
                 && finalTotal == 4
                 && finalRows.All(e => e.StatusText == "已完成")
                 && preview3 is { PendingCount: 0 };
Console.WriteLine($"  续下更新: {(okFetchNew ? "✔" : "✘")}");

// ---------------------------------------------------------------- 阶段 O：已下载置灰（磁盘优先）
//
// 「续下时把已下载的置灰」—— 判据必须是**磁盘上的文件**：
// 任务记录会被「清理已完成」清掉、重装程序会丢，视频却还躺在文件夹里。
// 任务记录与下载历史只用来解释"为什么没有"（上次失败 / 曾下载过但文件已不在）。

Console.WriteLine();
Console.WriteLine("阶段 O：续下置灰（磁盘上的文件说了算）");

// O1：扫描器本身 —— .mp4 也算、0 字节不算、集号补零与不补零都要对
var scanDir = Path.Combine(runRoot, "scan");
Directory.CreateDirectory(scanDir);
var scanSeries = BuildSeries(4);
File.WriteAllText(Path.Combine(scanDir, "自检剧集.01.ts"), "x");
File.WriteAllText(Path.Combine(scanDir, "自检剧集.02.mp4"), "xx");            // 转过 MP4 的产物
File.WriteAllBytes(Path.Combine(scanDir, "自检剧集.03.ts"), Array.Empty<byte>());  // 0 字节：不算下过

var scanned = EpisodeFileScanner.ScanExisting(scanDir, "{title}.{number:00}", scanSeries, new[] { 1, 2, 3, 4 });
var scannedNoPad = EpisodeFileScanner.ScanExisting(scanDir, "{title}.{number}", scanSeries, new[] { 1, 2 });
Console.WriteLine($"  扫描目录命中: {string.Join("、", scanned.Keys.OrderBy(n => n))}（应 1、2：3 是 0 字节、4 不存在）");
Console.WriteLine($"  模板 {{number}}（不补零）命中: {string.Join("、", scannedNoPad.Keys.OrderBy(n => n))}（应空：文件名是补零的）");

// O2：任务把 4 集都下完之后预检 —— 站点上的集都该是「已下载」
var previewDone = await manager7.PreviewFetchNewAsync(task7);
Console.WriteLine($"  全部下完后预检: 候选 {previewDone?.Candidates.Count} 项，" +
                  $"已下载 {previewDone?.DownloadedCount}、待补 {previewDone?.PendingCount}（应 4 / 0）");

// O3：站点更新出第 5 集；历史里说第 5 集下过、但文件已不在 → 说法要准确
siteEpisodeCount = 5;
manager7.History = new MemoryHistory(new[]
{
    new DownloadHistoryEntry
    {
        PageUrl = task7.PageUrl,
        SiteName = "本地测试站",
        SeriesTitle = "自检剧集",
        EpisodeNumber = 5,
        FilePath = Path.Combine(scanDir, "自检剧集.05.ts"),
        FileBytes = 5 * 1024 * 1024,     // 当初下出来 5 MB —— 这个大小要能显示出来
    },
});
var previewHistory = await manager7.PreviewFetchNewAsync(task7);
Console.WriteLine($"  站点多出第 5 集（历史里有记录、文件不在）后预检:");
foreach (var c in previewHistory?.Candidates ?? new List<FetchNewCandidate>())
    Console.WriteLine($"    第{c.Number:00}集 {c.Note}{(c.IsDownloaded ? "（置灰，不可勾）" : "")}");

// O4：下载完成后必须**自动记账**（"每次下载记住下载了哪些视频"）
var histDir = Path.Combine(runRoot, "hist");
Directory.CreateDirectory(histDir);

var recorded = new MemoryHistory();
var dispatcher8 = new FakeDispatcher();
using var manager8 = new DownloadTaskManager(null, dispatcher8.Post) {
    SeriesParser = (_, _) => Task.FromResult(BuildSeries(3)),
    History = recorded,
};

var task8 = manager8.Enqueue(BuildSeries(3), new SeriesDownloadOptions {
    OutputDirectory = histDir,
    EpisodeConcurrency = 2,
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
});
await dispatcher8.InvokeAsync(() => { });

var histDone = new TaskCompletionSource();
task8.PropertyChanged += (_, e) => {
    if (e.PropertyName == nameof(SeriesTask.State) && task8.IsFinished) histDone.TrySetResult();
};
await WaitUntilAsync(async () => await dispatcher8.InvokeAsync(() => task8.IsRunning),
    TimeSpan.FromSeconds(30), "任务开始下载");
await histDone.Task.WaitAsync(TimeSpan.FromMinutes(2));
await dispatcher8.InvokeAsync(() => { });

var histEntries = recorded.All();
Console.WriteLine($"  下载 3 集自动记账: {histEntries.Count} 条 → " +
                  $"{string.Join("、", histEntries.OrderBy(e => e.EpisodeNumber).Select(e => $"第{e.EpisodeNumber}集({e.FileBytes}B)"))}");
var histFilesOk = histEntries.All(e => e.FilePath is not null && File.Exists(e.FilePath));

// O5：把任务**移除**掉 —— 这部剧的下载历史要跟着一起销。
// 任务都删了、账还留着的话，下次再下这部剧会凭空冒出"当初下过、文件已不在"的提示，
// 而那个文件是用户自己连同任务一起清掉的。
// （「清理已完成」不走这条：那只是收拾列表，视频还在盘上，账得留着。）
manager8.Remove(task8);
await WaitUntilAsync(() => Task.FromResult(recorded.All().Count == 0),
    TimeSpan.FromSeconds(5), "移除任务后历史被销掉");

var histAfterRemove = recorded.All().Count;
Console.WriteLine($"  移除任务后历史剩余: {histAfterRemove} 条（应 0）");

var okDiskFirst = scanned.Keys.OrderBy(n => n).SequenceEqual(new[] { 1, 2 })
                  && scannedNoPad.Count == 0
                  && previewDone is { DownloadedCount: 4, PendingCount: 0 }
                  && previewDone.Candidates.All(c => c.IsDownloaded && c.Note == "已下载" && c.FilePath is not null)
                  && previewHistory is { DownloadedCount: 4 }
                  && previewHistory.Candidates.SingleOrDefault(c => c.Number == 5)
                      is { IsDownloaded: false, Note: "曾下载过（5.0 MB），文件已不在" }
                  && histEntries.Count == 3
                  && histEntries.Select(e => e.EpisodeNumber).OrderBy(n => n).SequenceEqual(new[] { 1, 2, 3 })
                  && histEntries.All(e => e.SeriesTitle == "自检剧集" && e.PageUrl == task8.PageUrl)
                  && histEntries.All(e => e.FileBytes > 0)
                  && histFilesOk
                  && histAfterRemove == 0;
Console.WriteLine($"  已下载置灰 + 自动记账 + 移除销账: {(okDiskFirst ? "✔" : "✘")}");

// ---------------------------------------------------------------- 阶段 P：站点快照（续下不联网）
//
// 首轮入队时就把"站点上全部集"的元数据存下来（不只用户勾的那几集）：
// 续下优先用这份快照 —— 解析器一次都不该被调用（下面故意让它抛异常来证明），
// 所以断网、站点挂了、代理没开时照样能续下。
// 站点解析失败时，联网预检要**降级**用快照而不是直接报错。

Console.WriteLine();
Console.WriteLine("阶段 P：站点快照（续下不必联网）");

var snapDir = Path.Combine(runRoot, "snapshot");
Directory.CreateDirectory(snapDir);

// 只勾第 2 集：快照里仍应有全部 3 集，请求头也要一起存（下载分片要带 Referer）
var snapshotSeries = BuildSeries(3);
foreach (var e in snapshotSeries.AllEpisodes) e.IsSelected = e.Number == 2;
snapshotSeries.Headers["Referer"] = "https://example.com/";
snapshotSeries.Headers["Origin"] = "https://example.com";

var snapSiteCount = 3;
var snapParseCalls = 0;
var snapAllowParse = false;   // 先禁止联网：走快照路径时一次都不该解析页面

var snapStorePath = Path.Combine(snapDir, "m3u8.db");
var dispatcher9 = new FakeDispatcher();
using var manager9 = new DownloadTaskManager(null, dispatcher9.Post, new TaskStore(snapStorePath)) {
    SeriesParser = (_, _) => {
        Interlocked.Increment(ref snapParseCalls);
        if (!snapAllowParse) throw new InvalidOperationException("这条路径不该联网");
        return Task.FromResult(BuildSeries(snapSiteCount));
    },
};

var task9 = manager9.Enqueue(snapshotSeries, new SeriesDownloadOptions {
    OutputDirectory = snapDir,
    EpisodeConcurrency = 1,
    SegmentConcurrency = 2,
    FfmpegPath = null,
    WriteReport = true,
    SeriesSubdirectory = true,
    MaxRetries = 1,
    RetryBaseDelayMs = 200,
});
await dispatcher9.InvokeAsync(() => { });

var snapDone = new TaskCompletionSource();
task9.PropertyChanged += (_, e) => {
    if (e.PropertyName == nameof(SeriesTask.State) && task9.IsFinished) snapDone.TrySetResult();
};
await WaitUntilAsync(async () => await dispatcher9.InvokeAsync(() => task9.IsRunning),
    TimeSpan.FromSeconds(30), "任务开始下载");
await snapDone.Task.WaitAsync(TimeSpan.FromMinutes(2));
await dispatcher9.InvokeAsync(() => { });

var snapEpisodes = await dispatcher9.InvokeAsync(() =>
    task9.Snapshot?.Episodes.Select(m => m.Number).ToList() ?? new List<int>());
var snapHeaders = await dispatcher9.InvokeAsync(() => task9.Snapshot?.Headers.Count ?? 0);
var taskEpisodes = await dispatcher9.InvokeAsync(() => task9.Episodes.Count);
Console.WriteLine($"  首轮只勾第 2 集 → 任务清单 {taskEpisodes} 行；快照 {snapEpisodes.Count} 集" +
                  $"（{string.Join("、", snapEpisodes)}）、请求头 {snapHeaders} 个");

// 1) 快照预检：完全不联网
var snapPreview = await manager9.PreviewFromSnapshotAsync(task9);
var snapGrey = snapPreview?.Candidates.Where(c => c.IsDownloaded).Select(c => c.Number).ToList() ?? new List<int>();
var snapPending = snapPreview?.Candidates.Where(c => !c.IsDownloaded).Select(c => c.Number).ToList() ?? new List<int>();
var snapCallsAfter = Interlocked.CompareExchange(ref snapParseCalls, 0, 0);
Console.WriteLine($"  快照预检：共 {snapPreview?.Candidates.Count} 项，已下载置灰 " +
                  $"{string.Join("、", snapGrey)}，待补 {string.Join("、", snapPending)}");
Console.WriteLine($"  这一步的页面解析次数：{snapCallsAfter}（应为 0）");

// 2) 站点解析不了时，联网预检要降级用快照，而不是直接报错
FetchNewPreview? degraded = null;
string? degradedError = null;
try {
    degraded = await manager9.PreviewFetchNewAsync(task9);
} catch (Exception ex) {
    degradedError = ex.Message;
}
Console.WriteLine($"  站点解析失败时：{(degraded is null ? "抛异常 ✘（" + degradedError + "）" : "降级用快照 ✔")}" +
                  $"（Refreshed={degraded?.Refreshed}，原因：{degraded?.RefreshError}）");

// 3) 站点更新出第 4 集 → 刷新应报告 1 个新集，并把快照扩到 4 集
snapAllowParse = true;
snapSiteCount = 4;
var refreshResult = await manager9.RefreshEpisodesSnapshotAsync(task9);
var snapAfterRefresh = await dispatcher9.InvokeAsync(() => task9.Snapshot?.Episodes.Count ?? 0);
Console.WriteLine($"  刷新快照：站点 {refreshResult.TotalOnSite} 集，新发现 " +
                  $"{string.Join("、", refreshResult.NewCandidates.Select(c => c.Number))}；快照现 {snapAfterRefresh} 集");

// 4) 落盘 → 换一个管理器恢复：快照必须还在（否则重启后又得联网）
await dispatcher9.InvokeAsync(() => manager9.SaveNow());
var dispatcher10 = new FakeDispatcher();
using var manager10 = new DownloadTaskManager(null, dispatcher10.Post, new TaskStore(snapStorePath));
var snapRestoredCount = await manager10.RestoreAsync();
var restoredSnapshot = await dispatcher10.InvokeAsync(() =>
    manager10.Tasks.FirstOrDefault()?.Snapshot);
await dispatcher10.InvokeAsync(() => { });
Console.WriteLine($"  重启恢复 {snapRestoredCount} 个任务：快照 {restoredSnapshot?.Episodes.Count} 集、" +
                  $"请求头 {restoredSnapshot?.Headers.Count} 个");

// 5) 用快照续下时某集失败了 → 收尾应**自动重新拉取元数据**（很可能是站点改版让地址失效）
//    这里把第 1 集的播放页换成一个连不上的地址，让这一集必然失败
await dispatcher9.InvokeAsync(() => {
    var broken = task9.Snapshot!.Episodes.First(e => e.Number == 1);
    broken.PageUrl = "http://127.0.0.1:1/never.html";
});

var failPreview = await manager9.PreviewFromSnapshotAsync(task9);
var callsBeforeFail = Interlocked.CompareExchange(ref snapParseCalls, 0, 0);
var failRoundStarted = await manager9.ResumeAsync(task9, includeNewEpisodes: true,
    preview: failPreview, onlyNumbers: new HashSet<int> { 1 });
await WaitUntilAsync(async () => await dispatcher9.InvokeAsync(() => task9.IsFinished),
    TimeSpan.FromMinutes(2), "失败的那一轮跑完");
await dispatcher9.InvokeAsync(() => { });
var callsAfterFail = Interlocked.CompareExchange(ref snapParseCalls, 0, 0);
var failRow = await dispatcher9.InvokeAsync(() =>
    task9.Episodes.FirstOrDefault(e => e.Number == 1)?.StatusText ?? "");
var failMessage = await dispatcher9.InvokeAsync(() => task9.Message ?? "");
var refreshedAfterFail = await dispatcher9.InvokeAsync(() => task9.Snapshot?.CapturedAt);

Console.WriteLine($"  用快照续下第 1 集（地址已失效）→ {failRow}；" +
                  $"收尾刷新元数据：解析次数 {callsBeforeFail} → {callsAfterFail}");
Console.WriteLine($"    提示：{failMessage}");

var okSnapshot = snapEpisodes.Count == 3
                 && snapEpisodes.OrderBy(n => n).SequenceEqual(new[] { 1, 2, 3 })
                 && taskEpisodes == 1                       // 任务清单仍只有勾选的那一集
                 && snapHeaders >= 2                        // 请求头必须存下来（否则续下会 403）
                 && snapPreview is { Candidates.Count: 3 }
                 && snapGrey.SequenceEqual(new[] { 2 })     // 已下好的那集置灰
                 && snapPending.OrderBy(n => n).SequenceEqual(new[] { 1, 3 })
                 && snapCallsAfter == 0                     // ★ 快照预检一次都没联网
                 && degraded is { Refreshed: false }        // 解析失败时降级
                 && !string.IsNullOrWhiteSpace(degraded.RefreshError)
                 && degraded.Candidates.Count == 3
                 && refreshResult.NewCandidates.Count == 1
                 && refreshResult.NewCandidates[0].Number == 4
                 && refreshResult.TotalOnSite == 4
                 && snapAfterRefresh == 4
                 && restoredSnapshot?.Episodes.Count == 4
                 && (restoredSnapshot?.Headers.Count ?? 0) >= 2
                 && failRoundStarted                       // 用快照续下
                 && failRow == "失败"                       // 那一集确实失败
                 && callsAfterFail > callsBeforeFail        // ★ 失败后自动刷新了元数据
                 && refreshedAfterFail is not null
                 && failMessage.Contains("已重新拉取集列表");
Console.WriteLine($"  站点快照: {(okSnapshot ? "✔" : "✘")}");

// ---------------------------------------------------------------- 阶段 Q：统一库（真 SQLite）
//
// 存储实现搬进 Core 之后，自检终于能直接测真货 —— 以前它住在界面层，
// 自检只碰得到 MemoryHistory，SQL 语句得靠一个一次性的 _diag 项目单独兜。

Console.WriteLine();
Console.WriteLine("阶段 Q：统一库（真 SQLite：下载历史 + 设置 + 任务）");

var dbDir = Path.Combine(runRoot, "db");
Directory.CreateDirectory(dbDir);

const string urlQ = "https://example.com/vodplay/9-1-1.html";
const string urlR = "https://example.com/vodplay/8-1-1.html";

var dbPath = Path.Combine(dbDir, "m3u8.db");
var historyQ = new SqliteDownloadHistory(new SqliteDatabase(dbPath));

for (var n = 1; n <= 3; n++) {
    historyQ.Record(new DownloadHistoryEntry {
        PageUrl = urlQ,
        SiteName = "测试站",
        SeriesTitle = "测试剧",
        EpisodeNumber = n,
        FilePath = $@"D:\x\测试剧.{n:00}.mp4",
        FileBytes = 100 + n,
    });
}

// upsert：同一集重下不能变成两行
historyQ.Record(new DownloadHistoryEntry {
    PageUrl = urlQ,
    SiteName = "测试站",
    SeriesTitle = "测试剧",
    EpisodeNumber = 2,
    FileBytes = 999,
});
historyQ.Record(new DownloadHistoryEntry {
    PageUrl = urlR,
    SiteName = "测试站",
    SeriesTitle = "另一部剧",
    EpisodeNumber = 1,
    FileBytes = 5,
});

var qUpsert = historyQ.FindBySeries(urlQ);
var qEp2 = qUpsert.Single(e => e.EpisodeNumber == 2);
var qOther = historyQ.FindBySeries(urlR).Count;
var qAll = historyQ.All().Count;
Console.WriteLine($"  记 3 集 + 重下第 2 集 + 另一部剧 1 集: 本剧 {qUpsert.Count} 条（应 3）、" +
                  $"第 2 集 {qEp2.FileBytes}B（应 999）、另一部剧 {qOther} 条（应 1）、All {qAll} 条（应 4）");

// 销账：整部走，不带走别的剧；空地址不能变成"清空全表"
historyQ.Forget(urlQ);
var qForget = historyQ.FindBySeries(urlQ).Count;
var qOtherAfter = historyQ.FindBySeries(urlR).Count;
historyQ.Forget("");
historyQ.Forget("   ");
var qAllAfter = historyQ.All().Count;
Console.WriteLine($"  销账: 本剧 {qForget} 条（应 0）、另一部剧 {qOtherAfter} 条（应 1）；" +
                  $"空地址后 All {qAllAfter} 条（应 1）");

// 真落盘：重开一个实例（重开连接）还读得到
var qReopened = new SqliteDownloadHistory(dbPath).FindBySeries(urlR).Count;
Console.WriteLine($"  重开实例后另一部剧仍 {qReopened} 条（应 1）");

// 设置：写一轮非默认值再读回来（一行一列一项，缺项/空值退回默认值）
var settingsPath = Path.Combine(dbDir, "settings.db");
var qDefaults = new SqliteSettingsStore(new SqliteDatabase(settingsPath)).Load();
var qWritten = new AppSettings {
    FfmpegPath = @"D:\tools\ffmpeg.exe",
    DefaultOutputDirectory = @"D:\视频",
    EpisodeConcurrency = 5,
    SegmentConcurrency = 32,
    AutoSkipInvalidSegments = false,
    SeriesSubdirectory = false,
    FullDecodeCheck = false,
    MinimizeToTrayOnClose = false,
    UserAgent = "TestAgent/1.0",
    ProxyEnabled = true,
    ProxyUrl = "http://127.0.0.1:7897",
    CheckUpdateOnStartup = true,
};
var qSaved = new SqliteSettingsStore(new SqliteDatabase(settingsPath)).Save(qWritten);
// 重开实例再读：走的是真库不是内存
var qReadBack = new SqliteSettingsStore(new SqliteDatabase(settingsPath)).Load();

var qDefaultsOk = qDefaults.EpisodeConcurrency == 2 && qDefaults.SegmentConcurrency == 16
                  && qDefaults.AutoSkipInvalidSegments && qDefaults.SeriesSubdirectory
                  && qDefaults.FullDecodeCheck && qDefaults.MinimizeToTrayOnClose
                  && !qDefaults.ProxyEnabled && !qDefaults.CheckUpdateOnStartup
                  && qDefaults.FfmpegPath is null;
var qRoundTrip = qSaved
                 && qReadBack.FfmpegPath == qWritten.FfmpegPath
                 && qReadBack.DefaultOutputDirectory == qWritten.DefaultOutputDirectory
                 && qReadBack.FfmpegDownloadUrl is null
                 && qReadBack.EpisodeConcurrency == 5 && qReadBack.SegmentConcurrency == 32
                 && !qReadBack.AutoSkipInvalidSegments && !qReadBack.SeriesSubdirectory
                 && !qReadBack.FullDecodeCheck && !qReadBack.MinimizeToTrayOnClose
                 && qReadBack.UserAgent == "TestAgent/1.0"
                 && qReadBack.ProxyEnabled && qReadBack.ProxyUrl == "http://127.0.0.1:7897"
                 && qReadBack.CheckUpdateOnStartup;
Console.WriteLine($"  设置写读往返: 空库读回默认值 {qDefaultsOk}；写 12 项后读回一致 {qRoundTrip}");

// 任务列表：带快照的和不带快照的各一个，存进去再读回来（四张表拆开存、组装回来）
var taskDbPath = Path.Combine(dbDir, "tasks.db");
var qTaskIn = new List<SeriesTaskRecord>
{
    new()
    {
        Id = "t1", Title = "测试剧", SiteName = "测试站", PageUrl = "https://example.com/1-1-1.html",
        OutputDirectory = @"D:\视频", ResolvedDirectory = @"D:\视频\测试剧 - 测试站",
        SourceName = "lzm3u8", PreferredSourceId = 3,
        State = nameof(SeriesTaskState.Completed), Percent = 100, DownloadedBytes = 123456,
        ReportPath = @"D:\视频\报告.md", Message = "完成", SkippedAdSegments = 3,
        CreatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
        FinishedAt = DateTimeOffset.Parse("2026-01-02T04:05:06+08:00"),
        EpisodeConcurrency = 3, SegmentConcurrency = 8, PreferHeight = 1080,
        FfmpegPath = @"D:\tools\ffmpeg.exe", FileNamePattern = "{title}.{number:00}",
        SeriesSubdirectory = false,
        Episodes =
        {
            new TaskEpisodeRecord
            {
                Number = 1, Title = "第01集", Status = nameof(EpisodeDownloadStatus.Completed),
                Percent = 100, Bytes = 999, OutputPath = @"D:\视频\a.mp4",
            },
            new TaskEpisodeRecord
            {
                Number = 2, Title = "第02集", Status = nameof(EpisodeDownloadStatus.Failed),
                Percent = 42.5, Error = "连接超时",
            },
        },
        Snapshot = new SeriesSnapshot
        {
            Kind = nameof(SiteKind.Generic), SiteName = "测试站", PageUrl = "https://example.com/1-1-1.html",
            SeriesId = "42", Title = "测试剧", SourceId = 3,
            CapturedAt = DateTimeOffset.Parse("2026-01-02T03:04:05+08:00"),
            Headers = new Dictionary<string, string>
            {
                ["Referer"] = "https://example.com/",
                ["User-Agent"] = "UA/1.0",
            },
            Episodes =
            {
                new EpisodeMetadata { Number = 1, Title = "第01集", PageUrl = "https://example.com/1-1-1.html", Key = "k1", SourceId = 3 },
                new EpisodeMetadata { Number = 2, Title = "第02集", PageUrl = "https://example.com/1-1-2.html", SourceId = 3 },
                new EpisodeMetadata { Number = 3, Title = "第03集", PageUrl = "https://example.com/1-1-3.html", Key = "ep3", SourceId = 3 },
            },
        },
    },
    new()
    {
        Id = "t2", Title = "没快照的剧", SiteName = "测试站", PageUrl = "https://example.com/2-1-1.html",
        OutputDirectory = @"D:\视频", State = nameof(SeriesTaskState.Paused),
        CreatedAt = DateTimeOffset.Now,
    },
};

var qTaskSaved = new SqliteTaskStore(new SqliteDatabase(taskDbPath)).Save(qTaskIn);
var qTaskOut = new SqliteTaskStore(new SqliteDatabase(taskDbPath)).Load();
var qT1 = qTaskOut.FirstOrDefault(r => r.Id == "t1");
var qT2 = qTaskOut.FirstOrDefault(r => r.Id == "t2");

var qTasksRoundTrip = qTaskSaved
    && qTaskOut.Count == 2
    && qTaskOut[0].Id == "t1" && qTaskOut[1].Id == "t2"        // 列表顺序要保住
    && qT1 is {
        Title: "测试剧", State: "Completed", Percent: 100, DownloadedBytes: 123456,
        SkippedAdSegments: 3, SeriesSubdirectory: false, EpisodeConcurrency: 3,
        SegmentConcurrency: 8, PreferHeight: 1080, PreferredSourceId: 3,
        SourceName: "lzm3u8", ReportPath: not null, FinishedAt: not null,
    }
    && qT1.Episodes.Count == 2
    && qT1.Episodes[0] is { Number: 1, OutputPath: not null, Bytes: 999 }
    && qT1.Episodes[1] is { Number: 2, Percent: 42.5, Error: "连接超时", OutputPath: null }
    && qT1.Snapshot is { SeriesId: "42", SourceId: 3 }
    && qT1.Snapshot.Headers.Count == 2
    && qT1.Snapshot.Headers["Referer"] == "https://example.com/"
    && qT1.Snapshot.Episodes.Count == 3
    && qT1.Snapshot.Episodes[2] is { Number: 3, Key: "ep3" }
    && qT2 is { State: "Paused", Snapshot: null };
Console.WriteLine($"  任务存取往返: 存 2 个（1 个带 3 集快照）读回 {qTaskOut.Count} 个、" +
                  $"第一个 {qT1?.Episodes.Count} 集/快照 {qT1?.Snapshot?.Episodes.Count} 集 → {qTasksRoundTrip}");

// 记账规则（DownloadHistoryRecorder：只记「完成 + 有产物路径」的集）：
// 只有「已完成 + 产物路径非空」的集进历史；失败的不记，完成了却没产物路径的也不记
var recorderDbPath = Path.Combine(dbDir, "recorder.db");
var recorderHistory = new SqliteDownloadHistory(new SqliteDatabase(recorderDbPath));
var productFile = Path.Combine(dbDir, "记账剧.01.mp4");
File.WriteAllText(productFile, new string('x', 2048));

DownloadHistoryRecorder.Record(recorderHistory, "https://example.com/5-1-1.html", "测试站", "记账剧", new[]
{
    new EpisodeDownloadReport
    {
        Episode = new SiteEpisode { Number = 1, SourceId = 1, PageUrl = "u1", Title = "第01集" },
        Status = EpisodeDownloadStatus.Completed,
        OutputPath = productFile,
    },
    new EpisodeDownloadReport
    {
        Episode = new SiteEpisode { Number = 2, SourceId = 1, PageUrl = "u2", Title = "第02集" },
        Status = EpisodeDownloadStatus.Failed,               // 失败 → 不记
        OutputPath = Path.Combine(dbDir, "不该记账.02.mp4"),
    },
    new EpisodeDownloadReport
    {
        Episode = new SiteEpisode { Number = 3, SourceId = 1, PageUrl = "u3", Title = "第03集" },
        Status = EpisodeDownloadStatus.Completed,            // 完成了但没有产物路径 → 也不记
    },
});

var qRecorded = recorderHistory.FindBySeries("https://example.com/5-1-1.html");
var qRecorderRule = qRecorded.Count == 1
                    && qRecorded[0] is {
                        EpisodeNumber: 1, SeriesTitle: "记账剧", SiteName: "测试站", FileBytes: 2048,
                    };
Console.WriteLine($"  记账规则（只记完成且有产物的）: {qRecorded.Count} 条 → {qRecorderRule}");

var okUnified = qUpsert.Count == 3
                && qUpsert.Select(e => e.EpisodeNumber).SequenceEqual(new[] { 1, 2, 3 })
                && qEp2.FileBytes == 999
                && qOther == 1 && qAll == 4
                && qForget == 0 && qOtherAfter == 1 && qAllAfter == 1
                && qReopened == 1
                && qDefaultsOk && qRoundTrip
                && qTasksRoundTrip && qRecorderRule;
Console.WriteLine($"  统一库: {(okUnified ? "✔" : "✘")}");

// ---------------------------------------------------------------- 阶段 R：站内搜索解析
//
// 全是**离线的假页面**，结构照抄真实站点（2026-09 抓下来的原样）——
// 自检不碰外网，所以只能把真实页面的骨架搬进来守住解析规则。
//
// 守的是这几条（都真被坑过）：
//   1. 影迷界影院的搜索结果条目是 /video/{id}.html，不是 /t/{id}.html ——
//      漏了 /video/ 这个前缀，真正搜到的那部剧会被整个滤掉，只剩页脚推荐位；
//   2. /play/{id}/{sid}/{nid}.html 这种斜杠播放页**不是**详情页，不能当结果收；
//   3. 推荐位那种纯文本链接没有「结果条目」特征（海报 alt / 标题 div），严格解析必须不认。

Console.WriteLine();
Console.WriteLine("阶段 R：站内搜索解析（离线假页面）");

var searchRoot = new Uri("https://www.example.com");

// --- 影迷界影院：真命中 1 条（/video/）+ 推荐位 2 条（/t/，纯文本）---
const string wakuredoSearchPage = """
    <html><body>
    <form action="/search.html" method="get"><input name="wd" type="text"></form>
    <div class="list">
      <a class="c" href="/video/2337178967.html" title="交锋">
        <div class="pic"><img src="https://img.example.com/a.jpg" alt="交锋" loading="lazy">
          <span class="ep">更新至第25集</span><span class="sc">★9.0</span></div>
        <div class="nm">交锋</div>
        <div class="m">当代国安剧 · 2026</div>
      </a>
    </div>
    <div class="recommend">
      <a href="/t/482750.html">他们的谎言短剧免费播放</a>
      <a href="/t/392408.html">带着空间养兽夫我成团宠了电视剧在线观看免费星辰</a>
    </div>
    </body></html>
    """;

// --- 青苹果影院：真命中 2 条（/voddetail/ + 海报 alt），外加一条斜杠播放页做反例 ---
//
// 这张卡片**故意照真实页面抄**：同一个详情页在卡片里有 5 个链接（海报、更新至04集、
// 剧名、导演、简介）。划分"这一条的范围"如果拿"下一个链接"当边界，就会在第二个同 URL
// 的链接处截断 —— 海报、标签、简介一个都取不到。
const string qmaoSearchPage = """
    <html><body>
    <form action="/vodsearch/-------------.html"><input name="wd" type="text"></form>
    <div class="TagBookList_tagBookBox">
    <div class="TagBookList_tagItem">
      <a class="image_imageScaleBox TagBookList_bookImageBox" href="/voddetail/30450.html">
        <img alt="交锋" loading="lazy" width="184" height="264" src="/upload/vod/a.webp"></a>
      <a class="TagBookList_totalChapterNum" href="/voddetail/30450.html">更新至04集</a>
      <div class="TagBookList_bookInfo">
        <a class="TagBookList_bookName" href="/voddetail/30450.html"><b>交锋</b></a>
        <a class="TagBookList_bookAuthor" href="/voddetail/30450.html">
          <span>姚晓峰</span><span>王凯 , 周依然</span></a>
        <div class="TagBookList_tagsBox">
          <a href="/vodsearch/----国产---------.html" target="_blank">国产</a>&nbsp;</div>
        <a class="TagBookList_intro" href="/voddetail/30450.html">　故事由一宗世纪之交的泄密大案而起。</a>
      </div>
    </div>
    <div class="TagBookList_tagItem">
      <a class="TagBookList_bookImageBox" href="/voddetail/31779.html">
        <img alt="权力交锋" loading="lazy" src="/upload/vod/b.webp"></a>
      <a class="TagBookList_totalChapterNum" href="/voddetail/31779.html">全9集</a>
      <div class="TagBookList_bookInfo">
        <a class="TagBookList_bookName" href="/voddetail/31779.html"><b>权力交锋</b></a>
        <div class="TagBookList_tagsBox"><a href="/vodsearch/x.html">海外</a></div>
        <a class="TagBookList_intro" href="/voddetail/31779.html">母女之间的一场碰撞。</a>
      </div>
    </div>
    </div>
    <div class="episodes">
      <a href="/play/2337178967/7/1.html"><img alt="第01集" src="/x.jpg"></a>
    </div>
    </body></html>
    """;

// --- 反例：没有搜索表单的站（连搜索框都没有）---
const string noSearchPage = """
    <html><body><div class="hero">本站不提供搜索</div>
    <a href="/voddetail/1.html">某部剧</a></body></html>
    """;

var rWakuredo = SiteSearch.ParseStrict(wakuredoSearchPage, searchRoot);
var rWakuredoLoose = SiteSearch.ParseLoose(wakuredoSearchPage, searchRoot);
var rQmao = SiteSearch.ParseStrict(qmaoSearchPage, searchRoot);
var rFormWakuredo = SiteSearch.FindSearchForm(wakuredoSearchPage);
var rFormNone = SiteSearch.FindSearchForm(noSearchPage);
var rSearchUrl = SiteSearch.BuildSearchUrl(searchRoot, "/search.html", "wd", "交锋 第2季");

// 严格解析只留真命中：影迷界 1 条（推荐位的 /t/ 没有条目特征，滤掉）；
// 青苹果 2 条（/play/ 那条是播放页，不算结果 —— 少了斜杠播放页的排除就会多出"第01集"）
var rWakuredoOk = rWakuredo.Count == 1
                  && rWakuredo[0].Title == "交锋"
                  && rWakuredo[0].PageUrl == "https://www.example.com/video/2337178967.html";
var rQmaoOk = rQmao.Count == 2
              && rQmao[0].Title == "交锋"
              && rQmao[0].PageUrl == "https://www.example.com/voddetail/30450.html"
              && rQmao[1].Title == "权力交锋";

// 卡片上的额外信息：海报、角标、类型、简介。缺任何一个，弹窗里就只剩剧名，
// 而搜「交锋」出来的一堆同名剧光看剧名分不清 —— 这几条是"能不能认出是哪部"的关键。
//
// 角标还分两类，一正一反都要钉住：**"更新至04集"必须丢掉**（vod_remarks 是上传者手填的，
// 实测青苹果给《交锋》写"更新至04集"、而同一个详情页有第01集…第28集，站点自己就不一致），
// **"全9集"必须留着**（结论性标记，用来分辨电影还是剧、完结没有）。
var rEntryOk = rQmao[0] is {
    Badge: null,                            // 「更新至04集」→ 丢掉
    Note: "国产",
    PosterUrl: "https://www.example.com/upload/vod/a.webp",
    Intro: "故事由一宗世纪之交的泄密大案而起。",
}
               && rQmao[1] is {
                   Badge: "全9集",               // 结论性标记 → 留着
                   Note: "海外",
                   PosterUrl: "https://www.example.com/upload/vod/b.webp",
               }
               // 影迷界：角标在 span.ep（这条也是"更新至…"，同样丢掉）、类型在 div.m，评分拼在类型后面
               && rWakuredo[0] is {
                   Badge: null,
                   Note: "当代国安剧 · 2026 · ★9.0",
                   PosterUrl: "https://img.example.com/a.jpg",
               }
               // 宽松解析拿不准"这是不是结果条目"，所以不抓图，只留剧名
               && rWakuredoLoose.All(h => h.PosterUrl is null && h.Badge is null);

// 宽松解析会连推荐位一起收（28 条那种垃圾），但**剧名不能是状态文本**：
// 结果是 1 条真命中 + 2 条推荐位 = 3 条，且没有一条叫"更新至第25集"
var rLooseOk = rWakuredoLoose.Count == 3
               && rWakuredoLoose.All(h => !h.Title.Contains("更新至"));

var rFormOk = rFormWakuredo is { Action: "/search.html", Field: "wd" } && rFormNone is null;
var rUrlOk = rSearchUrl == "https://www.example.com/search.html?wd=%E4%BA%A4%E9%94%8B%20%E7%AC%AC2%E5%AD%A3";

Console.WriteLine($"  影迷界影院（/video/ 详情页）: 严格 {rWakuredo.Count} 条" +
                  $"（应 1）首发《{rWakuredo.FirstOrDefault()?.Title}》→ {rWakuredoOk}");
Console.WriteLine($"  青苹果影院（/voddetail/ + 海报 alt）: 严格 {rQmao.Count} 条（应 2，" +
                  $"斜杠播放页 /play/… 不算）→ {rQmaoOk}");
Console.WriteLine($"  卡片信息（海报 / 角标 / 类型 / 简介）: 《{rQmao[0].Title}》" +
                  $"类型={rQmao[0].Note} 简介={(rQmao[0].Intro is null ? "无" : "有")}" +
                  $" 海报={(rQmao[0].PosterUrl is null ? "无" : "有")} → {rEntryOk}");
Console.WriteLine($"  角标取舍（更新至…是手填的会过期→丢，全…集是结论→留）: " +
                  $"「更新至04集」→{(rQmao[0].Badge is null ? "已丢弃" : rQmao[0].Badge)}、" +
                  $"「全9集」→{rQmao[1].Badge ?? "（不该丢！）"}");
Console.WriteLine($"  宽松解析含推荐位但剧名不是状态文本: {rWakuredoLoose.Count} 条（应 3）→ {rLooseOk}");
Console.WriteLine($"  首页搜索表单: 影迷界 {rFormWakuredo?.Action}/{rFormWakuredo?.Field}、" +
                  $"没有搜索框的站 {(rFormNone is null ? "判为不支持" : "误判为支持")} → {rFormOk}");
Console.WriteLine($"  搜索地址拼接（关键词要转义）: {rSearchUrl} → {rUrlOk}");

var okSearch = rWakuredoOk && rQmaoOk && rEntryOk && rLooseOk && rFormOk && rUrlOk;
Console.WriteLine($"  站内搜索解析: {(okSearch ? "✔" : "✘")}");

// --- 站点清单（下拉框的数据源）：空库退回内置；写一轮再读回来，顺序和校验都要对 ---
var siteDbPath = Path.Combine(dbDir, "sites.db");
var rSiteStore = new SqliteSiteStore(new SqliteDatabase(siteDbPath));
var rSitesDefault = rSiteStore.Load();
var rSitesWritten = rSiteStore.Save(new[] {
    new SearchSite("站点一", "www.example.com"),           // 省了协议：要能自动补 https://
    new SearchSite("", "https://www.example.org"),         // 没填名字：显示时退回域名
    new SearchSite("空地址", ""),                           // 空地址 → 丢掉
    new SearchSite("协议不对", "ftp://www.example.com"),    // 不是 http(s) → 丢掉
    new SearchSite("站点三", "https://www.example.net"),
});
var rSitesBack = new SqliteSiteStore(new SqliteDatabase(siteDbPath)).Load();
var rSitesOk = rSitesDefault.Count == SiteCatalog.BuiltIn.Count
               && rSitesWritten
               && rSitesBack.Count == 3
               && rSitesBack[0].Root?.ToString() == "https://www.example.com/"
               && rSitesBack[1].Display == "https://www.example.org"
               && rSitesBack[2].Name == "站点三";
Console.WriteLine($"  站点清单: 空库退回内置 {rSitesDefault.Count} 个（应 {SiteCatalog.BuiltIn.Count}）；" +
                  $"写 5 条（空地址 / 协议不对各 1 条）读回 {rSitesBack.Count} 条、" +
                  $"顺序 {rSitesBack[0].Name}→{rSitesBack[^1].Name} → {rSitesOk}");

var okSiteCatalog = rSitesOk;
Console.WriteLine($"  站点清单存取: {(okSiteCatalog ? "✔" : "✘")}");

Console.WriteLine();
var ok = okB && okC && okPaused && okStopped && okResume && okSettled && okRetry
         && okResumeSubset && okFallback && okDuration && encOk && okSingle
         && okRegistry && okNnyy && okNnyyMovie && okGeneric && okEmpty && okWakuredo
         && okNoSystemProxy && okInserted && okInsertedGuard && okVersionCompare
         && okFetchNew && okDiskFirst && okSnapshot && okUnified && okSearch && okSiteCatalog;
Console.WriteLine(ok
    ? "自检结果       : ✔ 通过"
    : $"自检结果       : ✘ 失败（阶段B {okB} / 阶段C {okC} / 暂停 {okPaused} / 暂停后静止 {okStopped}" +
      $" / 续传 {okResume} / 续传后静止 {okSettled} / 重试 {okRetry}" +
      $" / 续传只下选中集 {okResumeSubset} / 多源兜底 {okFallback} / 时长核对 {okDuration}" +
      $" / 密文首字节 0x3C {encOk} / 单文件服务 {okSingle}" +
      $" / 适配器登记 {okRegistry} / 努努影院 {okNnyy} / 努努电影页 {okNnyyMovie} / 通用兜底 {okGeneric}" +
      $" / 空页面报错 {okEmpty} / 影迷界影院 {okWakuredo} / 直连不走代理 {okNoSystemProxy}" +
      $" / 插播广告识别 {okInserted}(反例 {okInsertedGuard}) / 版本比较 {okVersionCompare}" +
      $" / 续下更新 {okFetchNew} / 已下载置灰 {okDiskFirst} / 站点快照 {okSnapshot}" +
      $" / 统一库 {okUnified} / 站内搜索解析 {okSearch} / 站点清单 {okSiteCatalog}）");

listener.Stop();
runOk = ok;                    // 退出钩子据此决定删临时根还是留证据
return ok ? 0 : 1;

// ---------------------------------------------------------------- 辅助

/// <summary>
/// 清掉上一次没走到退出钩子的残留（进程被强杀、断电、CI 取消作业）。
/// **只删主人已经不在的**：每个临时根里写着创建者的 PID（owner.pid），
/// 那个进程还活着就跳过 —— 否则同一台机器上并发跑的自检会互相把目录删掉。
/// </summary>
static void CleanStaleSelfTestTemp() {
    try {
        foreach (var dir in Directory.GetDirectories(Path.GetTempPath(), "m3u8-selftest-*")) {
            if (IsOwnedByLiveProcess(dir)) continue;
            TryDeleteDirectory(dir);
        }
    } catch {
        // 兜底清理本身失败不该影响自检
    }
}

/// <summary>这个临时根的主人还在跑吗（没写 pid / pid 读不出来 / 进程已经没了 → 当残留）</summary>
static bool IsOwnedByLiveProcess(string dir) {
    try {
        var pidFile = Path.Combine(dir, "owner.pid");
        if (!File.Exists(pidFile)) return false;
        if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid)) return false;

        using var process = System.Diagnostics.Process.GetProcessById(pid);
        return !process.HasExited;
    } catch {
        // GetProcessById 对不存在的 pid 会抛 —— 那就是残留
        return false;
    }
}

/// <summary>删一个目录树，删不掉就算了（有文件被占用时留给下次兜底清理）</summary>
static void TryDeleteDirectory(string path) {
    try {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    } catch {
        // 忽略：清理失败不能让自检本身失败
    }
}

/// <summary>轮询等待条件成立，超时就抛异常（自检失败要立刻可见）</summary>
static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what) {
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline) {
        if (await condition()) return;
        await Task.Delay(50);
    }

    throw new TimeoutException($"等待超时：{what}");
}

/// <summary>
/// 造一条只用于"时长探测"的 TS：2 秒一个带 PCR 的包，跨度 <paramref name="seconds"/> 秒。
/// 自检里的假分片没有 PCR，所以内置探测读不出时长 —— 这个构造件专门把那条路验证掉。
/// </summary>
static byte[] BuildTsWithPcr(double seconds) {
    const int packet = 188;
    const int pcrPeriodMs = 2000;
    const int ticksPerSecond = 90000;

    var packetCount = (int)(seconds * 1000 / pcrPeriodMs) + 1;
    var buffer = new byte[packetCount * packet];
    long pcr = 0;

    for (var n = 0; n < packetCount; n++) {
        var i = n * packet;
        buffer[i] = 0x47;
        buffer[i + 1] = 0x01;              // PID 0x0100 的负载起始包（探测只看自适应字段）
        buffer[i + 2] = 0x00;
        buffer[i + 3] = 0x30;              // afc=3（自适应字段 + 负载）
        buffer[i + 4] = 7;                 // 自适应字段长度
        buffer[i + 5] = 0x10;              // PCR_flag
        buffer[i + 6] = (byte)(pcr >> 25);
        buffer[i + 7] = (byte)(pcr >> 17);
        buffer[i + 8] = (byte)(pcr >> 9);
        buffer[i + 9] = (byte)(pcr >> 1);
        buffer[i + 10] = (byte)(((pcr & 1) << 7) | 0x7E);   // 保留位 + 扩展高 1 位
        buffer[i + 11] = 0x00;             // 扩展低 8 位

        pcr += (long)pcrPeriodMs * ticksPerSecond / 1000;
    }

    return buffer;
}

static byte[] BuildFakeTs(int size, byte marker) {
    var buffer = new byte[size];
    for (var i = 0; i < size; i += 188) {
        buffer[i] = 0x47;
        for (var j = 1; j < 188 && i + j < size; j++) buffer[i + j] = marker;
    }
    return buffer;
}

/// <summary>AES-128-CBC + PKCS7 加密（自检里扮演源站的加密分片）</summary>
static byte[] AesEncryptPkcs7(byte[] plain, byte[] key) {
    using var aes = System.Security.Cryptography.Aes.Create();
    aes.Mode = System.Security.Cryptography.CipherMode.CBC;
    aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
    aes.Key = key;
    aes.IV = new byte[16];                       // 与清单里的 IV=0x0000… 对应
    using var enc = aes.CreateEncryptor();
    return enc.TransformFinalBlock(plain, 0, plain.Length);
}

/// <summary>解密回明文（用于自检自查构造件是否成立）；填充非法返回 null</summary>
static byte[]? AesDecryptPkcs7(byte[] cipher, byte[] key) {
    try {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = new byte[16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    } catch { return null; }
}

/// <summary>
/// 内存版下载历史（自检用）。
/// 真实实现是 <c>SqliteDownloadHistory</c>（阶段 Q 直接测它）；这里这版是用来
/// 给下载流程**造历史数据**的 —— 比如"历史里说第 5 集下过但文件已不在"这种场景，
/// 用内存实现比先往真库里写几行再跑要省事。
/// </summary>
internal sealed class MemoryHistory : IDownloadHistoryStore {
    private readonly List<DownloadHistoryEntry> _entries;

    public MemoryHistory(IEnumerable<DownloadHistoryEntry>? entries = null) =>
        _entries = entries?.ToList() ?? new List<DownloadHistoryEntry>();

    public void Record(DownloadHistoryEntry entry) => _entries.Add(entry);

    public void Forget(string pageUrl) => _entries.RemoveAll(e => e.PageUrl == pageUrl);

    public IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl) =>
        _entries.Where(e => e.PageUrl == pageUrl).OrderBy(e => e.EpisodeNumber).ToList();

    // 每次查询都返回一份新列表 —— 真实实现（SQLite）就是这样的。
    // 返回内部引用的话，销账会把先前取到的"旧快照"一起清空（自检真的这么挂过一次）。
    public IReadOnlyList<DownloadHistoryEntry> All() => _entries.ToList();
}

/// <summary>单线程假 Dispatcher：模拟 WinUI 的 DispatcherQueue.TryEnqueue（异步封送）</summary>
internal sealed class FakeDispatcher {
    private readonly BlockingCollection<Action> _queue = new();
    private int _executed;

    public FakeDispatcher() {
        var thread = new Thread(() => {
            foreach (var action in _queue.GetConsumingEnumerable()) {
                try { action(); } catch (Exception ex) { Console.Error.WriteLine("UI 线程异常: " + ex.Message); }
                Interlocked.Increment(ref _executed);
            }
        }) { IsBackground = true, Name = "FakeUI" };

        thread.Start();
    }

    public int Executed => Volatile.Read(ref _executed);

    public void Post(Action action) => _queue.Add(action);

    public Task InvokeAsync(Action action) {
        var tcs = new TaskCompletionSource();
        _queue.Add(() => {
            try { action(); tcs.SetResult(); } catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func) {
        var tcs = new TaskCompletionSource<T>();
        _queue.Add(() => {
            try { tcs.SetResult(func()); } catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
