using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace M3U8Downloader.Core.Staging;

/// <summary>
/// 某一集暂存目录的清单。
///
/// 为什么要这个东西：源站可能**每次请求都重新生成播放列表**（例如随机插入广告），
/// 于是「分片序号 ↔ 内容」的对应关系在不同次请求之间会变。如果续传时直接复用
/// 上一次残留的分片，就会拼出一个错位的文件 —— 而且往往"看起来大小正常"。
///
/// 因此把清单和分片放在同一个暂存目录里，续传前先比对指纹：
/// 指纹不一致就丢弃旧分片重新下载。
/// </summary>
public sealed class StagingManifest
{
    /// <summary>清单格式版本</summary>
    public int Version { get; set; } = 1;

    /// <summary>本集播放列表地址</summary>
    public string? PlaylistUrl { get; set; }

    /// <summary>播放页地址（站点模式）</summary>
    public string? PageUrl { get; set; }

    /// <summary>剧集名，便于人工辨认这个暂存目录是哪一集</summary>
    public string? Title { get; set; }

    /// <summary>最终产物路径</summary>
    public string? OutputPath { get; set; }

    /// <summary>分片总数（含被跳过的广告分片）</summary>
    public int SegmentCount { get; set; }

    /// <summary>播放列表声明总时长（秒）</summary>
    public double TotalDuration { get; set; }

    /// <summary>被判定为广告/无效而跳过的分片序号</summary>
    public List<int> SkippedSegments { get; set; } = new();

    /// <summary>分片 URI 列表的指纹。任何变化（含广告位置变化）都会导致不一致。</summary>
    public string Fingerprint { get; set; } = "";

    /// <summary>创建时间</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>最近一次更新时间</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>给用户看的一句话描述（方法不会被序列化，无需 JsonIgnore）</summary>
    public string Describe() =>
        $"{Title ?? "(未命名)"} · {SegmentCount} 片 · {TimeSpan.FromSeconds(TotalDuration):hh\\:mm\\:ss}" +
        $" · 指纹 {Fingerprint[..Math.Min(12, Fingerprint.Length)]}";
}

/// <summary>暂存目录的读写与清理。所有删除操作都限定在"我们自己生成的文件"范围内。</summary>
public static class StagingStore
{
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>分片文件名前缀（与 HlsDownloader 的命名保持一致）</summary>
    private static readonly string[] SegmentPrefixes = { "seg_", "init_" };

    /// <summary>我们生成的临时文件后缀</summary>
    private static readonly string[] TempSuffixes = { ".part" };

    /// <summary>
    /// 暂存目录里由本程序生成的文件名白名单（分片之外的）。
    /// 合并出来的中间文件（merged.ts / merged.mp4）必须在这里 ——
    /// 漏掉它会被清理逻辑误判成"外来文件"，导致整个暂存目录删不掉。
    /// </summary>
    private static readonly string[] OwnedNames =
    {
        ManifestFileName,
        "index.m3u8",
        "merged.ts",
        "merged.mp4",
    };

    public static string ManifestPath(string dir) => Path.Combine(dir, ManifestFileName);

    /// <summary>对分片 URI 列表求指纹（顺序敏感：顺序变了就说明对应关系变了）</summary>
    public static string Fingerprint(IEnumerable<string> segmentUris)
    {
        var sb = new StringBuilder();
        var count = 0;
        foreach (var uri in segmentUris)
        {
            sb.Append(uri).Append('\n');
            count++;
        }
        sb.Append('#').Append(count);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static StagingManifest? TryLoad(string directory)
    {
        try
        {
            var path = ManifestPath(directory);
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path, Encoding.UTF8);
            return string.IsNullOrWhiteSpace(json)
                ? null
                : JsonSerializer.Deserialize<StagingManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string directory, StagingManifest manifest)
    {
        try
        {
            Directory.CreateDirectory(directory);
            manifest.UpdatedAt = DateTimeOffset.Now;

            var path = ManifestPath(directory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(manifest, JsonOptions), new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // 清单写不进去不该让下载失败：退化成"每次都重新下"而已
        }
    }

    /// <summary>是否是本程序生成的临时文件（分片 / 半成品）</summary>
    private static bool IsOurTemporaryFile(string name) =>
        SegmentPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))
        || TempSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    /// <summary>只删除由本程序生成的分片/临时文件，绝不动用户自己的东西</summary>
    public static int ClearSegments(string directory)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(directory)) return 0;

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!IsOurTemporaryFile(Path.GetFileName(file))) continue;

                try { File.Delete(file); removed++; } catch { }
            }
        }
        catch
        {
            // 尽力而为
        }
        return removed;
    }

    /// <summary>
    /// 全部成功后才调用：删除整个暂存目录。
    ///
    /// 安全约束：目录里若还残留"不是我们生成的文件"，就**不删目录**、只清自己的分片，
    /// 避免误删用户放进来的东西。
    /// </summary>
    public static bool TryRemoveStagingDirectory(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return true;

            // 先判断有没有外来文件；有就保守处理，只清分片
            var foreign = Directory.EnumerateFiles(directory)
                .Select(Path.GetFileName)
                .Where(name => name is not null
                               && !OwnedNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                               && !IsOurTemporaryFile(name))
                .ToList();

            if (foreign.Count > 0)
            {
                ClearSegments(directory);
                return false;
            }

            ClearSegments(directory);

            // 清单等"我们自己的文件"也要删掉 —— 否则目录非空，Directory.Delete 会抛异常
            foreach (var name in OwnedNames)
            {
                var path = Path.Combine(directory, name);
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }

            Directory.Delete(directory, recursive: false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
