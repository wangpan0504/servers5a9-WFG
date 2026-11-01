using DfoServer.Game.Raids;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static bool Movie(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view, bool completed)
    {
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Guard() => context.Session.CanPublishRaidBattle(actor, view) && context.RaidDisplay == identity;
        if (!Guard()) return true;
        if (!context.Sender.TryPost(1, (ushort)CmdPacketType.RAID_MOVIE_SKIP, [1], "raid_movie_ack", Guard)) return false;
        if (!completed)
        {
            // 01C63430发送0仅请求跳过；599通知原电影控件，不能提前领取或结束阶段。
            return context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_MOVIE_SKIP, [], "raid_movie_skip_prompt",
                () => Guard() && context.RaidMoviePrompt != identity && context.RaidMovieCards != identity,
                () => context.RaidMoviePrompt = identity);
        }
        bool Cards() => Guard() && context.RaidMovieCards != identity;
        // 02396DE0 state4→036D2770→01C99AA0显示翻牌组合对象；第二字节是选项，不是阶段。
        // 这是演出显示推进，Battle仍在Reward，只有实际资产提交确认才能转阶段。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_STATE, [4, 0], "raid_movie_cards", Cards)) return false;
        var offers = battle.RewardPolicy(actor, view.Phase)!.Offers;
        for (int index = 0; index < offers.Length; index++)
        {
            var offer = offers[index];
            // 尚未选择时不登记本人owner：600有本人行会写控件已选哨兵，阻止660自然发送。
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, [offer.Kind, 0], "raid_card_choices", Cards,
                index == offers.Length - 1 ? () => context.RaidMovieCards = identity : null)) return false;
        }
        return true;
    }
}
