using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal sealed record GuildDungeonClearScrollDefinition(ItemTemplate Item, uint DungeonId, int Increment)
{
    public const uint Action = 167;
    public static GuildDungeonClearScrollDefinition? Parse(StackableFile raw, ItemTemplate item)
    {
        var action = StackableActionCatalog.Arguments(raw);
        if (raw.TemplateId != item.Id || item.Kind != ItemKind.Consumable || item.UseForbidden ||
            action is not { Label: "[guild dungeon clear count]", Parameters: [> 0, _, > 0] } ||
            raw.DeclaredUseActionPacket != 1 || raw.TypeDeclaration is null ||
            raw.HasMinimumLevel && raw.DeclaredMinimumLevel is null || raw.HasMaximumLevel && raw.DeclaredMaximumLevel is null ||
            raw.HasUsableJobs && raw.DeclaredUsableJobs is not { Count: > 0 } ||
            !raw.HasActionUsablePlace || raw.DeclaredActionUsablePlaces is not { Count: > 0 } ||
            raw.DeclaredActionUsablePlaces.Any(place => place is not ("[village]" or "[seria room]"))) return null;
        return new(item, (uint)action.Parameters[0], action.Parameters[2]);
    }
}
