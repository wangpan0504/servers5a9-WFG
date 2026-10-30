using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record MonsterCardUpgradeRequest(short TargetSlot, ImmutableArray<short> MaterialSlots);
internal sealed record MonsterCardUpgradeReceipt(bool Upgraded, int ItemId, byte Level);
