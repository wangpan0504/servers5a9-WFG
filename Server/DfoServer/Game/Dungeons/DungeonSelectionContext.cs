using DfoServer.Game.Characters;
using DfoServer.GameWorld.Towns;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonSelectionContext(long Id, long SelectionEpoch, ushort CharacterId, Location OriginPosition, Location ReturnPosition)
{
    public DfoServer.Game.Raids.RaidDungeonSelection? Raid { get; init; }
    public bool IsTutorial { get; init; }
    public bool IsClassExperience { get; init; }
    public int? HellLegendDungeonId { get; init; }
}
internal sealed record DungeonSelectionReceipt(Character Character, long Generation, long SelectionEpoch, long SelectionId)
{
    public bool Tutorial { get; init; }
    public bool OpensClientSelector { get; init; }
}
