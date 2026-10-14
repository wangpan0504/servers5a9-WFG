namespace DfoServer.Game.Inventory.Models;

internal sealed record CubeContractTick(Guid CycleId, Guid InstanceId, ushort CharacterId, long SelectionEpoch, sbyte CubeType,
    int ItemId, int Count, int IntervalMilliseconds, long DueTimestamp);
