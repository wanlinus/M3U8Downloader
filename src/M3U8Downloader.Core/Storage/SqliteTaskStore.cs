using System.Text.Json;
using Microsoft.Data.Sqlite;
using M3U8Downloader.Core.Tasks;

namespace M3U8Downloader.Core.Storage;

/// <summary>
/// 任务列表：统一库（<c>data\m3u8.db</c>）里的四张表。
///
/// <code>
/// tasks              一个任务一行（标量字段 + 下载参数）
/// task_episodes      任务里每一集一行（进度、状态、产物路径）
/// task_snapshots     站点快照，一个任务一行
/// snapshot_episodes  快照里每一集一行（站点上全部集，不只是勾选的）
/// </code>
///
/// 为什么不把一个任务整个序列化成一行 JSON：那样等于把原来的 <c>tasks.json</c>
/// 按任务切开，除了"在数据库里"没有任何好处 —— 进度一变还是要重写整行。
/// 拆到"一集一行"之后，读回来能按需组装，集数再多也不会因为一行超长而重写。
///
/// 写入是**整表重写放一个事务里**（Save 收到的是完整列表）：中途失败整体回滚，
/// 不会留下"任务没了、集还在"的半截状态 —— 比原来写 .tmp 再改名更稳，
/// 数据量大时也比重新序列化整份 JSON 便宜。
///
/// 所有方法都吞异常：任务列表读不回就是空列表，程序照常起。
/// </summary>
public sealed class SqliteTaskStore {
    private readonly SqliteDatabase _database;

    /// <summary>用统一库（不传就是数据目录里那个）</summary>
    public SqliteTaskStore(SqliteDatabase? database = null) =>
        _database = database ?? SqliteDatabase.Default;

    /// <summary>库文件路径</summary>
    public string FilePath => _database.FilePath;

    /// <summary>读取全部任务（按列表顺序）；任何异常都返回空表 —— 任务列表坏了也不该让程序起不来</summary>
    public List<SeriesTaskRecord> Load() {
        var records = new List<SeriesTaskRecord>();

        try {
            using var connection = _database.Open();
            var byId = new Dictionary<string, SeriesTaskRecord>(StringComparer.Ordinal);

            // 1) 任务本体
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = TaskSelect;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) {
                    var record = new SeriesTaskRecord {
                        Id = reader.GetString(0),
                        Title = reader.GetString(2),
                        SiteName = reader.GetString(3),
                        PageUrl = reader.GetString(4),
                        OutputDirectory = reader.GetString(5),
                        ResolvedDirectory = OptString(reader, 6),
                        SourceName = OptString(reader, 7),
                        PreferredSourceId = OptInt(reader, 8),
                        State = reader.GetString(9),
                        Percent = reader.GetDouble(10),
                        DownloadedBytes = reader.GetInt64(11),
                        ReportPath = OptString(reader, 12),
                        Message = OptString(reader, 13),
                        SkippedAdSegments = (int)reader.GetInt64(14),
                        CreatedAt = At(reader, 15, DateTimeOffset.Now),
                        FinishedAt = OptAt(reader, 16),
                        EpisodeConcurrency = (int)reader.GetInt64(17),
                        SegmentConcurrency = (int)reader.GetInt64(18),
                        PreferHeight = OptInt(reader, 19),
                        FfmpegPath = OptString(reader, 20),
                        FileNamePattern = reader.GetString(21),
                        SeriesSubdirectory = reader.GetInt64(22) != 0,
                    };

                    byId[record.Id] = record;
                    records.Add(record);
                }
            }

