using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.GameWorld.Guilds;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    GuildHallWaitClock? guildHallWait;

    // 私有setter只失效本会话计时，不访问存储、网络或其他账号锁。
    void InvalidateGuildHallWait(Character? before, Character? after)
    {
        if (before?.Id != after?.Id || before?.Guild?.Id != after?.Guild?.Id ||
            before?.Position != after?.Position &&
            (before is null || after is null ||
             towns.IsGuildHall(before.Position, completed) != towns.IsGuildHall(after.Position, completed)))
            guildHallWait = null;
    }

    public GuildContributionCommit? TickGuildHallWait(IGuildHallWaitStore store, GuildHallWaitCatalog rules, TimeProvider time)
    {
        try
        {
            return owner.Execute(() =>
            {
                if (!Ready || Phase != SessionPhase.Town || Selected is not { Guild.Id: > 0 } character ||
                    !towns.IsGuildHall(character.Position, completed) || CaptureGuildManagement() is not { } actor)
                {
                    guildHallWait = null;
                    return null;
                }
                if (guildHallWait is null)
                {
                    var membership = store.ReadGuildHallWaitMembership(actor);
                    if (membership is null || membership.GuildId != character.Guild.Id) return null;
                    guildHallWait = new(membership, time);
                    return null;
                }
                var credit = guildHallWait.Prepare(rules);
                if (credit is null) return null;
                var result = store.CreditGuildHallWait(actor, credit, rules, time.GetUtcNow().ToUnixTimeSeconds());
                if (!result.MembershipCurrent) guildHallWait = null;
                else guildHallWait.Complete(credit, result, rules);
                return result.Contribution;
            });
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return null; }
    }
}
