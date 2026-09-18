using Microsoft.Data.Sqlite;
using M3U8Downloader.Core.Tasks;

namespace M3U8Downloader;

/// <summary>
/// 下载历史的 SQLite 实现：<c>%APPDATA%\M3U8Downloader\downloads.db</c>。
///
/// 为什么放在界面层而不是 Core：「Core 不引任何第三方包」是仓库的硬约定，
/// 另外三个项目都依赖它 —— SQLite 一旦进 Core，命令行、自检、界面
/// 全都会被带上 e_sqlite3 原生库。Core 只留 <see cref="IDownloadHistoryStore"/> 协议。
///
/// 表结构只有一张：一部剧（page_url）的某一集（episode_number）为联合主键，
/// 重下同一集就是覆盖旧记录（upsert），不会攒出一堆重复行。
///
/// 所有方法都**吞掉异常**：历史记不上、库被锁、文件被删，都不该影响下载。
/// 连接每次现开现关 —— SQLite 连接不是线程安全的，而调用方来自下载 worker 线程。
/// </summary>
public sealed class SqliteDownloadHistory : IDownloadHistoryStore
{
    private readonly string _connectionString;

    public SqliteDownloadHistory(string? filePath = null)
    {
        FilePath = filePath ?? DefaultFilePath;

        // 目录建不出来也别抛：后面每个操作自己都吞异常，退化成"不记账"而已
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        }
        catch
        {
            // 忽略：库开不起来时读写的 catch 会兜住
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            // 默认就是读写；显式写出来是为了让"只读打开"之类的意外不会悄悄发生
            Mode = SqliteOpenMode.ReadWriteCreate,

            // 关掉连接池：写入频率很低（一轮下载几十次），池化省不下什么，
            // 代价却是库文件被程序长期占着 —— 用户想删掉它、备份一份、
            // 或拿 DB 工具打开看两眼都会被 "being used by another process" 挡住。
            Pooling = false,
        }.ToString();

        EnsureSchema();
    }

    /// <summary>数据库文件路径</summary>
    public string FilePath { get; }

    /// <summary>默认位置：数据目录下的 downloads.db（与设置、任务列表同一个文件夹）</summary>
    public static string DefaultFilePath => Core.AppPaths.HistoryFile;

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 并发写时不要立刻报 "database is locked"，等一下比直接失败好
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private void EnsureSchema()
    {
        try
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS downloads (
                    page_url        TEXT    NOT NULL,
                    episode_number  INTEGER NOT NULL,
                    site_name       TEXT    NOT NULL DEFAULT '',
                    series_title    TEXT    NOT NULL DEFAULT '',
                    file_path       TEXT,
                    file_bytes      INTEGER NOT NULL DEFAULT 0,
                    downloaded_at   TEXT    NOT NULL,
                    PRIMARY KEY (page_url, episode_number)
                );
                CREATE INDEX IF NOT EXISTS idx_downloads_title ON downloads (series_title);
                """;
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 库建不起来（磁盘满、目录无权限）就退化成"不记账"
        }
    }

    public void Record(DownloadHistoryEntry entry)
    {
        try
        {
            using var connection = Open();
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
        }
        catch
        {
            // 历史是辅助信息，记不上不该影响下载
        }
    }

    public IReadOnlyList<DownloadHistoryEntry> FindBySeries(string pageUrl)
    {
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

    private IReadOnlyList<DownloadHistoryEntry> Query(string sql, Action<SqliteCommand>? configure)
    {
        var list = new List<DownloadHistoryEntry>();
        try
        {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            configure?.Invoke(cmd);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                list.Add(new DownloadHistoryEntry
                {
                    PageUrl = reader.GetString(0),
                    EpisodeNumber = reader.GetInt32(1),
                    SiteName = reader.GetString(2),
                    SeriesTitle = reader.GetString(3),
                    FilePath = reader.IsDBNull(4) ? null : reader.GetString(4),
                    FileBytes = reader.GetInt64(5),
                    DownloadedAt = DateTimeOffset.TryParse(reader.GetString(6), out var at)
                        ? at
                        : DateTimeOffset.MinValue,
                });
            }
        }
        catch
        {
            // 读不出来就当没有历史，界面照样能用
        }

        return list;
    }
}
