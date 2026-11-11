using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

// 哈林灾难领域。六张 dgn 都没有疲劳标签，官方公告的 8 点门槛和每房 3 点不能写成 StartFatigue：
// StartFatigue 会在入场一次扣光，并关掉逐房扣点。
// 实图同时要求编号 5341–5345 和类型 disaster dungeon：只认类型会在入口 5340 一旦有迷宫时按房收费；只认编号会在类型改掉后继续收费。
// 默认难度2在翻牌给礼盒490012200，不在Boss死亡时重复直发。etc/dungeonetc/disasterdungeon.etc 的
// [basic reward list by dungeon difficulty] 难度 2 原行是
// `normal` 10000 -1 490012200 `gold` 0 -1 490012200。
// 入口 disaster.dgn 的 [designate dungeon difficulty] 与 [difficulty] 都是 2。
// 五张实图 dgn 和 etc/itemdropinfo_clearreward.etc 都不写这个编号。
// 礼盒 [etc] 打开后是 20 个账号绑定的 10309039，数量由开箱逻辑处理，这里不拆成 20 个散件。
// 小地图物件破坏没有服务端事件，闪耀的精炼时空石不在这里发。
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

    public static bool HasCrystalCard(DungeonDefinition definition, byte difficulty) => IsPlayable(definition) && difficulty == 2;

    public static SpecialDungeonClearReward? ClearReward(DungeonDefinition definition, byte difficulty = 0) =>
        IsPlayable(definition) && !HasCrystalCard(definition, difficulty) ? Reward : null;

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

    static readonly SpecialDungeonClearReward Reward = new(0, "灾难领域",
        "背包已满，净化的异形结晶礼盒改由邮件发放。",
        [new(CrystalBoxId, SpecialDungeonClearReward.ProbabilityScale, 1)]);
}
