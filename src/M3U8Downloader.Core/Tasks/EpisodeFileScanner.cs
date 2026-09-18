using M3U8Downloader.Core.Sites;

namespace M3U8Downloader.Core.Tasks;

/// <summary>
/// 「这一集下过没有」—— 直接看文件夹里有没有对应的产物文件。
///
/// 为什么不只信任务记录：记录会被清理（「清理已完成」、重装程序、换台机器），
/// 视频却还在文件夹里。用户自己最清楚这一点 —— 想补更的时候，最靠谱的依据
/// 就是磁盘上那几个文件。
///
/// 判定方式是拿文件名模板**正向**算出这一集该叫什么，再看文件在不在，
/// 而不是拿正则去反向猜：模板里的 <c>{title}</c> 要过一遍非法字符替换、
/// 集号还可能有补零差异，正向算才能保证和下载时用的是同一套规则。
/// </summary>
public static class EpisodeFileScanner
{
    /// <summary>认得出来的产物扩展名（下载产物 + 转封装结果）</summary>
    private static readonly string[] VideoExtensions =
    {
        ".ts", ".mp4", ".mkv", ".flv", ".avi", ".webm", ".mov", ".m4v", ".mpg", ".mpeg",
    };

    /// <summary>
    /// 找这一集的产物文件，返回绝对路径；没找到返回 null。
    /// 目录不存在、没权限、文件名非法等一律当作"没找到"，绝不抛给调用方。
    /// </summary>
    public static string? FindEpisodeFile(string directory, string fileNamePattern,
        SiteSeries series, int number)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileNamePattern))
            return null;
        if (!Directory.Exists(directory)) return null;

        string name;
        try
        {
            name = SeriesDownloader.BuildFileName(fileNamePattern, series, number);
        }
        catch
        {
            return null;
        }

        // 常见扩展名先各试一次（绝大多数情况命中这里）
        foreach (var ext in VideoExtensions)
        {
            var path = Path.Combine(directory, name + ext);
            if (IsRealFile(path)) return path;
        }

        // 扩展名大小写不同、或用户自己转成了别的容器：宽松地按「名字 + 任意扩展名」再找一遍。
        // 暂存目录（xxx.m3u8tmp）是目录不是文件，EnumerateFiles 不会命中它。
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, name + ".*"))
                if (IsRealFile(path)) return path;
        }
        catch
        {
            // 枚举失败（权限、路径过长）就当没找到
        }

        return null;
    }

    /// <summary>批量扫描，返回「集号 → 产物路径」，只含确实在磁盘上、且非空的集</summary>
    public static Dictionary<int, string> ScanExisting(string directory, string fileNamePattern,
        SiteSeries series, IEnumerable<int> numbers)
    {
        var found = new Dictionary<int, string>();
        foreach (var number in numbers)
        {
            var path = FindEpisodeFile(directory, fileNamePattern, series, number);
            if (path is not null) found[number] = path;
        }
        return found;
    }

    /// <summary>空文件不算下过（中途中断、占位都可能留下 0 字节文件）</summary>
    private static bool IsRealFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
