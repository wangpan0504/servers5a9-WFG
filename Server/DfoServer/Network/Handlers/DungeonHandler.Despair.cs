using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed partial class DungeonHandler
{
    async Task EnableSettlementAsync(GameConnectionContext context, ushort? reportedBossKey = null)
    {
        if (context.Session.FormalEnteredDungeonId is not (>= 11008 and <= 11107))
        {
            await DungeonSettlementProjection.EnableAsync(context, reportedBossKey);
            return;
        }
        if (despairRewards is null || titles is null || titleProjection is null || inventoryProjection is null)
            throw new InvalidOperationException("绝望正式通关缺少完整奖励与称号投影依赖");
        if (context.Session.CompleteDungeonDespair(despairRewards, dungeons.Despair, titles) is not { } receipt) return;
        bool Guard() => context.Session.CanPublishDespairClear(receipt);
        if (receipt.Title.Plan is not null)
            await titleProjection.PublishAsync(context, receipt.Title, inventoryProjection);
        else
            await inventoryProjection.RefreshAsync(context, new(true, 0, receipt.Commit.Inventory)
            {
                Receipt = new(receipt.Commit.Character, receipt.Dungeon.Generation, receipt.Dungeon.SelectionEpoch)
                {
                    Phase = SessionPhase.Settlement, DungeonIdentity = receipt.Pending.Fact.Identity,
                    PreviousQuests = receipt.Pending.Expected.ActiveMainQuests,
                    PreviousSkillSwitchInventory = receipt.Pending.Expected.SkillSwitchInventory
                }
            });
        bool published = false;
        await context.Notify(NotiPacketType.TOD_CLEAR_REWARD,
            DungeonDespairPackets.Clear(receipt.Pending.PlayMilliseconds, receipt.Pending.Reward!), "formal_despair_clear",
            () => published = Guard());
        if (published) context.Session.ConfirmDespairClearPublished(receipt);
        log.Write("formal_despair_clear", new { Connection = context.Id, receipt.Pending.Fact.Identity,
            receipt.Pending.Entry.Layer, receipt.Commit.Progress.NextLayer, receipt.Commit.Duplicate, Published = published,
            Rule = Game.Dungeons.DungeonDespairRewardPlan.RuleId });
    }
}
