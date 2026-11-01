using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class MarriageSameAccountSchema
{
    const string OldConstraint = "CHECK(length(second_account)>0 AND second_account<>first_account)";
    const string NewConstraint = "CHECK(length(second_account)>0)";
    public static string Definition => MarriageSchema.RecordsSql.Replace(OldConstraint, NewConstraint, StringComparison.Ordinal);

    public static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='marriage_records'";
        string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("缺少婚姻关系表");
        string normalized = SqliteMigrations.NormalizeTable(actual, "marriage_records");
        if (normalized == SqliteMigrations.NormalizeTable(Definition, "marriage_records")) return;
        if (normalized != SqliteMigrations.NormalizeTable(MarriageSchema.RecordsSql, "marriage_records"))
            throw new InvalidDataException("同账号婚姻迁移拒绝未知关系表结构");
        // 复用已发布的精确CHECK迁移，保留全部行、外键和索引；版本由外层迁移事务推进。
        SqliteMigrations.ReplaceTableConstraint(db, tx, "marriage_records", OldConstraint, NewConstraint);
    }
}
