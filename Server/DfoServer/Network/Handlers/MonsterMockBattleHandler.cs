using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class MonsterMockBattleHandler(ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.MONSTER_MOCK_BATTLE,
        HandleAsync, nameof(MonsterMockBattleHandler), DfoServer.Game.Session.UserStateRequirement.WorldReady);

    async Task<CommandResult> HandleAsync(GameConnectionContext context, Packet packet)
    {
        if (packet.Kind != 1) return CommandResult.Handled;
        if (packet.Body.Length != 45 || context.MonsterMockBattle is null)
        {
            log.Write("monster_mock_battle_rejected", new { Connection = context.Id, Reason = "invalid_length_or_unavailable", packet.Body.Length });
            await context.Reply(CmdPacketType.MONSTER_MOCK_BATTLE,
                MonsterMockBattlePackets.Acknowledge(7, 10, [255, 255, 255, 255, 255], [0, 0, 0, 0, 0]),
                "monster_mock_battle_invalid", () => context.Owner.IsCurrent);
        }
        else await context.MonsterMockBattle.UseAsync(MonsterMockBattleRequests.Parse(packet.Body), packet);
        return CommandResult.Handled;
    }
}
