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
    public int Team { get; init; } = Source?.Team is >= 0 ? Source.Team.Value : 100;
    public bool IsFriendly { get; init; }
    public bool IsSpecialChild => Source?.SpecialChild is not null;
    public bool IsScriptedRetirement => Source?.ScriptedRetirement == true;
    public int EnemyType => AiCharacter is null ? 1 : 2;
    public bool CanReportDeath => AiCharacter is null ? IsBlocking : AiCharacter.Faction == "[monster]";
    public bool IsBoss => CanReportDeath && (Source?.RankKind == "[boss]" || AiCharacter?.RankKind == "[boss]");
    public byte ExperienceType => AiCharacter is null ? SpawnType : checked((byte)(SpawnType - 5));
    public bool IsScriptedBoss => Source?.RankKind == "[boss]" && Source.Modifiers.SequenceEqual(new[] { "[dummy]" });
}
internal sealed record DungeonPlannedSpecialObject(ushort ObjectKey, DungeonRoomSpecialObject Source);

// 冻结房间对象身份和阵营资格；不建立战斗、死亡、清房或奖励状态。
internal sealed record DungeonMonsterPlan(DungeonRoomDefinition Room, IReadOnlyList<DungeonPlannedMonster> Monsters)
{
    public IReadOnlyList<DungeonPlannedSpecialObject> SpecialObjects { get; init; } = Array.Empty<DungeonPlannedSpecialObject>();
    public uint NextObjectKey => (uint)Monsters.Select(monster => (int)monster.ObjectKey)
        .Concat(SpecialObjects.Select(actor => (int)actor.ObjectKey)).DefaultIfEmpty(0).Max() + 1;
    public int? BasisLevel { get; init; }
    public int? PlayerTeam { get; init; }

    public static DungeonMonsterPlan Create(DungeonRoomCatalog catalog, DungeonMap map, ushort characterId, ushort firstObjectKey,
        int? basisLevel = null, int? playerTeam = null)
        => FromRoom(catalog.Get(map,requirePassiveDefinitions: false), characterId, firstObjectKey, basisLevel, playerTeam: playerTeam);

    internal static DungeonMonsterPlan FromRoom(DungeonRoomDefinition room, ushort characterId, ushort firstObjectKey, int? basisLevel = null,
        bool includeSpecial = true, int? playerTeam = null)
    {
        if (basisLevel is < 1 or > byte.MaxValue)
            throw new InvalidDataException("副本基准等级超出演员创建范围");
        if (room.MonsterTeams is { } teams && teams.Count > room.Monsters.Count)
            throw new InvalidDataException("房间 monster team 索引超过怪物资源行");
        if (playerTeam is < 0)
            throw new InvalidDataException("本人阵营不能使用怪物的负值回退声明");
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
            int? rawTeam = room.MonsterTeams is { } monsterTeams && index < monsterTeams.Count
                ? monsterTeams[index] : source.Team;
            int team = rawTeam is >= 0 ? rawTeam.Value : 100;
            bool friendly = playerTeam is { } actualTeam && team == actualTeam;
            result.Add(new(index, (ushort)nextKey, source.MonsterId, level, source)
            {
                SpawnType = source.RankKind switch { "[champion]" => 1, "[super champion]" => 2, "[boss]" => 3, _ => (byte)0 },
                // 02A15E31保留显式team；02C9F43C相对本人比较，不把0或非100统一当作友军。
                Team = team,
                IsFriendly = friendly,
                IsBlocking = !source.ScriptedRetirement && !friendly
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
                IsBlocking = false,
                IsFriendly = source.Faction is "[character]" or "[neutral]"
            });
        }

        var objects = new List<DungeonPlannedSpecialObject>();
        foreach (var parent in includeSpecial ? room.SpecialObjects : [])
        {
            if (parent.SourceOrdinal is < 0 or > sbyte.MaxValue || parent.RawValues.Count != 5 || parent.RawValues[0] != parent.ObjectId ||
                parent.RawValues[4] != parent.Children.Count)
                throw new NotSupportedException("特殊父对象身份或关联范围未支持");
            objects.Add(new(Allocate(), parent));
            foreach (var child in parent.Children)
            {
                if (child.Kind != "[monster]") continue; // 其他子规则由实际父脚本消费，保留原声明、不冒充怪物。
                if (child.Path is null || child.RawValues.Count != 5 || child.RawValues[0] <= 0)
                    throw new NotSupportedException("特殊怪物缺少已确认资源或完整子声明");
                int level = child.RawValues[1] <= 0 ? basisLevel ?? 0 : child.RawValues[1];
                if (level is < 1 or > byte.MaxValue)
                    throw new NotSupportedException("特殊怪物的自动等级需要有效副本基准等级");
                var source = new DungeonRoomMonster(child.RawValues[0], child.RawValues, "[special passive object]", "[normal]", child.Path)
                    { SpecialParent = parent, SpecialChild = child };
                // 子actor仅通过父对象产生，不作为普通清房阻塞目标；原生报告仍绑定同一key与实例核验。
                result.Add(new(result.Count, Allocate(), source.MonsterId, level, source) { IsBlocking = false });
            }
        }
        return new(room, Array.AsReadOnly(result.ToArray()))
        {
            SpecialObjects = Array.AsReadOnly(objects.ToArray()),
            BasisLevel = basisLevel,
            PlayerTeam = playerTeam
        };

        ushort Allocate()
        {
            if (nextKey == characterId) nextKey++;
            if (nextKey > ushort.MaxValue) throw new InvalidDataException("特殊actor的对象key耗尽");
            return checked((ushort)nextKey++);
        }
    }
}
