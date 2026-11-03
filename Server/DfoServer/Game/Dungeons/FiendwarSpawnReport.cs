namespace DfoServer.Game.Dungeons;

// 客户端原生调用的生成请求，不能证明对象已成功创建或已经死亡。
internal readonly record struct FiendwarSpawnReport(uint Serial, uint Dungeon, uint Map, uint Seed,
    uint Position, uint Sequence, uint ParentKey, uint MonsterId, uint ObjectKey, ulong LeaseId = 0)
{
    internal bool Valid => Serial != 0 && Sequence != 0 && Position <= ushort.MaxValue &&
        ParentKey is > 0 and <= ushort.MaxValue && ObjectKey is > 0 and < ushort.MaxValue &&
        ParentKey != ObjectKey &&
        (MonsterId is 107013155 or 107013156 or >= 107013169 and <= 107013176 or 107013188 or
            109008114 or 109008115 or >= 109008120 and <= 109008123 or 109008139 or
            407000557 or 407000559);

    internal bool Matches(FormalDungeonReceipt room) => Valid && Dungeon == room.Entry.DungeonId &&
        Map == room.Snapshot.CurrentMap.Id && Seed == room.MapSeed &&
        Position == (uint)(room.Snapshot.CurrentMap.Position.X | room.Snapshot.CurrentMap.Position.Y << 8);

    internal FiendwarDynamicSpawn Spawn => new(checked((ushort)ParentKey), checked((int)MonsterId), checked((ushort)ObjectKey));
}
