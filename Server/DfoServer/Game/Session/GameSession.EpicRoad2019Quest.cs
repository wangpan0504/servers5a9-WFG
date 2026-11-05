using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool EpicRoad2019QuestActive => serverEvents?.Read(ServerEventService.EpicRoadId).Active == true;

    ServerEventSchedule? CaptureEpicRoad2019QuestSchedule() =>
        serverEvents?.Read(ServerEventService.EpicRoadId).Schedule;

    bool IsCurrentEpicRoad2019QuestSchedule(ServerEventSchedule? schedule) =>
        schedule is not null && serverEvents is not null &&
        new InventoryEventPrecondition(schedule, timeProvider ?? TimeProvider.System)
            .Matches(serverEvents.Read(ServerEventService.EpicRoadId).Schedule);
}
