using System.Text;
using M3U8Downloader.Core;

namespace M3U8Downloader.Core.Tasks;

/// <summary>
/// 任务生命周期的诊断日志。
///
/// 为什么需要它：界面上只能看到「下载中 + 一句话」，一旦出现
/// 「明明暂停了还在下」「继续下载之后一直不停」这类问题，
/// 光凭界面无法判断到底在下载哪几集、有没有反复重下同一集。
/// 有了它就能一眼看出：某一轮到底领到了哪几集、每一轮何时结束、结果如何。
///
/// 约定：
/// - **默认关闭**（CLI 与自检都是关闭的，不会凭空产生文件）；
/// - 每次进程启动写一个新文件，不覆盖上次的现场；
/// - 只写状态变化类的事件，不写每个分片，更不写分片内容；
/// - 文件超过 4 MB 就停止写入，避免长期挂着下载把磁盘写满。
/// </summary>
public sealed class TaskDiagnostics
{
    private const long MaxBytes = 4 * 1024 * 1024;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly string _name;
    private bool _stopped;

    private TaskDiagnostics(string name, string path)
    {
        _name = name;
        _path = path;
    }

    /// <summary>是否启用</summary>
    public bool Enabled { get; private set; }

    public string? FilePath => Enabled ? _path : null;

    /// <summary>
    /// 显式启用诊断日志（图形界面在启动时调一次）。
    ///
    /// 默认是关闭的：自检、命令行会各自创建 <see cref="DownloadTaskManager"/> 实例，
    /// 每跑一次就凭空多一个日志文件 —— 这种副作用必须由使用方主动要求才发生。
    /// </summary>
    public static TaskDiagnostics Create(string name)
    {
        var path = "";
        try
        {
            var dir = AppPaths.LogsDirectory;
            Directory.CreateDirectory(dir);

            var probe = Path.Combine(dir, ".probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            path = Path.Combine(dir, $"{name}-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        }
        catch
        {
            return new TaskDiagnostics(name, "") { Enabled = false };
        }

        return new TaskDiagnostics(name, path) { Enabled = true };
    }

    /// <summary>什么都不记（默认值）</summary>
    public static TaskDiagnostics Disabled { get; } = new("", "") { Enabled = false };

    /// <summary>写一行。任何异常都吞掉：诊断日志绝不能影响下载。</summary>
    public void Write(string message)
    {
        if (!Enabled || _stopped) return;

        try
        {
            lock (_gate)
            {
                var info = new FileInfo(_path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    _stopped = true;
                    return;
                }

                var line = $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}";
                File.AppendAllText(_path, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 写不进去就算了
        }
    }

    /// <summary>给任务打上短前缀，便于在日志里区分多个任务</summary>
    public string Tag(string taskId, string title) => $"[{taskId} {title}]";
}
