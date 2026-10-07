using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

internal sealed class SkillHandler(SkillLearningCatalog catalog, ServerLog log) : IGameCommandModule
{
    public void Register(GameCommandRegistry commands) => commands.Register(CmdPacketType.BUY_SKILL, PurchaseAsync, nameof(SkillHandler));

    private async Task<CommandResult> PurchaseAsync(GameConnectionContext context, Packet packet)
    {
        var parsed = SkillRequests.ParsePurchase(packet.Body);
        if (context.BlocksTownActions || parsed.Items.Any(item => item.Decrease > 1))
            return CommandResult.CloseConnection;
        var request = new SkillLearningCommand(parsed.Context, parsed.Mode, Array.AsReadOnly(parsed.Items
            .Select(item => new SkillLearningChangeRequest(item.SkillId, item.Decrease != 0, item.Magnitude)).ToArray()));
        var result = context.Session.LearnSkills(request, catalog);
        if (result.Success)
        {
            // 扣点、等级、绑定及revision已提交，ACK仅发布该事实，不以发送失败重跑。
            var receipt = result.Receipt ?? throw new InvalidOperationException("Missing skill commit receipt");
            await context.Reply(CmdPacketType.BUY_SKILL, SkillPackets.Success(parsed.Context, parsed.Mode, receipt.Character.Learning, result.Changes),
                "skill_purchase_committed", () => context.Session.CanPublishLearning(receipt));
            return CommandResult.Handled;
        }

        ushort? failure = result.Failure switch
        {
            SkillLearningFailure.MissingSkill => 0x0015,
            SkillLearningFailure.InsufficientSp => 0x0016,
            SkillLearningFailure.Mastered => 0x0012,
            _ => null
        };
        log.Write("skill_purchase_rejected", new { Connection = context.Id, Reason = result.Failure.ToString(), parsed.Context, parsed.Mode });
        if (failure is not ushort knownCode)
            return CommandResult.CloseConnection;
        await context.Reply(CmdPacketType.BUY_SKILL, SkillPackets.Failure(knownCode), "skill_purchase_rejected");
        return CommandResult.Handled;
    }
}
