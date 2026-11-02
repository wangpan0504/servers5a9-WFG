using DfoServer.Game.Characters;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.BattlePass;

internal sealed record GuildHallEntryFact(Guid Id, DateTimeOffset PlannedAt, TimeProvider Clock,
    ushort CharacterId, uint GuildId, Location Origin, Location Target)
{
    public bool Matches(Character expected, Location target, uint guildId) => Id != Guid.Empty &&
        CharacterId == expected.Id && GuildId != 0 && GuildId == guildId && expected.Guild?.Id == guildId &&
        Origin == expected.Position && Target == target && (Origin.Town != Target.Town || Origin.Area != Target.Area);
}

internal static class BattlePassGuildHallEntrySource
{
    public static bool Matches(BattlePassMission definition, byte town) => definition is
        { Id: 10, RequiredCount: 1, Experience: 100, RawTypeCode: 10, RawParameter: 8 } && town == 8;
}
