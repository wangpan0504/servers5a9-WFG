using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class InventoryItemRevisionSchema
{
    internal const string Column = "inventory_items_revision INTEGER NOT NULL DEFAULT 0 CHECK(inventory_items_revision>=0)";

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        foreach (string table in new[] { "characters", "account_cargo" })
        {
            using var column = db.CreateCommand();
            column.Transaction = tx;
            column.CommandText = $"SELECT count(*) FROM pragma_table_info('{table}') WHERE name='inventory_items_revision'";
            if (Convert.ToInt64(column.ExecuteScalar()) == 0)
                SqliteMigrations.Execute(db, tx, $"ALTER TABLE {table} ADD COLUMN " + Column);
        }
        foreach (var (_, sql) in Triggers())
            SqliteMigrations.Execute(db, tx, sql.Replace("CREATE TRIGGER ", "CREATE TRIGGER IF NOT EXISTS ", StringComparison.Ordinal));
        Validate(db, tx);
    }

    internal static void Validate(SqliteConnection db, SqliteTransaction tx)
    {
        using var command = db.CreateCommand();
        command.Transaction = tx;
        foreach (string table in new[] { "characters", "account_cargo" })
        {
            command.CommandText = $"SELECT sql FROM sqlite_schema WHERE type='table' AND name='{table}'";
            if (command.ExecuteScalar() is not string definition || !Normalize(definition).Contains(Normalize(Column), StringComparison.Ordinal))
                throw new InvalidDataException("Inventory item revision column is missing or changed: " + table);
        }
        command.CommandText = "SELECT sql FROM sqlite_schema WHERE type='trigger' AND name=$name";
        command.Parameters.AddWithValue("$name", "");
        foreach (var (name, sql) in Triggers())
        {
            command.Parameters["$name"].Value = name;
            if (command.ExecuteScalar() is not string actual || Normalize(actual) != Normalize(sql))
                throw new InvalidDataException("Inventory item revision trigger is missing or changed: " + name);
        }
    }

    static string Normalize(string sql) => string.Concat(sql.Where(c => !char.IsWhiteSpace(c)))
        .Replace("IFNOTEXISTS", "", StringComparison.OrdinalIgnoreCase).Replace("\"", "").TrimEnd(';').ToLowerInvariant();

    // 标记随原资产事务提交/回滚；位置、详情及邮件归属变化同样不能沿用旧物品快照。
    internal static IEnumerable<(string Name, string Sql)> Triggers()
    {
        foreach (string table in new[] { "character_inventory_items", "character_inventory_slots", "character_title_book_slots",
            "inventory_avatar_details", "inventory_creature_details", "mailbox_attachments" })
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            string name = $"inventory_version_{table}_{operation.ToLowerInvariant()}";
            string[] rows = operation switch { "INSERT" => ["NEW"], "DELETE" => ["OLD"], _ => ["OLD", "NEW"] };
            bool direct = table.StartsWith("character_", StringComparison.Ordinal);
            string characters = direct
                ? string.Join(" OR ", rows.Select(row => $"(id={row}.character_id AND account={row}.account)"))
                : $"(id,account) IN(SELECT character_id,account FROM character_inventory_items WHERE item_uid IN({string.Join(",", rows.Select(row => row + ".item_uid"))}))";
            string accounts = direct
                ? string.Join(" OR ", rows.Select(row => $"({row}.character_id IS NULL AND account={row}.account)"))
                : $"account IN(SELECT account FROM character_inventory_items WHERE character_id IS NULL AND item_uid IN({string.Join(",", rows.Select(row => row + ".item_uid"))}))";
            yield return (name, $"""
                CREATE TRIGGER {name} AFTER {operation} ON {table}
                BEGIN
                    UPDATE characters SET inventory_items_revision=inventory_items_revision+1 WHERE {characters};
                    UPDATE account_cargo SET inventory_items_revision=inventory_items_revision+1 WHERE {accounts};
                END;
                """);
        }
    }
}
