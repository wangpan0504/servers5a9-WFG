namespace DfoServer.Game.Raids;

// 由原Battle签发；从选图沿Loading传至Run，不能用当前代次替换旧选择。
internal sealed record FiendwarDungeonTarget(Guid Instance, int Area, int Dungeon, long Generation,
    long FacilitySerial, long EnemySerial, long MonsterSerial, int MazeOrdinal);

internal sealed record FiendwarFacilityEncounter(FiendwarDungeonTarget Target, int Boss, int Kind);

internal sealed class FiendwarEntryChangedException() : InvalidOperationException("fiendwar-entry-changed");
