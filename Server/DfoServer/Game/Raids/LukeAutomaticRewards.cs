using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Raids;

// 只收集本轮已提交并确认权益的展示失败；未到期或忙碌权益仍沿原路径等待。
internal sealed class LukeRewardPublicationException(IEnumerable<Exception> errors)
    : AggregateException("Luke reward publication failed after committed settlement", errors);

// 每实例时钟串行调用；在线沿Session资产提交，离线只预约空闲账号，不接管在线玩家。
internal sealed class LukeAutomaticRewards(ILukeRewardRecoveryStore store, AccountOwners owners, OnlineSessionDirectory online,
    Func<AccountOwners.Lease, ushort, Character?> load, ItemCatalog items, GoldLimitCatalog gold, EpicPieceCatalog pieces,
    TimeProvider clock)
{
    internal async Task SettleAsync(AntonRaidBattle battle)
    {
        var publicationErrors = new List<Exception>();
        try
        {
            foreach (var (account, eligibility) in battle.DueLukeRewards())
                foreach (var offer in eligibility.Offers.GroupBy(row => row.Kind).Select(group => group.OrderBy(row => row.Card).First()))
                {
                    if (battle.HasConfirmedLukeReward(eligibility.Character, eligibility.Phase, offer.Kind)) continue;
                    // 原660已提交但ACK丢失时直接确认精确原回执，不能改用最低Card0重放而卡住非零牌。
                    if (battle.SubmittedLukeReward(eligibility.Character, eligibility.Phase, offer.Kind) is { } submitted)
                    { battle.ConfirmAutomaticLukeReward(eligibility, submitted); continue; }
                    var session = online.Find(account);
                    if (session is not null)
                    {
                        if (!session.CommandGate.Wait(0)) continue;
                        RaidRewardReceipt? receipt;
                        (bool Claimed, DfoServer.Game.Inventory.Models.InventoryResult? Refresh, PreparedRaidReward? Prepared) recovered = default;
                        try
                        {
                            if (session.Selected?.Id == eligibility.Character && session.RaidCombatView()?.Instance == battle.Instance)
                                receipt = session.ClaimRaidReward(offer.Kind, offer.Card, store, items, gold, pieces, clock.GetUtcNow(), automaticLuke: true);
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
                            catch (Exception error) { publicationErrors.Add(error); }
                            finally { battle.ConfirmAutomaticLukeReward(eligibility, recovered.Prepared!); }
                            continue;
                        }
                        if (receipt is null) continue;
                        // 资产已提交即满足自动结算义务；网络展示失败不重发资产，也不永久锁住全团。
                        try { if (session.AutomaticRaidRewardArrived is { } publish) await publish(receipt); }
                        catch (Exception error) { publicationErrors.Add(error); }
                        finally { battle.ConfirmAutomaticLukeReward(eligibility, receipt.Prepared); }
                        continue;
                    }
                    if (!owners.TryAcquire(account, static () => { }, DuplicateLoginPolicy.Reject, out var lease, out _)) continue;
                    using (lease)
                    {
                        var character = load(lease!, eligibility.Character);
                        if (character is null) continue;
                        var target = new LukeRewardRecoveryTarget(character.Id, eligibility.Instance, eligibility.Phase, offer.Kind, offer.Card);
                        var commit = LukeRewardRecovery.Claim(lease!, character, target, eligibility.Event, eligibility.Anchor, store,
                            () => (items, gold, pieces), clock.GetUtcNow());
                        var prepared = store.ReadLukeRewardRecovery(lease!, commit.Character, target).Prepared;
                        battle.ConfirmAutomaticLukeReward(eligibility, prepared);
                    }
                }
            var view = battle.View;
            _ = battle.AdvanceAfterRewards(view.Instance, view.Revision);
        }
        catch (Exception error)
        {
            // 后续资产或确认失败仍按原异常传播；保留此前展示错误而不冒充结算完成。
            if (publicationErrors.Count != 0)
                error.Data["LukeRewardPublicationFailures"] = new AggregateException(publicationErrors);
            throw;
        }
        if (publicationErrors.Count != 0) throw new LukeRewardPublicationException(publicationErrors);
    }
}
