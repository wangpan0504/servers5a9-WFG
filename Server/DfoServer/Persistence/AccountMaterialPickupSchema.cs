using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// 沿原冻结拾取账本扩展一种无实体资产，保留来源外键和其他三类收据。
internal static class AccountMaterialPickupSchema
{
    const string Table = "character_dungeon_drop_pickups";
    internal const string Column = "account_material TEXT CHECK(account_material IS NULL OR (json_valid(account_material) AND json_type(account_material)='object'))";

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='character_dungeon_drop_pickups'";
        string sql = query.ExecuteScalar() as string ?? throw new InvalidDataException("账号材料拾取迁移缺少原账本");
        if (!sql.Contains(Column, StringComparison.Ordinal))
            SqliteMigrations.Execute(db, tx, $"ALTER TABLE {Table} ADD COLUMN {Column}");
        if (!sql.Contains("CHECK((account_material IS NULL", StringComparison.Ordinal))
        {
            var (original, replacement) = AssetCheck(sql);
            SqliteMigrations.ReplaceTableConstraint(db, tx, Table, original, replacement);
        }
    }

    internal static string Definition(string sql, string table)
    {
        if (table != Table || sql.Contains("CHECK((account_material IS NULL", StringComparison.Ordinal)) return sql;
        var (original, replacement) = AssetCheck(sql);
        return sql.Replace(original, Column + "," + replacement, StringComparison.Ordinal);
    }

    static (string Original, string Replacement) AssetCheck(string sql)
    {
        int start = sql.IndexOf("CHECK((epic_piece IS NULL", StringComparison.Ordinal);
        int end = start < 0 ? -1 : sql.IndexOf("PRIMARY KEY(", start, StringComparison.Ordinal);
        if (start < 0 || end < 0) throw new InvalidDataException("账号材料拾取迁移不是已确认的原资产约束");
        string original = sql[start..end].TrimEnd();
        if (!original.EndsWith(")),", StringComparison.Ordinal))
            throw new InvalidDataException("账号材料拾取原资产约束未完整结束");
        string condition = original["CHECK(".Length..^2];
        string replacement = "CHECK((account_material IS NULL AND (" + condition + ")) OR " +
            "(account_material IS NOT NULL AND epic_piece IS NULL AND destination_slot IS NULL AND item_uid IS NULL AND " +
            "after_core IS NULL AND currency_kind IS NULL AND after_balance IS NULL AND account_revision IS NULL AND " +
            "destination_list IS NULL AND receipt_format=1 AND " +
            "json_type(account_material,'$.ItemId') IS 'integer' AND json_extract(account_material,'$.ItemId')>0 AND " +
            "json_type(account_material,'$.Amount') IS 'integer' AND json_extract(account_material,'$.Amount')=amount AND " +
            "json_type(account_material,'$.Balance') IS 'integer' AND json_extract(account_material,'$.Balance') BETWEEN amount AND 2147483647 AND " +
            "json_type(account_material,'$.WalletRevision') IS 'integer' AND json_extract(account_material,'$.WalletRevision')>0)),";
        return (original, replacement);
    }
}
