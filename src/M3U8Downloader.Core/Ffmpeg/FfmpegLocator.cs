using System.Diagnostics;
using System.Runtime.InteropServices;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Ffmpeg;

/// <summary>ffmpeg 的来源，用于在界面上说明"这个是从哪找到的"</summary>
public enum FfmpegSource {
    /// <summary>用户手工指定</summary>
    Custom,

    /// <summary>应用自己下载并管理的副本</summary>
    Managed,

    /// <summary>系统 PATH 里的</summary>
    SystemPath,
}

/// <summary>一个可执行文件的探测结果</summary>
public sealed record FfmpegBinary(string Path, string Version) {
    public string FileName => System.IO.Path.GetFileName(Path);
    public override string ToString() => $"{FileName} {Version}";
}

/// <summary>ffmpeg 安装状态</summary>
public sealed class FfmpegStatus {
    /// <summary>严格门槛：ffmpeg 与 ffprobe **都**可用才算 Installed</summary>
    public bool IsInstalled => Ffmpeg is not null && Ffprobe is not null;

    public FfmpegBinary? Ffmpeg { get; init; }
    public FfmpegBinary? Ffprobe { get; init; }
    public FfmpegSource? Source { get; init; }

    public string? FfmpegPath => Ffmpeg?.Path;
    public string? FfprobePath => Ffprobe?.Path;

    /// <summary>界面上直接可显示的一句话状态</summary>
    public string Describe() {
        if (IsInstalled) {
            var where = Source switch {
                FfmpegSource.Custom => "手动指定",
                FfmpegSource.Managed => "应用内下载",
                FfmpegSource.SystemPath => "系统 PATH",
                _ => "未知来源",
            };
            return $"已就绪：ffmpeg {Ffmpeg!.Version}（{where}）\n{Ffmpeg.Path}";
        }

        if (Ffmpeg is null && Ffprobe is null)
            return "未找到 ffmpeg。可以点「自动下载」，或手动指定已有的 ffmpeg.exe。";

        if (Ffmpeg is null)
            return $"只找到了 ffprobe（{Ffprobe!.Version}），缺少 ffmpeg.exe。";

        return $"只找到了 ffmpeg（{Ffmpeg.Version}），缺少 ffprobe.exe" +
               "（转封装只需要 ffmpeg，媒体分析/校验需要 ffprobe）。";
    }
}

/// <summary>
/// 定位 ffmpeg / ffprobe。
///
/// 查找顺序（与 M3U8Quicker 一致，也是这类工具最不容易出错的顺序）：
/// 1. 设置里指定的路径
/// 2. 程序目录下的 <c>ffmpeg\</c>（绿色免安装：整个文件夹拷走就能用，不污染系统其它位置）
/// 3. 用户目录下的 <c>%LOCALAPPDATA%\M3U8Downloader\ffmpeg\</c>（程序目录不可写时的退路）
/// 4. 系统 PATH
///
/// ffprobe 一律优先找 ffmpeg 同目录下的同名文件，找不到再回退 PATH。
/// </summary>
public static class FfmpegLocator {
    public const string AppFolderName = "M3U8Downloader";

    public static string FfmpegFileName => OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    public static string FfprobeFileName => OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";

    /// <summary>程序所在目录（发布后即 exe 所在目录）</summary>
    public static string AppDirectory => AppContext.BaseDirectory;

    /// <summary>
    /// 程序目录下的 ffmpeg 文件夹 —— **首选安装位置**。
    /// 放在程序自己身边，整个文件夹拷到别的机器上照样能用，也不往系统其它地方写东西。
    /// </summary>
    public static string AppLocalDirectory => Path.Combine(AppDirectory, "ffmpeg");

