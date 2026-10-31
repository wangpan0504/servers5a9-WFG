using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Raids;

internal interface IFiendwarFailureStore
{
    void CommitFiendwarFailure(AntonRaidBattle.FiendwarFailure candidate);
}

internal sealed partial class AntonRaidBattle
{
    sealed record FiendwarPayment(ushort Character, string Account, long Admitted, ImmutableArray<InventoryItem> Items);
    ImmutableArray<FiendwarPayment> fiendwarPayments = [];
    FiendwarFailure? fiendwarFailure;
    bool fiendwarFailureCommitted;

    // 仅冻结开战原扣费计划；只有同事务扣票成功、时钟激活后才可能生成失败能力。
    internal void PrepareFiendwarPayments(ImmutableArray<RaidStartReservation> reservations)
    {
        if (Kind != RaidKind.Fiendwar) return;
        if (script.EntryCost is not { } cost || script.RetryItem != cost)
            throw new InvalidDataException("漩涡重试票与实际入场扣费不一致");
        var payments = ImmutableArray.CreateBuilder<FiendwarPayment>(reservations.Length);
        foreach (var row in reservations)
        {
            var items = ImmutableArray.CreateBuilder<InventoryItem>();
            foreach (var item in row.Inventory.Before.Items.Where(item => item.Core.ItemId == cost.ItemId))
            {
                int remaining = row.Inventory.After.Items.SingleOrDefault(after => after.Uid == item.Uid)?.Core.Count ?? 0;
                int removed = item.Core.Count - remaining;
                if (removed > 0) items.Add(item with { Uid = 0, Core = item.Core with { Value = removed } });
            }
            if (row.Lifecycle is not { } lifecycle || items.Sum(item => item.Core.Count) != cost.Count || items.Count > 10)
                throw new InvalidDataException("漩涡退票缺少完整实际扣费计划");
            payments.Add(new(row.Actor.Character, row.Actor.Owner.Account, lifecycle.At.ToUnixTimeSeconds(), items.ToImmutable()));
        }
        fiendwarPayments = payments.ToImmutable();
    }

    internal sealed class FiendwarFailure
    {
        internal sealed record Member(ushort Character, string Account, long Admitted, bool Refund, ImmutableArray<InventoryItem> Items);
        internal Guid Instance { get; }
        internal string Source { get; }
        internal DateTimeOffset At { get; }
        internal ImmutableArray<Member> Members { get; }
        private FiendwarFailure(AntonRaidBattle battle)
        {
            Instance = battle.Instance;
            Source = battle.lukeCompletion!.Source;
            At = battle.lukeClock.GetUtcNow();
            // 主动离团/断线不在当前成员中，不由失败回调重新赋予资格。
            Members = battle.fiendwarPayments.Select(row => new Member(row.Character, row.Account, row.Admitted,
                battle.members.Keys.Any(actor => actor.Character == row.Character && actor.Owner.Account == row.Account), row.Items)).ToImmutableArray();
        }
        internal static FiendwarFailure? Capture(AntonRaidBattle battle) =>
            battle.Kind == RaidKind.Fiendwar && battle.current.Fiendwar is { Failed: true, Cleared: false } &&
            battle.lukeClockActive && battle.lukeCompletion is not null && !battle.fiendwarPayments.IsEmpty
                ? battle.fiendwarFailure ??= new(battle) : null;
    }

    internal FiendwarFailure? PendingFiendwarFailure()
    { lock (gate) return !current.ClockFaulted && !fiendwarFailureCommitted ? FiendwarFailure.Capture(this) : null; }

    internal void AcceptFiendwarFailure(FiendwarFailure candidate)
    {
        lock (gate)
        {
            if (!ReferenceEquals(candidate, fiendwarFailure)) throw new InvalidOperationException("漩涡失败回执不属于原实例");
            fiendwarFailureCommitted = true;
        }
    }
}
