using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

internal enum AssaultModeRejection
{
    None,
    StateInvalid,
    NotAssaultDungeon,
    DailyLimitReached,
    HiddenLocked,
    NotOpenToday,
    ResourceUnavailable,
    LevelTooLow,
    PrerequisiteQuest,
    MissingItems,
}

// 已通过 CMD1547 校验的强袭授权：Parent 为客户端选中的主副本(7300)，Mission 为 ACK1547 回给客户端、
// 随后 C2S15/C2S16 实际进入的任务副本。材料只在 C2S16 入场提交时扣除。
internal sealed record AssaultModeSelection(uint Parent, uint Mission, AssaultModeMissionRequirement Requirement)
{
    public bool Hidden { get; init; }
    public int DayKey { get; init; }
}

// 当日强袭计数：Clears=NOTI1536 b0（普通任务通关次数），Hidden=b1（隐藏关卡通关次数）。
internal readonly record struct AssaultDailyCounts(int Clears, int Hidden);
internal sealed record AssaultDailyCommit(Character Character, AssaultDailyCounts Counts);

internal sealed record AssaultModeDecision(AssaultModeSelection? Selection, AssaultModeRejection Reason, string? Detail = null)
{
    public bool Accepted => Selection is not null;
}

internal static class AssaultModeEntryPolicy
{
    // A81B50 以常量2调用 3BB9A90(id,2,0,0,-1)，该参数经 <=4 校验后作为难度；任务副本 dgn 指定难度0。
    // 线上 C2S16 offset4 是否即为2待确认，故仅接受 0 或 2 并归一到 dgn 指定难度。
    public const byte ClientAssaultDifficulty = 2;

    public static AssaultModeDecision Select(AssaultModeCatalog catalog, Character character, uint parent, int day,
        EpicPieceCatalog pieces, DateTimeOffset utcNow, Func<int, int> draw, AssaultDailyCounts counts = default, int dayKey = 0)
    {
        if (parent > int.MaxValue || !catalog.Missions.TryGetValue((int)parent, out var missions) || missions.IsEmpty)
            return new(null, AssaultModeRejection.NotAssaultDungeon);
        bool hidden = parent == catalog.HiddenBaseDungeon;
        if (catalog.Day(day) is not { } open)
            return new(null, AssaultModeRejection.NotOpenToday, "day=" + day);
        if (hidden)
        {
            // 客户端 A81090：b0>=3 且 b1<1 时开放隐藏副本；当日任务取 [daily open dungeon] 的隐藏列。
            if (!catalog.HiddenUnlocked(counts.Clears, counts.Hidden))
                return new(null, AssaultModeRejection.HiddenLocked, counts.Clears + "/" + counts.Hidden);
            if (!missions.Contains(open.HiddenDungeon))
                return new(null, AssaultModeRejection.NotOpenToday, "hidden=" + open.HiddenDungeon);
            missions = [open.HiddenDungeon];
        }
        else
        {
            if (!open.Dungeons.Contains((int)parent))
                return new(null, AssaultModeRejection.NotOpenToday, "day=" + day);
            if (counts.Clears >= AssaultModeCatalog.DailyClearLimit)
                return new(null, AssaultModeRejection.DailyLimitReached, counts.Clears.ToString());
        }
        var requirements = new List<AssaultModeMissionRequirement>();
        foreach (int mission in missions)
        {
            if (!catalog.TryGetRequirement(mission, out var requirement, out string? error))
                return new(null, AssaultModeRejection.ResourceUnavailable, mission + ": " + error);
            requirements.Add(requirement!);
        }
        // 任务副本在 5 个区域间如何选择（随机/轮换/按日）待确认；当前均匀随机。
        int index = missions.Length == 1 ? 0 : draw(missions.Length);
        if (index < 0 || index >= missions.Length) throw new InvalidDataException("强袭任务副本抽取越界");
        var chosen = requirements[index];
        if (character.Level < chosen.MinimumLevel)
            return new(null, AssaultModeRejection.LevelTooLow, character.Level + "<" + chosen.MinimumLevel);
        if (!QuestEligibility.Completed(character).Contains(AssaultModeCatalog.PrerequisiteQuestId))
            return new(null, AssaultModeRejection.PrerequisiteQuest);
        if (!ConsumptionPlan(character.Inventory, chosen, pieces, utcNow).Accepted)
            return new(null, AssaultModeRejection.MissingItems, chosen.ItemId + "x" + chosen.ItemCount);
        return new(new(parent, checked((uint)chosen.MissionDungeonId), chosen) { Hidden = hidden, DayKey = dayKey }, AssaultModeRejection.None);
    }

    public static InventoryResult ConsumptionPlan(InventorySnapshot inventory, AssaultModeMissionRequirement requirement,
        EpicPieceCatalog pieces, DateTimeOffset utcNow) =>
        InventoryMaterialConsumptionService.Plan(inventory, [new ItemMaterialCost(requirement.ItemId, requirement.ItemCount)], pieces, utcNow);

    // 计数键：本机自然日（重置时刻待确认）。
    public static int DayKey(DateTimeOffset localNow) => DateOnly.FromDateTime(localNow.DateTime).DayNumber;

    public static AssaultDailyCounts AfterClear(AssaultDailyCounts counts, bool hidden) =>
        hidden ? counts with { Hidden = Math.Min(counts.Hidden + 1, 255) } : counts with { Clears = Math.Min(counts.Clears + 1, 255) };
}
