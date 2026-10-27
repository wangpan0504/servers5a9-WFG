using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

// 仅本连接本阶段的实际写出记账；不是领取资格，不保存第二份资产或在线角色。
internal sealed class LukeRewardResultProjection
{
    RaidCombatDisplayIdentity? identity;
    readonly HashSet<PreparedRaidReward> written = new(ReferenceEqualityComparer.Instance);

    internal bool Post(GameConnectionContext context, RaidRewardReceipt receipt, LukeRewardResult result)
    {
        var body = LukeRewardPackets.Result(result);
        return context.Owner.Execute(() =>
        {
            if (result.Owner != receipt.Prepared.Character || result.Kind != receipt.Prepared.Kind || result.Card != receipt.Prepared.Card ||
                result.Item != receipt.Offer.LukeResult || context.Session.LukeResultView(receipt) is not { } captured) return false;
            var expected = RaidCombatDisplayIdentity.Capture(captured.Actor, captured.View);
            if (identity != expected) { identity = expected; written.Clear(); }
            bool Current() => identity == expected && context.Session.LukeResultView(receipt) is { } current &&
                RaidCombatDisplayIdentity.Capture(current.Actor, current.View) == expected;
            bool DisplayReady() => Current() && context.RaidDisplay == expected;
            // 排在587后，泵才检验written；不在party命令门禁里等待慢socket。
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, body, "luke_reward_result",
                () => DisplayReady() && !written.Contains(receipt.Prepared),
                () => { if (DisplayReady()) written.Add(receipt.Prepared); })) return true;
            context.Sender.TryPost(1, (ushort)CmdPacketType.SELECT_RAID_REWARD_CARD, [1], "luke_reward_selected",
                () => DisplayReady() && written.Contains(receipt.Prepared), afterWritten: () =>
                {
                    // 泵已退出owner；Confirm会锁外发布其它成员，不能塞进Written回调。
                    if (context.Owner.Execute(() => DisplayReady() && written.Contains(receipt.Prepared)))
                        context.Session.ConfirmRaidRewardPublished(receipt);
                });
            return true;
        });
    }
}
