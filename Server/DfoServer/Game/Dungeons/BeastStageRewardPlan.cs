using System.Collections.Immutable;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 阶段罐绑定清房的原死亡候选，与其成长回执共用一次库存提交；不是独立领取入口。
internal sealed class BeastStageRewardPlan
{
    BeastStageRewardPlan(Character expected, PreparedDungeonDeath prepared, int stage, QuestDropPlan? questDrops,
        InventoryPlan inventory, ImmutableArray<InventorySlot> slots)
        => (Expected, Prepared, Stage, QuestDrops, Inventory, ChangedSlots) =
            (expected, prepared, stage, questDrops, inventory, slots);

    public Character Expected { get; }
    public PreparedDungeonDeath Prepared { get; }
    public int Stage { get; }
    public QuestDropPlan? QuestDrops { get; }
    public InventoryPlan Inventory { get; }
    public ImmutableArray<InventorySlot> ChangedSlots { get; }

    public static BeastStageRewardPlan Create(Character expected, SinglePlayerDungeonRun run, PreparedDungeonDeath prepared,
        int stage, QuestDropPlan? questDrops, ItemCatalog items, GoldLimitCatalog goldLimits, EpicPieceCatalog pieces,
        DateTimeOffset at)
    {
        if (!run.Entry.BeastMode || !run.IsPendingDeath(prepared) || prepared.Death.Identity.CharacterId != expected.Id ||
            prepared.BeastRoomClear is not { } clear || clear.Identity != prepared.Result.Snapshot.Identity ||
            clear.Map != prepared.Result.Snapshot.CurrentMap || BeastDungeonRotation.StageRewardItem(stage) is not { } itemId ||
            questDrops is not null && (!ReferenceEquals(questDrops.Expected, expected) || questDrops.EventId != prepared.Death.EventId))
            throw new InvalidDataException("阶段奖励需要原魔兽实例的待提交清房死亡");
        if (!items.TryGet(itemId, out var template) ||
            !template.Name.Contains("魔兽地下城奖励袖珍罐", StringComparison.Ordinal))
            throw new InvalidDataException("阶段奖励袖珍罐资源缺失或编号不符");
        var basis = expected with { Inventory = questDrops?.Inventory.After ?? expected.Inventory };
        var grant = InventoryRewardGrantService.TryGrantBatch(basis, [new(itemId, 1)], items, goldLimits, pieces, at,
            allowOverflowMail: true);
        if (!grant.Accepted || grant.Plan is not { } reward)
            throw new InvalidOperationException("阶段奖励无法规划，清房保持待提交");
        var inventory = new InventoryPlan(expected.Inventory, reward.After, "beast_stage_reward", new(expected),
            [.. questDrops?.Inventory.OverflowItems ?? [], .. reward.OverflowItems]);
        return new(expected, prepared, stage, questDrops, inventory, reward.ChangedSlots);
    }

    public void Verify(Character expected, PreparedDungeonDeath prepared, QuestDropPlan? questDrops)
    {
        if (!ReferenceEquals(Expected, expected) || !ReferenceEquals(Prepared, prepared) || !ReferenceEquals(QuestDrops, questDrops))
            throw new InvalidDataException("阶段奖励不能替换原角色、死亡或任务掉落规划");
    }
}
