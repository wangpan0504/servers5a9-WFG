using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Game.TagEvildom;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class TagEvildomRunProjection(TagEvildomRewardCatalog rewards, ItemCatalog items,
    ITagEvildomClearStore store, TagEvildomProjection configuration, ServerLog log)
{
    public TagEvildomRewardCatalog Rewards => rewards;

    public FormalDungeonDeathResult? Commit(GameConnectionContext context, TagEvildomClearPreparation pending) =>
        context.Session.CommitFormalTagEvildomClear(pending, rewards, items, store);

    public async Task ReturnAsync(GameConnectionContext context, TownProjection town, TownTravelReceipt receipt)
    {
        var primary = await town.RefreshTagEvildomPrimaryAsync(context, receipt);
        if (primary is not null) await configuration.InitializeAsync(context, primary);
        else await town.ReturnToSelectionAsync(context, () => context.Session.CanPublishTravel(receipt));
    }

    public async Task ClearAsync(GameConnectionContext context, TagEvildomClearReceipt receipt)
    {
        if (!context.Session.CanPublishTagEvildomClear(receipt)) return;
        bool Guard() => context.Session.CanPublishTagEvildomClear(receipt);
        bool sent = false;
        bool ResultGuard()
        {
            sent = context.Session.CanPublishTagEvildomResult(receipt);
            return sent;
        }
        var committed = receipt.Commit;
        if (context.Session.CanPublishTagEvildomResult(receipt))
        {
            var presentation = new TagEvildomClearPresentation(committed.Plan.Reward.Items
                .Select(item => new TagEvildomRewardRow(checked((uint)item.ItemId), checked((uint)item.Count))).ToImmutableArray(),
                committed.Plan.ElapsedMilliseconds);
            await context.Notify(NotiPacketType.EVILDOM_GATHERING_CLEAR_REWARD, TagEvildomRunPackets.ClearPresentation(presentation),
                "tag_evildom_clear_reward", ResultGuard);
            if (!sent) return;
            // 仅本连接成功写出后标记；同一终点重传不重复弹结果窗，数据库回执仍独立永久幂等。
            context.Session.ConfirmTagEvildomResultPublished(receipt);
        }
        if (context.Session.TagEvildomClearState(receipt, configuration.Store, configuration.Now) is { } state)
            await context.Notify(NotiPacketType.EVILDOM_GATHERING, TagEvildomPackets.Gathering(state), "tag_evildom_clear_counts", Guard);
        await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "tag_evildom_reward_mail", Guard);
        log.Write("tag_evildom_clear_committed", new { Connection = context.Id, receipt.Dungeon.Snapshot.Identity,
            committed.MailId, committed.Duplicate, committed.Plan.Reward.DefinitionId, committed.Plan.ElapsedMilliseconds,
            Recipient = committed.Plan.Fact.Admission.Primary.Id, Rule = TagEvildomRewardPolicy.Rule });
    }
}
