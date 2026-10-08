using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonPlannedMonster(
    int SourceOrdinal,
    ushort ObjectKey,
    int MonsterId,
    int CandidateLevel,
    DungeonRoomMonster Source)
{
    public byte SpawnType { get; init; }
    public bool IsBlocking { get; init; } = true;
}

// 此计划只分配房间对象身份；不建立战斗、死亡、清房或奖励状态。
internal sealed record DungeonMonsterPlan(DungeonRoomDefinition Room, IReadOnlyList<DungeonPlannedMonster> Monsters)
{
    public static DungeonMonsterPlan Create(DungeonRoomCatalog catalog, DungeonMap map, ushort characterId, ushort firstObjectKey)
        => FromRoom(catalog.Get(map), characterId, firstObjectKey);

    internal static DungeonMonsterPlan FromRoom(DungeonRoomDefinition room, ushort characterId, ushort firstObjectKey)
    {
        if (room.MonsterTeams is { } teams && teams.Count > room.Monsters.Count)
            throw new InvalidDataException("房间 monster team 索引超过怪物资源行");
        var result = new List<DungeonPlannedMonster>();
        int nextKey = firstObjectKey;
        for (int index = 0; index < room.Monsters.Count; index++)
        {
            var source = room.Monsters[index];
            var raw = source.RawValues;
            if (raw.Count != 8 || raw[0] != source.MonsterId || source.MonsterId < 0
                || source.PlacementKind != "[fixed]" || source.RankKind is not ("[normal]" or "[boss]")
                || raw[6] != 1 || raw[7] != 1)
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的 monster 资源行 {index} 不是已支持的固定怪物条件");

            // 本服明确保留3145这条完整资源声明为Lv1，不把非零标志推广成通用相对等级公式。
            bool localLevelOne = room.Map.Id == 76125 &&
                room.Map.Path == "map/cataclysm/granflores/01_mirkwood/3145_76125.map" && index == 4 &&
                source.Path == "monster/goblin/goblin.mob" && source.RankKind == "[normal]" &&
                raw.SequenceEqual(new int[] { 1, 1, 0, 503, 233, 0, 1, 1 });
            if (raw[1] != 0 && !localLevelOne)
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的 monster 资源行 {index} 相对等级来源未闭合");

            // 对象 key 与模板 ID、资源行索引独立；跳过当前角色，耗尽后拒绝而不回绕。
            if (nextKey == characterId)
                nextKey++;
            if (nextKey > ushort.MaxValue)
                throw new InvalidDataException("房间怪物对象 key 超出 ushort 容量");
            // 资源品级经模板+24进入客户端记录+14：普通0、Boss3；完整创建及战斗仍需独立验收。
            // raw[2]的完整整数在计划层保留，投影层再检查wire范围，不截断生成等级。
            result.Add(new(index, (ushort)nextKey, source.MonsterId, localLevelOne ? 1 : raw[2], source)
            {
                SpawnType = source.RankKind == "[boss]" ? (byte)3 : (byte)0,
                // team 与资源行同索引；0为友方任务对象，缺项沿用普通阻塞行为。
                IsBlocking = room.MonsterTeams is not { } monsterTeams || index >= monsterTeams.Count || monsterTeams[index] != 0
            });
            nextKey++;
        }

        return new(room, Array.AsReadOnly(result.ToArray()));
    }
}
