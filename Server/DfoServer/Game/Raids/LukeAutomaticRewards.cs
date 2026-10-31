using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Raids;

// 每实例时钟串行调用；在线沿Session资产提交，离线只预约空闲账号，不接管在线玩家。
internal sealed class LukeAutomaticRewards(ILukeRewardRecoveryStore store, AccountOwners owners, OnlineSessionDirectory online,
    Func<AccountOwners.Lease, ushort, Character?> load, ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces,
    TimeProvider clock)
{
    internal async Task SettleAsync(AntonRaidBattle battle)
    {
        foreach (var (account, eligibility) in battle.DueLukeRewards())
            foreach (var offer in eligibility.Offers.GroupBy(row => row.Kind).Select(group => group.OrderBy(row => row.Card).First()))
            {
                if (battle.HasConfirmedLukeReward(eligibility.Character, eligibility.Phase, offer.Kind)) continue;
                var session = online.Find(account);
                if (session is not null)
                {
                    if (!session.CommandGate.Wait(0)) continue;
                    RaidRewardReceipt? receipt;
                    (bool Claimed, DfoServer.Game.Inventory.Models.InventoryResult? Refresh) recovered = default;
                    try
                    {
                        if (session.Selected?.Id == eligibility.Character && session.RaidCombatView()?.Instance == battle.Instance)
                            receipt = session.ClaimRaidReward(offer.Kind, offer.Card, store, items, gold, pieces, clock.GetUtcNow());
                        else
                        {
                            receipt = null;
                            recovered = session.ClaimAutomaticLukeRecovery(eligibility, offer, store, load, items, gold, pieces, clock.GetUtcNow());
                        }
                    }
                    finally { session.CommandGate.Release(); }
                    if (recovered.Claimed)
                    {
                        try
                        {
                            if (recovered.Refresh is { } refresh && session.AutomaticLukeRecoveryArrived is { } publishRecovery)
                                await publishRecovery(refresh);
                        }
                        finally { battle.ConfirmAutomaticLukeReward(eligibility, offer.Kind); }
                        continue;
                    }
                    if (receipt is null) continue;
                    // 资产已提交即满足自动结算义务；网络展示失败不重发资产，也不永久锁住全团。
                    try { if (session.AutomaticRaidRewardArrived is { } publish) await publish(receipt); }
                    finally { battle.ConfirmAutomaticLukeReward(eligibility, offer.Kind); }
                    continue;
                }
                if (!owners.TryAcquire(account, static () => { }, DuplicateLoginPolicy.Reject, out var lease, out _)) continue;
                using (lease)
                {
                    var character = load(lease!, eligibility.Character);
                    if (character is null) continue;
                    var target = new LukeRewardRecoveryTarget(character.Id, eligibility.Instance, eligibility.Phase, offer.Kind, offer.Card);
                    _ = LukeRewardRecovery.Claim(lease!, character, target, eligibility.Event, eligibility.Anchor, store,
                        () => (items, gold, pieces), clock.GetUtcNow());
                    battle.ConfirmAutomaticLukeReward(eligibility, offer.Kind);
                }
            }
        var view = battle.View;
        _ = battle.AdvanceAfterRewards(view.Instance, view.Revision);
    }
}
