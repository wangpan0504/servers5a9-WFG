namespace DfoServer.Game.Dungeons;

internal sealed record RevivalBlessingTimeout(ushort CharacterId, long Generation, long SelectionEpoch,
    Guid InstanceId, long UntilMilliseconds);
