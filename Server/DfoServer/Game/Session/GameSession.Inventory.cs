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

    // 选角时补建1号橱窗：仅当槽0不存在才写库，用当前穿戴作为基础预设；已有存档不做迁移。
    public bool InitializeAvatarPresets(AvatarPresetService presets)
    {
        var captured = owner.Execute(() => (Character: Selected, SelectionEpoch, Ready, Phase));
        if (captured.Character is not { } character) return false;
        var result = presets.PlanEnsureBase(character);
        if (!result.Accepted || result.Plan is not { Changed: true }) return false;
        return owner.Execute(() =>
        {
            // 规划在锁外完成；回锁后重新核对选角快照，避免把结果提交给已被替换的角色。
            if (Ready != captured.Ready || Phase != captured.Phase || SelectionEpoch != captured.SelectionEpoch ||
                !ReferenceEquals(Selected, character)) return false;
            CommitInventory(character, result);
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
            formalDungeon?.PendingTutorialClear is not null || formalDungeon?.PendingInventoryDiscard is not null ||
            formalDungeon?.AltarSettlement is { Stage: < 2 })
            return InventoryResult.Reject(invalidStateError);
        var result = operation(Selected);
        return CommitInventory(Selected, result, invalidStateError);
    });

    InventoryResult CommitInventory(Character character, InventoryResult result, ushort qualificationError = 1)
    {
        // 帝国双Actor资料在入场时冻结；所有库存用例共用提交门禁，涵盖穿脱、修理和耐久报告。
        if (IsImperialArenaCombat && result.Plan?.ChangedSlots.Any(slot => slot.List == InventoryListType.Equipment) == true)
            return InventoryResult.Reject(qualificationError);
        if (itemCycles is not null && result.Accepted && result.Plan is { } original)
            foreach (var use in original.UsedItems)
                result = itemCycles.ApplyUsage(character, result, 0, use.ItemId, use.Count);
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
        bool requireTown = true, SessionPhase? requiredPhase = null, InventorySlot? usedItem = null) =>
        PlanAndCommitInventory(plan, invalidStateError, requireTown, false, requiredPhase, usedItem: usedItem);

    public InventoryResult PlanAndCommitShopBuy(int shopId, Func<Character, InventoryResult> plan) =>
        PlanAndCommitInventory(plan, 17, requireTown: shopId == 400000097, allowSettlement: false,
            additionalGuard: shopId == 400000097
                ? () => Phase == SessionPhase.Town && Selected is { } character &&
                    towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character))
                : null);

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
        DfoServer.GameWorld.Items.BoosterUsePlacesDefinition places, int itemId = 0) =>
        PlanAndCommitInventory(plan, 19, requireTown: false, allowSettlement: false, additionalGuard: () =>
            places.PendingRule is null && (!places.Village && !places.SeriaRoom ||
                Phase == SessionPhase.Town && (places.Village || places.SeriaRoom && Selected is { } character &&
                    towns.IsRoom(character.Position, DfoServer.Game.Quests.QuestEligibility.Completed(character)))), consumableItemId: itemId);

    public InventoryResult UsePremiumContract(ConsumableUseRequest request, InventoryPremiumContractUseService service) =>
        PlanAndCommitInventory(character => service.Plan(character, request, service.Clock.GetUtcNow().ToUnixTimeSeconds()),
            123, requireTown: false, allowSettlement: false, consumableItemId: request.ItemId);

    public InventoryResult RepairEquipment(RepairEquipmentRequest request, InventoryRepairService repair) =>
        // 战斗中的耐久由本版客户端维护；CMD23 mode1触发后仍核对副本实例、契约期限与当日次数。
        PlanAndCommitInventory(character => repair.Repair(character, request,
            clientBattleWear: Phase == SessionPhase.Dungeon && formalDungeon is not null,
            inDungeon: Phase is SessionPhase.Dungeon or SessionPhase.Settlement), 19,
            requireTown: request.Mode != 1 && request.RepairItemSlot < 0, allowSettlement: true,
            usedItem: request.RepairItemSlot >= 0 ? new(InventoryListType.Main, request.RepairItemSlot) : null);

    public InventoryResult DisjointEquipment(DisjointRequest request, InventoryDisjointService disjoint, out DisjointReceipt? receipt)
    {
        DisjointReceipt? plannedReceipt = null;
        var result = PlanAndCommitInventory(character => disjoint.Disjoint(character, request, out plannedReceipt), 19,
            requireTown: true, allowSettlement: true);
        receipt = result.Accepted ? plannedReceipt : null;
        return result;
    }

    InventoryResult PlanAndCommitInventory(Func<Character, InventoryResult> plan, ushort invalidStateError,
        bool requireTown, bool allowSettlement, SessionPhase? requiredPhase = null, Func<bool>? additionalGuard = null,
        int? consumableItemId = null, InventorySlot? usedItem = null)
    {
        var capture = owner.Execute(() => (Character: CanPlan() ? Selected : null,
            SelectionEpoch, Phase, Dungeon: formalDungeon, Identity: formalDungeon?.Run.Snapshot.Identity));
        if (capture.Character is not { } character)
            return InventoryResult.Reject(invalidStateError);

        // 只读资源解析和批量规划不占用所属账号锁；此回调不能写库或修改在线状态。
        var result = plan(character);
        if (result is { Accepted: true, Plan: { } planned } && UsedItemId(character) is > 0 and var usedId &&
            !planned.UsedItems.Any(row => row.ItemId == usedId))
        {
            long beforeCount = character.Inventory.Items.Where(item => item.Core.ItemId == usedId).Sum(item => (long)item.Core.Count);
            long afterCount = planned.After.Items.Where(item => item.Core.ItemId == usedId).Sum(item => (long)item.Core.Count);
            if (beforeCount > afterCount)
            {
                // 已明确声明为使用源的计划才记使用次数，移动、出售和邮件扣物不进入此路径。
                result = result with { Plan = planned.WithAfter(planned.After,
                    usedItems: planned.UsedItems.Add(new(usedId, checked((int)(beforeCount - afterCount))))) };
            }
        }
        return owner.Execute(() =>
        {
            // 换角、接管及 GM/其他业务替换快照后，不能把旧随机结果提交给新的状态，也不自动重抽。
            if (!CanPlan() || SelectionEpoch != capture.SelectionEpoch || Phase != capture.Phase ||
                !ReferenceEquals(formalDungeon, capture.Dungeon) || formalDungeon?.Run.Snapshot.Identity != capture.Identity ||
                !ReferenceEquals(Selected, character))
                return InventoryResult.Reject(invalidStateError);
            if (result.Plan?.UnlockSkillTree == true && (HasClassExperienceContext || Selected?.Position is { Town: 10, Area: 4 }))
                return InventoryResult.Reject(invalidStateError);
            var committed = CommitInventory(character, result, invalidStateError);
            if (committed.Accepted && UsedItemId(character) is { } itemId) CommitDungeonItemUse(itemId);
            return committed;
        });

        bool CanPlan()
        {
            if (pendingRental is not null || additionalGuard?.Invoke() == false || !Ready || Selected is null || HasPendingTownQuestCommit || HasPendingFormalCard ||
                requiredPhase is { } expected && Phase != expected || formalDungeon?.PendingTutorialClear is not null) return false;
            if (UsedItemId(Selected) is { } itemId && !CanUseDungeonItem(itemId)) return false;
            // 城镇未提交手册和已安装回答缓存可随库存自然失效；副本继续保留专用取放资格。
            if (requireTown) return Phase == SessionPhase.Town || allowSettlement && CanUseSettlementNpcServices();
            return (Phase == SessionPhase.Town || !HasUncommittedQuestQuickClear) &&
                !HasActiveDungeonContinuation && !HasPendingQuestReplay &&
                formalDungeon?.Ground.HasPendingOperation != true &&
                (Phase == SessionPhase.Town || Phase is SessionPhase.Dungeon or SessionPhase.Settlement &&
                    formalDungeon is { HasPendingFatigue: false } dungeon && !dungeon.Run.HasPendingDeath && !dungeon.Run.HasPendingSettlement);
        }

        int? UsedItemId(Character character) => usedItem is { } slot ?
            character.Inventory.Find(slot)?.Core.ItemId ?? 0 : consumableItemId;
    }
}