    /// <summary>
    /// 用户目录下的 ffmpeg 文件夹 —— 仅当程序目录不可写时使用。
    /// 例如装到了 <c>C:\Program Files\</c> 下，普通用户没有写权限。
    /// （用 Local 而非 Roaming：解包后约 330MB，放漫游配置会被域环境同步。）
    /// </summary>
    public static string UserLocalDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolderName, "ffmpeg");

    /// <summary>安装目标目录：优先程序目录，不可写则退回用户目录</summary>
    public static string PreferredInstallDirectory =>
        CanWriteTo(AppLocalDirectory) ? AppLocalDirectory : UserLocalDirectory;

    /// <summary>是否落在了退路目录（界面可据此提示用户）</summary>
    public static bool IsUsingFallbackDirectory =>
        !string.Equals(PreferredInstallDirectory, AppLocalDirectory, StringComparison.OrdinalIgnoreCase);

    /// <summary>按优先级列出所有可能的"应用管理副本"目录</summary>
    public static IEnumerable<string> ManagedDirectories {
        get {
            yield return AppLocalDirectory;
            yield return UserLocalDirectory;
        }
    }

    /// <summary>兼容旧名字：等同于首选安装目录</summary>
    public static string ManagedDirectory => PreferredInstallDirectory;

    public static string ManagedFfmpegPath => Path.Combine(PreferredInstallDirectory, FfmpegFileName);
    public static string ManagedFfprobePath => Path.Combine(PreferredInstallDirectory, FfprobeFileName);

    /// <summary>某个目录能否写入（用于决定装到哪里）</summary>
    public static bool CanWriteTo(string directory) {
        try {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>探测入口</summary>
    public static async Task<FfmpegStatus> DetectAsync(AppSettings? settings = null, CancellationToken ct = default) {
        settings ??= AppSettingsStore.Load();

        // 1) 用户手动指定
        var custom = settings.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) {
            var status = await ProbePairAsync(custom!, FfmpegSource.Custom, ct).ConfigureAwait(false);
            if (status.IsInstalled) return status;
        }

        // 2) 应用管理副本：程序目录优先，其次用户目录
        foreach (var dir in ManagedDirectories) {
            var exe = Path.Combine(dir, FfmpegFileName);
            if (!File.Exists(exe)) continue;

            var status = await ProbePairAsync(exe, FfmpegSource.Managed, ct).ConfigureAwait(false);
            if (status.IsInstalled) return status;
        }

        // 3) 系统 PATH
        var fromPath = await ProbeFromPathAsync(ct).ConfigureAwait(false);
        if (fromPath.IsInstalled) return fromPath;

        // 严格门槛没过：把各自找到的情况如实报出来，便于界面提示缺哪一半
        var ffmpeg = await ProbeSingleAsync(
            !string.IsNullOrWhiteSpace(custom) && File.Exists(custom) ? custom! : ManagedFfmpegPath, ct)
            .ConfigureAwait(false)
            ?? await ProbeSingleAsync("ffmpeg", ct).ConfigureAwait(false);

        var ffprobePath = ffmpeg is not null
            ? Path.Combine(Path.GetDirectoryName(ffmpeg.Path) ?? "", FfprobeFileName)
            : null;

        var ffprobe = (ffprobePath is not null && File.Exists(ffprobePath)
                ? await ProbeSingleAsync(ffprobePath, ct).ConfigureAwait(false)
                : null)
            ?? await ProbeSingleAsync("ffprobe", ct).ConfigureAwait(false);

        return new FfmpegStatus {
            Ffmpeg = ffmpeg,
            Ffprobe = ffprobe,
            Source = ffmpeg is not null ? FfmpegSource.Custom : null,
        };
    }

    private static async Task<FfmpegStatus> ProbePairAsync(string ffmpegPath, FfmpegSource source, CancellationToken ct) {
        var ffmpeg = await ProbeSingleAsync(ffmpegPath, ct).ConfigureAwait(false);
        if (ffmpeg is null) return new FfmpegStatus { Source = source };

        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg.Path) ?? "", FfprobeFileName);
        FfmpegBinary? ffprobe = null;

        if (File.Exists(sibling))
            ffprobe = await ProbeSingleAsync(sibling, ct).ConfigureAwait(false);

        ffprobe ??= await ProbeSingleAsync("ffprobe", ct).ConfigureAwait(false);

        return new FfmpegStatus { Ffmpeg = ffmpeg, Ffprobe = ffprobe, Source = source };
    }

    private static async Task<FfmpegStatus> ProbeFromPathAsync(CancellationToken ct) {
        var ffmpeg = await ProbeSingleAsync("ffmpeg", ct).ConfigureAwait(false);
        if (ffmpeg is null) return new FfmpegStatus();

        // PATH 里命中的是裸名字，用 where/which 解析成绝对路径方便界面显示
        var resolved = await ResolveOnPathAsync(ffmpeg.Path, ct).ConfigureAwait(false);
        if (resolved is not null && !string.Equals(resolved, ffmpeg.Path, StringComparison.OrdinalIgnoreCase)) {
            var re = await ProbeSingleAsync(resolved, ct).ConfigureAwait(false);
            if (re is not null) ffmpeg = re;
        }

        FfmpegBinary? ffprobe = null;
        var sibling = Path.Combine(Path.GetDirectoryName(ffmpeg.Path) ?? "", FfprobeFileName);
        if (File.Exists(sibling))
            ffprobe = await ProbeSingleAsync(sibling, ct).ConfigureAwait(false);

        ffprobe ??= await ProbeSingleAsync("ffprobe", ct).ConfigureAwait(false);

        return new FfmpegStatus { Ffmpeg = ffmpeg, Ffprobe = ffprobe, Source = FfmpegSource.SystemPath };
    }

    /// <summary>跑一次 <c>-version</c>，成功才认为这个可执行文件可用</summary>
    public static async Task<FfmpegBinary?> ProbeSingleAsync(string pathOrName, CancellationToken ct = default) {
        try {
            var psi = new ProcessStartInfo(pathOrName) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-version");

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            try {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                TryKill(process);
                return null;
            }

            if (process.ExitCode != 0) return null;

            var stdout = await stdoutTask.ConfigureAwait(false);
            _ = await stderrTask.ConfigureAwait(false);

            var version = ParseVersion(stdout);
            return new FfmpegBinary(pathOrName, version);
        } catch {
            return null;
        }
    }

    /// <summary>从 <c>-version</c> 的首行解析版本号，如 "ffmpeg version 7.1-full_build..." → "7.1"</summary>
    internal static string ParseVersion(string stdout) {
        foreach (var raw in stdout.Split('\n')) {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            foreach (var prefix in new[] { "ffmpeg version ", "ffprobe version " }) {
                if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = line[prefix.Length..].Trim();
                var token = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                return string.IsNullOrWhiteSpace(token) ? "unknown" : token;
            }

            // 有些精简构建首行不是这格式，退而求其次取第一行
            return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "unknown";
        }

        return "unknown";
    }

    private static async Task<string?> ResolveOnPathAsync(string name, CancellationToken ct) {
        try {
            var finder = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo(finder) {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(Path.GetFileNameWithoutExtension(name));

            using var process = Process.Start(psi);
            if (process is null) return null;

            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => l.Length > 0);
        } catch {
            return null;
        }
    }

    private static void TryKill(Process process) {
        try { process.Kill(entireProcessTree: true); } catch { /* 已经退出 */ }
    }
}
