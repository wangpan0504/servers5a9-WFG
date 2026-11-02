using DfoServer.Game.Events;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Session;

internal sealed partial class GameSession
{
    internal bool Strongway2019QuestActive => serverEvents?.Read(ServerEventService.Strongway2019Id).Active == true;

    ServerEventSchedule? CaptureStrongway2019QuestSchedule() =>
        serverEvents?.Read(ServerEventService.Strongway2019Id).Schedule;

    bool IsCurrentStrongway2019QuestSchedule(ServerEventSchedule? schedule) =>
        schedule is not null && serverEvents is not null &&
        new InventoryEventPrecondition(schedule, timeProvider ?? TimeProvider.System)
            .Matches(serverEvents.Read(ServerEventService.Strongway2019Id).Schedule);
}
