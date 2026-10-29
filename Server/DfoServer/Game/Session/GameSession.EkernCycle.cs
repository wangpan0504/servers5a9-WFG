using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed record EkernCycleReceipt(long Generation, long Epoch, Character Character, EkernCycleProgress Progress);

internal sealed partial class GameSession
{
    public EkernCycleReceipt? CaptureEkernCycle(bool entranceOnly = false) => owner.Execute(() =>
    {
        if (Selected is not { } character || entranceOnly &&
            (!towns.Areas.TryGetValue((character.Position.Town,character.Position.Area),out var area) ||
             area.DungeonWorldMap is not { } world || !towns.DungeonMaps.TryGetValue(world,out var map) ||
             !map.Options.Any(option => option.DungeonId is 5097 or 5098))) return null;
        var progress = repository.LoadEkernCycle(owner,character.Id,ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow()));
        return new EkernCycleReceipt(owner.Generation,SelectionEpoch,character,progress);
    });

    public bool CanPublishEkernCycle(EkernCycleReceipt receipt) =>
        owner.IsCurrent && receipt.Generation == owner.Generation && receipt.Epoch == SelectionEpoch && ReferenceEquals(Selected,receipt.Character) &&
        receipt.Progress.Day >= ShopPurchaseCount.Today((timeProvider ?? TimeProvider.System).GetUtcNow());
}
