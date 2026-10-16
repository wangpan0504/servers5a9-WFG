using System.Buffers.Binary;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// #233 CMD1547 SELECT_ASSAULT_MODE_DUNGEON：正文 u32 主副本 id（实机 841C0000=7300）。
// 校验今日开放、等级、前置任务6119、材料后回 ACK1547 成功(任务副本 id)，客户端随后自行发 C2S15/C2S16；
// 材料在 C2S16 入场提交时扣除（GameSession.InstallFormalDungeon），此处只做预检与授权。
internal sealed class AssaultModeHandler(ServerLog log, AssaultModeCatalog? catalog = null, EpicPieceCatalog? pieces = null,
    TimeProvider? clock = null, ItemCatalog? items = null, GoldLimitCatalog? goldLimits = null) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) =>
        commands.Register(CmdPacketType.SELECT_ASSAULT_MODE_DUNGEON, SelectAsync, nameof(AssaultModeHandler));

    async Task<CommandResult> SelectAsync(GameConnectionContext context, Packet request)
    {
        if (request.Body.Length != 4)
        {
            log.Write("assault_mode_select_request", new { Connection = context.Id, Length = request.Body.Length,
                Body = Convert.ToHexString(request.Body), Accepted = false, Reason = "body-length-unconfirmed" });
            return CommandResult.Unhandled;
        }
        uint parent = BinaryPrimitives.ReadUInt32LittleEndian(request.Body);
        if (catalog is null || pieces is null)
        {
            log.Write("assault_mode_select_request", new { Connection = context.Id, Parent = parent, Accepted = false, Reason = "catalog-unavailable" });
            return CommandResult.Unhandled;
        }
        var localNow = (clock ?? TimeProvider.System).GetLocalNow();
        byte day = AssaultModePackets.DayIndex(localNow, Environment.GetEnvironmentVariable(AssaultModePackets.DayIndexOverrideVariable));
        var rewards = items is not null && goldLimits is not null ? new Game.Session.AssaultModeRewardServices(catalog, items, goldLimits, pieces) : null;
        var decision = context.Session.AuthorizeAssaultMode(parent, catalog, pieces, day, null, AssaultModeEntryPolicy.DayKey(localNow), rewards);
        log.Write("assault_mode_select_request", new { Connection = context.Id, Parent = parent, Day = day,
            decision.Accepted, Reason = decision.Reason.ToString(), decision.Detail, Mission = decision.Selection?.Mission });
        if (decision.Selection is { } selection)
            await context.Reply(CmdPacketType.SELECT_ASSAULT_MODE_DUNGEON, AssaultModePackets.SelectAccepted(selection.Mission),
                "assault_mode_select_accepted");
        else
            await context.Reply(CmdPacketType.SELECT_ASSAULT_MODE_DUNGEON, AssaultModePackets.SelectRejected(),
                "assault_mode_select_rejected");
        return CommandResult.Handled;
    }
}
