using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace M3U8Downloader.Core.Tasks;

/// <summary>任务里一集的持久化记录</summary>
public sealed class TaskEpisodeRecord
{
    public int Number { get; set; }
    public string Title { get; set; } = "";

    /// <summary><see cref="Sites.EpisodeDownloadStatus"/> 的名字（用枚举名而不是中文，改文案也不会读坏旧文件）</summary>
    public string Status { get; set; } = nameof(Sites.EpisodeDownloadStatus.Pending);

    public double Percent { get; set; }
    public long Bytes { get; set; }

    /// <summary>产物路径（已完成时有）—— 继续下载时据此判断这一集还在不在</summary>
    public string? OutputPath { get; set; }

    public string? Error { get; set; }
}

/// <summary>
/// 一个下载任务的持久化记录。程序重启后据此把任务列表和断点位置恢复回来。
/// </summary>
public sealed class SeriesTaskRecord
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string SiteName { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public string OutputDirectory { get; set; } = "";

    /// <summary>实际产物目录（输出目录 + 「剧名 - 站点」子目录）；老记录里没有，为 null 时界面退回根目录</summary>
    public string? ResolvedDirectory { get; set; }

    public string? SourceName { get; set; }
    public int? PreferredSourceId { get; set; }

    /// <summary><see cref="SeriesTaskState"/> 的名字</summary>
    public string State { get; set; } = nameof(SeriesTaskState.Queued);

    public double Percent { get; set; }
    public long DownloadedBytes { get; set; }
    public string? ReportPath { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }

    // ---- 下载参数（继续下载时复用）----
    public int EpisodeConcurrency { get; set; } = 2;
    public int SegmentConcurrency { get; set; } = 16;
    public int? PreferHeight { get; set; }
    public string? FfmpegPath { get; set; }
    public string FileNamePattern { get; set; } = "{title}.{number:00}";
    public bool SeriesSubdirectory { get; set; } = true;

    public List<TaskEpisodeRecord> Episodes { get; set; } = new();

    public SeriesTaskState ParsedState =>
        Enum.TryParse<SeriesTaskState>(State, ignoreCase: true, out var s) ? s : SeriesTaskState.Queued;
}

/// <summary>
/// 任务列表的落盘仓库。
///
/// 位置：<c>%APPDATA%\M3U8Downloader\tasks.json</c>（与设置同一个目录，每用户一份）。
/// 写入方式与设置一致：先写 <c>.tmp</c> 再原子替换，避免中途断电留下半个 JSON。
///
/// 为什么要把整个任务列表（含每一集的进度与产物路径）都存下来：
/// 关掉程序再打开时，用户不需要重新贴地址、重新下已经下好的集；
/// 未完成的集靠暂存目录里的分片 + 清单指纹继续下（见 StagingManifest）。
/// </summary>
public sealed class TaskStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 中文不要被转义成 \uXXXX，方便用户自己打开文件看一眼
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public TaskStore(string? filePath = null) => FilePath = filePath ?? DefaultFilePath;

    /// <summary>存放任务列表的目录</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M3U8Downloader");

    /// <summary>任务列表文件</summary>
    public static string DefaultFilePath => Path.Combine(DefaultDirectory, "tasks.json");

    public string FilePath { get; }

    /// <summary>
    /// 读取任务记录。任何异常都返回空表 —— 任务列表坏了也不该让程序起不来。
    /// </summary>
    public List<SeriesTaskRecord> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<SeriesTaskRecord>();

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(json)) return new List<SeriesTaskRecord>();

            var records = JsonSerializer.Deserialize<List<SeriesTaskRecord>>(json, JsonOptions);
            return records ?? new List<SeriesTaskRecord>();
        }
        catch
        {
            return new List<SeriesTaskRecord>();
        }
    }

    /// <summary>原子写入；返回是否成功（失败不抛，由调用方决定是否提示）</summary>
    public bool Save(IEnumerable<SeriesTaskRecord> records)
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var temp = FilePath + ".tmp";
            var json = JsonSerializer.Serialize(records.ToList(), JsonOptions);
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>清空任务列表文件（用户点「清理已完成」并把列表清空时用）</summary>
    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch { }
    }
}
