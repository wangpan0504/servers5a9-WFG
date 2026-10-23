using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// 只放宽奖励下界；死亡、任务和掉落仍共享原回执，不重建表或补发历史资产。
internal static class ZeroExperienceDungeonSchema
{
    internal const string OldGrowth = "amount INTEGER NOT NULL CHECK(amount>0)";
    internal const string NewGrowth = "amount INTEGER NOT NULL CHECK(amount>=0)";
    internal const string OldTotal = "total_experience BETWEEN 1 AND 4294967295";
    internal const string NewTotal = "total_experience BETWEEN 0 AND 4294967295";
    internal static string SettlementSql => SettlementProficiencySchema.SettlementSql.Replace(OldTotal, NewTotal, StringComparison.Ordinal);

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE name='character_dungeon_settlement_receipts' AND type='table'";
        string actual = query.ExecuteScalar() as string ?? throw new InvalidDataException("零经验迁移缺少通关回执");
        string canonical = SqliteMigrations.NormalizeTable(actual, SettlementProficiencySchema.Settlement);
        if (canonical != SqliteMigrations.NormalizeTable(SettlementProficiencySchema.SettlementSql, SettlementProficiencySchema.Settlement) &&
            canonical != SqliteMigrations.NormalizeTable(SettlementSql, SettlementProficiencySchema.Settlement))
            throw new InvalidDataException("零经验迁移遇到未知通关结构");
        SqliteMigrations.ReplaceTableConstraint(db, tx, SettlementProficiencySchema.Growth, OldGrowth, NewGrowth);
        SqliteMigrations.ReplaceTableConstraint(db, tx, SettlementProficiencySchema.Settlement, OldTotal, NewTotal);
        query.CommandText = "PRAGMA foreign_key_check";
        using var failures = query.ExecuteReader();
        if (failures.Read()) throw new InvalidDataException("零经验迁移不能保全旧回执外键");
    }
}
