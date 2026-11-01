using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DfoServer.Testing;

// 每次完整快照独占一个只读连接和一致读事务；不缓存表名、行或跨请求资产。
internal static class ClientSettingsSnapshots
{
    const string Tables = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT IN ('account_client_settings','character_client_settings','character_game_settings','account_hotkey_settings','account_quick_chat','account_chat_emoticons','account_recent_emoticons','character_quest_notify') ORDER BY name";
    static readonly string[] SettingsQueries =
    [
        "SELECT * FROM account_client_settings ORDER BY account",
        "SELECT * FROM character_client_settings ORDER BY character_id",
        "SELECT * FROM character_game_settings ORDER BY character_id",
        "SELECT * FROM account_hotkey_settings ORDER BY account,profile",
        "SELECT * FROM account_quick_chat ORDER BY account,bank",
        "SELECT * FROM account_chat_emoticons ORDER BY account,emoticon_id",
        "SELECT * FROM account_recent_emoticons ORDER BY account",
        "SELECT * FROM character_quest_notify ORDER BY character_id"
    ];

    public static string Capture(string database, bool assets)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open();
        using var transaction = db.BeginTransaction(deferred: true);
        object[][] Rows(string sql)
        {
            using var command = db.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<object[]>();
            while (reader.Read())
            {
                var row = new object[reader.FieldCount];
                reader.GetValues(row);
                rows.Add(row);
            }
            return rows.ToArray();
        }
        // 保留原 JSON 层次、Sqlite GetValues 类型、NULL/blob 编码和每张表排序。
        // 未吞 busy/锁/SQL 异常；只读事务释放时结束，不提交任何业务变更。
        return assets
            ? JsonSerializer.Serialize(Rows(Tables).Select(row => Rows($"SELECT * FROM [{row[0]}] ORDER BY rowid")))
            : string.Concat(SettingsQueries.Select(query => JsonSerializer.Serialize(Rows(query))));
    }
}
