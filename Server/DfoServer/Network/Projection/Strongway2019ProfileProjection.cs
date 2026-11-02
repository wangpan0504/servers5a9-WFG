using DfoServer.Game.Session;
using DfoServer.Game.Strongway2019;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class Strongway2019ProfileProjection(IStrongway2019StatisticsStore store, int startDelaySeconds,
    IStrongway2019RewardStore? rewards = null)
{
    internal Strongway2019StatisticsCommit? Commit(GameConnectionContext context, Strongway2019RewardPreparation expected) =>
        context.Session.CommitStrongway2019Statistics(expected, store);

    internal async Task InformationAsync(GameConnectionContext context, Func<bool>? canSend = null)
    {
        if (context.Session.ChannelEntry?.Type != 55) return;
        var selected = context.Session.Selected;
        long epoch = context.Session.SelectionEpoch;
        var state = context.Session.ReadStrongway2019Statistics(store);
        if (selected is null || state is null) return;
        var account = rewards is null ? null : context.Session.ReadStrongway2019AccountRewards(rewards);
        bool Current() => context.Owner.IsCurrent && context.Session.SelectionEpoch == epoch &&
            ReferenceEquals(context.Session.Selected, selected) && (canSend?.Invoke() ?? true);
        var body = Strongway2019ProfilePackets.CharacterData(state, startDelaySeconds);
        var userBody = account is { } snapshot ? Strongway2019ProfilePackets.UserData(snapshot.Challenge, snapshot.Matching) : null;
        if (context.Stop.IsCancellationRequested) return;
        try
        {
            await context.Notify(NotiPacketType.DPL_E_CHARAC_DATA, body, "strongway_character_history", Current);
            if (userBody is not null && Current() && !context.Stop.IsCancellationRequested)
                await context.Notify(NotiPacketType.DPL_E_USER_DATA, userBody, "strongway_account_rewards", Current);
        }
        // 成绩和奖励提交不在此隔离范围内；单连接发送失败不阻止原比赛其余成员结算。
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
}
