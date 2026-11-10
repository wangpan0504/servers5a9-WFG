using System.Collections.Immutable;

namespace DfoServer.Game.Inventory.Models;

internal enum VariousBuffLifetime : byte { Timed = 0, DungeonEntries = 1, EffectApplications = 2 }

// 只保存已授予的身份与动态生命周期，不保存PVF效果副本，也不借物品冷却字段编码抽选结果。
internal sealed record VariousBuffState(int BuffId, int ItemId, VariousBuffLifetime Lifetime,
    long UntilMilliseconds, int Remaining, long DailyUntilMilliseconds = 0)
{
    public long GrantedMilliseconds { get; init; }
    public int Stacks { get; init; } = 1;
    public bool Active(long now) => (UntilMilliseconds == 0 || UntilMilliseconds > now) &&
        (DailyUntilMilliseconds == 0 || DailyUntilMilliseconds > now) && (Lifetime == VariousBuffLifetime.Timed || Remaining > 0);

    public static void Validate(ImmutableArray<VariousBuffState> rows)
    {
        if (rows.IsDefault || rows.Select(row => row.BuffId).Distinct().Count() != rows.Length || rows.Any(row =>
            row.BuffId <= 0 || row.ItemId <= 0 || row.Lifetime is < VariousBuffLifetime.Timed or > VariousBuffLifetime.EffectApplications ||
            row.UntilMilliseconds < 0 || row.DailyUntilMilliseconds < 0 || row.Remaining < 0 || row.GrantedMilliseconds < 0 || row.Stacks <= 0 ||
            row.Lifetime != VariousBuffLifetime.Timed && row.Remaining == 0))
            throw new InvalidDataException("通用Buff动态状态无效");
    }
}
