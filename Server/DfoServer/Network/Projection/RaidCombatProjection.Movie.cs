using DfoServer.Game.Raids;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static partial class RaidCombatProjection
{
    internal static bool Movie(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view, Guid movieEvent, bool completed)
    {
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Guard() => context.Session.CanPublishRaidMovie(actor, battle, view.Phase, movieEvent) && context.RaidDisplay == identity;
        // 最后一名无资格成员确认可同步进入等待；已接受的659仍需ACK，但不能投到新actor或新阶段。
        bool Acknowledge() => actor.IsCurrent && context.Session.CurrentRaidBattle(actor) == battle &&
            battle.View.Phase == view.Phase && context.RaidDisplay == identity;
        var confirmation = context.Session.ConfirmRaidMovie(actor, battle, view, movieEvent, completed,
            () => context.RaidDisplay == identity,
            () => context.Sender.TryPost(1, (ushort)CmdPacketType.RAID_MOVIE_SKIP, [1], "raid_movie_ack", Acknowledge));
        if (confirmation is null) return true;
        view = confirmation.View;
        // 完成事实与ACK已在同一owner/Battle边界接受，广播始终在锁外。
        if (confirmation.Changed) battle.Publish(battle.View);
        if (!confirmation.Acknowledged) return false;
        if (!completed)
        {
            if (battle.HasAntonRewards && view.AntonCardsReady) return true;
            // 01C63430发送0仅请求跳过；599通知原电影控件，不能提前领取或结束阶段。
            return context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_MOVIE_SKIP, [], "raid_movie_skip_prompt",
                () => Guard() && context.RaidMoviePrompt != identity && context.RaidMovieCards != identity,
                () => context.RaidMoviePrompt = identity);
        }
        if (battle.HasAntonRewards || battle.Kind == RaidKind.Fiendwar) return true;
        if (battle.Kind == RaidKind.Prey)
        {
            return PostPreyResults(context, actor, battle, view);
        }
        if (view.Phase == 0 || battle.LukeEligibility(actor.Character, view.Phase) is { Eligible: false })
            return true;
        return PostLukeMovieCards(context, actor, battle, view, movieEvent);
    }

    static bool PostPreyResults(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view)
    {
        if (!battle.PreyMovieCardsReady(actor, view) ||
            battle.LukeEligibility(actor.Character, view.Phase) is not { Eligible: true } ||
            battle.RewardPolicy(actor, view.Phase) is not { Offers.IsEmpty: false } policy ||
            battle.PreyTeamCommittedRewards(actor, view) is not { } team) return true;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view) with { State = AntonRaidBattleState.Reward };
        bool Current()
        {
            var latest = battle.View;
            return context.Session.CanPublishRaidBattle(actor, latest) && context.RaidDisplay == identity &&
                latest.Phase == view.Phase && battle.PreyMovieCardsReady(actor, latest) && context.RaidMovieCards != identity;
        }
        byte Written() => context.PreyResultDisplay is { } display && display.Identity == identity ? display.Mask : (byte)0;
        void Record(byte bit) => context.PreyResultDisplay = (identity, (byte)(Written() | bit));
        // 原生0x10C只揭示结果，不发送660；全部实际个人数据和完整kind3表必须先于开窗。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST,
            PreyRewardPackets.Personal(actor.Character, policy.Offers), "prey_result_personal",
            () => Current() && (Written() & 1) == 0, () => Record(1))) return false;
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST, PreyRewardPackets.Team(team),
            "prey_result_team", () => Current() && (Written() & 4) == 0, () => Record(4))) return false;
        // type3的0x10C只在显示阶段1开启；不能为开窗改写引导的真实阶段及原资格。
        byte displayPhase = battle.PreyGuide ? (byte)1 : checked((byte)view.Phase);
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_STATE, [4, displayPhase], "prey_result_open",
            () => Current() && (Written() & 5) == 5, () => context.RaidMovieCards = identity)) return false;
        return true;
    }

    // 引导P1领取后会立即进入真实P2；须在原阶段仍有效时写完已提交结果，不能等新阶段广播补旧牌面。
    internal static async Task PublishPreyGuideResultsAsync(GameConnectionContext context, RaidRewardReceipt receipt)
    {
        if (!context.Session.CanPublishRaidReward(receipt) ||
            context.Session.RaidStatusSnapshot() is not { Battle.Kind: RaidKind.Prey, Battle.PreyGuide: true } snapshot ||
            snapshot.View.State != AntonRaidBattleState.Reward || snapshot.View.Phase != receipt.Prepared.Phase ||
            snapshot.View.Instance != receipt.Prepared.Instance) return;
        if (!PostPreyResults(context, snapshot.Actor, snapshot.Battle, snapshot.View))
            throw new IOException("Prey guide result queue unavailable");
        await context.Sender.DrainAsync();
    }

    static bool PostLukeMovieCards(GameConnectionContext context, RaidActor actor, AntonRaidBattle battle,
        AntonRaidBattleView view, Guid? acceptedMovieEvent = null)
    {
        if (battle.CaptureLukeMovieCards(actor, view.Phase, acceptedMovieEvent) is not { } snapshot ||
            snapshot.View.Instance != view.Instance || view.Phase != 0 && snapshot.Team.IsDefaultOrEmpty) return true;
        view = snapshot.View;
        var identity = RaidCombatDisplayIdentity.Capture(actor, view);
        bool Cards() => context.Session.CanPublishRaidMovie(actor, battle, view.Phase, snapshot.Event) && context.RaidDisplay == identity &&
            battle.LukeMovieCardsReady(actor, view.Phase, snapshot.Event) && context.RaidMovieCards != identity &&
            (view.Phase == 0 || context.LukeResults.MovieTeamWritten(identity));
        context.LukeResults.PostTeam(context, actor, battle, view, movie: snapshot);
        // 02396DE0 state4→036D2770→01C99AA0显示翻牌组合对象；第二字节写入真实阶段。
        // 这是演出显示推进，Battle仍在Reward，只有实际资产提交确认才能转阶段。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_STATE, [4, checked((byte)view.Phase)], "raid_movie_cards", Cards)) return false;
        var offers = snapshot.Policy.Offers;
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
        var team = battle.FiendwarTeamEntitlements(view);
        if (team.IsDefaultOrEmpty) return true;
        // 原生第二页按分类2枚举完整owner表，须在state4装配子页之前准备。
        if (!context.Sender.TryPost(0, (ushort)NotiPacketType.RAID_REWARD_LIST,
            PreyRewardPackets.FiendwarTeamResults(team), "fiendwar_team_reward_entitlements", Cards)) return false;
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
