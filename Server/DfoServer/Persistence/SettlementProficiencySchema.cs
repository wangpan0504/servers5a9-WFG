using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

// v74：通关与击杀回执保存本笔经验里真正记入熟练度的额度。满级入口线后角色经验到账为 0，
// 显示层用「角色到账 + 熟练度到账」构成有效经验；重复报告按回执同值回放，不重新推导。
internal static class SettlementProficiencySchema
{
    internal const string Settlement = "character_dungeon_settlement_receipts";
    internal const string Growth = "character_dungeon_growth_receipts";
    internal const string Column = "proficiency_gain INTEGER NOT NULL DEFAULT 0 CHECK(proficiency_gain BETWEEN 0 AND 4294967295)";

    // 列位于冻结分项之后、表级约束之前，与 ALTER TABLE ADD COLUMN 的归位一致。
    // 目标结构以 v70 通关分项重建后的完整列序为准（秘药、冒险团在其他加成之前）。
    internal static string SettlementSql => SettlementBreakdownSchema.SettlementSql
        .Replace(DungeonExperienceBonusSchema.JsonColumn,
            DungeonExperienceBonusSchema.JsonColumn + "," + Column, StringComparison.Ordinal);

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        Add(db, tx, Settlement);
        Add(db, tx, Growth);
    }

    // 旧行按 0 补齐，不重算历史奖励；已有列必须是同一约束定义，其余结构不在此步重建。
    static void Add(SqliteConnection db, SqliteTransaction tx, string table)
    {
        using var query = db.CreateCommand();
        query.Transaction = tx;
        query.CommandText = "SELECT count(*) FROM pragma_table_info('" + table + "') WHERE name='proficiency_gain'";
        if (query.ExecuteScalar() is 0L)
            SqliteMigrations.Execute(db, tx, "ALTER TABLE " + table + " ADD COLUMN " + Column);
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + table + "'";
        if (query.ExecuteScalar() is not string sql || !sql.Contains(Column, StringComparison.Ordinal))
            throw new InvalidDataException("熟练度到账列结构不兼容: " + table);
    }
}
