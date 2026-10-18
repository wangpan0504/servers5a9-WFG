using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public DungeonInventoryDiscardResult DiscardFormalDungeonItem(InventoryDiscardRequest request, InventoryService inventory)
    {
        var capture = owner.Execute(() =>
        {
            var dungeon = formalDungeon;
            bool allowed = Ready && Selected is not null && Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
                dungeon is not null && !dungeon.Ground.HasPendingPickup && !dungeon.Run.HasPendingDeath &&
                !dungeon.Run.HasPendingSettlement && !dungeon.HasPendingFatigue && !HasPendingQuestScene &&
                dungeon.PendingTutorialClear is null &&
                (dungeon.PendingInventoryDiscard is null || dungeon.PendingInventoryDiscard.Plan.Request == request);
            return (Expected: allowed ? Selected : null, Dungeon: dungeon, Identity: dungeon?.Run.Snapshot.Identity,
                Pending: dungeon?.PendingInventoryDiscard);
        });
        if (capture.Expected is not { } expected || capture.Dungeon is not { } dungeon || capture.Identity is not { } identity)
            return new(19);
        // 模板查询和纯库存规划不占用账号锁；结果不明重试复用原准备，不重分key或重新扣物。
        var planned = capture.Pending is { } pending ? (Plan: (InventoryDiscardPlan?)pending.Plan, Error: (ushort)0) :
            inventory.Discard(expected, request);
        if (planned.Plan is not { } plan) return new(planned.Error);
        return owner.Execute(() =>
        {
            if (!Ready || !ReferenceEquals(Selected, expected) || !ReferenceEquals(formalDungeon, dungeon) ||
                dungeon.Run.Snapshot.Identity != identity || Phase is not (SessionPhase.Dungeon or SessionPhase.Settlement) ||
                !ReferenceEquals(dungeon.PendingInventoryDiscard, capture.Pending) || dungeon.Ground.HasPendingPickup ||
                dungeon.Run.HasPendingDeath || dungeon.Run.HasPendingSettlement || dungeon.HasPendingFatigue ||
                HasPendingQuestScene || dungeon.PendingTutorialClear is not null)
                return new DungeonInventoryDiscardResult(19);
            var prepared = dungeon.PendingInventoryDiscard;
            if (prepared is null)
            {
                uint key = expected.DungeonState?.NextObjectKey ?? 0;
                if (key == expected.Id) key++;
                if (key is 0 or > ushort.MaxValue) return new DungeonInventoryDiscardResult(19);
                var ground = new DungeonGroundObject(Guid.NewGuid(), identity,
                    new(expected.Id, key, dungeon.Run.Snapshot.CurrentMap, request.X, request.Y, 0), plan.GroundCore)
                {
                    ResourceIdentity = "inventory",
                    InventoryOrigin = new(plan.Source, request.Count, expected.Revision)
                };
                if (!dungeon.Ground.PrepareInventoryDrop(ground)) return new DungeonInventoryDiscardResult(19);
                prepared = new(expected, plan, ground);
                dungeon.PendingInventoryDiscard = prepared;
            }
            Selected = repository.CommitDungeonDiscard(owner, prepared);
            if (!dungeon.Ground.ConfirmInventoryDrop(prepared.Ground))
                throw new InvalidOperationException("已提交地面丢弃不能确认原准备");
            dungeon.PendingInventoryDiscard = null;
            var receipt = new InventoryCommitReceipt(Selected, owner.Generation, SelectionEpoch)
            {
                Phase = Phase,
                DungeonIdentity = identity,
                PreviousQuests = expected.ActiveMainQuests,
                PreviousSkillSwitchInventory = expected.SkillSwitchInventory
            };
            return new DungeonInventoryDiscardResult(0, new InventoryResult(true, 0, prepared.Plan.Inventory) { Receipt = receipt }, prepared.Ground);
        });
    }
}
