using System.Collections.Concurrent;

namespace DfoServer.Game.ExpertJob;

internal sealed record ExpertJobStoreRecord(ushort OwnerId, byte JobType, byte Mode, string Name,
    uint First, ushort Second, ushort Third, ushort Fourth, byte Grade, uint Endurance)
{
    public Guid Instance { get; init; } = Guid.NewGuid();
}

internal sealed class ExpertJobStoreBoard
{
    readonly ConcurrentDictionary<ushort, ExpertJobStoreRecord> stores = new();

    public void Open(ExpertJobStoreRecord store) => stores[store.OwnerId] = store;

    public bool TryClose(ushort owner, out ExpertJobStoreRecord store) => stores.TryRemove(owner, out store!);

    public bool TryGet(ushort owner, out ExpertJobStoreRecord store) => stores.TryGetValue(owner, out store!);
}
