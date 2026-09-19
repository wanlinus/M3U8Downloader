namespace M3U8Downloader.Core;

/// <summary>
/// 程序数据放在哪里 —— 设置、任务列表、下载历史、诊断日志全都在**同一个文件夹**里。
///
/// 位置优先选**程序目录下的 <c>data\</c>**：本程序是"解压即用"的便携形态，
/// 用户把整个文件夹拷到 U 盘或另一台机器上时，任务、历史、设置会一起跟过去。
///
/// 程序目录不可写时（装在 <c>C:\Program Files</c>、只读介质、被策略锁住）自动退回
/// <c>%APPDATA%\M3U8Downloader\</c> —— 宁可在系统盘存一份，也不能让程序用不了。
///
/// 探测结果**只算一次并缓存**（<see cref="Lazy{T}"/>）：设置、任务、历史必须落在同一个
/// 目录里，各自探测一次的话，一旦出现分歧就会变成"设置读到了 A、历史写到 B"的怪现象。
/// </summary>
public static class AppPaths {
    /// <summary>数据文件夹名（位于程序目录下）</summary>
    public const string DataFolderName = "data";

    private static readonly Lazy<Resolved> Cached = new(Resolve, isThreadSafe: true);

    /// <summary>数据目录（已确保存在；连它都建不出来时会退回用户目录）</summary>
    public static string DataDirectory => Cached.Value.Directory;

    /// <summary>true = 用的是程序目录下的 <c>data\</c>（便携：整个文件夹拷走即带走全部数据）</summary>
    public static bool IsPortable => Cached.Value.Portable;

    /// <summary>退回用户目录的原因（便携时为空）——「关于」里可以如实说明</summary>
    public static string? FallbackReason => Cached.Value.Reason;

    /// <summary>
    /// 统一的数据文件：设置、任务列表、下载历史**都在这个 SQLite 库里**。
    ///
    /// 从"三个文件"（settings.json / tasks.json / downloads.db）合并过来是后来的决定 ——
    /// 合并的理由与代价见 <see cref="Storage.SqliteDatabase"/> 与 AGENTS.md。
    /// 合并时写过的旧文件迁移逻辑已经删掉了（个人项目，没有别的用户要照顾），
    /// 所以数据目录里如果还躺着那三个文件，程序**不会**读它们。
    /// </summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, "m3u8.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>旧版固定位置：<c>%APPDATA%\M3U8Downloader</c>（程序目录不可写时退到这里）</summary>
    public static string LegacyDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "M3U8Downloader");

    private sealed record Resolved(string Directory, bool Portable, string? Reason);

    private static Resolved Resolve() {
        var portable = Path.Combine(AppContext.BaseDirectory, DataFolderName);
        if (TryPrepare(portable, out _)) return new Resolved(portable, true, null);

        var fallback = LegacyDirectory;
        TryPrepare(fallback, out var error);
        return new Resolved(fallback, false, error ?? "程序目录不可写");
    }

    /// <summary>
    /// 目录能用吗：建得出来**而且真的写得进去**。
    /// 只判断"目录存在"是不够的 —— 只读介质上目录可能早就存在，写的时候才失败。
    /// </summary>
    private static bool TryPrepare(string directory, out string? error) {
        error = null;
        try {
            Directory.CreateDirectory(directory);

            // 探针文件名带随机串：多开时不会互相踩
            var probe = Path.Combine(directory, ".write-test-" + Path.GetRandomFileName());
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        } catch (Exception ex) {
            error = ex.Message;
            return false;
        }
    }
}
