using System.Collections.Immutable;
using DfoServer.Game.Inventory.Models;

namespace DfoServer.Game.Raids;

internal interface IRaidFailureRefundStore
{
    void CommitRaidFailureRefund(AntonRaidBattle.RaidFailureRefund candidate);
}

internal sealed partial class AntonRaidBattle
{
    sealed record RaidPayment(ushort Character, string Account, string Source, long Admitted, ImmutableArray<InventoryItem> Items);
    ImmutableArray<RaidPayment> raidPayments = [];
    RaidFailureRefund? raidFailureRefund;
    bool raidFailureRefundCommitted;

    // 仅冻结开战原扣费计划；只有同事务扣票成功、时钟激活后才可能生成失败能力。
    internal void PrepareRaidPayments(ImmutableArray<RaidStartReservation> reservations)
    {
        // 无生命周期的诊断开战没有正式失败结算能力。
        if (reservations.All(row => row.Lifecycle is null)) return;
        if (reservations.Any(row => row.Lifecycle != reservations[0].Lifecycle))
            throw new InvalidDataException("攻坚退票入场身份不一致");
        if (script.EntryCost is not { } cost || Kind == RaidKind.Fiendwar && script.RetryItem != cost)
            throw new InvalidDataException("攻坚退票缺少实际入场扣费声明");
        var payments = ImmutableArray.CreateBuilder<RaidPayment>(reservations.Length);
        foreach (var row in reservations)
        {
            var payment = FiendwarPaymentSnapshot.Capture(row, cost);
            payments.Add(new(row.Actor.Character, row.Actor.Owner.Account, row.Lifecycle!.Source, row.Lifecycle.At.ToUnixTimeSeconds(), payment.Items));
        }
        raidPayments = payments.ToImmutable();
    }

    internal sealed class RaidFailureRefund
    {
        internal sealed record Member(ushort Character, string Account, long Admitted, bool Refund, ImmutableArray<InventoryItem> Items);
        internal Guid Instance { get; }
        internal RaidKind Kind { get; }
        internal string Source { get; }
        internal DateTimeOffset At { get; }
        internal ImmutableArray<Member> Members { get; }
        private RaidFailureRefund(AntonRaidBattle battle)
        {
            Instance = battle.Instance;
            Kind = battle.Kind;
            Source = battle.raidPayments[0].Source;
            At = battle.lukeClock.GetUtcNow();
            // 主动离团/断线不在当前成员中，不由失败回调重新赋予资格。
            Members = battle.raidPayments.Select(row => new Member(row.Character, row.Account, row.Admitted,
                battle.members.Keys.Any(actor => actor.Character == row.Character && actor.Owner.Account == row.Account), row.Items)).ToImmutableArray();
        }
        internal static RaidFailureRefund? Capture(AntonRaidBattle battle) =>
            battle.current.PhaseExpired && battle.current.State == AntonRaidBattleState.Fighting &&
            battle.lukeClockActive && !battle.raidPayments.IsEmpty
                ? battle.raidFailureRefund ??= new(battle) : null;
    }

    internal RaidFailureRefund? PendingRaidFailureRefund()
    { lock (gate) return !current.ClockFaulted && !raidFailureRefundCommitted ? RaidFailureRefund.Capture(this) : null; }

    internal void AcceptRaidFailureRefund(RaidFailureRefund candidate)
    {
        lock (gate)
        {
            if (!ReferenceEquals(candidate, raidFailureRefund)) throw new InvalidOperationException("攻坚失败回执不属于原实例");
            raidFailureRefundCommitted = true;
        }
    }

    internal DateTimeOffset FiendwarRecoveryNow => lukeClock.GetUtcNow();
}
