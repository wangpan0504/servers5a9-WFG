using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonPlannedMonster(
    int SourceOrdinal,
    ushort ObjectKey,
    int MonsterId,
    int CandidateLevel,
    DungeonRoomMonster? Source)
{
    public DungeonRoomAiCharacter? AiCharacter { get; init; }
    public byte SpawnType { get; init; }
    public bool IsBlocking { get; init; } = true;
    public int EnemyType => AiCharacter is null ? 1 : 2;
    public bool CanReportDeath => AiCharacter is null ? IsBlocking : AiCharacter.Faction == "[monster]";
    public bool IsBoss => CanReportDeath && (Source?.RankKind == "[boss]" || AiCharacter?.RankKind == "[boss]");
    public byte ExperienceType => AiCharacter is null ? SpawnType : checked((byte)(SpawnType - 5));
    public bool IsScriptedBoss => Source?.RankKind == "[boss]" && Source.Modifiers.SequenceEqual(new[] { "[dummy]" });
}

// 此计划只分配房间对象身份；不建立战斗、死亡、清房或奖励状态。
internal sealed record DungeonMonsterPlan(DungeonRoomDefinition Room, IReadOnlyList<DungeonPlannedMonster> Monsters)
{
    public int? BasisLevel { get; init; }

    public static DungeonMonsterPlan Create(DungeonRoomCatalog catalog, DungeonMap map, ushort characterId, ushort firstObjectKey, int? basisLevel = null)
        => FromRoom(catalog.Get(map,requirePassiveDefinitions: false), characterId, firstObjectKey, basisLevel);

    internal static DungeonMonsterPlan FromRoom(DungeonRoomDefinition room, ushort characterId, ushort firstObjectKey, int? basisLevel = null)
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
                || source.PlacementKind != "[fixed]" || source.RankKind is not ("[normal]" or "[champion]" or "[super champion]" or "[boss]") ||
                !(source.Modifiers.Count == 0 || source.RankKind == "[boss]" && source.Modifiers.SequenceEqual(new[] { "[dummy]" }))
                || raw[6] is < 0 or > 255 || raw[7] is < 0 or > 255)
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的 monster 资源行 {index} 不是已支持的固定怪物条件");
            // 最后两项是参考资源中的掉落数量声明，不能把数量2误当成怪物创建条件。

            // 与参考服务端相同，标志1使用副本basis level加资源偏移；未知标志和缺失基础拒绝。
            if (raw[1] is not (0 or 1) || raw[1] == 1 && basisLevel is null)
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的 monster 资源行 {index} 相对等级来源缺失");
            int level = raw[1] == 0 ? raw[2] : checked(basisLevel!.Value + raw[2]);
            if (raw[1] == 1 && level is < 1 or > 255)
                throw new InvalidDataException("怪物相对等级超出创建范围");

            // 对象 key 与模板 ID、资源行索引独立；跳过当前角色，耗尽后拒绝而不回绕。
            if (nextKey == characterId)
                nextKey++;
            if (nextKey > ushort.MaxValue)
                throw new InvalidDataException("房间怪物对象 key 超出 ushort 容量");
            // 资源品级经模板+24进入客户端记录+14：普通0、精英1/2、Boss3。
            // 绝对等级保留完整整数，相对等级使用已核验基准；原始行始终保留。
            result.Add(new(index, (ushort)nextKey, source.MonsterId, level, source)
            {
                SpawnType = source.RankKind switch { "[champion]" => 1, "[super champion]" => 2, "[boss]" => 3, _ => (byte)0 },
                // team 与资源行同索引；0为友方任务对象，缺项沿用普通阻塞行为。
                IsBlocking = room.MonsterTeams is not { } monsterTeams || index >= monsterTeams.Count || monsterTeams[index] != 0
            });
            nextKey++;
        }

        for (int index = 0; index < room.AiCharacters.Count; index++)
        {
            var source = room.AiCharacters[index];
            if (source.RawValues.Count != 6 || source.RawValues[0] != source.Id || source.Id <= 0 ||
                source.Level is < 1 or > 255 || source.Faction is not ("[monster]" or "[character]" or "[neutral]") ||
                source.RankKind is not ("[normal]" or "[champion]" or "[super champion]" or "[boss]"))
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的APC资源行 {index} 尚未支持");
            if (nextKey == characterId) nextKey++;
            if (nextKey > ushort.MaxValue) throw new InvalidDataException("房间APC对象key超出ushort容量");
            // Full03 03FA9810确认类型5..8走APC构造器；APC索引属于独立MAP数组。
            result.Add(new(index,(ushort)nextKey++,source.Id,source.Level,null)
            {
                AiCharacter = source,
                SpawnType = source.RankKind switch { "[champion]" => 6, "[super champion]" => 7, "[boss]" => 8, _ => (byte)5 },
                // 两端均不把APC放入普通门阻塞集；敌对Boss单独参与终点判定。
                IsBlocking = false
            });
        }

        return new(room, Array.AsReadOnly(result.ToArray())) { BasisLevel = basisLevel };
    }
}
