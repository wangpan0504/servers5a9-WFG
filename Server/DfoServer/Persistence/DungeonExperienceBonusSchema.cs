using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal static class DungeonExperienceBonusSchema
{
    internal const string Table = "character_dungeon_settlement_receipts";
    internal const string OldTotal = "total_experience=base_experience+rank_experience+growth_contract_experience";
    internal const string NewTotal = OldTotal + "+other_bonus_experience";
    internal const string OtherColumn = "other_bonus_experience INTEGER NOT NULL DEFAULT 0 CHECK(other_bonus_experience BETWEEN 0 AND 4294967295)";
    internal const string JsonColumn = "bonus_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(bonus_json) AND json_type(bonus_json)='array')";
    const string GrowthColumn = "growth_contract_experience INTEGER NOT NULL DEFAULT 0 CHECK(growth_contract_experience BETWEEN 0 AND 4294967295),";

    internal static string Sql => DungeonSceneSettlementSchema.SettlementSql
        .Replace(OldTotal, NewTotal, StringComparison.Ordinal)
        .Replace(GrowthColumn, GrowthColumn + OtherColumn + "," + JsonColumn + ",", StringComparison.Ordinal);

    internal static void Upgrade(SqliteConnection db, SqliteTransaction tx)
    {
        using var query = db.CreateCommand(); query.Transaction = tx;
        query.CommandText = "SELECT sql FROM sqlite_schema WHERE type='table' AND name='" + Table + "'";
        string actual = (string?)query.ExecuteScalar() ?? throw new InvalidDataException("缺少通关经验账本");
        string normalized = SqliteMigrations.NormalizeTable(actual, Table);
        if (normalized == SqliteMigrations.NormalizeTable(Sql, Table)) return;
        // 从当前 schema 回退重放的夹具可能已带 v74 追加的熟练度到账列，本步视为已应用。
        if (normalized == SqliteMigrations.NormalizeTable(SettlementProficiencySchema.SettlementSql, Table)) return;
        // 目标结构已含秘药/冒险团分项（本步已执行或已由后续迁移补齐）时无事可做。
        if (normalized == SqliteMigrations.NormalizeTable(SettlementBreakdownSchema.SettlementSql, Table)) return;
        // 本地整合构建先于本步建出的结构：只缺其他加成与冻结分项。
        if (normalized == SqliteMigrations.NormalizeTable(SettlementBreakdownSchema.PanelNumberedSettlementSql, Table))
        {
            SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Table + " ADD COLUMN " + OtherColumn);
            SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Table + " ADD COLUMN " + JsonColumn);
            SqliteMigrations.ReplaceTableConstraint(db, tx, Table,
                "total_experience=base_experience+rank_experience+growth_contract_experience+potion_experience+adventure_experience)",
                "total_experience=base_experience+rank_experience+growth_contract_experience+potion_experience+adventure_experience+other_bonus_experience)");
            return;
        }
        if (normalized != SqliteMigrations.NormalizeTable(DungeonSceneSettlementSchema.SettlementSql, Table))
            throw new InvalidDataException("经验加成迁移拒绝未知通关账本结构");
        SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Table + " ADD COLUMN " + OtherColumn);
        SqliteMigrations.Execute(db, tx, "ALTER TABLE " + Table + " ADD COLUMN " + JsonColumn);
        // 原位扩展已知约束，不重建父子账本，不触发历史行或改动UID/角色revision。
        SqliteMigrations.ReplaceTableConstraint(db, tx, Table, OldTotal + ")", NewTotal + ")");
    }
}
