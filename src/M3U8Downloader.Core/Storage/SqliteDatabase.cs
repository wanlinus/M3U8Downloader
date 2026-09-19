using Microsoft.Data.Sqlite;

namespace M3U8Downloader.Core.Storage;

/// <summary>
/// 数据目录里的那个 SQLite 库（<c>data\m3u8.db</c>）：**设置、任务列表、下载历史都在里面**。
///
/// 早先是三个文件（settings.json / tasks.json / downloads.db）。合并过来的理由：
/// 三样东西本来就是同一件事的三面，各自一套"读文件→反序列化→改→原子替换"的样板代码，
/// 而且只有历史那份能按主键增量更新，另外两份每次都要整表重写。现在统一成一个库，
/// 写入是每项/每任务/每集一行，改什么写什么。
///
/// 代价（当初"Core 零第三方包"那条约定就是为了躲它）：两个项目各多一个
/// <c>e_sqlite3.dll</c>（1.9 MB）。以自包含发布的体量衡量可以接受，换来的好处是
/// **存储实现只此一份** —— 自检能直接测到真货，而不是只测得到一个替身。
///
/// 库里除业务表外还有一张 <c>meta</c>（目前只存 schema 版本）。
///
/// 所有方法都吞异常：存储坏了不该让程序起不来 —— 设置读不出就用默认值、
/// 历史记不上就少一条账、任务读不回就是空列表，程序照常跑。
/// </summary>
public sealed class SqliteDatabase {
    /// <summary>schema 版本。加表/加列时 +1，并在 <see cref="EnsureSchema"/> 里补升级语句。</summary>
    public const int SchemaVersion = 2;

    private static readonly Lazy<SqliteDatabase> Cached =
        new(() => new SqliteDatabase(AppPaths.DatabaseFile), isThreadSafe: true);

    /// <summary>数据目录里的默认库：进程内共用一份（省掉重复的建表检查）</summary>
    public static SqliteDatabase Default => Cached.Value;

    private readonly string _connectionString;

