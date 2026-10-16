using System.Collections.Immutable;

namespace DfoServer.Game.Characters;

// 记录同次名册的只读投影身份；收到索引请求时不能改用新查询的行序。
internal sealed record CharacterRosterSnapshot(long Generation, long SelectionEpoch, ImmutableArray<CharacterRosterEntry> Entries)
{
    public ushort Capacity { get; init; } = 32;
}
