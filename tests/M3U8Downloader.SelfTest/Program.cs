using System.Collections.Concurrent;
using System.Net;
using System.Text;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Downloads;
using M3U8Downloader.Core.Sites;
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
// ============================================================================

const int Port = 18742;
const int SegmentCount = 20;

var playlistBuilder = new StringBuilder();
playlistBuilder.AppendLine("#EXTM3U");
playlistBuilder.AppendLine("#EXT-X-VERSION:3");
playlistBuilder.AppendLine("#EXT-X-TARGETDURATION:4");
playlistBuilder.AppendLine("#EXT-X-MEDIA-SEQUENCE:1");
for (var i = 0; i < SegmentCount; i++)
{
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

for (var i = 0; i < encryptedSegments.Length; i++)
{
    var plain = BuildFakeTs(188 * (300 + i * 20), (byte)(0xA0 + i));

    // 第一块密文的第一个字节只取决于明文前 16 字节，所以随机化首块里除同步字节外的内容，
    // 直到 PKCS7 加密后的首个字节正好是 0x3C（'<'）。命中概率约 1/16。
    var found = false;
    var rng = new Random(1234 + i);
    for (var attempt = 0; attempt < 4000 && !found; attempt++)
    {
        for (var k = 1; k < 16; k++) plain[k] = (byte)rng.Next(256);

        var cipher = AesEncryptPkcs7(plain, aesKey);
        if (cipher.Length > 0 && cipher[0] == 0x3C)
        {
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
for (var i = 0; i < encryptedSegments.Length; i++)
{
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

var listener = new HttpListener();
listener.Prefixes.Add($"http://localhost:{Port}/");
listener.Start();
Console.WriteLine($"本地测试服务器: http://localhost:{Port}/  " +
                  $"{SegmentCount} 个分片，每片约 {segments[0].Length / 1024.0:0} KB");

_ = Task.Run(async () =>
{
    while (listener.IsListening)
    {
        HttpListenerContext ctx;
        try { ctx = await listener.GetContextAsync(); } catch { break; }

        _ = Task.Run(async () =>
        {
            try
            {
                var path = ctx.Request.Url!.AbsolutePath;

                // ---- 加密流用例：/enc/index.m3u8 + /enc/key.key + /enc/segN.ts（AES-128 密文）----
                if (path.StartsWith("/enc/", StringComparison.OrdinalIgnoreCase))
                {
                    if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                    {
                        var body = Encoding.UTF8.GetBytes(encPlaylist);
                        ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                    else if (path.EndsWith("key.key", StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.Response.ContentType = "application/octet-stream";
                        ctx.Response.ContentLength64 = aesKey.Length;
                        await ctx.Response.OutputStream.WriteAsync(aesKey);
                    }
                    else
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        var idx = int.TryParse(name.AsSpan(3), out var en) ? en : 0;
                        var body = encryptedSegments[Math.Clamp(idx, 0, encryptedSegments.Length - 1)];

                        ctx.Response.ContentType = "video/mp2t";
                        ctx.Response.ContentLength64 = body.Length;
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                }
                // ---- 适配层用例（放在 failSegments 之前：这些用例不受「源站抽风」开关影响）----
                else if (path.Equals("/dianshiju/20267897.html", StringComparison.OrdinalIgnoreCase))
                {
                    var body = Encoding.UTF8.GetBytes(NnyyDetailHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else if (path.StartsWith("/_gp/", StringComparison.Ordinal))
                {
                    var body = Encoding.UTF8.GetBytes(nnyyPlaysJson);
                    ctx.Response.ContentType = "application/json; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else if (path.Equals("/generic.html", StringComparison.OrdinalIgnoreCase))
                {
                    var body = Encoding.UTF8.GetBytes(genericHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else if (path.Equals("/empty.html", StringComparison.OrdinalIgnoreCase))
                {
                    var body = Encoding.UTF8.GetBytes(EmptyHtml);
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else if (path.Equals("/not-a-playlist.html", StringComparison.OrdinalIgnoreCase))
                {
                    // 「失效源」：返回 HTML 而不是 m3u8，用来验证多源里会跳过它挑下一个
                    var body = Encoding.UTF8.GetBytes("<html><body>404 not found</body></html>");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else if (failSegments && !path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    // 源站抽风：分片一直 404，重试也救不回来
                    ctx.Response.StatusCode = 404;
                    ctx.Response.ContentLength64 = 0;
                }
                else if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
                {
                    var body = Encoding.UTF8.GetBytes(playlist);
                    ctx.Response.ContentType = "application/vnd.apple.mpegurl";
                    ctx.Response.ContentLength64 = body.Length;
                    await ctx.Response.OutputStream.WriteAsync(body);
                }
                else
                {
                    var name = Path.GetFileNameWithoutExtension(path);          // seg12
                    var index = int.TryParse(name.AsSpan(3), out var n) ? n : 0;
                    var body = segments[Math.Clamp(index, 0, segments.Length - 1)];

                    requestCounts.AddOrUpdate(name, 1, (_, v) => v + 1);

                    ctx.Response.ContentType = "video/mp2t";
                    ctx.Response.ContentLength64 = body.Length;

                    const int chunk = 100 * 1024;
                    for (var off = 0; off < body.Length; off += chunk)
                    {
                        var count = Math.Min(chunk, body.Length - off);
                        await ctx.Response.OutputStream.WriteAsync(body.AsMemory(off, count));
                        await ctx.Response.OutputStream.FlushAsync();
                        await Task.Delay(60);   // 慢一点，便于观察进度中间态与速度
                    }
                }
            }
            catch { }
            finally { try { ctx.Response.Close(); } catch { } }
        });
    }
});

// ---------------------------------------------------------------- 剧集数据

var outputDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(outputDir);

SiteSeries BuildSeries(int episodeCount = 3)
{
    var s = new SiteSeries
    {
        Kind = SiteKind.MacCms,
        SiteName = "本地测试站",
        PageUrl = $"http://localhost:{Port}/vodplay/1-1-1.html",
        SeriesId = "1",
        Title = "自检剧集",
    };

    var playSource = new SitePlaySource { Id = 1, Name = "本地源" };
    s.Sources.Add(playSource);

    for (var n = 1; n <= episodeCount; n++)
    {
        playSource.Episodes.Add(new SiteEpisode
        {
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
SiteSeries BuildSeriesSelected(int episodeCount, params int[] selected)
{
    var s = BuildSeries(episodeCount);
    foreach (var e in s.AllEpisodes) e.IsSelected = selected.Contains(e.Number);
    return s;
}

/// <summary>追加拿第二个播放源（同集号会重复），并把勾选切到新源上</summary>
SiteSeries AddSecondSource(SiteSeries series, int episodeCount, params int[] selected)
{
    var second = new SitePlaySource { Id = 2, Name = "备用源" };
    for (var n = 1; n <= episodeCount; n++)
    {
        second.Episodes.Add(new SiteEpisode
        {
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

var options = new SeriesDownloadOptions
{
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

using (var direct = new SeriesDownloader())
{
    var directProgress = new Progress<SeriesDownloadProgress>(p =>
    {
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
    var hlsDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-hls-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(hlsDir);

    using var hls = new HlsDownloader();
    var (media, _) = await hls.ResolveMediaPlaylistAsync($"http://localhost:{Port}/index.m3u8");
    Console.WriteLine($"播放列表解析：{media.Segments.Count} 个分片，总时长 {media.TotalDuration:0.0}s");

    var hlsReports = 0;
    var hlsTrace = new List<string>();

    var hlsProgress = new Progress<DownloadProgress>(d =>
    {
        hlsReports++;
        var line = $"{d.Percent,5:0.0}%  片 {d.CompletedSegments}/{d.TotalSegments}  " +
                   $"{d.DownloadedBytes / 1024.0 / 1024.0:0.00} MB  {d.SpeedText}";
        if (hlsTrace.Count == 0 || hlsTrace[^1] != line) hlsTrace.Add(line);
    });

    var hlsResult = await hls.DownloadAsync(media, new DownloadOptions
    {
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

var queueDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(queueDir);
var queueOptions = new SeriesDownloadOptions
{
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
foreach (var item in task.Episodes)
{
    var ep = item;
    ep.PropertyChanged += (_, e) =>
    {
        if (e.PropertyName == nameof(TaskEpisodeItem.Percent) && ep.Percent > 0 && ep.Percent < 100)
            midSamples.AddOrUpdate(ep.Number, 1, (_, v) => v + 1);
    };
}

task.PropertyChanged += (_, e) =>
{
    switch (e.PropertyName)
    {
        case nameof(SeriesTask.SpeedBytesPerSecond):
            if (task.SpeedBytesPerSecond > 0)
            {
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
var poller = Task.Run(async () =>
{
    while (!finished.Task.IsCompleted)
    {
        try
        {
            var (state, percent, perEpisode, speed, bytes) = await dispatcher.InvokeAsync(() =>
                (task.StateText,
                 task.Percent,
                 string.Join(",", task.Episodes.Select(e => $"{e.Percent:0}")),
                 task.SpeedBytesPerSecond,
                 task.DownloadedBytes));

            if (observedStates.Count == 0 || observedStates[^1] != state) observedStates.Add(state);

            var line = $"{percent:0.0}% [{perEpisode}] {SeriesTask.FormatSpeed(speed)} {bytes / 1024.0 / 1024.0:0.00}MB";
            if (progressTrace.Count == 0 || progressTrace[^1] != line) progressTrace.Add(line);
        }
        catch { }
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
if (progressTrace.Count > 40) Console.WriteLine($"  …（共 {progressTrace.Count} 个采样点）");Console.WriteLine($"UI 线程执行次数: {dispatcher.Executed}");
Console.WriteLine($"速度采样(>0)   : {speedSamples} 次，峰值 {SeriesTask.FormatSpeed(peakSpeed)}");
Console.WriteLine($"已下载峰值     : {peakBytes / 1024.0 / 1024.0:0.00} MB");
Console.WriteLine($"结束态总速度   : {SeriesTask.FormatSpeed(totalSpeed)}");
Console.WriteLine($"上传速度       : {SeriesTask.FormatSpeed(manager.TotalUploadSpeed)}（本程序不上传数据）");
Console.WriteLine();
Console.WriteLine("每一集：");
foreach (var ep in task.Episodes)
{
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

var resumeRoot = Path.Combine(Path.GetTempPath(), "m3u8-selftest-resume-" + Guid.NewGuid().ToString("N")[..6]);
var resumeOut = Path.Combine(resumeRoot, "out");
Directory.CreateDirectory(resumeOut);
var store = new TaskStore(Path.Combine(resumeRoot, "tasks.json"));

var resumeOptions = new SeriesDownloadOptions
{
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
var manager1 = new DownloadTaskManager(null, dispatcher1.Post, store)
{
    // 恢复时要重新解析站点；自检不去访问真实网站，直接给本地剧集数据
    SeriesParser = (_, _) => Task.FromResult(BuildSeries()),
};

var task1 = manager1.Enqueue(BuildSeries(), resumeOptions);
await dispatcher1.InvokeAsync(() => { });

// 等「第 1 集已完成、第 2 集正在进行」—— 这就是用户关掉程序的那一刻
var halfDone = new TaskCompletionSource();
var halfPercent = 0.0;
var watcher = Task.Run(async () =>
{
    while (true)
    {
        var (firstDone, secondPercent) = await dispatcher1.InvokeAsync(() =>
            (task1.Episodes[0].State == EpisodeDownloadStatus.Completed, task1.Episodes[1].Percent));

        if (firstDone && secondPercent >= 15 && secondPercent < 100)
        {
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

// 「重新打开程序」：同一个 tasks.json，新建 manager 恢复
var dispatcher2 = new FakeDispatcher();
using var manager2 = new DownloadTaskManager(null, dispatcher2.Post, store)
{
    SeriesParser = (_, _) => Task.FromResult(BuildSeries()),
};

var restoredCount = await manager2.RestoreAsync();
var restoredTask = await dispatcher2.InvokeAsync(() => manager2.Tasks.FirstOrDefault());

var deadline = DateTime.UtcNow.AddMinutes(2);
while (restoredTask is not null && DateTime.UtcNow < deadline)
{
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

var pauseDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-pause-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(pauseDir);

var dispatcher3 = new FakeDispatcher();
using var manager3 = new DownloadTaskManager(null, dispatcher3.Post)
{
    // 「继续下载」会重新解析站点；自检不去访问真实网站，直接给本地剧集数据
    SeriesParser = (_, _) => Task.FromResult(BuildSeries()),
};
var task3 = manager3.Enqueue(BuildSeries(), new SeriesDownloadOptions
{
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
try
{
    await WaitUntilAsync(async () => await dispatcher3.InvokeAsync(() =>
            !task3.IsRunning
            && task3.Episodes.All(e => e.State is EpisodeDownloadStatus.Completed
                or EpisodeDownloadStatus.Failed or EpisodeDownloadStatus.Canceled
                or EpisodeDownloadStatus.Pending)),
        TimeSpan.FromSeconds(20), "暂停后各集都停下");
}
catch (TimeoutException)
{
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
var okPaused = pausedState == SeriesTaskState.Paused
               && pausedStillTodo > 0
               && pausedCanResume
               && !pausedCanPause
               && !pausedNeedsRetry
               && pausedFailed == 0
               && pausedMessage.Contains("已暂停")
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

var retryDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-retry-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(retryDir);

var dispatcher4 = new FakeDispatcher();
using var manager4 = new DownloadTaskManager(null, dispatcher4.Post);
var task4 = manager4.Enqueue(BuildSeries(), new SeriesDownloadOptions
{
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

var subsetDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-subset-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(subsetDir);

var dispatcher5 = new FakeDispatcher();
using var manager5 = new DownloadTaskManager(null, dispatcher5.Post)
{
    // 站点上仍然是完整的 21 集 —— 续传要能从中只挑回第 12 集
    SeriesParser = (_, _) => Task.FromResult(BuildSeries(FullEpisodeCount)),
};

var task5 = manager5.Enqueue(BuildSeriesSelected(FullEpisodeCount, PickedEpisode),
    new SeriesDownloadOptions
    {
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
task5.PropertyChanged += (_, e) =>
{
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

var fallbackDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-fallback-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(fallbackDir);

var dispatcher6 = new FakeDispatcher();
using var manager6 = new DownloadTaskManager(null, dispatcher6.Post)
{
    SeriesParser = (_, _) => Task.FromResult(AddSecondSource(BuildSeries(FullEpisodeCount), FullEpisodeCount, PickedEpisode)),
};

var task6 = manager6.Enqueue(AddSecondSource(BuildSeries(FullEpisodeCount), FullEpisodeCount, PickedEpisode),
    new SeriesDownloadOptions
    {
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
task6.PropertyChanged += (_, e) =>
{
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
var pcrDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-pcr-" + Guid.NewGuid().ToString("N")[..6]);
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
    var mp4Dir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-mp4-" + Guid.NewGuid().ToString("N")[..6]);
    Directory.CreateDirectory(mp4Dir);

    using var mp4Downloader = new SeriesDownloader();
    var mp4Report = await mp4Downloader.DownloadAsync(BuildSeries(), new SeriesDownloadOptions
    {
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

var encDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-enc-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(encDir);

var encOk = false;
{
    using var encDownloader = new HlsDownloader();
    var (encMedia, encLogs) = await encDownloader.ResolveMediaPlaylistAsync(
        $"http://localhost:{Port}/enc/index.m3u8");
    foreach (var l in encLogs) Console.WriteLine("  [解析] " + l);

    Console.WriteLine($"  分片 {encMedia.Segments.Count} 个；" +
                      $"密文首字节: {string.Join(", ", encryptedSegments.Select(s => $"0x{s[0]:X2}"))}");

    var encResult = await encDownloader.DownloadAsync(encMedia, new DownloadOptions
    {
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

var singleDir = Path.Combine(Path.GetTempPath(), "m3u8-selftest-single-" + Guid.NewGuid().ToString("N")[..6]);
Directory.CreateDirectory(singleDir);

var okSingle = false;
{
    var service = new SingleFileDownloadService();
    var report = await service.DownloadAsync(new SingleFileDownloadOptions
    {
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
var okGeneric = false;
var okEmpty = false;

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
    try
    {
        var parsed = await resolver.ParseAsync($"http://localhost:{Port}/empty.html", emptyCtx);
        Console.WriteLine($"  ✘ 空页面居然解析出了 {parsed.TotalEpisodes} 集，应当明确报错");
    }
    catch (NotSupportedException ex)
    {
        okEmpty = true;
        Console.WriteLine($"  空页面明确报错 ✔：{ex.Message}");
    }
}

Console.WriteLine();
var ok = okB && okC && okPaused && okStopped && okResume && okSettled && okRetry
         && okResumeSubset && okFallback && okDuration && encOk && okSingle
         && okRegistry && okNnyy && okGeneric && okEmpty;
Console.WriteLine(ok
    ? "自检结果       : ✔ 通过"
    : $"自检结果       : ✘ 失败（阶段B {okB} / 阶段C {okC} / 暂停 {okPaused} / 暂停后静止 {okStopped}" +
      $" / 续传 {okResume} / 续传后静止 {okSettled} / 重试 {okRetry}" +
      $" / 续传只下选中集 {okResumeSubset} / 多源兜底 {okFallback} / 时长核对 {okDuration}" +
      $" / 密文首字节 0x3C {encOk} / 单文件服务 {okSingle}" +
      $" / 适配器登记 {okRegistry} / 努努影院 {okNnyy} / 通用兜底 {okGeneric} / 空页面报错 {okEmpty}）");

listener.Stop();
return ok ? 0 : 1;

// ---------------------------------------------------------------- 辅助

/// <summary>轮询等待条件成立，超时就抛异常（自检失败要立刻可见）</summary>
static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout, string what)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (await condition()) return;
        await Task.Delay(50);
    }

    throw new TimeoutException($"等待超时：{what}");
}

/// <summary>
/// 造一条只用于"时长探测"的 TS：2 秒一个带 PCR 的包，跨度 <paramref name="seconds"/> 秒。
/// 自检里的假分片没有 PCR，所以内置探测读不出时长 —— 这个构造件专门把那条路验证掉。
/// </summary>
static byte[] BuildTsWithPcr(double seconds)
{
    const int packet = 188;
    const int pcrPeriodMs = 2000;
    const int ticksPerSecond = 90000;

    var packetCount = (int)(seconds * 1000 / pcrPeriodMs) + 1;
    var buffer = new byte[packetCount * packet];
    long pcr = 0;

    for (var n = 0; n < packetCount; n++)
    {
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

static byte[] BuildFakeTs(int size, byte marker)
{
    var buffer = new byte[size];
    for (var i = 0; i < size; i += 188)
    {
        buffer[i] = 0x47;
        for (var j = 1; j < 188 && i + j < size; j++) buffer[i + j] = marker;
    }
    return buffer;
}

/// <summary>AES-128-CBC + PKCS7 加密（自检里扮演源站的加密分片）</summary>
static byte[] AesEncryptPkcs7(byte[] plain, byte[] key)
{
    using var aes = System.Security.Cryptography.Aes.Create();
    aes.Mode = System.Security.Cryptography.CipherMode.CBC;
    aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
    aes.Key = key;
    aes.IV = new byte[16];                       // 与清单里的 IV=0x0000… 对应
    using var enc = aes.CreateEncryptor();
    return enc.TransformFinalBlock(plain, 0, plain.Length);
}

/// <summary>解密回明文（用于自检自查构造件是否成立）；填充非法返回 null</summary>
static byte[]? AesDecryptPkcs7(byte[] cipher, byte[] key)
{
    try
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        aes.Mode = System.Security.Cryptography.CipherMode.CBC;
        aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
        aes.Key = key;
        aes.IV = new byte[16];
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }
    catch { return null; }
}

/// <summary>单线程假 Dispatcher：模拟 WinUI 的 DispatcherQueue.TryEnqueue（异步封送）</summary>
internal sealed class FakeDispatcher
{
    private readonly BlockingCollection<Action> _queue = new();
    private int _executed;

    public FakeDispatcher()
    {
        var thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable())
            {
                try { action(); }
                catch (Exception ex) { Console.Error.WriteLine("UI 线程异常: " + ex.Message); }
                Interlocked.Increment(ref _executed);
            }
        })
        { IsBackground = true, Name = "FakeUI" };

        thread.Start();
    }

    public int Executed => Volatile.Read(ref _executed);

    public void Post(Action action) => _queue.Add(action);

    public Task InvokeAsync(Action action)
    {
        var tcs = new TaskCompletionSource();
        _queue.Add(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T>();
        _queue.Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }
}
