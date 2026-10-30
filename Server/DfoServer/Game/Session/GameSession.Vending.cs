using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Quests;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    public InventoryResult UseVendingMachine(InventoryVendingService service, VendingRequest request, out VendingReceipt? receipt)
    {
        VendingReceipt? planned = null;
        var result = PlanAndCommitInventory(character => service.Machine(request.Machine) is { } machine &&
            towns.Areas.TryGetValue((character.Position.Town, character.Position.Area), out var area) &&
            area.Map(QuestEligibility.Completed(character)) is { Error: null } map && map.Npcs.Any(npc => npc.Id == machine.NpcId)
                ? service.Open(character, request, out planned) : InventoryResult.Reject(1));
        receipt = result.Accepted ? planned : null;
        return result;
    }
}
