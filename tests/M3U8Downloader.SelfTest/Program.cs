using System.Collections.Concurrent;
using System.Net;
using System.Text;
using M3U8Downloader.Core;
using M3U8Downloader.Core.Sites;
using M3U8Downloader.Core.Tasks;

// ============================================================================
// 下载任务自检（无界面，不依赖外网）
//
// 分三个阶段验证「整部剧下载」的进度是否真的在刷新：
//   阶段 A ：SeriesDownloader（整部剧协调器）—— 上报频率、每集进度、总速度
//   阶段 A2：HlsDownloader（分片引擎）—— 分片级进度
//   阶段 B ：DownloadTaskManager（任务队列）—— 入队即可见分集清单、状态流转，
//            以及"绑定属性一律经 UI 线程封送"这一约定（用假 Dispatcher 模拟）
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
                if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
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

SiteSeries BuildSeries()
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

    for (var n = 1; n <= 3; n++)
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

var ok = task.State == SeriesTaskState.Completed
         && task.Episodes.Count == 3
         && task.Episodes.All(e => e.Percent >= 100 && e.Bytes > 0)
         && midSamples.Count == 3
         && speedSamples > 0
         && peakBytes > 0
         && stateSequence.Contains("已完成")
         && observedStates.Contains("下载中")
         && dispatcher.Executed > 0;

Console.WriteLine();
Console.WriteLine(ok ? "自检结果       : ✔ 通过" : "自检结果       : ✘ 失败");

listener.Stop();
return ok ? 0 : 1;

// ---------------------------------------------------------------- 辅助

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
