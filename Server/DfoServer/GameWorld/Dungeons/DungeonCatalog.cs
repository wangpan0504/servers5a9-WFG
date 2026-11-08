using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal readonly record struct DungeonPoint(int X, int Y);
// 0409B7E0 按物品编号写入定义 +0xDCC，后两个整数落在节点 +0x10 和 +0x14。
// Third 保留资源原值；018E6EDC区分非零消耗/零持有，多行AND/OR仍未接入。
internal readonly record struct DungeonRequiredItemRow(int ItemId, int Count, int Third);
// 迷宫 [randomized object creation] 中已闭合的单对象行。世界坐标来自 [pos]，[map] 只表示所在格。
internal sealed record DungeonRandomizedObject(DungeonPoint Map, int ObjectId, string Team, int PositionX, int PositionY)
{
    // 02A004B0消费29第五个u32：0为原骑乘初始化，1为Native21被动生成；模板命名空间不能代替此字段。
    public int SpawnMode { get; init; }
}
internal sealed record DungeonMap(string Kind, DungeonPoint Position, int Id, string Path)
{
    public int StoryStageIndex { get; init; } = -1;
    public int LayerIndex { get; init; } = -1;
    public int DummyStageIndex { get; init; } = -1;
    public bool IsConditional { get; init; }
    public IReadOnlyList<int> CandidateIds { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> CandidateWeights { get; init; } = Array.Empty<int>();
}
internal sealed record DungeonRawSection(string Tag, IReadOnlyList<PvfToken> Tokens);
// 实验性（未在95客户端实证）：深渊派对房间 = （maze 内或副本头）[hell dungeon] 1 + [seal door map index]（深渊房间地图）+ [seal door pos]（该房间在迷宫内的坐标）。
// 依据参考项目 dungeon_hell_room.go（对应 A21 FindHellMapRoom/TryBuildSealDoorHellPartyRoom）。
internal sealed record DungeonHellPartyRoom(int MapId, DungeonPoint Room)
{
    // 实验性：[hell party required item count]（95 PVF 里每个深渊 dgn 显式声明的入场票数，黑暗都市=30）；缺省 0 = 声明缺失，按等级公式回退。
    public int RequiredItemCount { get; init; }
    // 实验性：[seal door appear rate]（普通入场时密门出现概率声明；仅保留数据，参考项目 90 与已知协议都没有对应的密门运行时，未实现随机出现）。
    public int AppearRate { get; init; }
    // 实验性：传奇深渊 [seal door legend map index]（黑暗都市=313169）与 [hell legend party required item count]（=2）；0=无传奇深渊。
    public int LegendMapId { get; init; }
    public int LegendRequiredCount { get; init; }
}
internal sealed record DungeonMaze(
    int SourceOrdinal,
    int Width,
    int Height,
    string Greed,
    IReadOnlyList<DungeonPoint> Starts,
    IReadOnlyList<DungeonPoint> Bosses,
    IReadOnlyList<DungeonMap>? MapSpecification,
    IReadOnlyList<PvfToken> QuestConnectionTokens)
{
    // 实验性：只有普通（非任务连接）maze 带密门深渊房间时才非空；任务 maze 带 [escape hell]，不属于深渊派对。
    public DungeonHellPartyRoom? HellParty { get; init; }
    // 实验性：深渊房间地图的 PVF 路径（[seal door map index] 对应的 map 文件），用于把 HellParty.Room 处的普通房间换成深渊房间。
    public string? HellPartyPath { get; init; }
    public string? HellLegendPath { get; init; }
    public IReadOnlyList<DungeonMap> LayeredMaps { get; init; } = Array.Empty<DungeonMap>();
    // 保留覆盖前来源供资源诊断；不作为当前显式行的候选或演员准备输入。
    public IReadOnlyList<DungeonMap> OverriddenMaps { get; init; } = Array.Empty<DungeonMap>();
    // 同格普通图与Boss图在客户端有独立候选容器，不属于覆盖历史。
    public IReadOnlyList<DungeonMap> MapAlternatives { get; init; } = [];
    // 入场冻结的假Boss及最终真Boss；阶段身份区分同坐标、同模板的再次生成。
    public IReadOnlyList<DungeonMap> DummyStages { get; init; } = [];
    public IReadOnlyList<DungeonMap> StoryStages { get; init; } = Array.Empty<DungeonMap>();
    public DungeonBossEntrance? BossEntrance { get; init; }
    public IReadOnlyList<DungeonWarpMap> WarpMaps { get; init; } = Array.Empty<DungeonWarpMap>();
    public IReadOnlyList<DungeonMap> ConditionalMaps { get; init; } = Array.Empty<DungeonMap>();
    public IReadOnlyList<DungeonClearCondition> ClearConditions { get; init; } = [];
    public IReadOnlyList<DungeonClearPartyBuffCard> ClearPartyBuffCards { get; init; } = [];
    public IReadOnlyList<DungeonRandomizedObject> RandomizedObjects { get; init; } = [];
    public IReadOnlyList<DungeonRandomizedObjectGroup> RandomizedObjectGroups { get; init; } = [];
    public string? ResourceError { get; init; }
    public bool IsTrainingRoom { get; init; }
    public bool IsClassExperience { get; init; }
    // 仅由4000的严格资源解析设置；专用静态连接不代表正式限时玩法已接通。
    public bool IsLostLand { get; init; }
    public bool IsGriefTower { get; init; }
    public bool IsBossRush { get; init; }
    // 资源池已核验的同层唯一MAP；静态拓扑必须继续匹配它，不能只凭模式布尔值放宽BB。
    public DungeonMap? GriefRoom { get; init; }
    public bool IsDespairTower { get; init; }
    public bool IsUltimateAltar { get; init; }
    public bool IsDimensionHiddenBoss { get; init; }
    public bool IsAncientInfiniteVariant { get; init; }
    // 时空裂缝特殊迷宫额外产两片史诗碎片；身份取当前maze声明，不按地图或领主名称猜测。
    public bool IsInfernoSpecial { get; init; }
    // 实验性（#259）：比赛副本的 1x1 单图迷宫，地图取自 PVF 地图反向 [dungeon] 声明。
    public bool IsTournament { get; init; }
    public DungeonMap? WeddingCoupleRoom { get; init; }
    public DungeonMap? MockBattleRoom { get; init; }
    // 仅识别已确认的三个整数；原始 token 继续保留给资源诊断。
    public bool ConnectsQuest(uint questId, byte difficulty = 0, int state = 0) => QuestConnectionTokens.Count == 3 &&
        QuestConnectionTokens.All(token => token.Type == 0) &&
        QuestConnectionTokens[0].Value == state && QuestConnectionTokens[1].Value == questId &&
        (QuestConnectionTokens[2].Value == -1 || QuestConnectionTokens[2].Value == difficulty);
    public int? ConnectionQuestId => QuestConnectionTokens.Count == 3 && QuestConnectionTokens.All(token => token.Type == 0) &&
        QuestConnectionTokens[0].Value is 0 or 1 && QuestConnectionTokens[1].Value is > 0 and < 30000 && QuestConnectionTokens[2].Value is >= -1 and <= 4
            ? QuestConnectionTokens[1].Value : null;
    public bool HasActiveQuestConnection(int questId) => ConnectionQuestId == questId && QuestConnectionTokens[0].Value == 0;

    // 布局实质相同判定：同一任务连接的 maze 可能在资源里重复出现（如 SunderLandDark 的转职任务
    // 7104/7105 各重复一次），字段完全一致时视为同一路线，避免入场被歧义拒绝。
    public bool SameLayout(DungeonMaze other) =>
        Width == other.Width && Height == other.Height && Greed == other.Greed && ResourceError == other.ResourceError &&
        Starts.SequenceEqual(other.Starts) && Bosses.SequenceEqual(other.Bosses) &&
        SameMaps(MapSpecification, other.MapSpecification) && SameMaps(LayeredMaps, other.LayeredMaps) &&
        SameMaps(MapAlternatives, other.MapAlternatives) && SameMaps(DummyStages, other.DummyStages) &&
        SameMaps(StoryStages, other.StoryStages) && SameMaps(ConditionalMaps, other.ConditionalMaps) &&
        WarpMaps.SequenceEqual(other.WarpMaps) && ClearConditions.SequenceEqual(other.ClearConditions) &&
        ClearPartyBuffCards.SequenceEqual(other.ClearPartyBuffCards) &&
        RandomizedObjects.SequenceEqual(other.RandomizedObjects) &&
        SameObjectGroups(RandomizedObjectGroups, other.RandomizedObjectGroups) &&
        IsBossRush == other.IsBossRush && IsInfernoSpecial == other.IsInfernoSpecial && SameEntrance(BossEntrance, other.BossEntrance);

    static bool SameObjectGroups(IReadOnlyList<DungeonRandomizedObjectGroup> left, IReadOnlyList<DungeonRandomizedObjectGroup> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.SelectCount == pair.Second.SelectCount &&
            pair.First.Regenerate == pair.Second.Regenerate && pair.First.MinimapIcon == pair.Second.MinimapIcon &&
            pair.First.Objects.SequenceEqual(pair.Second.Objects));

    static bool SameEntrance(DungeonBossEntrance? left, DungeonBossEntrance? right) =>
        left is null || right is null ? left is null && right is null :
        left.TimeMode == right.TimeMode && left.TimeValue == right.TimeValue && left.Targets.SequenceEqual(right.Targets);

    static bool SameMaps(IReadOnlyList<DungeonMap>? left, IReadOnlyList<DungeonMap>? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
            if (!SameMap(left[i], right[i])) return false;
        return true;
    }

    static bool SameMap(DungeonMap left, DungeonMap right) =>
        left.Kind == right.Kind && left.Position == right.Position && left.Id == right.Id && left.Path == right.Path &&
        left.StoryStageIndex == right.StoryStageIndex && left.LayerIndex == right.LayerIndex && left.IsConditional == right.IsConditional &&
        left.DummyStageIndex == right.DummyStageIndex &&
        left.CandidateIds.SequenceEqual(right.CandidateIds) && left.CandidateWeights.SequenceEqual(right.CandidateWeights);

    // 只描述资源结构，不表示准入通过或可以向客户端发布初始化通知。
    public string MapStructure => MapSpecification is null ? "unsupported" : "explicit";
    public string? UnsupportedReason => ResourceError ?? (MapSpecification is null ? "缺少显式 map specification，地图生成合同未实现" : null);
    // 资源查询及inspect JSON提供整数索引；未知格式不会阻断其他maze或生成已批准通路。
    public DungeonGreedGrid GreedGrid => DungeonGreedGrid.Read(Width, Height, Greed);
    public DungeonStaticTopology StaticTopology => DungeonStaticTopology.Read(this);
}

internal sealed record DungeonRequiredItem(int ItemId, int Count, int Flag);

internal sealed record DungeonDefinition(int Id, string Path, int? MinimumLevel, int? BasisLevel, IReadOnlyList<DungeonMaze> Mazes, IReadOnlyList<DungeonRawSection> RawSections)
{
    public int? LimitTimeSeconds => RawSections.Where(row => row.Tag == "limit time").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 } seconds] }] ? seconds.Value : null;
    public ImmutableHashSet<int> DisjointForbiddenDropRarities { get; init; } = ImmutableHashSet<int>.Empty;
    // 客户端逐段覆盖；舰炮先声明4，raid段再声明12，取最后一段而非拒绝重复标签。
    public int? CoinLimit => RawSections.LastOrDefault(row => row.Tag == "coin limit") is
        { Tokens: [{ Type: 0, Value: >= 0 } count] } ? count.Value : null;
    internal IReadOnlyList<int> PersonalCoinInfoLimits { get; init; } = [];
    public int? PersonalCoinInfoLimit(byte difficulty) => difficulty < PersonalCoinInfoLimits.Count
        ? PersonalCoinInfoLimits[difficulty] : null;
    public byte AltarKind => RawSections.Where(row => row.Tag == "blood dungeon").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 1 or 2 } kind] }] ? (byte)kind.Value : (byte)0;
    public int AltarMaxRounds => RawSections.Where(row => row.Tag == "blood max round").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 and <= byte.MaxValue } count] }] ? count.Value : 0;
    public int DummyAppearCount => RawSections.Where(row => row.Tag == "dummy appear count").ToArray() is
        [{ Tokens: [{ Type: 0, Value: >= 0 and < ushort.MaxValue } count] }] ? count.Value : 0;
    public string? DungeonType { get; init; }
    // 仅三个正式泰波尔斯模式，任务版与升级版继续走各自合同。
    public bool IsTayberrs => Id is 291100320 or 291100322 or 5349 && WarriorsDungeon == "Tayberrs";
    public bool IsTayberrsStory => Id == 291100321 &&
        string.Equals(Path, "dungeon/legend/tayberrs/tayberrs_quest.dgn", StringComparison.OrdinalIgnoreCase);
    public SpecialDungeonClearReward? SpecialClearReward { get; init; }
    public bool IsOperationHope => RawSections.Where(row => row.Tag == "operation hope dungeon").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 1 }] }];
    public bool IsTutorial => RawSections.Where(row => row.Tag == "tutorial dungeon").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 1 }] }];
    // [crack of dimension dungeon] 正数：异次元裂缝副本，通关奖励读 etc/crackofdimensionlist.etc。
    public bool IsCrackOfDimension => RawSections.Where(row => row.Tag == "crack of dimension dungeon").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 }] }];
    // 独立模式声明残缺时也不能落回普通入场，绕过任务和双材料消耗。
    public bool RequiresCosmoEntryContract => Id == DungeonCosmoRewardCatalog.DungeonId || RawSections.Any(row => row.Tag == "cosmo dungeon");
    // 英雄模式是独立正式副本，畸形标签或路径不能降级为普通魔界大战战役。
    public bool RequiresEvildomWarChallengeContract => Id == 400000105 ||
        Path.StartsWith("dungeon/gcontents/200114_theevildomwar_challenge/", StringComparison.OrdinalIgnoreCase) ||
        RawSections.Any(row => row.Tag == "evildom war challenge dungeon");
    public bool IsEvildomWarChallenge => Id == 400000105 &&
        Path.Equals("dungeon/gcontents/200114_theevildomwar_challenge/400000105_eastharlem.dgn", StringComparison.OrdinalIgnoreCase) &&
        RawSections.Where(row => row.Tag == "evildom war challenge dungeon").ToArray() is [{ Tokens.Count: 0 }];
    public int? MinimumPartyCount => RawSections.Where(row => row.Tag == "min party count").ToArray() is
        [{ Tokens: [{ Type: 0, Value: >= 1 and <= 4 } count] }] ? count.Value : null;
    public bool IsCosmoDungeon => Id == DungeonCosmoRewardCatalog.DungeonId &&
        RawSections.Where(row => row.Tag == "cosmo dungeon").ToArray() is [{ Tokens.Count: 0 }];
    // 时空裂缝（[inferno dungeon]）：整条路线本身就是深渊场地，没有 [seal door] 密门，只能按深渊派对模式进入。
    // 与超星空共用副本头的 [hell dungeon] 1 与 [hell party required item count]，用 [inferno dungeon] 区分。
    public bool IsInfernoDungeon => !RequiresCosmoEntryContract &&
        RawSections.Any(row => row.Tag == "inferno dungeon" && row.Tokens.Count == 0) &&
        RawSections.Any(row => row.Tag == "hell dungeon" && row.Tokens is [{ Type: 0, Value: 1 }]);
    // 时空裂缝的前置任务（副本头 [inferno quest]）：与世界地图同一副本的 [is clear quest] 是同一条。
    public int InfernoQuestId => RawSections.Where(row => row.Tag == "inferno quest").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 and <= ushort.MaxValue } quest] }] ? quest.Value : 0;
    // 副本头的邀请函数量：超星空 40、地轨中心时空裂缝 30、列车 22。缺失为 0，不能退化成免费入场。
    public int HellPartyItemCount => RawSections.Where(row => row.Tag == "hell party required item count").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 and <= ushort.MaxValue } count] }] ? count.Value : 0;
    public int CosmoInvitationCount => HellPartyItemCount;
    public int CosmoStoneCount => RawSections.Where(row => row.Tag == "hell party required item count add").ToArray() is
        [{ Tokens: [{ Type: 0, Value: -1 }, { Type: 0, Value: > 0 and <= ushort.MaxValue } count] }] ? count.Value : 0;
    public int CosmoQuestId => RawSections.Where(row => row.Tag == "cosmo quest").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 and <= ushort.MaxValue } quest] }] ? quest.Value : 0;
    // [tournament dungeon] 1：魂·黄龙/青龙大会等比赛副本（实验性，依据来自 Go 项目反汇编）。
    public bool IsTournament => RawSections.Where(row => row.Tag == "tournament dungeon").ToArray() is [{ Tokens: [{ Type: 0, Value: 1 }] }];
    public ushort? TournamentRoundFatigue => RawSections.Where(row => row.Tag == "tournament round fatigue").ToArray() is
        [{ Tokens: [{ Type: 0, Value: >= 0 and <= ushort.MaxValue } amount] }] ? (ushort)amount.Value : null;
    public DungeonTournamentRewards? TournamentRewards { get; init; }
    // 团本：raid dungeon=1，raid normal dungeon/anton dungeon 为无参标记。冒险团推荐通关不计团本。
    public bool IsRaid => RawSections.Any(row => row.Tag == "raid dungeon" && row.Tokens is [{ Type: 0, Value: 1 }]) ||
        RawSections.Any(row => (row.Tag is "raid normal dungeon" or "anton dungeon") && row.Tokens.Count == 0);
    // 资源识别与正式准入分开；畸形声明也不能落回普通入场。
    public bool RequiresGriefEntryContract => Id == 5115 || GriefFloor is not null ||
        Path.StartsWith("dungeon/towers/grief/", StringComparison.OrdinalIgnoreCase) ||
        RawSections.Any(row => row.Tag == "tower of grief");
    public GriefTowerFloor? GriefFloor { get; init; }
    public bool IsDespairTower => RawSections.Where(row => row.Tag == "tower of despair").ToArray() is [{ Tokens.Count: 0 }];
    public bool IsBossRushDungeon => string.Equals(DungeonType?.Trim(), "boss rush dungeon", StringComparison.OrdinalIgnoreCase);
    public int TimeoutSeconds => RawSections.Where(row => row.Tag == "dungeon timeout").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 } seconds, { Type: 0, Value: 1 }] }] ? seconds.Value : 0;
    public bool IsDazzlementTower => string.Equals(DungeonType?.Trim(), "tower of dazzlement", StringComparison.OrdinalIgnoreCase);
    // GM伴侣AIC只在普通副本注入。祭坛（[blood dungeon]）、塔与亡者峡谷各图由资源自带AI演员、
    // 波次或楼层合同，再叠加伴侣会与地图断言、重复演员或楼层规则冲突；按资源标记整类排除，新增同类副本自动覆盖。
    // Boss Rush（337）由用户指定放行，与普通副本同一合同。
    static readonly string[] CompanionBlockedTypes = ["advance altar", "tower of death", "tower of boss"];
    public bool AllowsCompanionAi => AltarKind == 0 && !IsDespairTower && !IsDazzlementTower &&
        !RequiresGriefEntryContract && !Path.StartsWith("dungeon/towers/", StringComparison.OrdinalIgnoreCase) &&
        !CompanionBlockedTypes.Contains(DungeonType?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);
    // 0409EC16 把唯一整数收成非零字节；本资源里合法形状是单独的 1。
    public bool EnterWithoutFatigue => RawSections.Where(row => row.Tag == "enter without fatigue").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 1 }] }];
    public ImmutableArray<DungeonRequiredItemRow> RequiredItemRows
    {
        get
        {
            var rows = RawSections.Where(row => row.Tag == "required item").ToArray();
            if (rows.Length != 1 || rows[0].Tokens.Count == 0 || rows[0].Tokens.Count % 3 != 0 ||
                rows[0].Tokens.Any(token => token.Type != 0))
                return [];
            var result = ImmutableArray.CreateBuilder<DungeonRequiredItemRow>(rows[0].Tokens.Count / 3);
            for (int i = 0; i < rows[0].Tokens.Count; i += 3)
            {
                int item = rows[0].Tokens[i].Value, count = rows[0].Tokens[i + 1].Value, third = rows[0].Tokens[i + 2].Value;
                if (item <= 0 || count <= 0) return [];
                result.Add(new(item, count, third));
            }
            return result.MoveToImmutable();
        }
    }
    public bool DeclaresRequiredItem => RawSections.Any(row => row.Tag == "required item");
    public bool IsOrdinaryAnton => Marker("anton dungeon") && Marker("raid normal dungeon");
    public string? WarriorsDungeon { get; init; }
    public bool IsOrdinaryLuke => Id is >= 5101 and <= 5106 &&
        Path.StartsWith("dungeon/luke_normal/",StringComparison.OrdinalIgnoreCase) &&
        Marker("normal luke dungeon") && Marker("raid normal dungeon");
    public bool IsOrdinaryPrey => Id is >= 400000032 and <= 400000046 &&
        Path.StartsWith("dungeon/gcontents/190606_prey_raid_normal/", StringComparison.OrdinalIgnoreCase) && !IsRaid;
    public bool IsAncientInfinite => Id is >= 7101 and <= 7107 &&
        Path.StartsWith("dungeon/ancient_infinited/", StringComparison.OrdinalIgnoreCase) &&
        RawSections.Where(row => row.Tag == "ancient dungeon").ToArray() is { Length: > 0 } ancient &&
        ancient.All(row => row.Tokens is [{ Type: 0, Value: 1 }]);
    public bool IsModernDimension => Id is >= 7165 and <= 7179 &&
        RawSections.Any(row => row.Tag == "dimension dungeon" && row.Tokens.Count == 0);
    public bool IsNormalFiendwar => Id == 400000016 && WarriorsDungeon == "normalfiend";
    public bool IsWeddingCouple => Id == 3541 && Path.Equals("dungeon/china/chn_wedding_couple.dgn", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(DungeonType?.Trim(), "wedding couple", StringComparison.OrdinalIgnoreCase);
    public bool HasWeddingCouplePartyLimit => RawSections.Where(row => row.Tag == "limit party count").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 2 }] }];
    // 普通安徒恩保留完整消耗数量；第三列1为消耗，不能把数量8忽略成免费入场。
    public DungeonRequiredItemRow? OrdinaryEntryTicket => RequiredItemRows is [{ Count: > 0, Third: 1 } ticket] &&
        (ticket.Count == 1 || IsOrdinaryAnton || IsWeddingCouple) ? ticket : null;
    public bool DisablePaidCard => RawSections.Where(row => row.Tag == "gold card use").ToArray() is
        [{ Tokens: [{ Type: 0, Value: 0 }] }];
    public bool DisableMonsterAndClearExperience => Marker("no experience monster and result") ||
        (IsWeddingCouple || IsTournament) && Marker("disable exp");
    public bool DeclaresStartFatigue => RawSections.Any(row => row.Tag == "use fatigue only start dungeon");
    // #511 [use fatigue only start dungeon] N：只认唯一一段且恰为一个 1..65535 的整数；其他形状返回 null（保持逐房扣点）。
    public ushort? StartFatigue => RawSections.Where(row => row.Tag == "use fatigue only start dungeon").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 and <= ushort.MaxValue } amount] }] ? (ushort)amount.Value : null;
    public bool IsGriefTower => (GriefFloor is { } floor && floor.DungeonId == Id &&
        Path.StartsWith("dungeon/towers/grief/", StringComparison.OrdinalIgnoreCase) ||
        Id == 5115 && Path.Equals("dungeon/towers/grief/5115_001.dgn", StringComparison.OrdinalIgnoreCase)) &&
        RawSections.Where(row => row.Tag == "tower of grief").ToArray() is [{ Tokens.Count: 0 }];
    // [required item] 物品 数量 第三列：只识别三个整数且前两项为正的单行声明；第三列含义未确认，原样保留。
    public DungeonRequiredItem? RequiredItem => RawSections.Where(row => row.Tag == "required item").ToArray() is
        [{ Tokens: [{ Type: 0, Value: > 0 } item, { Type: 0, Value: > 0 } count, { Type: 0 } flag] }] ? new(item.Value, count.Value, flag.Value) : null;
    public bool DisableClearReward => RawSections.Any(row => row.Tag == "disable clear reward" && row.Tokens.Count == 0);
    // 只采用完整结果块中的唯一整数0；缺省或未确认形状不改变原结算入口。
    public bool HasNoResultBoard
    {
        get
        {
            var markers = RawSections.Select((section, index) => (Section: section, Index: index))
                .Where(row => row.Section.Tag is "dungeon clear result" or "/dungeon clear result").ToArray();
            if (markers is not [{ Section: { Tag: "dungeon clear result", Tokens.Count: 0 } },
                { Section: { Tag: "/dungeon clear result", Tokens.Count: 0 } }])
                return false;
            var block = RawSections.Skip(markers[0].Index + 1).Take(markers[1].Index - markers[0].Index - 1).ToArray();
            return block.All(section => section.Tag is "reward card" or "result board") &&
                block.Where(section => section.Tag == "result board").ToArray() is [{ Tokens: [{ Type: 0, Value: 0 }] }];
        }
    }
    public bool AllowsPractice => !IsTutorial && !IsClassExperience && !IsTournament && Id != 5000 &&
        RawSections.Where(row => row.Tag == "prohibit practice").All(row => row.Tokens is [{ Type: 0, Value: 0 }]);
    // 强袭任务副本均带 [disable black diamond card reward]（无值标记）：通关翻牌不提供黑钻额外卡。
    public bool DisableBlackDiamondCardReward => RawSections.Any(row => row.Tag == "disable black diamond card reward" && row.Tokens.Count == 0);
    public bool IsClassExperience => Id == 7142 &&
        Path.Equals("dungeon/cataclysm/changeclass/7142_changeclass.dgn", StringComparison.OrdinalIgnoreCase) &&
        Marker("disable drop item") && Marker("disable clear reward") && Marker("no fatigue") &&
        Marker("no experience monster and result") && Marker("no giveup panalty") &&
        RawSections.Where(row => row.Tag == "enter without fatigue").ToArray() is [{ Tokens: [{ Type: 0, Value: 1 }] }];
    public bool RoomMoveKeepState { get; init; }
    public bool IndividualMapMovement { get; init; }
    public bool DisableGiveupPenalty { get; init; }
    public bool DisableFatigueConsumption { get; init; }

    bool Marker(string name)
    {
        var rows = RawSections.Where(row => row.Tag == name).ToArray();
        return rows.Length > 0 && rows.All(row => row.Tokens.Count == 0);
    }

    // -1仅表示未指定；有效固定值与未确认声明都不能由任务推断覆盖。
    public bool HasDesignatedDifficultyConstraint => RawSections.Any(row => row.Tag == "designate dungeon difficulty" &&
        row.Tokens is not [{ Type: 0, Value: -1 }]);

    public bool TryGetDesignatedDifficulty(out byte difficulty)
    {
        difficulty = 0;
        var sections = RawSections.Where(section => section.Tag == "designate dungeon difficulty").ToArray();
        if (sections.Length != 1 || sections[0].Tokens.Count != 1 || sections[0].Tokens[0] is not { Type: 0, Value: >= 0 and <= 4 } value)
            return false;
        difficulty = checked((byte)value.Value);
        return true;
    }

    public bool AllowsDifficulty(byte difficulty, bool story, int? connectionQuestId = null)
    {
        if (difficulty > 4) return false;
        if (story)
        {
            var markers = RawSections.Where(section => section.Tag is "story mode" or "/story mode").ToArray();
            if (markers.Length != 0)
            {
                if (markers.Length > 2) return AllowsQuestStoryDifficulty(difficulty,connectionQuestId);
                // 故事块有自己的难度范围；块外普通路线的指定难度和难度表不能覆盖它。
                if (markers is not [{ Tag: "story mode", Tokens.Count: 0 },{ Tag: "/story mode", Tokens.Count: 0 }])
                    return false;
                int start = RawSections.ToList().FindIndex(section => section.Tag == "story mode");
                var block = RawSections.Skip(start + 1).TakeWhile(section => section.Tag != "/story mode").ToArray();
                var size = block.Where(section => section.Tag == "difficulty size").ToArray();
                if (size.Length != 1 || size[0].Tokens is not [{ Type: 0, Value: >= 1 and <= 5 }]) return false;
                var lists = block.Where(section => section.Tag == "quest list").ToArray();
                var ends = block.Where(section => section.Tag == "/quest list").ToArray();
                // 仅完整、无歧义的整数名册能证明本任务不属于故事块；未知引用保持原兼容范围。
                bool outside = connectionQuestId is > 0 && lists is [{ Tokens.Count: > 0 }] &&
                    lists[0].Tokens.All(token => token.Type == 0 && token.Value > 0) &&
                    lists[0].Tokens.Select(token => token.Value).Distinct().Count() == lists[0].Tokens.Count &&
                    ends is [{ Tokens.Count: 0 }] && Array.IndexOf(block,ends[0]) == Array.IndexOf(block,lists[0]) + 1 &&
                    lists[0].Tokens.All(token => token.Value != connectionQuestId);
                if (!outside) return difficulty < size[0].Tokens[0].Value;
            }
        }
        var designated = RawSections.Where(section => section.Tag == "designate dungeon difficulty").ToArray();
        var levels = RawSections.Where(section => section.Tag == "difficulty level").ToArray();
        // 同一副本在迷宫后再写一遍相同的指定难度（5097、5098 都是 2）。没有难度表时按一条处理。
        // 数值不一致，或同时还写了难度表，仍然整段拒绝。
        if (designated.Length > 1 && levels.Length == 0 &&
            designated.All(section => section.Tokens.SequenceEqual(designated[0].Tokens)) &&
            designated[0].Tokens is [{ Type: 0, Value: >= -1 and <= 4 }])
            designated = [designated[0]];
        if (designated.Length > 1 || designated.Length == 1 && (designated[0].Tokens is not [{ Type: 0, Value: >= -1 and <= 4 }] ||
            designated[0].Tokens[0].Value >= 0 && designated[0].Tokens[0].Value != difficulty)) return false;
        // 无难度表时，明确的指定难度本身就是唯一合法值；普通卢克只声明该字段，不能再要求额外的 difficulty。
        // 同时声明 difficulty 时仍核对一致性，未知、重复或冲突声明不能扩成任意难度。
        if (levels.Length == 0)
        {
            var plain = RawSections.Where(section => section.Tag == "difficulty").ToArray();
            if (designated.Length == 1 && designated[0].Tokens is [{ Type: 0, Value: >= 0 and <= 4 } locked])
                return difficulty == locked.Value && (plain.Length == 0 || plain is [{ Tokens: [{ Type: 0, Value: var shown }] }] && shown == locked.Value);
            // 普雷妮十五图仅声明difficulty 2，匹配客户端正常16请求也发2；不能按缺省0拒绝。
            if (IsOrdinaryPrey && designated.Length == 0)
                return difficulty == 2 && plain is [{ Tokens: [{ Type: 0, Value: 2 }] }];
            // 星河两图只声明固定难度2，真实选图请求也发2；不套用缺省零难度。
            if (EpicRoadResourcePaths.Dungeons.GetValueOrDefault(Id) == Path && designated.Length == 0)
                return difficulty == 2 && plain is [{ Tokens: [{ Type: 0, Value: 2 }] }];
            // 月轮山巨龙三图只声明固定难度1，没有普通五档难度表，不能按缺省0拒绝。
            if (Id is >= 3200 and <= 3202 && Path.StartsWith("dungeon/event/dragon_kurun_kor/", StringComparison.OrdinalIgnoreCase) && designated.Length == 0)
                return difficulty == 1 && plain is [{ Tokens: [{ Type: 0, Value: 1 }] }];
            // 普雷普通/引导、漩涡正式图只有difficulty声明；正式团本授权仍由原Battle签发。
            if (IsRaid && (Path.StartsWith("dungeon/kcontents3/isysraid_guide/", StringComparison.Ordinal) ||
                Path.StartsWith("dungeon/gcontents/190307_prey_raid/", StringComparison.Ordinal) ||
                Path.StartsWith("dungeon/fiendwar_kr/", StringComparison.Ordinal)) && designated.Length == 0)
                return plain is [{ Tokens: [{ Type: 0, Value: >= 0 and <= 4 } value] }] && difficulty == value.Value;
            return IsTournament && designated.Length == 1 || difficulty == 0;
        }
        // 部分副本重复声明同一难度表；内容相同不构成冲突，未知或不一致声明仍拒绝。
        return levels[0].Tokens.Count == 5 && levels[0].Tokens.All(token => token.Type == 0 && token.Value is 0 or 1) &&
            levels.All(section => section.Tokens.SequenceEqual(levels[0].Tokens)) &&
            levels[0].Tokens[difficulty].Value != 0;
    }

    // 任务连接和故事名册都不等于专用模式；普通任务保留普通开放档，固定及专用模式不得借此扩档。
    public bool AllowsOrdinaryQuestDifficulty(byte difficulty, int connectionQuestId) =>
        DungeonType is null && !IsTutorial && !IsClassExperience && !IsTournament && !IsRaid &&
        !RequiresGriefEntryContract && !IsInfernoDungeon && !RequiresCosmoEntryContract && !HasDesignatedDifficultyConstraint &&
        AllowsDifficulty(0, true, connectionQuestId) && AllowsDifficulty(difficulty, false);

    bool AllowsQuestStoryDifficulty(byte difficulty, int? connectionQuestId)
    {
        if (connectionQuestId is not > 0) return false;
        // 多块仅支持明确的整数任务归属；逐块校验，不以顺序或最大难度合并故事范围。
        var owners = new HashSet<int>();
        List<DungeonRawSection>? block = null;
        int? selectedSize = null;
        foreach (var section in RawSections)
        {
            if (section.Tag == "story mode")
            {
                if (block is not null || section.Tokens.Count != 0) return false;
                block = [];
            }
            else if (section.Tag == "/story mode")
            {
                if (block is null || section.Tokens.Count != 0) return false;
                var sizes = block.Where(row => row.Tag == "difficulty size").ToArray();
                var lists = block.Where(row => row.Tag == "quest list").ToArray();
                var ends = block.Where(row => row.Tag == "/quest list").ToArray();
                if (sizes is not [{ Tokens: [{ Type: 0, Value: >= 1 and <= 5 }] }] ||
                    lists.Length != 1 || lists[0].Tokens.Count == 0 ||
                    lists[0].Tokens.Any(token => token.Type != 0 || token.Value <= 0) ||
                    ends is not [{ Tokens.Count: 0 }] || block.IndexOf(ends[0]) != block.IndexOf(lists[0]) + 1) return false;
                foreach (var token in lists[0].Tokens)
                {
                    if (!owners.Add(token.Value)) return false;
                    if (token.Value == connectionQuestId) selectedSize = sizes[0].Tokens[0].Value;
                }
                block = null;
            }
            else block?.Add(section);
        }
        return block is null && selectedSize.HasValue && difficulty < selectedSize.Value;
    }
}
internal sealed class DungeonCatalog
{
    private readonly PvfArchive archive;
    private readonly IReadOnlyDictionary<int, string> dungeons, maps;
    private readonly ConcurrentDictionary<int, Lazy<DungeonDefinition>> definitions = new();
    private readonly Lazy<DungeonMapPool> mapPool;
    private readonly Lazy<IReadOnlyDictionary<(int Dungeon, byte Difficulty), DungeonMap>> revisedBeastBossMaps;
    private readonly Lazy<GriefTowerCatalog> griefTower;
    private readonly Lazy<BossRushCatalog> bossRush;
    private readonly Lazy<DungeonAltarAdmissionCatalog> altarAdmission;
    public DungeonAltarAdmissionCatalog AltarAdmission => altarAdmission.Value;
    private readonly Lazy<TowerOfDespairCatalog> despair;
    public TowerOfDespairCatalog Despair => despair.Value;
    private readonly Lazy<DungeonEnterCountCatalog> enterCounts;
    public DungeonEnterCountCatalog EnterCounts => enterCounts.Value;
    private readonly Lazy<EkernCycleCatalog> ekernCycle;
    public EkernCycleCatalog EkernCycle => ekernCycle.Value;
    private readonly Lazy<PreyNormalCatalog> preyNormal;
    public PreyNormalCatalog PreyNormal => preyNormal.Value;
    private readonly Lazy<AntonNormalSequenceCatalog> antonNormalSequence;
    public AntonNormalSequenceCatalog AntonNormalSequence => antonNormalSequence.Value;
    private readonly Lazy<ImmutableDictionary<int, SpecialDungeonClearReward>> specialClearRewards;
    public ImmutableDictionary<int, SpecialDungeonClearReward> SpecialClearRewards => specialClearRewards.Value;
    private DungeonCatalog(PvfArchive archive, IReadOnlyDictionary<int, string> dungeons, IReadOnlyDictionary<int, string> maps)
    {
        (this.archive, this.dungeons, this.maps) = (archive, dungeons, maps);
        mapPool = new(() => DungeonMapPool.Load(archive,maps),LazyThreadSafetyMode.ExecutionAndPublication);
        revisedBeastBossMaps = new(() => RevisedBeastLayout.ReadBossMaps(archive, maps),LazyThreadSafetyMode.ExecutionAndPublication);
        griefTower = new(() => new GriefTowerCatalog(archive),LazyThreadSafetyMode.ExecutionAndPublication);
        bossRush = new(() => new BossRushCatalog(archive),LazyThreadSafetyMode.ExecutionAndPublication);
        altarAdmission = new(() => DungeonAltarAdmissionCatalog.Load(archive),LazyThreadSafetyMode.ExecutionAndPublication);
        despair = new(() => TowerOfDespairCatalog.Load(archive),LazyThreadSafetyMode.ExecutionAndPublication);
        enterCounts = new(() => DungeonEnterCountCatalog.Load(archive),LazyThreadSafetyMode.ExecutionAndPublication);
        ekernCycle = new(() => EkernCycleCatalog.Load(archive),LazyThreadSafetyMode.ExecutionAndPublication);
        preyNormal = new(() => PreyNormalCatalog.Load(archive), LazyThreadSafetyMode.ExecutionAndPublication);
        antonNormalSequence = new(() => AntonNormalSequenceCatalog.Load(archive), LazyThreadSafetyMode.ExecutionAndPublication);
        specialClearRewards = new(() => archive.Contains(DungeonCardPriceCatalog.ResourcePath)
            ? DungeonSpecificCardRewards.ParseSpecialClearRewards(archive.ReadTokens(DungeonCardPriceCatalog.ResourcePath), archive.ResolveString)
            : ImmutableDictionary<int, SpecialDungeonClearReward>.Empty, LazyThreadSafetyMode.ExecutionAndPublication);
    }
    public DungeonMaze RevisedBeastMaze(int id, byte difficulty = 0) =>
        RevisedBeastLayout.Read(Get(id), archive.ResolveString, maps, archive.Contains, revisedBeastBossMaps.Value, difficulty);

