using System.Globalization;
using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 通关事务内推进账号冒险团：点数、胶囊、月份与限购沿用统一余额写入，再补等级、经验、余数与勇者经验。
    // 两步都按 revision 精确匹配，任一步失配整笔回滚，不留下半份进度。
    static void WriteAdventureProgression(SqliteConnection db, SqliteTransaction tx, string account,
        AdventureState before, AdventureState after, AdventureCatalog catalog)
    {
        WriteAdventureBalances(db, tx, account, before, after, catalog);
        using var command = db.CreateCommand(); command.Transaction = tx;
        command.Parameters.AddWithValue("$account", account);
        command.Parameters.AddWithValue("$level", after.Level);
        command.Parameters.AddWithValue("$experience", checked((long)after.Experience));
        command.Parameters.AddWithValue("$remainder", after.Remainder.ToString(CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$brave", checked((long)after.BraveExperience));
        command.Parameters.AddWithValue("$revision", before.Revision + 1);
        command.CommandText = """
            UPDATE account_adventures SET level=$level,experience=$experience,remainder=$remainder,brave_experience=$brave
            WHERE account=$account AND revision=$revision
            """;
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidOperationException("冒险团等级经验版本失配");
    }
}
