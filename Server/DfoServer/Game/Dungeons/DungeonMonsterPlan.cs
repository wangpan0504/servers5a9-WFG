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
    // 实验性：深渊派对波次怪（真实敌对怪：阻挡清房/开门，给经验与掉落）。非空即由 WithHellWaves 产生，START_MAP 里按"隐藏模板行"发，不进普通怪行。
    public DungeonHellWaveMonster? HellWave { get; init; }
    // 实验性：该波次怪被击杀时额外投的 hellparty 装备骰次数（每个 Order 的最后一只带；参考项目是"最后一只死的"，这里静态指定）。
    public int HellRewardRolls { get; init; }
}
internal sealed record DungeonPlannedSpecialObject(ushort ObjectKey, DungeonRoomSpecialObject Source);

// 冻结房间对象身份和阵营资格；不建立战斗、死亡、清房或奖励状态。
internal sealed record DungeonMonsterPlan(DungeonRoomDefinition Room, IReadOnlyList<DungeonPlannedMonster> Monsters)
{
    public IReadOnlyList<DungeonPlannedSpecialObject> SpecialObjects { get; init; } = Array.Empty<DungeonPlannedSpecialObject>();
    public uint NextObjectKey => (uint)Monsters.Select(monster => (int)monster.ObjectKey)
        .Concat(SpecialObjects.Select(actor => (int)actor.ObjectKey)).Concat(HellWaves.Select(wave => (int)wave.ObjectKey)).DefaultIfEmpty(0).Max() + 1;
    // 实验性（未在95客户端实证）：深渊派对房间柱子的波次怪（START_MAP 隐藏模板行 + NOTI 679）。不进 Monsters：不参与清房/开门判定，也不产生经验/掉落。
    public IReadOnlyList<DungeonHellWaveMonster> HellWaves { get; init; } = Array.Empty<DungeonHellWaveMonster>();
    // 给波次怪分配 object key（跳过角色 key）与 START_MAP 行序号（参考项目 running index：普通/APC 分别接着既有行往后排）。
    public DungeonMonsterPlan WithHellWaves(IReadOnlyList<DungeonHellWaveMonster> waves, ushort characterId, int rewardRolls = 0)
    {
        int next = (int)NextObjectKey;
        int normalIndex = Monsters.Count(monster => monster.SpawnType < 5);
        int apcIndex = Monsters.Count(monster => monster.SpawnType >= 5) + SpecialObjects.Count;
        var result = new List<DungeonHellWaveMonster>();
        foreach (var wave in waves)
        {
            if (next == characterId) next++;
            if (next > ushort.MaxValue) throw new InvalidDataException("深渊波次 object key 超出 ushort 范围");
            result.Add(wave with { ObjectKey = (ushort)next++, PacketIndex = wave.Type >= 5 ? apcIndex++ : normalIndex++ });
        }
        // 深渊装备只从最后一波掉：恶魔是前面的波次，死后出来的 APC/随从才是最后一波。
        // 死亡时再从这最后一波里挑实际最后死的那只（IsLastLivingHellWaveMember）。
        int finalOrder = result.Count == 0 ? -1 : result.Max(wave => wave.Order);
        var planned = new List<DungeonPlannedMonster>();
        foreach (var wave in result)
        {
            // 波次怪的场景坐标由客户端模板决定，服务端不知道，落点 x/y 记 0；Source 只为复用掉落/经验流水线。
            DungeonPlannedMonster monster = wave.Type >= 5
                ? new(Monsters.Count + planned.Count, wave.ObjectKey, wave.MonsterId, wave.Level, null)
                {
                    AiCharacter = new DungeonRoomAiCharacter(wave.MonsterId, [wave.MonsterId, 0, 0, 0, 0, 0], "[monster]", "[normal]", wave.Path, wave.Level),
                    SpawnType = wave.Type, IsBlocking = true, HellWave = wave
                }
                : new(Monsters.Count + planned.Count, wave.ObjectKey, wave.MonsterId, wave.Level,
                    new DungeonRoomMonster(wave.MonsterId, [wave.MonsterId, 0, wave.Level, 0, 0, 0, 0, 0], "[fixed]", "[normal]", wave.Path))
                {
                    SpawnType = 0, IsBlocking = true, HellWave = wave
                };
            planned.Add(monster with { HellRewardRolls = rewardRolls > 0 && wave.Order == finalOrder ? rewardRolls : 0 });
        }
        return this with { HellWaves = Array.AsReadOnly(result.ToArray()), Monsters = Array.AsReadOnly(Monsters.Concat(planned).ToArray()) };
    }
    public int? BasisLevel { get; init; }
    public int? PlayerTeam { get; init; }
    // 实验性（#259）：比赛副本的对阵表与对手；非比赛房间为 null。
    public DungeonTournamentPlan? Tournament { get; init; }

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
                || source.PlacementKind != "[fixed]" || source.RankKind is not ("[normal]" or "[named]" or "[champion]" or "[super champion]" or "[boss]") ||
                source.RankKind == "[named]" && (source.NpcParameter.HasValue || source.Modifiers.Count != 0 || source.RankParameterCount.HasValue) ||
                !(source.NpcParameter.HasValue ? source.Modifiers is ["[NPC]"] :
                    source.Modifiers.Count == 0 || source.RankKind == "[boss]" && source.Modifiers is ["[dummy]"] || source.IsDisplayHuntDummy)
                || raw[6] is < 0 or > 255 || raw[7] is < 0 or > 255)
                throw new NotSupportedException($"unsupported: 地图 {room.Map.Id} 的 monster 资源行 {index} 不是已支持的固定怪物条件");
            // 最后两项是参考资源中的掉落数量声明，不能把数量2误当成怪物创建条件。
            // 纯NPC行由客户端沿原ordinal取模板+70，经02A160F7写actor+45E8；不增wire字段或改变阵营/品级。

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
            // Full03 0425C88D先置模板+24=0；0425D27B的未匹配rank成功尾部保留0。
            // 仅兼容真实450001的裸[named]，保留原标记；不放开任意未知品级或附加效果。
            // 资源品级经模板+24进入客户端记录+14：普通0、精英1/2、Boss3。
            // 绝对等级保留完整整数，相对等级使用已核验基准；原始行始终保留。
            int? rawTeam = room.MonsterTeams is { } monsterTeams && index < monsterTeams.Count
                ? monsterTeams[index] : source.Team;
            int team = rawTeam is >= 0 ? rawTeam.Value : 100;
            bool friendly = playerTeam is { } actualTeam && team == actualTeam;
            result.Add(new(index, (ushort)nextKey, source.MonsterId, level, source)
            {
                SpawnType = source.RankKind switch { "[named]" => 0, "[champion]" => 1, "[super champion]" => 2, "[boss]" => 3, _ => (byte)0 },
                // 02A15E31保留显式team；02C9F43C相对本人比较，不把0或非100统一当作友军。
                Team = team,
                IsFriendly = friendly,
                IsBlocking = !source.ScriptedRetirement && !friendly && !source.IsDisplayHuntDummy
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
            // 带 [hellparty] 的柱子由地图文件在客户端本地生成。再发一条 type9 会叠出第二根柱子，打到的也不是地图脚本那根。
            if (!parent.Children.Any(child => child.Kind == "[hellparty]"))
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
