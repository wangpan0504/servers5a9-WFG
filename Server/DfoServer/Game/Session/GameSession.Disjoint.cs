using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Session;

internal sealed record DisjointRejectedSource(Character Character, long Generation, long SelectionEpoch,
    SessionPhase Phase, InventoryItem Source);

internal sealed partial class GameSession
{
    public DisjointRejectedSource? CaptureDisjointRejectedSource(DisjointRequest request) => owner.Execute(() =>
        Ready && Selected is { } character && request.Position.List == InventoryListType.Main &&
        character.Inventory.Find(request.Position) is { Core.Kind: ItemKind.Equipment } source
            ? new DisjointRejectedSource(character, owner.Generation, SelectionEpoch, Phase, source) : null);

    public bool CanPublishDisjointRejectedSource(DisjointRejectedSource captured)
    {
        if (!owner.IsCurrent) return false;
        try
        {
            return owner.Execute(() => Ready && owner.Generation == captured.Generation &&
                SelectionEpoch == captured.SelectionEpoch && Phase == captured.Phase &&
                ReferenceEquals(Selected, captured.Character) && Selected.Revision == captured.Character.Revision &&
                Selected.Inventory.Find(captured.Source.Position) == captured.Source);
        }
        catch (InvalidOperationException) when (!owner.IsCurrent) { return false; }
    }
}
