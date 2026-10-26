using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal sealed record NpcFavorPoint(int NpcId, int Points)
{
    public static void Validate(ImmutableArray<NpcFavorPoint> rows)
    {
        if (rows.IsDefault || rows.Length > byte.MaxValue || rows.Any(row => row.NpcId <= 0 || row.Points <= 0) ||
            rows.Select(row => row.NpcId).Distinct().Count() != rows.Length)
            throw new InvalidDataException("角色NPC好感度快照无效");
    }
}
