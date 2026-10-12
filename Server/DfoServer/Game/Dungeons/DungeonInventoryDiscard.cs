using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;

namespace DfoServer.Game.Dungeons;

internal sealed record DungeonInventoryDiscardOrigin(InventoryItem Source, uint Count, long ExpectedRevision);
internal sealed record DungeonInventoryDiscardPreparation(Character Expected, InventoryDiscardPlan Plan, DungeonGroundObject Ground);
internal sealed record DungeonInventoryDiscardResult(ushort Error, InventoryResult? Inventory = null, DungeonGroundObject? Ground = null);
