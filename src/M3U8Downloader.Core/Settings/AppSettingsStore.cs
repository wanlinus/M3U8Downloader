using M3U8Downloader.Core.Storage;

namespace M3U8Downloader.Core.Settings;

/// <summary>
/// 设置的对外入口。
///
/// **设置存在统一库（<c>data\m3u8.db</c>）的 <c>settings</c> 表里**，不再是独立的
/// <c>settings.json</c>。这里保留静态方法是因为 Core 里有三处随手调用它：
/// <c>FfmpegLocator</c> 找 ffmpeg、<c>HlsDownloader</c> 与 <c>SiteResolver</c> 取代理 ——
/// 换成注入式接口会把这四处一起牵动，而"读一下当前设置"这种事不值得。
///
/// 读写的实现在 <see cref="SqliteSettingsStore"/>。任何异常都回退到默认值：
/// 设置坏了也不该让程序起不来。
/// </summary>
public static class AppSettingsStore {
    private static readonly Lazy<SqliteSettingsStore> Store =
        new(() => new SqliteSettingsStore(), isThreadSafe: true);

    /// <summary>数据目录（设置、任务列表、下载历史都在这个目录里的那个库中）</summary>
    public static string SettingsDirectory => AppPaths.DataDirectory;

    /// <summary>
    /// 数据文件完整路径（就是那个库）——「关于」与命令行用它告诉用户"数据存在哪"。
    /// 名字不再叫"设置文件"：设置只是库里的三张表之一。
    /// </summary>
    public static string DataFilePath => AppPaths.DatabaseFile;

    /// <summary>读设置；库坏了、表没了都退回默认值</summary>
    public static AppSettings Load() => Store.Value.Load();

    /// <summary>写设置；返回是否成功（失败不抛，由调用方决定怎么提示）</summary>
    public static bool Save(AppSettings settings) => Store.Value.Save(settings);
}