    public int DungeonCount => dungeons.Count;
    public bool ContainsDungeon(int id) => dungeons.ContainsKey(id);
    // 裂缝1/2/3资源是同一地区的不同子变体，选图只列第一份；奖励核查不能只覆盖选图入口。
    public IEnumerable<DungeonDefinition> InfernoDungeons => dungeons.Where(pair =>
        pair.Value.StartsWith("dungeon/infernomode/", StringComparison.OrdinalIgnoreCase))
        .Select(pair => Get(pair.Key)).Where(definition => definition.IsInfernoDungeon);
    internal string? DungeonPath(int id) => dungeons.GetValueOrDefault(id);
    internal string? MapPath(int id) => maps.GetValueOrDefault(id);

    internal DungeonMaze ResolveImperialArenaMaze(DungeonDefinition definition, DungeonMaze maze, IReadOnlyList<int> opponents)
    {
        if (!ImperialArenaEntryPolicy.Supports(definition) || opponents.Count != maze.Width - 1 ||
            opponents.Distinct().Count() != opponents.Count || maze.MapSpecification is null)
            throw new InvalidDataException("帝国竞技场路线与正式资源不一致");
        var selected = maze.MapSpecification.Select(map => map.Position.X == 0 ? map : new DungeonMap(map.Kind, map.Position,
            opponents[map.Position.X - 1], maps.GetValueOrDefault(opponents[map.Position.X - 1]) ??
            throw new InvalidDataException("帝国竞技场候选地图不在目录"))).ToArray();
        if (selected.Any(map => !archive.Contains(map.Path))) throw new InvalidDataException("帝国竞技场候选地图不存在");
        return maze with { MapSpecification = selected };
    }
    public GriefTowerCatalog GriefTower => griefTower.Value;
    public BossRushCatalog BossRush => bossRush.Value;

