using Microsoft.Data.Sqlite;
using M3U8Downloader.Core.Settings;

namespace M3U8Downloader.Core.Storage;

/// <summary>
/// 设置：统一库（<c>data\m3u8.db</c>）里的 <c>settings</c> 表。
///
/// 表是**一行、一列一项**（<c>CHECK (id = 1)</c> 保证只有一行），不是 key-value ——
/// 那样只是把 JSON 的字段拆成行，除了"在数据库里"之外没有任何好处，还丢掉了列类型。
///
/// 读的时候：**缺列、为 NULL、库里压根没有那一行，都退回 <see cref="AppSettings"/> 的默认值**。
/// 所以以后加设置项是安全的 —— 老库读出来就是新字段的默认值，
/// 不需要写 ALTER TABLE（加了列也只是让 SQL 能直接改它）。
/// </summary>
public sealed class SqliteSettingsStore {
    private const string Columns =
        "ffmpeg_path, ffmpeg_download_url, default_output_directory, episode_concurrency, " +
        "segment_concurrency, auto_skip_invalid_segments, series_subdirectory, full_decode_check, " +
        "minimize_to_tray_on_close, user_agent, proxy_enabled, proxy_url, check_update_on_startup";

    private readonly SqliteDatabase _database;

    /// <summary>用统一库（不传就是数据目录里那个）</summary>
    public SqliteSettingsStore(SqliteDatabase? database = null) =>
        _database = database ?? SqliteDatabase.Default;

    /// <summary>读设置；任何异常（库坏了、表没了）都回退到默认值</summary>
    public AppSettings Load() {
        var settings = new AppSettings();      // 默认值打底，读到什么覆盖什么

        try {
            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT {Columns} FROM settings WHERE id = 1;";

            using var reader = cmd.ExecuteReader();
            if (!reader.Read()) return settings;

            var row = new SqliteRow(reader);       // 按列名读，读不到就用构造好的默认值打底
            settings.FfmpegPath = row.StrOrNull("ffmpeg_path");
            settings.FfmpegDownloadUrl = row.StrOrNull("ffmpeg_download_url");
            settings.DefaultOutputDirectory = row.StrOrNull("default_output_directory");
            settings.EpisodeConcurrency = row.Int("episode_concurrency", settings.EpisodeConcurrency);
            settings.SegmentConcurrency = row.Int("segment_concurrency", settings.SegmentConcurrency);
            settings.AutoSkipInvalidSegments = row.Bool("auto_skip_invalid_segments", settings.AutoSkipInvalidSegments);
            settings.SeriesSubdirectory = row.Bool("series_subdirectory", settings.SeriesSubdirectory);
            settings.FullDecodeCheck = row.Bool("full_decode_check", settings.FullDecodeCheck);
            settings.MinimizeToTrayOnClose = row.Bool("minimize_to_tray_on_close", settings.MinimizeToTrayOnClose);
            settings.UserAgent = row.StrOrNull("user_agent");
            settings.ProxyEnabled = row.Bool("proxy_enabled", settings.ProxyEnabled);
            settings.ProxyUrl = row.StrOrNull("proxy_url");
            settings.CheckUpdateOnStartup = row.Bool("check_update_on_startup", settings.CheckUpdateOnStartup);
        } catch {
            return new AppSettings();
        }

        settings.Normalize();
        return settings;
    }

    /// <summary>写设置（整行覆盖）。返回是否成功，失败不抛 —— 调用方决定怎么提示。</summary>
    public bool Save(AppSettings settings) {
        try {
            settings.Normalize();

            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO settings (id, {Columns}, updated_at)
                VALUES (1, $ffmpeg, $ffmpegUrl, $outDir, $episodeConc, $segmentConc, $skipAds, $subdir,
                        $decode, $tray, $ua, $proxyOn, $proxyUrl, $checkUpdate, $at)
                ON CONFLICT (id) DO UPDATE SET
                    ffmpeg_path                = excluded.ffmpeg_path,
                    ffmpeg_download_url        = excluded.ffmpeg_download_url,
                    default_output_directory   = excluded.default_output_directory,
                    episode_concurrency        = excluded.episode_concurrency,
                    segment_concurrency        = excluded.segment_concurrency,
                    auto_skip_invalid_segments = excluded.auto_skip_invalid_segments,
                    series_subdirectory        = excluded.series_subdirectory,
                    full_decode_check          = excluded.full_decode_check,
                    minimize_to_tray_on_close  = excluded.minimize_to_tray_on_close,
                    user_agent                 = excluded.user_agent,
                    proxy_enabled              = excluded.proxy_enabled,
                    proxy_url                  = excluded.proxy_url,
                    check_update_on_startup    = excluded.check_update_on_startup,
                    updated_at                 = excluded.updated_at;
                """;

            cmd.Parameters.AddWithValue("$ffmpeg", Null(settings.FfmpegPath));
            cmd.Parameters.AddWithValue("$ffmpegUrl", Null(settings.FfmpegDownloadUrl));
            cmd.Parameters.AddWithValue("$outDir", Null(settings.DefaultOutputDirectory));
            cmd.Parameters.AddWithValue("$episodeConc", settings.EpisodeConcurrency);
            cmd.Parameters.AddWithValue("$segmentConc", settings.SegmentConcurrency);
            cmd.Parameters.AddWithValue("$skipAds", settings.AutoSkipInvalidSegments ? 1 : 0);
            cmd.Parameters.AddWithValue("$subdir", settings.SeriesSubdirectory ? 1 : 0);
            cmd.Parameters.AddWithValue("$decode", settings.FullDecodeCheck ? 1 : 0);
            cmd.Parameters.AddWithValue("$tray", settings.MinimizeToTrayOnClose ? 1 : 0);
            cmd.Parameters.AddWithValue("$ua", Null(settings.UserAgent));
            cmd.Parameters.AddWithValue("$proxyOn", settings.ProxyEnabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$proxyUrl", Null(settings.ProxyUrl));
            cmd.Parameters.AddWithValue("$checkUpdate", settings.CheckUpdateOnStartup ? 1 : 0);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.Now.ToString("O"));

            cmd.ExecuteNonQuery();
            return true;
        } catch {
            return false;
        }
    }

    private static object Null(string? value) => (object?)value ?? DBNull.Value;
}
