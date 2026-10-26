using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed record RaidStartCheck(ushort Error, string Reason)
{
    public static RaidStartCheck Evaluate(bool isLeader, int currentMembers, RaidDefinition? definition)
    {
        if (definition is null) return new(19, "no-raid-definition");
        if (!isLeader) return new(8, "not-leader");
        if (currentMembers < definition.StartMinimum) return new(22, "too-few-members");
        return new(19, definition.Resources?.StartBlocker ?? "formal-raid-resources-unprepared");
    }
}
