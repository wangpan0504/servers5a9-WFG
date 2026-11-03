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
        // 最后一名无资格成员确认可同步进入等待；已接受的659仍需ACK，但不能投到新actor或新阶段。
        bool Acknowledge() => actor.IsCurrent && context.Session.CurrentRaidBattle(actor) == battle &&
            battle.View.Phase == view.Phase && context.RaidDisplay == identity;
        if (!context.Sender.TryPost(1, (ushort)CmdPacketType.RAID_MOVIE_SKIP, [1], "raid_movie_ack", Acknowledge)) return false;
        if (!completed)
        {
            if (battle.HasAntonRewards && view.AntonCardsReady) return true;
            // 01C63430发送0仅请求跳过；599通知原电影控件，不能提前领取或结束阶段。
            return context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_MOVIE_SKIP, [], "raid_movie_skip_prompt",
                () => Guard() && context.RaidMoviePrompt != identity && context.RaidMovieCards != identity,
                () => context.RaidMoviePrompt = identity);
        }
        if (battle.HasAntonRewards)
        {
            if (battle.CompleteAntonMovie(actor, view)) battle.Publish(battle.View);
            return true;
        }
        if (battle.Kind == RaidKind.Fiendwar)
        {
            if (battle.CompleteFiendwarMovie(actor, view)) battle.Publish(battle.View);
            return true;
        }
        if (view.Phase == 0 || battle.LukeEligibility(actor.Character, view.Phase) is { Eligible: false })
        {
            bool changed = battle.CompleteLukeFirstMovie(actor, view);
            if (changed && (battle.LukeMovieCardsReady(view) || !battle.IsCurrent(view))) battle.Publish(battle.View);
            return true;
        }
        return PostLukeMovieCards(context, actor, battle, view);
    }

    static bool PostLukeMovieCards(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        if (!battle.LukeMovieCardsReady(view) ||
            battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: true } ||
            battle.RewardPolicy(actor, view.Phase) is not { } policy) return true;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Cards() => context.Session.CanPublishRaidBattle(actor, view) && context.RaidDisplay == identity &&
            battle.LukeMovieCardsReady(view) && context.RaidMovieCards != identity;
        context.LukeResults.PostTeam(context, actor, battle, view);
        // 02396DE0 state4→036D2770→01C99AA0显示翻牌组合对象；第二字节写入真实阶段。
        // 这是演出显示推进，Battle仍在Reward，只有实际资产提交确认才能转阶段。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_STATE, [4, checked((byte)view.Phase)], "raid_movie_cards", Cards)) return false;
        var offers = policy.Offers;
        for (int index = 0; index < offers.Length; index++)
        {
            var offer = offers[index];
            // 尚未选择时不登记本人owner：600有本人行会写控件已选哨兵，阻止660自然发送。
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, [offer.Kind, 0], "raid_card_choices", Cards,
                index == offers.Length - 1 ? () => context.RaidMovieCards = identity : null)) return false;
        }
        return true;
    }

    static bool PostFiendwarMovieCards(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        if (!battle.FiendwarMovieCardsReady(view) || battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: true } ||
            battle.RewardPolicy(actor, view.Phase) is not { Offers.IsEmpty: false } policy) return true;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Cards() => context.Session.CanPublishRaidBattle(actor, view) && context.RaidDisplay == identity &&
            battle.FiendwarMovieCardsReady(view) && context.RaidMovieCards != identity;
        // 00D30460读取byte2并传给036D2770→01C99AA0；0沿原生普通开牌分支，清014926D0的特殊展示状态。
        // 1523必须保留三字节，不能把587的阶段字节误当此布尔参数。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.FIEND_WAR_STATE, [4, 0, 0], "fiendwar_movie_cards", Cards)) return false;
        for (int index = 0; index < policy.Offers.Length; index++)
        {
            var offer = policy.Offers[index];
            if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, [offer.Kind, 0], "fiendwar_card_choices", Cards,
                index == policy.Offers.Length - 1 ? () => context.RaidMovieCards = identity : null)) return false;
        }
        return true;
    }
}
