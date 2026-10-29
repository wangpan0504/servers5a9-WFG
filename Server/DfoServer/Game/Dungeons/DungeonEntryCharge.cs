using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;
using System.Collections.Immutable;

namespace DfoServer.Game.Dungeons;

// #510/#511 普通入场在提交时一次结清的费用：入场票背包计划与入场一次性疲劳，与尝试号预留同一事务。
// Identity/MapId 是本次待安装实例的身份与起始房间，写入疲劳扣点账本。
// Weekly 是本次入场计入的冒险团每周地下城列；没有费用时走普通预留，同样按同一事务累加。
// 仅保存已解释的次数计划，参与调用方入场事务，不另开事务。
internal sealed record DungeonEntryIncount(int Dungeon, DungeonIncountPeriod Period, DungeonIncountRow? Row,
    ImmutableArray<AccountIncountCharge> Account, bool Increment)
{
    public DungeonIncountUsage? ExpectedUsage { get; init; }
}

internal sealed record BeastDungeonAdmission(int Day, int ExpectedUsed)
{
    public InventoryPlan? Ticket { get; init; }
}

internal sealed record DungeonEntryCommit(Character Character, DungeonIncountUsage? IncountUsage)
{
    public DfoServer.Game.Guild.GuildDungeonMembership? GuildMembership { get; init; }
}

internal sealed record DungeonEntryCharge(InventoryPlan? Items, ushort Fatigue, DungeonRunIdentity Identity, int MapId,
    WeeklyDungeonEntry? Weekly = null, GriefTowerAdmission? Grief = null)
{
    // 训练费独立于副本门票表达，付款仍在原入场事务中合并为一次背包提交。
    public InventoryPlan? GuildTraining { get; init; }
    public InventoryPlan? InventoryPayment
    {
        get
        {
            if (GuildTraining is not { } training) return Items;
            if (Items is null) return training;
            if (!ReferenceEquals(Items.After, training.Before))
                throw new InvalidDataException("训练费用必须紧随本次入场票规划");
            return Items.WithAfter(training.After);
        }
    }
    public DungeonSpecialEntry? Special { get; init; }
    public DazzlementTowerAdmission? Dazzlement { get; init; }
    public int DungeonId { get; init; }
    public DungeonEntryPlan? OrdinaryLukeEntry { get; init; }
    public OperationHopeAdmission? Hope { get; init; }
    public MarriageDungeonAdmission? Marriage { get; init; }
    public DungeonEntryIncount? Incount { get; init; }
    public BeastDungeonAdmission? Beast { get; init; }
    public DfoServer.Game.TagEvildom.TagEvildomAdmission? TagEvildom { get; init; }
    public DfoServer.Game.Guild.GuildDungeonAdmission? GuildDungeon { get; init; }
    public DfoServer.Game.ImperialArena.ImperialArenaAdmission? ImperialArena { get; init; }
    public DimensionDungeonAdmission? Dimension { get; init; }
    public bool IsEmpty => Items is null && GuildTraining is null && Fatigue == 0 && Special is null && Dazzlement is null && Grief is null && Hope is null && Marriage is null && Incount is null && Beast is null && TagEvildom is null && OrdinaryLukeEntry is null && GuildDungeon is null && ImperialArena is null && Dimension is null;
}
