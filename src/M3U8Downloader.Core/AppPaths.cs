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
    /// 合并的理由、代价与迁移方式见 <see cref="Storage.SqliteDatabase"/> 与 AGENTS.md。
    /// </summary>
    public static string DatabaseFile => Path.Combine(DataDirectory, "m3u8.db");

    /// <summary>旧版的设置文件（迁移来源；迁移完会改名成 <c>.migrated</c> 留档）</summary>
    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    /// <summary>旧版的任务列表文件（迁移来源）</summary>
    public static string TasksFile => Path.Combine(DataDirectory, "tasks.json");

    /// <summary>旧版的独立历史库（迁移来源）</summary>
    public static string LegacyHistoryFile => Path.Combine(DataDirectory, "downloads.db");

    public static string LogsDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>旧版固定位置：<c>%APPDATA%\M3U8Downloader</c>（迁移用，也是不可写时的退路）</summary>
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

    /// <summary>
    /// 把旧位置（<c>%APPDATA%\M3U8Downloader</c>）里的数据搬进现在的数据目录。
    ///
    /// 只在"现在用的是程序目录下的 <c>data\</c>"且"那边还没有同名文件"时搬 ——
    /// 老用户升级后任务列表、下载历史、设置都还在，不会因为换了目录就"全丢了"。
    /// 用**复制**而不是移动：万一搬得不对，旧的那份还留着。
    /// </summary>
    /// <returns>实际搬过来的条目名（供日志/提示用）</returns>
    public static IReadOnlyList<string> MigrateLegacyData() {
        var moved = new List<string>();
        if (!IsPortable) return moved;
        if (string.Equals(DataDirectory, LegacyDirectory, StringComparison.OrdinalIgnoreCase)) return moved;

        // 三个**旧版**数据文件（现在是统一库里的表）：目标已存在就跳过（用户已经在用新目录了，别覆盖）。
        //
        // 统一库一旦存在就整个跳过：库是权威，而这三个文件在迁移完成后会被改名成 .migrated ——
        // 也就是说 data\ 里"没有同名文件"是正常状态，不判断库就会每次启动都从旧位置再抄一份回来，
        // 抄回来的还是谁也不读的废文件。
        if (!File.Exists(DatabaseFile)) {
            foreach (var name in new[] { "settings.json", "tasks.json", "downloads.db" }) {
                var from = Path.Combine(LegacyDirectory, name);
                var to = Path.Combine(DataDirectory, name);
                if (File.Exists(to) || !File.Exists(from)) continue;

                try {
                    File.Copy(from, to);
                    moved.Add(name);
                } catch {
                    // 搬不过来就算了：程序照常跑，只是那份数据留在了旧位置
                }
            }
        }

        // 诊断日志：只在新的日志目录还是空的时候搬，免得新旧日志混在一起
        try {
            var oldLogs = Path.Combine(LegacyDirectory, "logs");
            var newLogs = LogsDirectory;
            var newLogsEmpty = !Directory.Exists(newLogs) || !Directory.EnumerateFileSystemEntries(newLogs).Any();

            if (Directory.Exists(oldLogs) && newLogsEmpty) {
                Directory.CreateDirectory(newLogs);
                foreach (var file in Directory.EnumerateFiles(oldLogs)) {
                    try { File.Copy(file, Path.Combine(newLogs, Path.GetFileName(file))); } catch { }
                }
                moved.Add("logs");
            }
        } catch {
            // 日志搬不动无所谓
        }

        return moved;
    }
}
