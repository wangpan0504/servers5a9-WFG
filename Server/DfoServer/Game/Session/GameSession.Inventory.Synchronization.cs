using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryCommitReceipt? ReadInventoryForSynchronization() => owner.Execute(() =>
        Ready && Selected is { } character ? InventoryReceipt(character) : null);
}