    public SqliteDatabase(string filePath) {
        FilePath = filePath;

        // 目录建不出来也别抛：每个操作自己都会吞异常，退化成"存不上"而已
        try {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        } catch {
            // 忽略：库开不起来时读写的 catch 会兜住
        }

        _connectionString = new SqliteConnectionStringBuilder {
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

    /// <summary>库文件路径</summary>
    public string FilePath { get; }

    /// <summary>
    /// 开一个连接。**调用方负责释放**（<c>using</c>）。
    /// 连接不是线程安全的，所以每次现开现关、不跨线程共享。
    /// </summary>
    public SqliteConnection Open() {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // 并发写时不要立刻报 "database is locked"，等一下比直接失败好
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    /// <summary>读一个 meta 值（没有就返回 null）</summary>
    public string? GetMeta(string name) {
        try {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT value FROM meta WHERE name = $name;";
            cmd.Parameters.AddWithValue("$name", name);
            return cmd.ExecuteScalar() as string;
        } catch {
            return null;
        }
    }

    /// <summary>写一个 meta 值</summary>
    public void SetMeta(string name, string value) {
        try {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText =
                "INSERT INTO meta (name, value) VALUES ($name, $value) " +
                "ON CONFLICT (name) DO UPDATE SET value = excluded.value;";
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$value", value);
            cmd.ExecuteNonQuery();
        } catch {
            // meta 写不进去不影响业务
        }
    }

    /// <summary>
    /// 建表 + 升级到当前 schema 版本。每次构造都跑一遍：语句全是
    /// <c>IF NOT EXISTS</c> / 有条件的 <c>ALTER TABLE</c>，重复执行没有副作用。
    /// </summary>
    private void EnsureSchema() {
        try {
            using var connection = Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = SchemaSql;
            cmd.ExecuteNonQuery();

            // 设置：**一行、一列一项**。不是 key-value —— 那样只是把 JSON 的字段拆成行，
            // 除了"在数据库里"之外没有任何好处，还丢掉了列类型。
            // 读的时候缺列/为 NULL 都退回 AppSettings 的默认值，所以升级时加列是安全的。
            cmd.CommandText = $"""
                INSERT INTO meta (key, value) VALUES ('schema_version', '{SchemaVersion}')
                ON CONFLICT (key) DO UPDATE SET value = excluded.value;
                """;
            cmd.ExecuteNonQuery();
        } catch {
            // 库建不起来（磁盘满、目录无权限）就退化成"什么都存不上"，程序照常跑
        }
    }

    private const string SchemaSql = """
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

        CREATE TABLE IF NOT EXISTS settings (
            id                          INTEGER PRIMARY KEY CHECK (id = 1),
            ffmpeg_path                 TEXT,
            ffmpeg_download_url         TEXT,
            default_output_directory    TEXT,
            episode_concurrency         INTEGER,
            segment_concurrency         INTEGER,
            auto_skip_invalid_segments  INTEGER,
            series_subdirectory         INTEGER,
            full_decode_check           INTEGER,
            minimize_to_tray_on_close   INTEGER,
            user_agent                  TEXT,
            proxy_enabled               INTEGER,
            proxy_url                   TEXT,
            check_update_on_startup     INTEGER,
            updated_at                  TEXT
        );

        CREATE TABLE IF NOT EXISTS tasks (
            id                      TEXT PRIMARY KEY,
            sort_order              INTEGER NOT NULL DEFAULT 0,
            title                   TEXT    NOT NULL DEFAULT '',
            site_name               TEXT    NOT NULL DEFAULT '',
            page_url                TEXT    NOT NULL DEFAULT '',
            output_directory        TEXT    NOT NULL DEFAULT '',
            resolved_directory      TEXT,
            source_name             TEXT,
            preferred_source_id     INTEGER,
            state                   TEXT    NOT NULL DEFAULT 'Queued',
            percent                 REAL    NOT NULL DEFAULT 0,
            downloaded_bytes        INTEGER NOT NULL DEFAULT 0,
            report_path             TEXT,
            message                 TEXT,
            skipped_ad_segments     INTEGER NOT NULL DEFAULT 0,
            created_at              TEXT    NOT NULL,
            finished_at             TEXT,
            episode_concurrency     INTEGER NOT NULL DEFAULT 2,
            segment_concurrency     INTEGER NOT NULL DEFAULT 16,
            prefer_height           INTEGER,
            ffmpeg_path             TEXT,
            file_name_pattern       TEXT    NOT NULL DEFAULT '{title}.{number:00}',
            series_subdirectory     INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE IF NOT EXISTS task_episodes (
            task_id     TEXT    NOT NULL,
            number      INTEGER NOT NULL,
            title       TEXT    NOT NULL DEFAULT '',
            status      TEXT    NOT NULL DEFAULT 'Pending',
            percent     REAL    NOT NULL DEFAULT 0,
            bytes       INTEGER NOT NULL DEFAULT 0,
            output_path TEXT,
            error       TEXT,
            PRIMARY KEY (task_id, number)
        );

        CREATE TABLE IF NOT EXISTS task_snapshots (
            task_id     TEXT PRIMARY KEY,
            kind        TEXT NOT NULL DEFAULT 'Generic',
            site_name   TEXT NOT NULL DEFAULT '',
            page_url    TEXT NOT NULL DEFAULT '',
            series_id   TEXT NOT NULL DEFAULT '',
            title       TEXT NOT NULL DEFAULT '',
            headers     TEXT NOT NULL DEFAULT '{}',
            source_id   INTEGER,
            captured_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS snapshot_episodes (
            task_id     TEXT    NOT NULL,
            number      INTEGER NOT NULL,
            title       TEXT    NOT NULL DEFAULT '',
            page_url    TEXT    NOT NULL DEFAULT '',
            ep_key      TEXT,
            source_id   INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY (task_id, number)
        );

        CREATE TABLE IF NOT EXISTS meta (
            name  TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );

        -- 站内搜索的站点清单（界面「站点」下拉框）。单独一张表而不是塞进 settings：
        -- 它是有序的列表，每项有名字和地址两个字段，塞进设置只能拼成一段 JSON。
        -- 老库升级到 2 时这张表会被 IF NOT EXISTS 建出来，不需要 ALTER。
        CREATE TABLE IF NOT EXISTS sites (
            id          INTEGER PRIMARY KEY AUTOINCREMENT,
            name        TEXT    NOT NULL DEFAULT '',
            url         TEXT    NOT NULL DEFAULT '',
            sort_order  INTEGER NOT NULL DEFAULT 0
        );
        """;
}
