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

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); } catch { }
    }
}
