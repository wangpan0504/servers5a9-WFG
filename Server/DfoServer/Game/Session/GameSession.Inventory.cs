using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public void InitializeDevilContract(DevilContractService service, DateTimeOffset now)
    {
        var captured = owner.Execute(() =>
        {
            if (Selected is null || Ready || Phase != SessionPhase.Unselected)
                throw new InvalidOperationException("魔王契约初始化必须属于当前选角阶段");
            return (Character: Selected, Epoch: SelectionEpoch);
        });
        // 四项资源读取、奖励分配均在账号锁外；提交只接受原选角快照。
        var gift = service.DailyGift(captured.Character, now);
        owner.Execute(() =>
        {
            if (Ready || Phase != SessionPhase.Unselected || SelectionEpoch != captured.Epoch ||
                !ReferenceEquals(Selected, captured.Character))
                throw new InvalidOperationException("魔王契约初始化的选角快照已变化");
            CommitInventory(captured.Character, gift);
            return true;
        });
    }

    public DfoServer.Game.Currency.AccountWallet AccountWallet() => owner.Execute(() =>
        Selected?.Inventory.AccountWallet ?? repository.LoadAccountWallet(owner));
    public InventoryResult DeleteInventory(InventoryDeleteRequest request, InventoryService inventory) => MutateInventory(
        character => inventory.Delete(character,request,preserveMaterials: PreservesDungeonConsumables),
        requireTown: request.List is InventoryListType.PersonalCargo or InventoryListType.AccountCargo);
    public InventoryResult MutateInventory(Func<Character, InventoryResult> operation, ushort invalidStateError = 1,
        bool requireTown = true) => owner.Execute(() =>
    {
        if (pendingRental is not null || !Ready || requireTown && Phase != SessionPhase.Town || Selected is null || HasPendingTownQuestCommit || HasPendingFormalCard ||
            formalDungeon?.PendingTutorialClear is not null || formalDungeon?.PendingInventoryDiscard is not null)
            return InventoryResult.Reject(invalidStateError);
        var result = operation(Selected);
        return CommitInventory(Selected, result, invalidStateError);
    });

    InventoryResult CommitInventory(Character character, InventoryResult result, ushort qualificationError = 1)
    {
        try
        {
            Selected = InventoryService.Commit(repository, owner, character, result);
        }
        catch (InventoryQualificationExpiredException)
        {
            return InventoryResult.Reject(qualificationError) with { RejectReason = "qualification_expired" };
        }
        return result with { Receipt = result.Accepted && result.Plan is not null
            ? new(Selected, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase,
                    DungeonIdentity = formalDungeon?.Run.Snapshot.Identity,
                    PreviousQuests = character.ActiveMainQuests,
                    PreviousSkillSwitchInventory = character.SkillSwitchInventory
                } : null };
    }

    public bool CanPublishInventory(InventoryCommitReceipt receipt) => owner.Execute(() =>
        Ready && receipt.Generation == owner.Generation && Phase == receipt.Phase &&
        receipt.DungeonIdentity == formalDungeon?.Run.Snapshot.Identity &&
        SelectionEpoch == receipt.SelectionEpoch && Selected?.Id == receipt.Character.Id &&
        ReferenceEquals(Selected, receipt.Character));

    public InventoryResult PlanAndCommitInventory(Func<Character, InventoryResult> plan, ushort invalidStateError = 1,
        bool requireTown = true, SessionPhase? requiredPhase = null) => PlanAndCommitInventory(plan, invalidStateError, requireTown, false, requiredPhase);

    public InventoryResult SellAtShop(SellItemRequest request, InventoryShopService shop) =>
        PlanAndCommitInventory(character => shop.Sell(character, request), requireTown: false);

    public ShopPurchaseCount[]? ReadShopPurchaseCounts(int npcId, InventoryShopService shop, out InventoryCommitReceipt? snapshot)
    {
        snapshot = owner.Execute<InventoryCommitReceipt?>(() =>
            Ready && Selected is not null && (Phase == SessionPhase.Town ||
                Phase is SessionPhase.Dungeon or SessionPhase.Settlement && formalDungeon is not null)
                ? new(Selected, owner.Generation, SelectionEpoch)
                {
                    Phase = Phase,
                    DungeonIdentity = formalDungeon?.Run.Snapshot.Identity
                } : null);
        // 只读当前已安装的库存快照，限购资源解析不占用账号锁。
        return snapshot is null ? null : shop.PurchaseCounts(snapshot.Character, npcId);
    }

    public InventoryResult PlanAndCommitBooster(Func<Character, InventoryResult> plan,
        DfoServer.GameWorld.Items.BoosterUsePlacesDefinition places) =>
        PlanAndCommitInventory(plan, 19, requireTown: false, allowSettlement: false, additionalGuard: () =>
            places.PendingRule is null && (!places.Village && !places.SeriaRoom ||
                Phase == SessionPhase.Town && (places.Village || places.SeriaRoom && Selected is { } character &&
                    towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character)))));

    public InventoryResult RepairEquipment(RepairEquipmentRequest request, InventoryRepairService repair) =>
        // 战斗中的耐久由本版客户端维护；CMD23 mode1触发后仍核对副本实例、契约期限与当日次数。
        PlanAndCommitInventory(character => repair.Repair(character, request,
            clientBattleWear: Phase == SessionPhase.Dungeon && formalDungeon is not null), 19, requireTown: request.Mode != 1, allowSettlement: true);

    public InventoryResult DisjointEquipment(DisjointRequest request, InventoryDisjointService disjoint, out DisjointReceipt? receipt)
    {
        DisjointReceipt? plannedReceipt = null;
        var result = PlanAndCommitInventory(character => disjoint.Disjoint(character, request, out plannedReceipt), 19,
            requireTown: true, allowSettlement: true);
        receipt = result.Accepted ? plannedReceipt : null;
        return result;
    }

    InventoryResult PlanAndCommitInventory(Func<Character, InventoryResult> plan, ushort invalidStateError,
        bool requireTown, bool allowSettlement, SessionPhase? requiredPhase = null, Func<bool>? additionalGuard = null)
    {
        var capture = owner.Execute(() => (Character: CanPlan() ? Selected : null,
            SelectionEpoch, Phase, Dungeon: formalDungeon, Identity: formalDungeon?.Run.Snapshot.Identity));
        if (capture.Character is not { } character)
            return InventoryResult.Reject(invalidStateError);

        // 只读资源解析和批量规划不占用所属账号锁；此回调不能写库或修改在线状态。
        var result = plan(character);
        return owner.Execute(() =>
        {
            // 换角、接管及 GM/其他业务替换快照后，不能把旧随机结果提交给新的状态，也不自动重抽。
            if (!CanPlan() || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                !ReferenceEquals(formalDungeon, capture.Dungeon) || formalDungeon?.Run.Snapshot.Identity != capture.Identity ||
                !ReferenceEquals(Selected, character))
                return InventoryResult.Reject(invalidStateError);
            return CommitInventory(character, result, invalidStateError);
        });

        bool CanPlan()
        {
            if (pendingRental is not null || additionalGuard?.Invoke() == false || !Ready || Selected is null || HasPendingTownQuestCommit || HasPendingFormalCard ||
                requiredPhase is { } expected && Phase != expected || formalDungeon?.PendingTutorialClear is not null) return false;
            // 城镇未提交手册和已安装回答缓存可随库存自然失效；副本继续保留专用取放资格。
            if (requireTown) return Phase == SessionPhase.Town || allowSettlement && CanUseSettlementNpcServices();
            return (Phase == SessionPhase.Town || pendingQuestQuickClear is null) &&
                !HasActiveDungeonContinuation && !HasPendingQuestReplay &&
                formalDungeon?.Ground.HasPendingOperation != true &&
                (Phase == SessionPhase.Town || Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
                    formalDungeon is { HasPendingFatigue: false } dungeon && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement);
        }
    }
}
