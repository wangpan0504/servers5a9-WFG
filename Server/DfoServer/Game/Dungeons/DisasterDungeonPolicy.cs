using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 哈林灾难领域。六张 dgn 都没有疲劳标签，官方公告的 8 点门槛和每房 3 点不能写成 StartFatigue：
// StartFatigue 会在入场一次扣光，并关掉逐房扣点。
// 实图同时要求编号 5341–5345 和类型 disaster dungeon：只认类型会在入口 5340 一旦有迷宫时按房收费；只认编号会在类型改掉后继续收费。
// 本服难度0/1/2统一在翻牌给礼盒490012200，不在Boss死亡时重复直发。etc/dungeonetc/disasterdungeon.etc 的
// [basic reward list by dungeon difficulty] 难度 2 原行是
// `normal` 10000 -1 490012200 `gold` 0 -1 490012200。
// 入口 disaster.dgn 的 [designate dungeon difficulty] 与 [difficulty] 都是 2。
// 五张实图 dgn 和 etc/itemdropinfo_clearreward.etc 都不写这个编号。
// 低档两列的原服发奖条件未还原，0/1采用本服兼容规则，不将该取舍表述为原表语义。
// 礼盒 [etc] 打开后是 20 个账号绑定的 10309039，数量由开箱逻辑处理，这里不拆成 20 个散件。
// 原生控制器按破坏数保留63489–63493之一，并以rewrite清除其余四只；仅保留石头的正常死亡掉一盒。
internal static class DisasterDungeonPolicy
{
    public const string TypeName = "disaster dungeon";
    public const int EntranceId = 5340;
    public const int FirstPlayableId = 5341;
    public const int LastPlayableId = 5345;
    public const ushort EntryMinimumFatigue = 8;
    public const ushort RoomFatigue = 3;
    public const int CrystalBoxId = 490012200;
    public const int CrystalId = 10309039;
    public const int CrystalCount = 20;

    public static bool IsRewardStone(DungeonEntryPlan entry, DungeonPlannedMonster monster) =>
        entry.DungeonId is >= FirstPlayableId and <= LastPlayableId && entry.IsOrdinary &&
        !entry.IsPractice && !entry.IsTutorial && !entry.IsClassExperience && !entry.DisableItemDrops &&
        entry.RaidEntry is null && monster.MonsterId is >= 63489 and <= 63493 &&
        monster.CanReportDeath && !monster.IsFriendly && !monster.IsSpecialChild &&
        monster.AiCharacter is null && monster.SourceMap is { } map && entry.IsTerminalMap(map) &&
        monster.Source?.Path.Equals($"monster/newmonsters/harlem/disaster/disasterstone/disasterstone{monster.MonsterId - 63488}.mob",
            StringComparison.OrdinalIgnoreCase) == true;

    // 五种MOB没有[item]；恢复本服对应档礼盒，盒内精炼石数量仍由真实礼盒资源随机。
    public static DungeonDropGenerationPlan? PrepareStoneDrop(DungeonEntryPlan entry, DungeonPlannedMonster monster,
        Func<int, ItemTemplate> resolveItem)
    {
        if (!IsRewardStone(entry, monster)) return null;
        int id = 490012195 + monster.MonsterId - 63489;
        var box = resolveItem(id);
        if (box.Id != id || box.Kind != ItemKind.Consumable || box.StackLimit < 1 || !box.Expiration.SupportsCreation)
            throw new InvalidDataException("灾难时空石奖励缺少可创建的对应档礼盒");
        return new("disaster-stone-box-local-v1;" + monster.Source!.Path,
            [new(1, new DungeonItemDropPool([new DungeonWeightedDropItem(box, 1)]), 1, 1, ProbabilityDenominator: 1)]);
    }

    public static bool CanRewardStone(SinglePlayerDungeonRun run, NativeDungeonCombatAuthority authority,
        PreparedDungeonDeath pending)
    {
        if (!IsRewardStone(run.Entry, pending.Monster) || !authority.WasReportedWithoutRewrite(pending.Death)) return false;
        var alternatives = run.CurrentMonsterPlan.Monsters.Where(monster => IsRewardStone(run.Entry, monster) &&
            monster.ObjectKey != pending.Monster.ObjectKey).ToArray();
        // 原生RewardCheck先清除四档，再留下一个可攻击档；缺少清除事实不能仅凭客户端选择的模板领奖。
        return alternatives.Length == 4 && alternatives.Select(monster => monster.MonsterId).Distinct().Count() == 4 &&
            alternatives.All(monster => authority.WasClearedWithRewrite(run, monster.ObjectKey));
    }

    public static bool IsPlayable(DungeonDefinition definition) =>
        definition.Id is >= FirstPlayableId and <= LastPlayableId && IsDisaster(definition);

    public static bool IsEntrance(DungeonDefinition definition) =>
        definition.Id == EntranceId && IsDisaster(definition);

    public static int SelectPlayable(DungeonDefinition entrance, Func<int, int> draw, int? sharedDungeon = null)
    {
        var ids = entrance.EnterRandomDungeons;
        if (!IsEntrance(entrance) || ids.Count == 0 || ids.Any(id => id is < FirstPlayableId or > LastPlayableId))
            throw new InvalidDataException("灾难领域入口缺少合法随机实图列表");
        if (ids.Distinct().Count() != ids.Count) throw new InvalidDataException("灾难领域随机实图重复");
        if (sharedDungeon is { } shared)
            return ids.Contains(shared) ? shared : throw new InvalidDataException("灾难领域队伍路线不在入口列表");
        int at = draw(ids.Count);
        return at >= 0 && at < ids.Count ? ids[at] : throw new InvalidDataException("灾难领域随机路线越界");
    }

    // 练习、教程、职业体验和无疲劳标记不套这套门槛。回放仍是实图入场，沿用同一门槛。
    public static bool AppliesFatigue(DungeonDefinition definition, bool practice) =>
        IsPlayable(definition) && !practice && !definition.DisableFatigueConsumption &&
        !definition.IsTutorial && !definition.IsClassExperience;

    public static bool HasCrystalCard(DungeonDefinition definition, byte difficulty) => IsPlayable(definition) && difficulty <= 2;

    // 剩余 0 由调用方先返回 fatigue-exhausted，这里不另造“图内耗尽禁止过房”。
    // 无限疲劳跳过门槛，与入场一次性疲劳同一口径。
    public static string? EntryFatigueRejection(ushort minimum, bool infiniteFatigue, int? storedRemaining, int clientRemaining)
    {
        if (minimum == 0 || infiniteFatigue) return null;
        if (storedRemaining is null) return "fatigue-owner-missing";
        if (storedRemaining.Value < minimum || clientRemaining < minimum)
            return "start-fatigue-insufficient:" + minimum;
        return null;
    }

    static bool IsDisaster(DungeonDefinition definition) =>
        string.Equals(definition.DungeonType?.Trim(), TypeName, StringComparison.OrdinalIgnoreCase);

}
