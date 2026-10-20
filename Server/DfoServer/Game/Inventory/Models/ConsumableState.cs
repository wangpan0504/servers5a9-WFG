using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal enum ConsumableStateKind : byte { Effect, Cooldown }
[Flags]
internal enum ConsumableMaintenance : byte { None = 0, Death = 1, Town = 2, Reselect = 4 }

internal sealed record ConsumableState(ConsumableStateKind Kind, int ItemId, long UntilMilliseconds,
    byte Maintenance, int CoolGroup)
{
    public bool Active(long now) => UntilMilliseconds > now;
    public bool Preserves(ConsumableMaintenance boundary) => (Maintenance & (byte)boundary) != 0;

    public static void Validate(ImmutableArray<ConsumableState> states)
    {
        if (states.IsDefault || states.Any(row => row.Kind is not (ConsumableStateKind.Effect or ConsumableStateKind.Cooldown) ||
            row.ItemId <= 0 || row.UntilMilliseconds <= 0 || row.Maintenance > (row.Kind == ConsumableStateKind.Effect ? 15 : 7)) ||
            states.Select(row => (row.Kind, row.ItemId)).Distinct().Count() != states.Length ||
            states.GroupBy(row => row.Kind).Any(group => group.Count() > byte.MaxValue))
            throw new InvalidDataException("消耗品持续状态无效");
    }
}
