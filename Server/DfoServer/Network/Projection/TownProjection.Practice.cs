using DfoServer.Game.Dungeons;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed partial class TownProjection
{
    public Task PracticeEntryAsync(GameConnectionContext context, FormalDungeonReceipt receipt, bool beforeInformation)
    {
        if (receipt.Practice is not { } transition) return Task.CompletedTask;
        var character = context.Session.ReadSkillCharacter(receipt.Character);
        var now = timer.GetUtcNow();
        var body = beforeInformation
            ? TownPackets.PlayerBeforePracticeEntry(character, stats.Compute(character), context.Session.ReadAdventureLevel(), transition, context.Channel, now)
            : TownPackets.Player(character, stats.Compute(character), context.Session.ReadAdventureLevel(), context.Channel, now);
        return context.Notify(NotiPacketType.USERINFO, body,
            beforeInformation ? "practice_entry_before_information" : "practice_entry_loaded", () => context.Session.CanPublishPracticeEntry(receipt));
    }

    public Task PracticeDayAsync(GameConnectionContext context, PracticeDayReceipt receipt)
    {
        var character = context.Session.ReadSkillCharacter(receipt.Character);
        return context.Notify(NotiPacketType.USERINFO,
            TownPackets.Player(character, stats.Compute(character), context.Session.ReadAdventureLevel(), context.Channel, timer.GetUtcNow()),
            "practice_daily_reset", () => context.Session.CanPublishPracticeDay(receipt));
    }
}