            // 2) 任务里的集
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = EpisodeSelect;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) {
                    if (!byId.TryGetValue(reader.GetString(0), out var record)) continue;

                    record.Episodes.Add(new TaskEpisodeRecord {
                        Number = (int)reader.GetInt64(1),
                        Title = reader.GetString(2),
                        Status = reader.GetString(3),
                        Percent = reader.GetDouble(4),
                        Bytes = reader.GetInt64(5),
                        OutputPath = OptString(reader, 6),
                        Error = OptString(reader, 7),
                    });
                }
            }

            // 3) 站点快照
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = SnapshotSelect;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) {
                    if (!byId.TryGetValue(reader.GetString(0), out var record)) continue;

                    record.Snapshot = new SeriesSnapshot {
                        Kind = reader.GetString(1),
                        SiteName = reader.GetString(2),
                        PageUrl = reader.GetString(3),
                        SeriesId = reader.GetString(4),
                        Title = reader.GetString(5),
                        Headers = ParseHeaders(reader.GetString(6)),
                        SourceId = OptInt(reader, 7),
                        CapturedAt = At(reader, 8, DateTimeOffset.Now),
                    };
                }
            }

            // 4) 快照里的集
            using (var cmd = connection.CreateCommand()) {
                cmd.CommandText = SnapshotEpisodeSelect;
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) {
                    if (!byId.TryGetValue(reader.GetString(0), out var record)) continue;
                    if (record.Snapshot is not { } snapshot) continue;

                    snapshot.Episodes.Add(new EpisodeMetadata {
                        Number = (int)reader.GetInt64(1),
                        Title = reader.GetString(2),
                        PageUrl = reader.GetString(3),
                        Key = OptString(reader, 4),
                        SourceId = (int)reader.GetInt64(5),
                    });
                }
            }
        } catch {
            return new List<SeriesTaskRecord>();
        }

        return records;
    }

    /// <summary>整表重写（一个事务）。返回是否成功，失败不抛。</summary>
    public bool Save(IEnumerable<SeriesTaskRecord> records) {
        try {
            var list = records.ToList();

            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            using (var clear = connection.CreateCommand()) {
                clear.Transaction = transaction;
                clear.CommandText =
                    "DELETE FROM task_episodes; DELETE FROM tasks; " +
                    "DELETE FROM task_snapshots; DELETE FROM snapshot_episodes;";
                clear.ExecuteNonQuery();
            }

            using var taskCmd = Prepare(connection, transaction, TaskInsert);
            using var episodeCmd = Prepare(connection, transaction, EpisodeInsert);
            using var snapshotCmd = Prepare(connection, transaction, SnapshotInsert);
            using var snapshotEpisodeCmd = Prepare(connection, transaction, SnapshotEpisodeInsert);

            var order = 0;
            foreach (var record in list) {
                taskCmd.Parameters.Clear();
                taskCmd.Parameters.AddWithValue("$id", record.Id);
                taskCmd.Parameters.AddWithValue("$order", order++);
                taskCmd.Parameters.AddWithValue("$title", record.Title);
                taskCmd.Parameters.AddWithValue("$site", record.SiteName);
                taskCmd.Parameters.AddWithValue("$pageUrl", record.PageUrl);
                taskCmd.Parameters.AddWithValue("$outDir", record.OutputDirectory);
                taskCmd.Parameters.AddWithValue("$resolvedDir", Null(record.ResolvedDirectory));
                taskCmd.Parameters.AddWithValue("$source", Null(record.SourceName));
                taskCmd.Parameters.AddWithValue("$sourceId", Null(record.PreferredSourceId));
                taskCmd.Parameters.AddWithValue("$state", record.State);
                taskCmd.Parameters.AddWithValue("$percent", record.Percent);
                taskCmd.Parameters.AddWithValue("$bytes", record.DownloadedBytes);
                taskCmd.Parameters.AddWithValue("$report", Null(record.ReportPath));
                taskCmd.Parameters.AddWithValue("$message", Null(record.Message));
                taskCmd.Parameters.AddWithValue("$ads", record.SkippedAdSegments);
                taskCmd.Parameters.AddWithValue("$created", record.CreatedAt.ToString("O"));
                taskCmd.Parameters.AddWithValue("$finished", record.FinishedAt?.ToString("O") ?? (object)DBNull.Value);
                taskCmd.Parameters.AddWithValue("$epConc", record.EpisodeConcurrency);
                taskCmd.Parameters.AddWithValue("$segConc", record.SegmentConcurrency);
                taskCmd.Parameters.AddWithValue("$height", Null(record.PreferHeight));
                taskCmd.Parameters.AddWithValue("$ffmpeg", Null(record.FfmpegPath));
                taskCmd.Parameters.AddWithValue("$pattern", record.FileNamePattern);
                taskCmd.Parameters.AddWithValue("$subdir", record.SeriesSubdirectory ? 1 : 0);
                taskCmd.ExecuteNonQuery();

                foreach (var episode in record.Episodes) {
                    episodeCmd.Parameters.Clear();
                    episodeCmd.Parameters.AddWithValue("$task", record.Id);
                    episodeCmd.Parameters.AddWithValue("$number", episode.Number);
                    episodeCmd.Parameters.AddWithValue("$title", episode.Title);
                    episodeCmd.Parameters.AddWithValue("$status", episode.Status);
                    episodeCmd.Parameters.AddWithValue("$percent", episode.Percent);
                    episodeCmd.Parameters.AddWithValue("$bytes", episode.Bytes);
                    episodeCmd.Parameters.AddWithValue("$path", Null(episode.OutputPath));
                    episodeCmd.Parameters.AddWithValue("$error", Null(episode.Error));
                    episodeCmd.ExecuteNonQuery();
                }

                if (record.Snapshot is not { } snapshot) continue;

                snapshotCmd.Parameters.Clear();
                snapshotCmd.Parameters.AddWithValue("$task", record.Id);
                snapshotCmd.Parameters.AddWithValue("$kind", snapshot.Kind);
                snapshotCmd.Parameters.AddWithValue("$site", snapshot.SiteName);
                snapshotCmd.Parameters.AddWithValue("$pageUrl", snapshot.PageUrl);
                snapshotCmd.Parameters.AddWithValue("$seriesId", snapshot.SeriesId);
                snapshotCmd.Parameters.AddWithValue("$title", snapshot.Title);
                snapshotCmd.Parameters.AddWithValue("$headers", SerializeHeaders(snapshot.Headers));
                snapshotCmd.Parameters.AddWithValue("$sourceId", Null(snapshot.SourceId));
                snapshotCmd.Parameters.AddWithValue("$captured", snapshot.CapturedAt.ToString("O"));
                snapshotCmd.ExecuteNonQuery();

                foreach (var meta in snapshot.Episodes) {
                    snapshotEpisodeCmd.Parameters.Clear();
                    snapshotEpisodeCmd.Parameters.AddWithValue("$task", record.Id);
                    snapshotEpisodeCmd.Parameters.AddWithValue("$number", meta.Number);
                    snapshotEpisodeCmd.Parameters.AddWithValue("$title", meta.Title);
                    snapshotEpisodeCmd.Parameters.AddWithValue("$pageUrl", meta.PageUrl);
                    snapshotEpisodeCmd.Parameters.AddWithValue("$key", Null(meta.Key));
                    snapshotEpisodeCmd.Parameters.AddWithValue("$sourceId", meta.SourceId);
                    snapshotEpisodeCmd.ExecuteNonQuery();
                }
            }

            transaction.Commit();
            return true;
        } catch {
            return false;
        }
    }

    /// <summary>清空任务列表（用户点「清理已完成」并把列表清空时用）</summary>
    public void Clear() {
        try {
            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "DELETE FROM task_episodes; DELETE FROM tasks; " +
                "DELETE FROM task_snapshots; DELETE FROM snapshot_episodes;";
            cmd.ExecuteNonQuery();
        } catch {
            // 清不掉就算了
        }
    }

    private static SqliteCommand Prepare(SqliteConnection connection, SqliteTransaction transaction, string sql) {
        var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        return cmd;
    }

    private static object Null(object? value) => value ?? DBNull.Value;

    private static string? OptString(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : reader.GetString(index);

    private static int? OptInt(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : (int)reader.GetInt64(index);

    private static DateTimeOffset At(SqliteDataReader reader, int index, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(reader.GetString(index), out var at) ? at : fallback;

    private static DateTimeOffset? OptAt(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : At(reader, index, DateTimeOffset.Now);

    /// <summary>请求头是个小字典（Referer / Origin / UA），存成一列 JSON 就够了，不单独建表</summary>
    private static string SerializeHeaders(Dictionary<string, string> headers) {
        try {
            return JsonSerializer.Serialize(headers);
        } catch {
            return "{}";
        }
    }

    private static Dictionary<string, string> ParseHeaders(string json) {
        try {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        } catch {
            return new Dictionary<string, string>();
        }
    }

    // 列顺序被下面的序号读取依赖，改的时候两边一起改
    private const string TaskSelect = """
        SELECT id, sort_order, title, site_name, page_url, output_directory, resolved_directory,
               source_name, preferred_source_id, state, percent, downloaded_bytes, report_path, message,
               skipped_ad_segments, created_at, finished_at, episode_concurrency, segment_concurrency,
               prefer_height, ffmpeg_path, file_name_pattern, series_subdirectory
        FROM tasks ORDER BY sort_order;
        """;

    private const string TaskInsert = """
        INSERT INTO tasks (id, sort_order, title, site_name, page_url, output_directory, resolved_directory,
                           source_name, preferred_source_id, state, percent, downloaded_bytes, report_path, message,
                           skipped_ad_segments, created_at, finished_at, episode_concurrency, segment_concurrency,
                           prefer_height, ffmpeg_path, file_name_pattern, series_subdirectory)
        VALUES ($id, $order, $title, $site, $pageUrl, $outDir, $resolvedDir,
                $source, $sourceId, $state, $percent, $bytes, $report, $message,
                $ads, $created, $finished, $epConc, $segConc,
                $height, $ffmpeg, $pattern, $subdir);
        """;

    private const string EpisodeSelect = """
        SELECT task_id, number, title, status, percent, bytes, output_path, error
        FROM task_episodes ORDER BY task_id, number;
        """;

    private const string EpisodeInsert = """
        INSERT INTO task_episodes (task_id, number, title, status, percent, bytes, output_path, error)
        VALUES ($task, $number, $title, $status, $percent, $bytes, $path, $error);
        """;

    private const string SnapshotSelect = """
        SELECT task_id, kind, site_name, page_url, series_id, title, headers, source_id, captured_at
        FROM task_snapshots;
        """;

    private const string SnapshotInsert = """
        INSERT INTO task_snapshots (task_id, kind, site_name, page_url, series_id, title, headers, source_id, captured_at)
        VALUES ($task, $kind, $site, $pageUrl, $seriesId, $title, $headers, $sourceId, $captured);
        """;

    private const string SnapshotEpisodeSelect = """
        SELECT task_id, number, title, page_url, ep_key, source_id
        FROM snapshot_episodes ORDER BY task_id, number;
        """;

    private const string SnapshotEpisodeInsert = """
        INSERT INTO snapshot_episodes (task_id, number, title, page_url, ep_key, source_id)
        VALUES ($task, $number, $title, $pageUrl, $key, $sourceId);
        """;
}
