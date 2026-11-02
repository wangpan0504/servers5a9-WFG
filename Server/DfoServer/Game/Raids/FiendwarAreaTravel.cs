using DfoServer.Game.Characters;
using DfoServer.Game.Town;

namespace DfoServer.Game.Raids;

internal sealed record FiendwarAreaEncounter(RaidParty Raid, RaidParty Members, AntonRaidBattle Battle,
    int Source, int Dungeon, AreaRequest Request);
internal sealed record FiendwarAreaTravelReservation(RaidActor Actor, Character Expected, TownTravelPlan Plan,
    long Generation, long Epoch, Func<TownTravelReceipt, Task<bool>> Publish);
internal sealed record FiendwarAreaTravelReceipt(RaidActor Actor, TownTravelReceipt Travel,
    Func<TownTravelReceipt, Task<bool>> Publish);

// 同一次原生36确认的全员位置只允许在一个事务中保存。
internal interface IFiendwarAreaTravelStore
{
    Character[] CommitFiendwarAreaTravel(IReadOnlyList<FiendwarAreaTravelReservation> reservations,
        Func<bool> current);
}
