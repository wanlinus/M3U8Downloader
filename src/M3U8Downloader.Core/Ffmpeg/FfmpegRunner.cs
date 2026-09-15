using System.Diagnostics;
using System.Text;

namespace M3U8Downloader.Core.Ffmpeg;

/// <summary>ffmpeg 命令执行结果</summary>
public sealed record FfmpegRunResult(bool Success, string? Error)
{
    public static readonly FfmpegRunResult Ok = new(true, null);
    public static FfmpegRunResult Fail(string error) => new(false, error);
}

/// <summary>
/// 调用 ffmpeg 做「流复制」转封装。
///
/// 只做 remux（<c>-c copy</c>），不重新编码：速度快（几分钟的视频几秒完成）、
/// 画质无损，也不需要 GPL-only 的编码器。
/// </summary>
public static class FfmpegRunner
{
    /// <summary>转封装超时（大文件也要给足时间）</summary>
    public static TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(20);

    /// <summary>
    /// 把 TS / fMP4 合并文件转成标准 MP4。
    /// <c>-movflags +faststart</c> 会把 moov 移到文件头，便于边下边播与快速拖动。
    /// </summary>
    public static async Task<FfmpegRunResult> RemuxToMp4Async(
        string ffmpegPath, string inputPath, string outputPath, CancellationToken ct = default)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y",                       // 覆盖已有文件
            "-i", inputPath,
            "-c", "copy",               // 流复制，不重新编码
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath,
        };

        return await RunAsync(ffmpegPath, args, ct).ConfigureAwait(false);
    }

    /// <summary>跑一次 ffmpeg，返回成功与否 + stderr 摘要</summary>
    public static async Task<FfmpegRunResult> RunAsync(
        string ffmpegPath, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,          // 不闪黑框
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return FfmpegRunResult.Fail("无法启动 ffmpeg");

            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return FfmpegRunResult.Fail(ct.IsCancellationRequested ? "已取消" : "ffmpeg 执行超时");
            }

            var stderr = await stderrTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                var detail = Summarize(stderr);
                return FfmpegRunResult.Fail(string.IsNullOrWhiteSpace(detail)
                    ? $"ffmpeg 退出码 {process.ExitCode}"
                    : $"ffmpeg 失败：{detail}");
            }

            return FfmpegRunResult.Ok;
        }
        catch (Exception ex)
        {
            return FfmpegRunResult.Fail($"ffmpeg 调用异常：{ex.Message}");
        }
    }

    private static string Summarize(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return "";

        var lines = stderr
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .TakeLast(3);

        var text = string.Join(" / ", lines);
        return text.Length > 300 ? text[..300] + "…" : text;
    }

    /// <summary>
    /// 用 ffprobe 读产物的容器信息（格式、时长、码率、每条流的编码/分辨率/帧率/采样率）。
    /// 读不出来返回 null（没装 ffprobe、或文件根本打不开）。
    /// </summary>
    public static async Task<ContainerProbe?> TryProbeContainerAsync(
        string ffmpegPath, string inputPath, CancellationToken ct = default)
    {
        var probe = FindSibling(ffmpegPath, "ffprobe.exe");
        if (probe is null) return null;

        var r = await RunCaptureAsync(probe, new[]
        {
            "-v", "error",
            "-show_entries", "format=format_name,duration,bit_rate,nb_streams",
            "-show_entries", "stream=index,codec_type,codec_name,width,height,r_frame_rate,sample_rate,channels,nb_frames",
            "-of", "json",
            inputPath,
        }, ct).ConfigureAwait(false);

        if (!r.Success || string.IsNullOrWhiteSpace(r.Output)) return null;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(r.Output);
            var root = doc.RootElement;
            var result = new ContainerProbe();

            if (root.TryGetProperty("format", out var fmt))
            {
                result.FormatName = GetString(fmt, "format_name");
                result.DurationSeconds = GetDouble(fmt, "duration");
                result.BitRate = (long)GetDouble(fmt, "bit_rate");
            }

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var s in streams.EnumerateArray())
                {
                    result.Streams.Add(new MediaStreamInfo
                    {
                        Index = (int)GetDouble(s, "index"),
                        CodecType = GetString(s, "codec_type"),
                        CodecName = GetString(s, "codec_name"),
                        Width = (int)GetDouble(s, "width"),
                        Height = (int)GetDouble(s, "height"),
                        FrameRate = GetString(s, "r_frame_rate"),
                        SampleRate = (int)GetDouble(s, "sample_rate"),
                        Channels = (int)GetDouble(s, "channels"),
                        FrameCount = (long)GetDouble(s, "nb_frames"),
                    });
                }
            }

            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 全量解码检查：把产物整条解一遍（不写任何输出）。
    ///
    /// 这是**唯一能发现"内容坏了"的检查** —— 包对齐、字节数、时长全对，
    /// 源站返回的坏包依然会原样躺在产物里（实测某站 16 集里 3 集有 `Packet corrupt`）。
    /// 退出码 0 且无告警才算通过；代价是读一遍文件（几百 MB 约几秒）。
    /// </summary>
    public static async Task<DecodeCheckResult?> RunDecodeCheckAsync(
        string ffmpegPath, string inputPath, CancellationToken ct = default)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var r = await RunCaptureAsync(ffmpegPath,
            new[] { "-hide_banner", "-v", "warning", "-i", inputPath, "-f", "null", "-" },
            ct).ConfigureAwait(false);
        clock.Stop();

        var result = new DecodeCheckResult
        {
            ExitCode = r.ExitCode,
            Elapsed = clock.Elapsed,
        };

        // 告警按"类型"归并计数：`Packet corrupt (stream = 0, dts = ...)` 这类只保留前缀
        var categories = new Dictionary<string, int>(StringComparer.Ordinal);
        var lines = (r.Output ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // 去掉 ffmpeg 的前缀（如 "[in#0/mpegts @ 0x...] "）与位置信息
            var text = System.Text.RegularExpressions.Regex.Replace(line, @"^\[[^\]]*\]\s*", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\(stream\s*=.*$", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*@\s*0x[0-9a-fA-F]+", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*dts\s*=\s*\d+", "");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*\d+ >= \d+", "");
            text = text.Trim();
            if (text.Length == 0) continue;

            // 输出阶段的告警不算产物的毛病 —— 它描述的是「检查方式自己」，见 IsMuxerStageWarning
            if (IsMuxerStageWarning(text))
            {
                result.IgnoredMuxerWarnings++;
                continue;
            }

            categories[text] = categories.TryGetValue(text, out var n) ? n + 1 : 1;
        }

        foreach (var kv in categories.OrderByDescending(kv => kv.Value).Take(5))
            result.Issues.Add($"{kv.Key} × {kv.Value}");

        result.Passed = r.ExitCode == 0 && result.Issues.Count == 0;
        return result;
    }

    /// <summary>
    /// 这条告警是不是「输出阶段」产生的（即：与产物内容无关）。
    ///
    /// 为什么必须把它们分开：<c>-f null</c> 的输出用的是两个**假编码器** ——
    /// 视频走 <c>wrapped_avframe</c>、音频走 <c>pcm_s16le</c>，都不真的编码。
    /// 而 <c>wrapped_avframe</c> 会把解码帧的 PTS 直接当作输出包的 DTS，
    /// 输出的时间基又被规范成整数帧率（如 25），输入的 90kHz 时间戳换算过来会**取整撞车**；
    /// 再加上 H.264 的 B 帧本来就让 PTS 非单调，于是 muxer 每遇到一个重复/回退的时间戳
    /// 就刷一条 <c>… non monotonically increasing dts to muxer in stream 0: 7500 >= 7500</c>
    /// （注意两个数可以**相等** —— 这正是取整撞车的特征）。
    ///
    /// 实测：《交锋》第 21 集（70887 帧、has_b_frames=2、25.09fps 变帧率）触发 258 条这种告警，
    /// 而同一条流的 DTS **完全单调**（0 处回退）、时长核对与容器探测全部通过、
    /// 整条流**零解码错误**（没有任何 Packet corrupt / error while decoding）。
    /// 也就是说这东西是检查工具自己的产物，报给用户只会造成恐慌。
    ///
    /// 判据只看 "to muxer"：真正指向内容问题的告警（Packet corrupt、
    /// error while decoding、Invalid NAL、concealing errors…）都不含这个词。
    /// </summary>
    private static bool IsMuxerStageWarning(string text) =>
        text.Contains("to muxer", StringComparison.OrdinalIgnoreCase);

    /// <summary>找 ffmpeg 同目录下的兄弟程序（ffprobe）</summary>
    private static string? FindSibling(string ffmpegPath, string fileName)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);
        if (string.IsNullOrEmpty(dir)) return null;

        var path = Path.Combine(dir, fileName);
        return File.Exists(path) ? path : null;
    }

    private static string GetString(System.Text.Json.JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static double GetDouble(System.Text.Json.JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == System.Text.Json.JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == System.Text.Json.JsonValueKind.String
            && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d)) return d;
        return 0;
    }

    /// <summary>
    /// 读产物的真实时长（秒）。读不出来返回 null。
    ///
    /// 用途：清单声明的总时长与产物实际时长对不上，就说明中间**少了片**。
    /// 只看"ffmpeg 退出码 0 / 文件非空"是查不出这种问题的 —— 缺一段照样能转封装成功。
    /// 优先用 ffprobe（同目录），没有就退回用 ffmpeg 自己解析。
    /// </summary>
    public static async Task<double?> TryGetDurationSecondsAsync(
        string ffmpegPath, string inputPath, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(ffmpegPath);

        // 1) ffprobe：输出干净，直接给秒数
        var probe = string.IsNullOrEmpty(dir) ? null : Path.Combine(dir, "ffprobe.exe");
        if (probe is not null && File.Exists(probe))
        {
            var r = await RunCaptureAsync(probe,
                new[] { "-v", "error", "-show_entries", "format=duration",
                        "-of", "default=noprint_wrappers=1:nokey=1", inputPath },
                ct).ConfigureAwait(false);

            if (r.Success
                && double.TryParse(r.Output.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var seconds)
                && seconds > 0)
            {
                return seconds;
            }
        }

        // 2) 退回 ffmpeg 自己解析：把产物当输入、不产出任何东西，从 stderr 里读 Duration
        var run = await RunCaptureAsync(ffmpegPath,
            new[] { "-hide_banner", "-i", inputPath, "-f", "null", "-" }, ct).ConfigureAwait(false);

        var m = System.Text.RegularExpressions.Regex.Match(run.Output, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (!m.Success) return null;

        return int.Parse(m.Groups[1].Value) * 3600
               + int.Parse(m.Groups[2].Value) * 60
               + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>跑一次命令并把 stdout + stderr 一起收回来（诊断用，不在意退出码）</summary>
    private static async Task<(bool Success, int ExitCode, string Output)> RunCaptureAsync(
        string exePath, IReadOnlyList<string> args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var process = Process.Start(psi);
            if (process is null) return (false, -1, "");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));

            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return (false, -1, "");
            }

            var text = (await stdoutTask.ConfigureAwait(false)) + "\n" + (await stderrTask.ConfigureAwait(false));
            return (process.ExitCode == 0, process.ExitCode, text);
        }
        catch
        {
            return (false, -1, "");
        }
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}
