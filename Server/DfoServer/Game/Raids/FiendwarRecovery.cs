using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 仅保存已实际支付的原票；不声称能由这份快照重建兵线、随机或战斗实例。
internal sealed record FiendwarPaymentSnapshot(ImmutableArray<InventoryItem> Items)
{
    internal static FiendwarPaymentSnapshot Capture(RaidStartReservation row, RaidCost cost)
    {
        var items = ImmutableArray.CreateBuilder<InventoryItem>();
        foreach (var item in row.Inventory.Before.Items.Where(item => item.Core.ItemId == cost.ItemId))
        {
            int remaining = row.Inventory.After.Items.SingleOrDefault(after => after.Uid == item.Uid)?.Core.Count ?? 0;
            int removed = item.Core.Count - remaining;
            if (removed > 0) items.Add(item with { Uid = 0, Core = item.Core with { Value = removed } });
        }
        var result = new FiendwarPaymentSnapshot(items.ToImmutable());
        result.Validate();
        if (row.Lifecycle is null || result.Items.Sum(item => item.Core.Count) != cost.Count)
            throw new InvalidDataException("漩涡退票缺少完整实际扣费计划");
        return result;
    }

    internal void Validate()
    {
        if (Items.IsDefaultOrEmpty || Items.Length > 10)
            throw new InvalidDataException("漩涡原票快照附件数量不合法");
        foreach (var item in Items)
        {
            item.Core.Validate();
            item.ValidateDetails(allocating: true);
            if (item.Uid != 0 || item.Core.Count <= 0)
                throw new InvalidDataException("漩涡原票快照不是实际扣费数量");
        }
    }
}

internal interface IFiendwarRecoveryStore
{
    void RecordFiendwarDeparture(RaidActor actor, Guid instance, DateTimeOffset? rejoinUntil);
    bool ChangeFiendwarRejoin(RaidActor original, RaidActor replacement, Guid instance, DateTimeOffset now, bool undo = false);
}

internal sealed record FiendwarInterruptionResult(int Refunded, int Abandoned, int MissingPayment);
