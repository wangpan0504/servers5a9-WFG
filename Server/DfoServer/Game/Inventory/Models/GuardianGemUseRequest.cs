namespace DfoServer.Game.Inventory.Models;

internal sealed record GuardianGemUseRequest(int MedalItemId, short MaterialSlot, int GemItemId, byte Socket);
