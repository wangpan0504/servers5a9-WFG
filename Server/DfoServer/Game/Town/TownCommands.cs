namespace DfoServer.Game.Town;

internal readonly record struct PositionRequest(ushort X, ushort Y, byte Movement, ushort ScaledSpeed, byte Action, ushort ActionValue);
internal readonly record struct AreaRequest(byte Town, byte Area, ushort X, ushort Y, byte Movement, ushort PreviousTown, ushort PreviousArea, byte PathKind, uint TargetNpc = 0);
