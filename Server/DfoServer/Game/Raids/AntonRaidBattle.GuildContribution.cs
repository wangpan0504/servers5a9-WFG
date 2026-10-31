using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Dictionary<ushort, ImmutableArray<ushort>> guildClearParties = [];

    // 只从已提交的真实清图事实记录；人数按最后一次实际小队，不把整团截成四人。
    void RecordGuildClearParty(IEnumerable<ushort> characters)
    {
        var party = characters.Order().ToImmutableArray();
        if (party.Length is < 1 or > 4 || party.Distinct().Count() != party.Length)
            throw new InvalidDataException("攻坚贡献清图队伍无效");
        foreach (ushort character in party) guildClearParties[character] = party;
    }
}
