using M3U8Downloader.Core.Sites;

namespace M3U8Downloader.Core.Storage;

/// <summary>
/// 站内搜索的站点清单：统一库（<c>data\m3u8.db</c>）里的 <c>sites</c> 表。
///
/// **为什么单独一张表，不塞进 settings**：它是个**有序的列表**（下拉框按顺序显示），
/// 每项有名字和地址两个字段。塞进"一行一列一项"的设置里，只能把整份清单拼成一段 JSON
/// 存进一列 —— 那就把设置表的设计初衷（保住列类型、改什么写什么）全丢了。
///
/// 写入是**整表覆盖**（一个事务里先 DELETE 再逐条 INSERT）：这个清单最多几十行、
/// 改一次是用户主动点「确定」，不值得为它做增量更新。
/// </summary>
public sealed class SqliteSiteStore : ISiteCatalogStore {
    private readonly SqliteDatabase _database;

    /// <summary>用统一库（不传就是数据目录里那个）</summary>
    public SqliteSiteStore(SqliteDatabase? database = null) =>
        _database = database ?? SqliteDatabase.Default;

    /// <summary>读清单；库有问题或者一条都没有，都退回内置清单</summary>
    public IReadOnlyList<SearchSite> Load() {
        var sites = new List<SearchSite>();

        try {
            using var connection = _database.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT name, url FROM sites ORDER BY sort_order, rowid;";

            using var reader = cmd.ExecuteReader();
            while (reader.Read()) {
                var row = new SqliteRow(reader);
                var url = row.StrOrNull("url");

                // 地址填坏的行直接跳过：留着它只会在下拉框里多一个必然搜不到的选项
                if (!SearchSite.TryResolve(url, out _)) continue;

                sites.Add(new SearchSite(row.StrOrNull("name") ?? "", url!.Trim()));
            }
        } catch {
            return SiteCatalog.BuiltIn;
        }

        return sites.Count > 0 ? sites : SiteCatalog.BuiltIn;
    }

    /// <summary>整表覆盖写；地址不合法的行会被丢掉（界面上应该有校验，这里是最后一道）</summary>
    public bool Save(IReadOnlyList<SearchSite> sites) {
        try {
            using var connection = _database.Open();
            using var transaction = connection.BeginTransaction();

            using (var clear = connection.CreateCommand()) {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM sites;";
                clear.ExecuteNonQuery();
            }

            var order = 0;
            foreach (var site in sites) {
                if (!SearchSite.TryResolve(site.Url, out _)) continue;

                using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    "INSERT INTO sites (name, url, sort_order) VALUES ($name, $url, $order);";
                insert.Parameters.AddWithValue("$name", site.Name.Trim());
                insert.Parameters.AddWithValue("$url", site.Url.Trim());
                insert.Parameters.AddWithValue("$order", order++);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        } catch {
            return false;
        }
    }
}
