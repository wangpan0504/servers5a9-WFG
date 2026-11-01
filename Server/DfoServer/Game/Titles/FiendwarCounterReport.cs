using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Titles;

// 这是受当前房间约束的客户端原生动作报告，不是服务端伤害模拟的证明；每份报告只记一次事件。
internal readonly record struct FiendwarCounterReport(uint Serial, uint Dungeon, uint Map, uint Seed,
    uint Position, uint Sequence, uint SourceKind, uint SourceId, uint SourceKey)
{
    internal bool Valid => Serial != 0 && Sequence != 0 && Position <= ushort.MaxValue &&
        SourceKey <= ushort.MaxValue && SourceKey != 0 &&
        (SourceKind == 1 && SourceId is 107013148 or 107013178 ||
         SourceKind == 2 && SourceId is >= 109035530 and <= 109035532);

    internal bool Matches(FormalDungeonReceipt room) => Valid && Dungeon == room.Entry.DungeonId &&
        Map == room.Snapshot.CurrentMap.Id && Seed == room.MapSeed &&
        Position == (uint)(room.Snapshot.CurrentMap.Position.X | room.Snapshot.CurrentMap.Position.Y << 8);
}

internal interface IFiendwarCounterStore
{
    // 重复报告返回false；事实与原房间流的消费水位必须在同一事务提交。
    bool RecordFiendwarCounter(AccountOwners.Lease owner, Character expected,
        Raids.AntonRaidBattle.FiendwarCounterFact fact, string stream, uint sequence);
}
