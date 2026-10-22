using DfoServer.Game.Adventure;
using DfoServer.GameWorld.Adventure;
using Microsoft.Data.Sqlite;

namespace DfoServer.Persistence;

internal sealed partial class CharacterRepository
{
    // 冒险团进度写入统一走余额写入：等级、经验、余数、勇者经验、排除额与点数、胶囊共用一次 revision 递增，
    // 任一处版本失配整笔回滚，不会留下半份进度。
    static void WriteAdventureProgression(SqliteConnection db, SqliteTransaction tx, string account,
        AdventureState before, AdventureState after, AdventureCatalog catalog) =>
        WriteAdventureBalances(db, tx, account, before, after, catalog);
}
