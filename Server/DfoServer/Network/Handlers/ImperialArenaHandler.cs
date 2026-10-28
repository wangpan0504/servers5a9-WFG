using DfoServer.Infrastructure;
using DfoServer.Game.Inventory.Models;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Parsers;
using DfoServer.Network.Projection;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class ImperialArenaHandler(ImperialArenaProjection projection, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.UPDATE_TAG_TOURNAMENT_CHARACTER, ConfigureAsync, nameof(ImperialArenaHandler));
        commands.Register(CmdPacketType.TAG_TOURNAMENT_ENTERABLE, PurchaseAsync, nameof(ImperialArenaHandler));
        commands.Register(CmdPacketType.TAG_TOURNAMENT_REQUEST_TIME_ATTACK_REWARD, ChallengeAsync, nameof(ImperialArenaHandler));
        commands.Register(CmdPacketType.TAG_TOURNAMENT_REQUEST_BEFORE_SEASON_RANK, PreviousRankAsync, nameof(ImperialArenaHandler));
        commands.Register(CmdPacketType.TAG_TOURNAMENT_REQUEST_BEFORE_SEASON_REWARD, PreviousRewardAsync, nameof(ImperialArenaHandler));
    }

    async Task<CommandResult> ChallengeAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) throw new InvalidDataException("帝国每周领奖请求必须为空");
        Game.ImperialArena.ImperialArenaChallengeCommit? commit = null;
        try { if (!context.BlocksTownActions) commit = projection.ClaimChallenge(context); }
        catch (InvalidOperationException error)
        { log.Write("imperial_arena_challenge_rejected", new { Connection = context.Id, Reason = error.Message }); }
        if (commit is null)
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_REQUEST_TIME_ATTACK_REWARD, [0, 19, 0], "imperial_arena_challenge_rejected");
        else
        {
            bool Guard() => context.Session.CanPublishImperialArenaChallenge(commit.Claim);
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_REQUEST_TIME_ATTACK_REWARD, [1], "imperial_arena_challenge_committed", Guard);
            await context.Notify(NotiPacketType.TAG_TOURNAMENT_HARD_MODE_INFO, ImperialArenaPackets.Challenge(projection.ReadChallenge(context)),
                "imperial_arena_challenge_claimed", Guard);
            await context.Notify(NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(), "imperial_arena_challenge_mail", Guard);
            log.Write("imperial_arena_challenge_committed", new { Connection = context.Id, commit.MailId, commit.Duplicate, commit.Claim.Challenge.Period.Week });
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> PreviousRankAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) throw new InvalidDataException("帝国上周排名请求必须为空");
        if (context.BlocksTownActions || context.Session.ReadImperialArena(projection.Store, projection.Now) is not { } snapshot)
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_REQUEST_BEFORE_SEASON_RANK, [0, 19, 0], "imperial_arena_rank_rejected");
        else
        {
            uint rank = projection.ReadChallenge(context).PreviousRank;
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_REQUEST_BEFORE_SEASON_RANK, ImperialArenaPackets.PreviousRank(rank), "imperial_arena_previous_rank",
                () => context.Session.CanPublishImperialArena(snapshot));
        }
        return CommandResult.Handled;
    }

    async Task<CommandResult> PreviousRewardAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Body.Length != 0) throw new InvalidDataException("帝国旧版排行领奖请求必须为空");
        // 本版没有[tag tournament ranking reward]表；荣誉奖励由原生每周挑战1618领取。
        await context.Reply(CmdPacketType.TAG_TOURNAMENT_REQUEST_BEFORE_SEASON_REWARD, [0, 19, 0], "imperial_arena_legacy_reward_unavailable");
        return CommandResult.Handled;
    }

    async Task<CommandResult> ConfigureAsync(GameConnectionContext context, Packet packet)
    {
        var request = ImperialArenaRequests.Configuration(packet.Body);
        var result = context.BlocksTownActions ? null : context.Session.ConfigureImperialArena(projection.Store,
            request.FirstServer, request.FirstIndex, request.SecondServer, request.SecondIndex, projection.Now);
        log.Write(result is null ? "imperial_arena_configuration_rejected" : "imperial_arena_configuration_committed",
            new { Connection = context.Id, Revision = result?.State.Configuration.Revision });
        if (result is null)
            await context.Reply(CmdPacketType.UPDATE_TAG_TOURNAMENT_CHARACTER, [0, 19, 0], "imperial_arena_configuration_rejected");
        else
            // 01779760成功分支读取两组server/index；由原生客户端决定重选第一名角色。
            await context.Reply(CmdPacketType.UPDATE_TAG_TOURNAMENT_CHARACTER, ImperialArenaPackets.Configured(result),
                "imperial_arena_configuration_committed", () => context.Session.CanPublishImperialArena(result));
        return CommandResult.Handled;
    }

    async Task<CommandResult> PurchaseAsync(GameConnectionContext context, Packet packet)
    {
        var mode = ImperialArenaRequests.Purchase(packet.Body);
        Game.ImperialArena.ImperialArenaProjection? result = null;
        try
        {
            // 购券要求已发布的双Actor仍然有效；主角修理/金币变化后先就地重建一次，不再要求重登。
            if (!context.BlocksTownActions) await projection.RefreshPairAsync(context);
            if (!context.BlocksTownActions) result = context.Session.PurchaseImperialArena(projection.Store, projection.Catalog, mode, projection.Now);
        }
        catch (InvalidOperationException error)
        { log.Write("imperial_arena_purchase_rejected", new { Connection = context.Id, Reason = error.Message }); }
        if (result is null)
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_ENTERABLE, [0, 19, 0], "imperial_arena_purchase_rejected");
        else
        {
            bool Guard() => context.Session.CanPublishImperialArena(result);
            var primary = context.Owner.Execute(() => context.Session.Selected!);
            await context.Notify(NotiPacketType.UPDATE_ITEM_LIST, InventoryPackets.Delta(primary.Inventory, InventoryListType.Main,
                [new(InventoryListType.Main, 0)]), "imperial_arena_entry_gold", Guard);
            await projection.DeliverAsync(context, result);
            // 1461成功会立即发15/16，须在ACK之前安装更新后的双Actor与本日券状态。
            await context.Reply(CmdPacketType.TAG_TOURNAMENT_ENTERABLE, [1], "imperial_arena_purchase_committed", Guard);
        }
        return CommandResult.Handled;
    }
}
