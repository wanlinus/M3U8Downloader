using Microsoft.Data.Sqlite;
using M3U8Downloader.Core.Tasks;

namespace M3U8Downloader.Core.Storage;

/// <summary>
/// 下载历史：统一库（<c>data\m3u8.db</c>）里的 <c>downloads</c> 表。
///
/// 表结构：一部剧（page_url）的某一集（episode_number）为联合主键，
/// 重下同一集就是覆盖旧记录（upsert），不会攒出一堆重复行。
///
/// 所有方法都**吞掉异常**：历史记不上、库被锁、文件被删，都不该影响下载。
/// 连接每次现开现关 —— SQLite 连接不是线程安全的，而调用方来自下载 worker 线程。
/// </summary>
public sealed class SqliteDownloadHistory : IDownloadHistoryStore {
    private readonly SqliteDatabase _database;

    /// <summary>用统一库（不传就是数据目录里那个）</summary>
    public SqliteDownloadHistory(SqliteDatabase? database = null) =>
        _database = database ?? SqliteDatabase.Default;

    /// <summary>指定库文件路径 —— 自检和一次性诊断用，正常路径请用默认库</summary>
    public SqliteDownloadHistory(string filePath) : this(new SqliteDatabase(filePath)) { }

    /// <summary>库文件路径</summary>
    public string FilePath => _database.FilePath;

    public void Record(DownloadHistoryEntry entry) {
        try {
            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                INSERT INTO downloads
                    (page_url, episode_number, site_name, series_title, file_path, file_bytes, downloaded_at)
                VALUES
                    ($url, $number, $site, $title, $path, $bytes, $at)
                ON CONFLICT (page_url, episode_number) DO UPDATE SET
                    site_name     = excluded.site_name,
                    series_title  = excluded.series_title,
                    file_path     = excluded.file_path,
                    file_bytes    = excluded.file_bytes,
                    downloaded_at = excluded.downloaded_at;
                """;
            cmd.Parameters.AddWithValue("$url", entry.PageUrl);
            cmd.Parameters.AddWithValue("$number", entry.EpisodeNumber);
            cmd.Parameters.AddWithValue("$site", entry.SiteName);
            cmd.Parameters.AddWithValue("$title", entry.SeriesTitle);
            cmd.Parameters.AddWithValue("$path", (object?)entry.FilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$bytes", entry.FileBytes);
            cmd.Parameters.AddWithValue("$at", entry.DownloadedAt.ToString("O"));
            cmd.ExecuteNonQuery();
        } catch {
            // 历史是辅助信息，记不上不该影响下载
        }
    }

    public void Forget(string pageUrl) {
        if (string.IsNullOrWhiteSpace(pageUrl)) return;

        try {
            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "DELETE FROM downloads WHERE page_url = $url;";
            cmd.Parameters.AddWithValue("$url", pageUrl);
            cmd.ExecuteNonQuery();
        } catch {
            // 删不掉不该影响「移除任务」本身
        }
    }

    public IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl) {
        if (string.IsNullOrWhiteSpace(pageUrl)) return Array.Empty<DownloadHistoryEntry>();
        return Query(
            "SELECT page_url, episode_number, site_name, series_title, file_path, file_bytes, downloaded_at " +
            "FROM downloads WHERE page_url = $url ORDER BY episode_number;",
            cmd => cmd.Parameters.AddWithValue("$url", pageUrl));
    }

    public IReadOnlyList<DownloadHistoryEntry> All() => Query(
        "SELECT page_url, episode_number, site_name, series_title, file_path, file_bytes, downloaded_at " +
        "FROM downloads ORDER BY downloaded_at DESC;",
        configure: null);

    private IReadOnlyList<DownloadHistoryEntry> Query(string sql, Action<SqliteCommand>? configure) {
        var list = new List<DownloadHistoryEntry>();
        try {
            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            configure?.Invoke(cmd);

            using var reader = cmd.ExecuteReader();
            var row = new SqliteRow(reader);       // 按列名读，表里加列/换顺序都不会错位
            while (reader.Read()) {
                list.Add(new DownloadHistoryEntry {
                    PageUrl = row.Str("page_url"),
                    EpisodeNumber = row.Int("episode_number"),
                    SiteName = row.Str("site_name"),
                    SeriesTitle = row.Str("series_title"),
                    FilePath = row.StrOrNull("file_path"),
                    FileBytes = row.Long("file_bytes"),
                    DownloadedAt = row.At("downloaded_at", DateTimeOffset.MinValue),
                });
            }
        } catch {
            // 读不出来就当没有历史，界面照样能用
        }

        return list;
    }
}
