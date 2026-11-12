namespace DfoServer.Game.Characters;

internal sealed record FatigueBatteryFillCharacter(ushort Id, long Revision, CharacterFatigueState Fatigue);
internal sealed record FatigueBatteryFillResult(FatigueBatteryFillCharacter[] Characters, int Changed);
