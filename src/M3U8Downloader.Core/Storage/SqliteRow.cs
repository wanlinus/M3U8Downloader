using Microsoft.Data.Sqlite;

namespace M3U8Downloader.Core.Storage;

/// <summary>
/// 按**列名**读 <see cref="SqliteDataReader"/> 的小包装。
///
/// 为什么不直接写序号（<c>reader.GetString(0)</c>）：那样表结构一改就会**静默错位** ——
/// 在中间插一列，后面所有序号都会读到隔壁列，**不报错也不崩**，只是数据悄悄串了。
/// 按列名读则是"列名写错 → GetOrdinal 当场抛异常"，错得响亮。
///
/// 列名 → 序号只解析一次并缓存：结果集的列在 reader 打开之后就固定了，
/// 没必要每行每列都重新查一遍列名。
/// </summary>
internal sealed class SqliteRow(SqliteDataReader reader) {
    private readonly Dictionary<string, int> _ordinals = new(StringComparer.OrdinalIgnoreCase);

    private int Ord(string name) {
        if (_ordinals.TryGetValue(name, out var index)) return index;
        index = reader.GetOrdinal(name);
        _ordinals[name] = index;
        return index;
    }

    public string Str(string name) => reader.GetString(Ord(name));

    public string? StrOrNull(string name) =>
        reader.IsDBNull(Ord(name)) ? null : reader.GetString(Ord(name));

    public int Int(string name) => (int)reader.GetInt64(Ord(name));

    /// <summary>为 NULL 时退回 <paramref name="fallback"/> —— 设置表就靠这个"缺项/空值退回默认值"</summary>
    public int Int(string name, int fallback) =>
        reader.IsDBNull(Ord(name)) ? fallback : (int)reader.GetInt64(Ord(name));

    public int? IntOrNull(string name) =>
        reader.IsDBNull(Ord(name)) ? null : (int)reader.GetInt64(Ord(name));

    public long Long(string name) => reader.GetInt64(Ord(name));

    public double Double(string name) => reader.GetDouble(Ord(name));

    /// <summary>SQLite 没有 bool，存的是 0/1</summary>
    public bool Bool(string name) => reader.GetInt64(Ord(name)) != 0;

    /// <summary>为 NULL 时退回 <paramref name="fallback"/></summary>
    public bool Bool(string name, bool fallback) =>
        reader.IsDBNull(Ord(name)) ? fallback : reader.GetInt64(Ord(name)) != 0;

    /// <summary>时间戳列（存的是 ISO 8601 字符串）</summary>
    public DateTimeOffset At(string name, DateTimeOffset fallback) =>
        DateTimeOffset.TryParse(reader.GetString(Ord(name)), out var at) ? at : fallback;

    public DateTimeOffset? AtOrNull(string name) =>
        reader.IsDBNull(Ord(name)) ? null : At(name, DateTimeOffset.Now);
}
