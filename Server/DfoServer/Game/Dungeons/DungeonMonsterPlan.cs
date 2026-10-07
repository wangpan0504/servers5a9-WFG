using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonPlannedMonster(
    int SourceOrdinal,
    ushort ObjectKey,
    int MonsterId,
    int CandidateLevel,
    DungeonRoomMonster Source);

// 此计划只分配房间对象身份；不建立战斗、死亡、清房或奖励状态。
internal sealed record DungeonMonsterPlan(DungeonRoomDefinition Room, IReadOnlyList<DungeonPlannedMonster> Monsters)
{
    public static DungeonMonsterPlan Create(DungeonRoomCatalog catalog, DungeonMap map, ushort characterId, ushort firstObjectKey)
        => FromRoom(catalog.Get(map), characterId, firstObjectKey);

    internal static DungeonMonsterPlan FromRoom(DungeonRoomDefinition room, ushort characterId, ushort firstObjectKey)
    {
        var result = new List<DungeonPlannedMonster>();
        int nextKey = firstObjectKey;
        for (int index = 0; index < room.Monsters.Count; index++)
        {
            var source = room.Monsters[index];
            var raw = source.RawValues;
            if (raw.Count != 8 || raw[0] != source.MonsterId || source.MonsterId < 0
                || source.PlacementKind != "[fixed]" || source.RankKind != "[normal]"
                || raw[1] != 0 || raw[6] != 1 || raw[7] != 1)
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的 monster 资源行 {index} 不是已支持的固定普通怪物条件");

            // 对象 key 与模板 ID、资源行索引独立；跳过当前角色，耗尽后拒绝而不回绕。
            if (nextKey == characterId)
                nextKey++;
            if (nextKey > ushort.MaxValue)
                throw new InvalidDataException("房间怪物对象 key 超出 ushort 容量");
            // raw[2] 仅为候选等级，保留资源整数，不推导成长公式或其他未知字段。
            result.Add(new(index, (ushort)nextKey, source.MonsterId, raw[2], source));
            nextKey++;
        }

        return new(room, Array.AsReadOnly(result.ToArray()));
    }
}
