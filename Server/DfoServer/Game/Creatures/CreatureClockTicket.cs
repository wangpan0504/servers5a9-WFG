namespace DfoServer.Game.Creatures;

internal sealed record CreatureClockTicket(Guid CycleId, ushort CharacterId, long SelectionEpoch, long Uid, long DueTimestamp,
    long Units, bool Revive);
