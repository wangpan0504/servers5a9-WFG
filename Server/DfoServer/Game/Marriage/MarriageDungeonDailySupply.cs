using DfoServer.Game.Characters;
using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Marriage;

internal sealed record MarriageDungeonDailySupply(int ItemId, int Count)
{
    public static MarriageDungeonDailySupply? Create(DungeonDefinition definition) =>
        definition.IsWeddingCouple && definition.RequiredItemRows is [{ ItemId: > 0, Count: > 0, Third: 1 } ticket]
            ? new(ticket.ItemId, ticket.Count) : null;

    // 完婚即拥有0级婚戒，不能以RingLevel>0排除尚未升级的戒指。
    public bool Eligible(Character character) => !character.IsPvpPrivate &&
        character.Marriage is { State: 4 } record && record.IsMember(character.Id);
}