    public static DungeonCatalog Load(PvfArchive pvf, IReadOnlyDictionary<int, string>? supplementalDungeons = null,
        IReadOnlyDictionary<int, string>? supplementalMaps = null)
    {

        // 这里只加载两个目录；未知副本的脚本不会因加载目录而被强制解析。
        var dungeons = ParseDirectory(pvf.ReadTokens("dungeon/dungeon.lst"), pvf.ResolveString, "dungeon", supplementalDungeons);
        var maps = ParseDirectory(pvf.ReadTokens("map/map.lst"), pvf.ResolveString, "map", supplementalMaps);
        return new(pvf, dungeons, RevisedBeastLayout.ReadMapPaths(pvf, maps));
    }

    public DungeonDefinition Get(int id)
    {
        if (id < 0 || !dungeons.TryGetValue(id, out var path))
            throw new InvalidDataException("副本索引不存在: " + id);
        // Lazy 在完整解析成功后才发布 Definition；失败缓存为异常，不发布局部 maze。
        return definitions.GetOrAdd(id, _ => new(() =>
        {
            var definition = ParseResource(id, path, archive.ReadTokens(path), archive.ResolveString, maps, archive.Contains);
            if (definition.IsTournament)
                definition = definition with { TournamentRewards = DungeonTournamentRewards.Parse(definition) };
            if (definition.IsTayberrs && SpecialClearRewards.TryGetValue(id, out var specialClear))
                definition = definition with { SpecialClearReward = specialClear };
            if (!definition.RequiresGriefEntryContract) return definition;
            if (!GriefTower.FloorsByDungeon.TryGetValue(id, out var floor))
                throw new InvalidDataException("悲叹副本未登记于楼层专表: " + id);
            return definition with { GriefFloor = floor };
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    public DungeonMaze ResolveOrdinaryMaze(DungeonDefinition definition, DungeonMaze maze, Func<int,int>? draw = null, bool hellParty = false,
        IReadOnlyDictionary<int,int>? bossWeights = null)
    {
        var choose = draw ?? System.Security.Cryptography.RandomNumberGenerator.GetInt32;
        if (bossWeights is not null)
        {
            if (definition.Id != DungeonCosmoRewardCatalog.DungeonId || maze.MapSpecification is null)
                throw new InvalidDataException("白斯追击效果缺少超星空终点声明");
            int replaced = 0;
            var maps = maze.MapSpecification.Select(map =>
            {
                if (map.Kind != "boss" || map.CandidateWeights.Count != map.CandidateIds.Count || map.CandidateIds.Count < 2)
                    return map;
                if (map.CandidateIds.Count != bossWeights.Count || map.CandidateIds.Any(id => !bossWeights.ContainsKey(id)))
                    throw new InvalidDataException("白斯追击效果与实际终点候选不匹配");
                replaced++;
                return map with { CandidateWeights = map.CandidateIds.Select(id => bossWeights[id]).ToArray() };
            }).ToArray();
            if (replaced != 1) throw new InvalidDataException("白斯追击效果须对应唯一加权终点");
            maze = maze with { MapSpecification = maps };
        }
        // 先按当前房间用途区分普通、dummy和Boss候选，不能提前将混合候选随机压成单图。
        if (UsesOrdinaryMapPool(definition, maze))
            maze = mapPool.Value.Resolve(definition,maze,choose,hellParty);
        return ResolveMapCandidates(maze,maps,archive.Contains,choose);
    }

    static bool UsesOrdinaryMapPool(DungeonDefinition definition, DungeonMaze maze) =>
        maze.ResourceError is null && maze.QuestConnectionTokens.Count == 0 &&
        (!maze.StaticTopology.Supported || maze.Starts.Count != 1 || maze.Bosses.Count != 1 ||
         maze.MapAlternatives.Count != 0 || definition.DummyAppearCount > 0);

    // 仅供静态消费者证明使用；临时排除确实不可选的显式候选，不改变正式随机池。
    internal DungeonMaze ResolveStaticConsumerMaze(DungeonDefinition definition, DungeonMaze maze, bool hellParty = false)
    {
        var candidate = PrepareStaticConsumerMaze(maze, maps, archive.Contains, UsesOrdinaryMapPool(definition, maze));
        return ResolveOrdinaryMaze(definition, candidate, _ => 0, hellParty);
    }

    // 动态MapPool目前按实际kind选择、忽略显式权重；只有原静态路径才按正权重判断可达性。
    internal static DungeonMaze PrepareStaticConsumerMaze(DungeonMaze maze, IReadOnlyDictionary<int, string> paths,
        Func<string, bool> exists, bool dynamicPool)
    {
        if (maze.ResourceError is not null) return maze;
        return maze with
        {
            MapSpecification = maze.MapSpecification?.Select(map => Filter(map, alternative: false)).ToArray(),
            MapAlternatives = maze.MapAlternatives.Select(map => Filter(map, alternative: true)).ToArray()
            // LayeredMaps是全部必需层，不是随机候选；固定story首项也不能被其它编号替换。
        };

        DungeonMap Filter(DungeonMap map, bool alternative)
        {
            var ids = map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds.Distinct().ToArray();
            if (ids.Length <= 1 || !alternative && map.StoryStageIndex >= 0) return map;
            bool alignedWeights = map.CandidateWeights.Count == ids.Length;
            bool weighted = !dynamicPool && !alternative && alignedWeights;
            if (weighted && (map.CandidateWeights.Any(weight => weight < 0) || checked(map.CandidateWeights.Sum()) <= 0))
                throw new InvalidDataException("地图候选权重无效");
            var indexes = Enumerable.Range(0, ids.Length).Where(index =>
                (!weighted || map.CandidateWeights[index] > 0) &&
                ids[index] >= 0 && paths.TryGetValue(ids[index], out var path) && exists(path)).ToArray();
            // 全空行保留原错误/角色kind处理，不删除整行后启用原本不可用的fallback。
            if (indexes.Length == 0 || indexes.Length == ids.Length) return map;
            return map with
            {
                CandidateIds = indexes.Select(index => ids[index]).ToArray(),
                CandidateWeights = alignedWeights
                    ? indexes.Select(index => map.CandidateWeights[index]).ToArray() : []
                // 保留原Id/Path，不让清除missing首项意外改变是否进入动态MapPool的判定。
            };
        }
    }

    internal static DungeonMaze ResolveMapCandidates(DungeonMaze maze, IReadOnlyDictionary<int,string> paths,
        Func<string,bool> exists, Func<int,int> draw)
    {
        if (maze.ResourceError is not null) return maze;
        var maps = maze.MapSpecification?.Select(map =>
        {
            // 阶段首项固定；普通基础行已按坐标排除全部阶段地图。
            var ids = map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds.Distinct().ToArray();
            int index = map.StoryStageIndex >= 0 || ids.Length == 1 ? 0 : map.CandidateWeights.Count == ids.Length
                ? WeightedIndex(map.CandidateWeights, draw) : draw(ids.Length);
            if (index < 0 || index >= ids.Length) throw new InvalidDataException("显式地图随机索引越界");
            return Resolve(map,ids[index],map.StoryStageIndex < 0 && ids.Length > 1);
        }).ToArray();
        var layers = new List<DungeonMap>();
        var layerIndexes = new Dictionary<DungeonPoint,int>();
        foreach (var map in maze.LayeredMaps)
        {
            var ids = map.LayerIndex >= 0 || map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds.ToArray();
            foreach (int id in ids)
            {
                int layerIndex = layerIndexes.GetValueOrDefault(map.Position);
                layerIndexes[map.Position] = layerIndex + 1;
                var layer = Resolve(map,id,ids.Length > 1) with { LayerIndex = layerIndex };
                layers.Add(layer);
            }
        }
        if ((maps is null || maps.SequenceEqual(maze.MapSpecification!)) && layers.SequenceEqual(maze.LayeredMaps) && maze.MapAlternatives.Count == 0)
        {
            if (maze.BossEntrance is null || maze.WarpMaps.Count == 0) return maze;
            // 候选未变化仍复验实际Boss入口声明；直接调用或with后的迷宫不能绕过来源唯一性。
            var conditional = ConditionalMaps(maze);
            return maze.ConditionalMaps.SequenceEqual(conditional) ? maze : maze with { ConditionalMaps = conditional };
        }
        var alternatives = maze.MapAlternatives.Select(map =>
        {
            var ids = map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds.Distinct().ToArray();
            int index = ids.Length == 1 ? 0 : draw(ids.Length);
            if (index < 0 || index >= ids.Length) throw new InvalidDataException("地图备选随机索引越界");
            return Resolve(map,ids[index],ids.Length > 1);
        }).ToArray();
        var resolved = maze with { MapSpecification = maps is null ? null : Freeze(maps), LayeredMaps = Freeze(layers),
            MapAlternatives = Freeze(alternatives),
            StoryStages = maps is null ? maze.StoryStages : Freeze(maps.Where(map => map.StoryStageIndex >= 0).OrderBy(map => map.StoryStageIndex)) };
        return resolved with { ConditionalMaps = ConditionalMaps(resolved) };

        DungeonMap Resolve(DungeonMap map,int id,bool freeze)
        {
            if (id < 0 || !paths.TryGetValue(id,out var path) || !exists(path))
                throw new InvalidDataException("选定地图不存在: " + id);
            return map with { Id = id,Path = path,CandidateIds = freeze ? Freeze(new[] { id }) : map.CandidateIds };
        }

        static int WeightedIndex(IReadOnlyList<int> weights, Func<int,int> random)
        {
            int total = checked(weights.Sum());
            if (total <= 0 || weights.Any(weight => weight < 0)) throw new InvalidDataException("地图候选权重无效");
            int roll = random(total);
            if (roll < 0 || roll >= total) throw new InvalidDataException("地图权重随机值越界");
            for (int index = 0; index < weights.Count; index++)
            {
                if (roll < weights[index]) return index;
                roll -= weights[index];
            }
            throw new InvalidDataException("地图权重抽取未命中候选");
        }
    }

    static IReadOnlyList<DungeonMap> ConditionalMaps(DungeonMaze maze)
    {
        if (maze.BossEntrance is null || maze.MapSpecification is null) return [];
        var maps = new List<DungeonMap>();
        foreach (var warp in maze.WarpMaps)
        {
            // Boss入口只支持每个逻辑房间唯一替换，多目标声明不能任取其一。
            if (maps.Any(map => map.Position == warp.Source))
                throw new InvalidDataException("Boss入口条件传送的同一来源存在多个目标");
            var source = maze.MapSpecification.Where(map => map.Position == warp.Source && map.StoryStageIndex < 0).ToArray();
            var destination = maze.MapSpecification.Where(map => map.Position == warp.Destination && map.StoryStageIndex < 0).ToArray();
            if (source.Length != 1 || destination.Length != 1)
                throw new NotSupportedException("条件传送需要唯一的来源和目标地图");
            maps.Add(destination[0] with { Position = warp.Source,Kind = source[0].Kind,IsConditional = true });
        }
        return Freeze(maps);
    }

    internal static IReadOnlyDictionary<int, string> ParseDirectory(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, string root,
        IReadOnlyDictionary<int, string>? supplemental = null)
    {
        if (tokens.Count == 0 || tokens.Count % 2 != 0)
            throw new InvalidDataException("PVF 目录记录不完整: " + root);
        var result = new Dictionary<int, string>();
        for (int i = 0; i < tokens.Count; i += 2)
        {
            if (tokens[i].Type != 0 || tokens[i].Value < 0 || tokens[i + 1].Type != 6)
                throw new InvalidDataException("PVF 目录 ID/路径类型无效: " + root);
            string path = PvfArchive.NormalizePath(root + "/" + resolve(tokens[i + 1].Value));
            if (!path.StartsWith(root + "/", StringComparison.Ordinal) || path.Length == root.Length + 1)
                throw new InvalidDataException("PVF 目录路径越过资源根: " + root);
            if (!result.TryAdd(tokens[i].Value, path))
                throw new InvalidDataException("重复 PVF 目录 ID: " + tokens[i].Value);
        }

        if (supplemental is not null)
            foreach (var (id, sourcePath) in supplemental)
            {
                if (id < 0)
                    throw new InvalidDataException("PVF 补充目录 ID 无效: " + id);
                string path = PvfArchive.NormalizePath(sourcePath);
                if (!path.StartsWith(root + "/", StringComparison.Ordinal) || path.Length == root.Length + 1)
                    throw new InvalidDataException("PVF 补充目录路径越过资源根: " + root);
                if (result.TryGetValue(id, out var existing))
                {
                    if (existing != path)
                        throw new InvalidDataException("PVF 补充目录与原登记冲突: " + id);
                }
                else result.Add(id, path);
            }

        return result.AsReadOnly();
    }

    internal static DungeonDefinition ParseResource(int id, string path, IReadOnlyList<PvfToken> tokens, Func<int, string> resolve, IReadOnlyDictionary<int, string> mapPaths, Func<string, bool> exists)
    {
        if (id < 0)
            throw new InvalidDataException("副本索引不能为负");
        var sections = Sections(tokens, resolve);
        bool dungeonHell = false;
        foreach (var section in sections)
        {
            if (section.Tag == "maze info") break;
            if (section.Tag == "hell dungeon" && section.Tokens is [{ Type: 0, Value: 1 }]) dungeonHell = true;
        }
        int? minimum = Scalar("minimum required level"), basis = Scalar("basis level");
        string? dungeonType = sections.LastOrDefault(section => section.Tag == "dungeon type")?.Tokens is [{ Type: 6 } type]
            ? resolve(type.Value) : null;
        bool allowOffGridBoss = string.Equals(dungeonType?.Trim(), "boss rush dungeon", StringComparison.OrdinalIgnoreCase);
        var mazes = new List<DungeonMaze>();
        Dictionary<string, DungeonRawSection>? current = null;
        var mazeSections = new List<DungeonRawSection>();
        bool mazeClosed = false;
        // 匹配PVF的5424缺少[maze info]，但完整布局直接位于副本头；仅接受魔界大战单迷宫的这一形式。
        // 仍由同一ParseMaze校验尺寸、起点、Boss与显式地图，不借用5418布局或补造房间。
        if (!sections.Any(section => section.Tag == "maze info") &&
            path.StartsWith("dungeon/kcontents2/theevildomwar/", StringComparison.Ordinal) &&
            sections.Any(section => section.Tag == "evildom war dungeon" && section.Tokens.Count == 0))
            current = new(StringComparer.Ordinal);
        for (int i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            // 无参数的退役布局是作用域边界；标量声明不是新布局，不截断普通路线。
            if (section.Tag == "beast dungeon")
            {
                if (section.Tokens.Count == 0) Finish();
                else if (section.Tokens is not [{ Type: 0 }])
                    throw new InvalidDataException("退役布局边界参数无效");
                continue;
            }
            if (section.Tag == "maze info")
            {
                if (section.Tokens.Count != 0)
                    throw new InvalidDataException("maze info 标记带有未知参数");
                Finish();
                current = new(StringComparer.Ordinal);
                mazeSections.Clear();
                mazeClosed = false;
                continue;
            }

            if (section.Tag == "/maze info")
            {
                // 尾随入口和传送规则仍归本路线，布局字段只在maze块内读取。
                mazeClosed = true;
                continue;
            }

            if (current is null) continue;
            mazeSections.Add(section);
            if (mazeClosed || section.Tag.StartsWith('/'))
                continue;
            if (section.Tag is not ("size" or "greed" or "map specification" or "start map" or "boss map" or "quest connection"))
                continue;
            if (section.Tag is "map specification" or "start map" or "boss map")
            {
                if (i + 1 >= sections.Count || sections[i + 1].Tag != "/" + section.Tag || sections[i + 1].Tokens.Count != 0)
                    throw new InvalidDataException("maze 块未正常结束: " + section.Tag);
            }
            if (!current.TryAdd(section.Tag, section))
            {
                if (section.Tag != "map specification")
                    throw new InvalidDataException("重复 maze 字段: " + section.Tag);
                var previous = current[section.Tag];
                // 0409C8CE..0409CC6A逐格累积；仅接受不同块坐标互不重叠的已证实形式。
                var occupied = MapBlockPositions(previous.Tokens);
                if (MapBlockPositions(section.Tokens).Overlaps(occupied))
                    throw new InvalidDataException("重复 map specification 块坐标");
                current[section.Tag] = previous with { Tokens = Freeze(previous.Tokens.Concat(section.Tokens)) };
            }
        }

        Finish();
        if (mazes.Count == 0)
            throw new NotSupportedException("unsupported: 未发现 maze info 资源结构");
        return new(id, path, minimum, basis, Freeze(mazes), Freeze(sections))
        {
            DungeonType = dungeonType,
            PersonalCoinInfoLimits = ReadPersonalCoinInfoLimits(),
            DisjointForbiddenDropRarities = ReadDisjointDropRarities(sections, resolve),
            WarriorsDungeon = sections.Where(section => section.Tag == "warriors dungeon").ToArray() is
                [{ Tokens: [{ Type: 6 } warriors] }] ? resolve(warriors.Value) : null,
            RoomMoveKeepState = Flag("room move keep state"),
            IndividualMapMovement = Flag("individual map movement"),
            DisableFatigueConsumption = sections.Any(section => section.Tag == "no fatigue" && section.Tokens.Count == 0),
            // 同名数值后值覆盖，无值开关重复置位；不同标签独立授予豁免。
            DisableGiveupPenalty = dungeonType is "tower of dazzlement" or "tower of death" or "boss rush dungeon" ||
                sections.Where(section => section.Tag is "raid dungeon" or "raid normal dungeon" or "risk dungeon" or
                    "dimension dungeon" or "tower of despair" or "blood dungeon" or "operation hope dungeon" or
                    "enable give up" or "anton dungeon" or "event dungeon" or "ancient dungeon" or "no giveup panalty")
                .GroupBy(section => section.Tag, StringComparer.Ordinal).Any(group => group.Key switch
                {
                    "raid dungeon" or "risk dungeon" or "operation hope dungeon" or "event dungeon" or "ancient dungeon" =>
                        group.Last().Tokens is [{ Type: 0, Value: 1 }],
                    "blood dungeon" => group.Last().Tokens is [{ Type: 0, Value: 1 or 2 }],
                    _ => group.Any(section => section.Tokens.Count == 0)
                })
        };

        IReadOnlyList<int> ReadPersonalCoinInfoLimits()
        {
            var rows = sections.Where(section => section.Tag == "coin info").ToArray();
            if (rows is not [{ Tokens.Count: 16 }] || rows[0].Tokens[0] is not { Type: 0, Value: 0 }) return [];
            var values = rows[0].Tokens;
            string[] names = ["[normal]", "[expert]", "[master]", "[king]", "[slayer]"];
            var limits = new int[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                int offset = 1 + i * 3;
                if (values[offset].Type != 6 || resolve(values[offset].Value) != names[i] ||
                    values[offset + 1] is not { Type: 0, Value: >= 0 } count ||
                    values[offset + 2] is not { Type: 0, Value: 0 }) return [];
                limits[i] = count.Value;
            }
            return Freeze(limits);
        }

        bool Flag(string tag) => sections.LastOrDefault(section => section.Tag == tag)?.Tokens is [{ Type: 0, Value: 1 }];

        HashSet<DungeonPoint> MapBlockPositions(IReadOnlyList<PvfToken> values)
        {
            if (values.Count == 0) throw new InvalidDataException("map specification 块不能为空");
            var points = new HashSet<DungeonPoint>();
            for (int at = 0; at < values.Count;)
            {
                if (values[at].Type != 6 || at + 3 >= values.Count || values[at + 1].Type != 0 || values[at + 2].Type != 0)
                    throw new InvalidDataException("map specification 块记录不完整");
                points.Add(new(values[at + 1].Value, values[at + 2].Value));
                at += 3;
                while (at < values.Count && values[at].Type == 0) at++;
            }
            return points;
        }

        int? Scalar(string tag)
        {
            var matches = sections.Where(s => s.Tag == tag).ToArray();
            if (matches.Length == 0)
                return null;
            if (matches.Length != 1 || matches[0].Tokens.Count != 1 || matches[0].Tokens[0].Type != 0 || matches[0].Tokens[0].Value < 0)
                throw new InvalidDataException("副本等级字段无效或重复: " + tag);
            return matches[0].Tokens[0].Value;
        }

        void Finish()
        {
            if (current is null)
                return;
            try { mazes.Add(ParseMaze(mazes.Count, current, mazeSections, resolve, mapPaths, exists, dungeonHell, allowOffGridBoss)); }
            catch (Exception error) when (error is InvalidDataException or NotSupportedException)
            {
                // 每条路线独立装载；未使用的旧路线不能阻断同副本的有效剧情路线。
                var connection = current.TryGetValue("quest connection", out var section) ? section.Tokens : Array.Empty<PvfToken>();
                mazes.Add(new(mazes.Count,0,0,"",[],[],null,connection) { ResourceError = error.Message });
            }
            current = null;
        }
    }

    private static DungeonMaze ParseMaze(int ordinal, IReadOnlyDictionary<string, DungeonRawSection> sections, IReadOnlyList<DungeonRawSection> mazeSections,
        Func<int, string> resolve, IReadOnlyDictionary<int, string> mapPaths, Func<string, bool> exists, bool dungeonHell, bool allowOffGridBoss)
    {
        var size = Integers(Required("size"));
        if (size.Length != 2 || size[0] <= 0 || size[1] <= 0)
            throw new InvalidDataException("maze size 必须是两个正整数");
        int width = size[0], height = size[1];
        var greedTokens = Required("greed");
        if (greedTokens.Count != 1 || greedTokens[0].Type != 6)
            throw new NotSupportedException("unsupported: greed 不是单个字符串");
        string greed = resolve(greedTokens[0].Value);
        // 保留原文；类型化GreedGrid独立返回不支持原因，未知编码不使整个副本定义失败。
        var starts = Points(Required("start map"));
        var bosses = Points(Required("boss map"), allowRepeated: true, allowOffGrid: allowOffGridBoss);
        IReadOnlyList<DungeonMap>? specification = null;
        var layered = new List<DungeonMap>();
        if (sections.TryGetValue("map specification", out var mapSection))
        {
            var values = mapSection.Tokens;
            if (values.Count == 0)
                throw new InvalidDataException("map specification 记录不完整");
            var items = new List<DungeonMap>();
            for (int i = 0; i < values.Count;)
            {
                if (values[i].Type != 6)
                    throw new NotSupportedException("unsupported: map specification 的类型标记未实现");
                string kind = resolve(values[i].Value);
                bool weightedBoss = kind == "boss_selection_probability";
                if (kind is not ("map" or "boss" or "layered") && !weightedBoss)
                    throw new NotSupportedException("unsupported: map specification 类型 " + kind);
                if (i + (weightedBoss ? 2 : 3) >= values.Count)
                    throw new NotSupportedException("unsupported: map specification 数值不完整");
                int[] numbers = Integers(values.Skip(i + 1).Take(weightedBoss ? 2 : 3).ToArray());
                var position = new DungeonPoint(numbers[0], numbers[1]);
                ValidatePoint(position);
                i += weightedBoss ? 3 : 4;
                var ids = new List<int>();
                var weights = new List<int>();
                if (weightedBoss)
                {
                    while (i < values.Count && values[i].Type == 0)
                    {
                        if (i + 1 >= values.Count || values[i].Value <= 0 || values[i + 1].Type != 0 || values[i + 1].Value <= 0)
                            throw new InvalidDataException("Boss候选权重行不完整");
                        ids.Add(values[i++].Value);
                        weights.Add(values[i++].Value);
                    }
                    if (ids.Count < 1) throw new InvalidDataException("Boss候选列表为空");
                }
                else
                {
                    ids.Add(numbers[2]);
                    while (i < values.Count && values[i].Type == 0)
                    {
                        if (values[i].Value < 0) throw new InvalidDataException("地图候选编号无效");
                        ids.Add(values[i++].Value);
                    }
                }
                if (!weightedBoss && numbers[2] < 0) throw new InvalidDataException("地图候选编号无效");
                foreach (int candidateId in ids)
                    if ((weightedBoss || ids.Count == 1) &&
                        (!mapPaths.TryGetValue(candidateId, out var candidatePath) || !exists(candidatePath)))
                        throw new InvalidDataException("map specification 引用的地图不存在: " + candidateId);
                var map = new DungeonMap(weightedBoss ? "boss" : kind,position,ids[0],mapPaths.GetValueOrDefault(ids[0]) ?? "")
                    { CandidateIds = ids.AsReadOnly(), CandidateWeights = weights.AsReadOnly() };
                if (kind == "layered") layered.Add(map); else items.Add(map);
            }

            // 普通迷宫可只声明部分房间，其余由地图池生成；完整性在所选路线规划时检查。
            specification = Freeze(items);
        }

        var quest = sections.TryGetValue("quest connection", out var connection) ? connection.Tokens : Freeze(Array.Empty<PvfToken>());
        if (connection is not null && quest.Count == 0)
            throw new InvalidDataException("quest connection 不能为空");
        var stages = new List<DungeonMap>();
        if (bosses.Count >= 2 && bosses.Distinct().Count() != bosses.Count && quest.Count != 0 && specification is not null)
        {
            var byPosition = specification.GroupBy(map => map.Position).ToDictionary(group => group.Key,group => new Queue<DungeonMap>(group));
            var stageMaps = specification.ToList();
            foreach (var point in bosses)
            {
                if (!byPosition.TryGetValue(point,out var candidates) || candidates.Count == 0)
                    throw new InvalidDataException("剧情阶段缺少对应的有序地图声明");
                var original = candidates.Dequeue();
                var stage = original with { StoryStageIndex = stages.Count };
                if (stages.Count == bosses.Count - 1) stage = stage with { Kind = "boss" };
                stageMaps[stageMaps.IndexOf(original)] = stage;
                stages.Add(stage);
            }
            if (stages.Zip(stages.Skip(1)).Any(pair =>
                Math.Abs(pair.First.Position.X-pair.Second.Position.X)+Math.Abs(pair.First.Position.Y-pair.Second.Position.Y) != 1))
                throw new InvalidDataException("剧情阶段连续坐标不合法");
            var bases = new List<DungeonMap>();
            foreach (var group in stageMaps.GroupBy(map => map.Position))
            {
                var roomStages = group.Where(map => map.StoryStageIndex >= 0).ToArray();
                if (roomStages.Length == 0) { bases.AddRange(group); continue; }
                var stageIds = roomStages.Select(map => map.Id).ToHashSet();
                var candidates = group.SelectMany(map => map.CandidateIds.Count == 0 ? new[] { map.Id } : map.CandidateIds)
                    .Where(id => !stageIds.Contains(id)).Distinct().ToArray();
                // 没有额外候选时也建立普通基础身份，入场不能直接消费阶段0。
                if (candidates.Length == 0) candidates = [roomStages[0].Id];
                var source = group.FirstOrDefault(map => map.StoryStageIndex < 0) ?? roomStages[0];
                bases.Add(source with { Id = candidates[0],Path = mapPaths.GetValueOrDefault(candidates[0]) ?? "",
                    StoryStageIndex = -1,CandidateIds = Freeze(candidates) });
            }
            specification = Freeze(bases.Concat(stages));
        }
        var overridden = new List<DungeonMap>();
        var alternatives = new List<DungeonMap>();
        if (stages.Count == 0 && specification is not null)
        {
            var effective = new List<DungeonMap>();
            foreach (var group in specification.GroupBy(map => map.Position))
            {
                // 0409C9B9清普通候选(+4)，0409CA2A另清Boss候选(+18)。只有同种重复行才覆盖。
                var kinds = group.GroupBy(map => map.Kind).Select(rows =>
                {
                    overridden.AddRange(rows.SkipLast(1));
                    return rows.Last();
                }).ToArray();
                var selected = kinds.FirstOrDefault(map => map.Kind == (bosses.Contains(group.Key) ? "boss" : "map")) ?? kinds[0];
                effective.Add(selected);
                alternatives.AddRange(kinds.Where(map => map != selected));
            }
            specification = Freeze(effective);
        }
        if (specification is not null && specification.GroupBy(map => map.Position).Any(group => group.Count() > 1 &&
            (stages.Count == 0 || group.Count(map => map.StoryStageIndex < 0) > 1))) throw new InvalidDataException("重复 map specification 坐标");
        var entrance = DungeonMazeConditions.Entrance(mazeSections,resolve);
        var warps = DungeonMazeConditions.Warps(mazeSections);
        if (entrance is not null)
        {
            foreach (var warp in warps)
            {
                ValidatePoint(warp.Source); ValidatePoint(warp.Destination);
            }
        }
        var maze = new DungeonMaze(ordinal,width,height,greed,starts,bosses,specification,quest)
            { IsBossRush = allowOffGridBoss, LayeredMaps = Freeze(layered),OverriddenMaps = Freeze(overridden),MapAlternatives = Freeze(alternatives),StoryStages = Freeze(stages),BossEntrance = entrance,WarpMaps = warps,
                ClearConditions = DungeonMazeConditions.Clear(mazeSections,resolve),
                ClearPartyBuffCards = DungeonMazeConditions.ClearPartyBuffCards(mazeSections),
                RandomizedObjects = DungeonMazeConditions.RandomizedObjects(mazeSections),
                RandomizedObjectGroups = DungeonRandomizedObjectGroups.Parse(mazeSections, resolve),
                IsDimensionHiddenBoss = mazeSections.Any(row => row.Tag == "hidden boss maze"),
                IsAncientInfiniteVariant = mazeSections.Any(row => row.Tag == "infinite difficulty"),
                IsInfernoSpecial = mazeSections.Any(row => row.Tag == "inferno special" && row.Tokens.Count == 0),
                HellParty = HellParty(), HellPartyPath = HellParty() is { } hellRoom ? mapPaths[hellRoom.MapId] : null,
            HellLegendPath = HellParty() is { LegendMapId: > 0 } legendRoom ? mapPaths[legendRoom.LegendMapId] : null };
        return maze with { ConditionalMaps = ConditionalMaps(maze) };
        // 实验性：深渊房间解析失败（缺字段/坐标越界/该坐标没有普通房间/深渊地图文件不存在）一律视为该 maze 无深渊派对，不抛错（不影响现有入场）。
        DungeonHellPartyRoom? HellParty()
        {
            var flags = mazeSections.Where(s => s.Tag == "hell dungeon").ToArray();
            var maps = mazeSections.Where(s => s.Tag == "seal door map index").ToArray();
            var positions = mazeSections.Where(s => s.Tag == "seal door pos").ToArray();
            bool mazeHell = flags.Length == 1 && flags[0].Tokens is [{ Type: 0, Value: 1 }];
            // maze 内 [hell dungeon] 1，或副本头（第一条 maze info 之前）的 [hell dungeon] 1 + 本 maze 密门。
            // 时空裂缝一类只有副本头标记、没有 seal door 的 Inferno maze 仍返回 null，不编造深渊房间。
            if (quest.Count != 0 || maps.Length != 1 || positions.Length != 1 ||
                !(mazeHell || flags.Length == 0 && dungeonHell) ||
                maps[0].Tokens is not [{ Type: 0, Value: > 0 } mapToken] ||
                positions[0].Tokens is not [{ Type: 0 } x, { Type: 0 } y]) return null;
            var room = new DungeonPoint(x.Value, y.Value);
            if (room.X < 0 || room.Y < 0 || room.X >= width || room.Y >= height ||
                DungeonGreedGrid.Read(width,height,greed) is not { Supported: true, Cells: { } cells } || cells[room.Y*width+room.X].Index == 0 ||
                !mapPaths.TryGetValue(mapToken.Value, out var hellPath) || !exists(hellPath)) return null;
            int Single(string tag) => mazeSections.Where(s => s.Tag == tag).ToArray() is [{ Tokens: [{ Type: 0, Value: >= 0 } value] }] ? value.Value : 0;
            return new(mapToken.Value, room) { RequiredItemCount = Single("hell party required item count"), AppearRate = Single("seal door appear rate"),
                LegendMapId = mapPaths.TryGetValue(Single("seal door legend map index"), out var legendPath) && exists(legendPath) ? Single("seal door legend map index") : 0,
                LegendRequiredCount = Single("hell legend party required item count") };
        }
        IReadOnlyList<PvfToken> Required(string tag) => sections.TryGetValue(tag, out var value) ? value.Tokens : throw new InvalidDataException("maze 缺少字段: " + tag);
        IReadOnlyList<DungeonPoint> Points(IReadOnlyList<PvfToken> values, bool allowRepeated = false, bool allowOffGrid = false)
        {
            int[] numbers = Integers(values);
            if (numbers.Length == 0 || numbers.Length % 2 != 0)
                throw new InvalidDataException("start/boss map 坐标不完整");
            var result = new List<DungeonPoint>();
            var seen = new HashSet<DungeonPoint>();
            for (int i = 0; i < numbers.Length; i += 2)
            {
                var point = new DungeonPoint(numbers[i], numbers[i + 1]);
                ValidatePoint(point, allowOffGrid);
                if (!seen.Add(point) && !allowRepeated)
                    throw new InvalidDataException("重复 start/boss map 坐标");
                result.Add(point);
            }

            return Freeze(result);
        }

        void ValidatePoint(DungeonPoint point, bool allowOffGrid = false)
        {
            if (point.X < 0 || point.Y < 0)
                throw new InvalidDataException("maze 坐标超出 size");
            if (point.X >= width || point.Y >= height)
            {
                if (!allowOffGrid)
                    throw new InvalidDataException("maze 坐标超出 size");
            }
        }
    }

    private static int[] Integers(IReadOnlyList<PvfToken> tokens)
    {
        if (tokens.Any(t => t.Type != 0))
            throw new NotSupportedException("unsupported: maze 数值包含未实现的 token 类型");
        return tokens.Select(t => t.Value).ToArray();
    }

    private static IReadOnlyList<DungeonRawSection> Sections(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var sections = new List<DungeonRawSection>();
        for (int i = 0; i < tokens.Count;)
        {
            if (tokens[i].Type != 3)
                throw new InvalidDataException("副本脚本存在无标签 token");
            string raw = resolve(tokens[i++].Value);
            if (!raw.StartsWith('[') || !raw.EndsWith(']') || raw.Length < 3)
                throw new InvalidDataException("副本脚本标签无效");
            string tag = raw[1..^1].Trim().ToLowerInvariant();
            int start = i;
            while (i < tokens.Count && tokens[i].Type != 3)
                i++;
            sections.Add(new(tag, Freeze(tokens.Skip(start).Take(i - start))));
        }

        return Freeze(sections);
    }

    private static ImmutableHashSet<int> ReadDisjointDropRarities(IReadOnlyList<DungeonRawSection> sections, Func<int, string> resolve)
    {
        var declarations = sections.Select((row, index) => (row, index))
            .Where(pair => pair.row.Tag is "no disjoint drop rarity" or "/no disjoint drop rarity").ToArray();
        if (declarations.Length == 0) return ImmutableHashSet<int>.Empty;
        if (declarations is not [{ row.Tag: "no disjoint drop rarity" }, { row: { Tag: "/no disjoint drop rarity", Tokens.Count: 0 } }] ||
            declarations[1].index != declarations[0].index + 1 || declarations[0].row.Tokens.Count == 0)
            throw new InvalidDataException("[no disjoint drop rarity] 必须为唯一的完整稀有度块");
        var result = ImmutableHashSet.CreateBuilder<int>();
        foreach (var token in declarations[0].row.Tokens)
        {
            int rarity = token.Type == 6 ? resolve(token.Value) switch
            {
                "common" => 0, "uncommon" => 1, "rare" => 2, "unique" => 3,
                "epic" => 4, "chronicle" => 5, "legendary" => 6, _ => -1
            } : -1;
            if (rarity < 0) throw new InvalidDataException("[no disjoint drop rarity] 含未知稀有度");
            result.Add(rarity);
        }
        return result.ToImmutable();
    }

    private static IReadOnlyList<T> Freeze<T>(IEnumerable<T> values) => Array.AsReadOnly(values.ToArray());
}
