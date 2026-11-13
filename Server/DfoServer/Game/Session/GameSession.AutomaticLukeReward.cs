using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Raids;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    // 共享钱包写入必须等待当前角色的在途资产规划完成，避免旧规划覆盖新钱包。
    bool HasPendingInventoryCommit => pendingRental is not null || pendingConsumableUse is not null || HasPendingFormalCard || HasPendingTownQuestCommit ||
        HasUncommittedQuestQuickClear || formalDungeon?.HasPendingFatigue == true || formalDungeon?.Run.HasPendingDeath == true ||
        formalDungeon?.Run.HasPendingSettlement == true || formalDungeon?.Ground.HasPendingOperation == true ||
        formalDungeon?.PendingTutorialClear is not null || pendingQuestCompletion is not null ||
        pendingQuestLifecycle is not null || HasPendingQuestScene;

    internal Func<InventoryResult, Task>? AutomaticLukeRecoveryArrived { get; set; }

    // 已离开原团、停在选角或改玩其它角色时，仍由当前账号owner领取已冻结的原角色资格。
    // 不替换当前角色的背包，只同步同账号共享钱包；原团奖励窗口不投影到另一角色。
    internal (bool Claimed, InventoryResult? Refresh, PreparedRaidReward? Prepared) ClaimAutomaticLukeRecovery(RaidPhaseEligibility eligibility,
        RaidRewardOffer offer, ILukeRewardRecoveryStore store, Func<AccountOwners.Lease, ushort, Character?> load,
        ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces, DateTimeOffset now) => owner.Execute(() =>
    {
        if (HasPendingInventoryCommit || eligibility.RaidType == RaidKind.Prey &&
            (eligibility.Source is not (PreyGuideRewards.Source or PreyGuideRewards.TeamSource) ||
                offer.Kind is not (0 or 1) || offer.Card != 0))
            return (false, (InventoryResult?)null, (PreparedRaidReward?)null);
        var before = Selected;
        var target = before?.Id == eligibility.Character ? before : load(owner, eligibility.Character);
        if (target is null) return (false, (InventoryResult?)null, (PreparedRaidReward?)null);
        var identity = new LukeRewardRecoveryTarget(target.Id, eligibility.Instance, eligibility.Phase, offer.Kind, offer.Card)
            { RaidType = eligibility.RaidType };
        var committed = LukeRewardRecovery.Claim(owner, target, identity, eligibility.Event, eligibility.Anchor, store,
            () => (items, gold, pieces), now);
        var prepared = store.ReadLukeRewardRecovery(owner, committed.Character, identity).Prepared;
        if (before is null) return (true, (InventoryResult?)null, prepared);
        Selected = before.Id == target.Id ? committed.Character :
            before with { Inventory = before.Inventory with { AccountWallet = committed.Character.Inventory.AccountWallet } };
        var refresh = new InventoryResult(true, 0, new(before.Inventory, Selected.Inventory, "raid_reward"))
        {
            Receipt = new(Selected, owner.Generation, SelectionEpoch)
            { Phase = Phase, DungeonIdentity = formalDungeon?.Run.Snapshot.Identity, PreviousQuests = before.ActiveMainQuests,
                PreviousSkillSwitchInventory = before.SkillSwitchInventory }
        };
        if (!committed.MailIds.IsEmpty && before.Id == target.Id) NotifyMailbox(target.Id, restoreOnTown: true);
        return (true, refresh, prepared);
    });
}
