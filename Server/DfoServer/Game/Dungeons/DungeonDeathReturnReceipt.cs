namespace DfoServer.Game.Dungeons;

internal sealed record DungeonDeathReturnReceipt(ushort CharacterId, long Generation, long SelectionEpoch,
    Guid InstanceId, long DeathSerial);
