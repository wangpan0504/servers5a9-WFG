using DfoServer.GameWorld.Dungeons;

namespace DfoServer.Game.Dungeons;

internal sealed record SingleDungeonAdmission(SingleDungeonGroup Group, int Week, TimeProvider Clock)
{
    internal static SingleDungeonAdmission At(SingleDungeonGroup group, TimeProvider clock) =>
        new(group, DungeonIncountPolicy.Period(clock.GetUtcNow(), group.ResetDay).Week, clock);
}

internal sealed class SingleDungeonAdmissionRejectedException() : InvalidOperationException("single-dungeon-account-bind-limit");
