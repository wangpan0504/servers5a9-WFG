using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

// 短时通知只保存原实例的收件代次，不改变兵线、机制符号或通关成果。
internal sealed record FiendwarSupportNotice(Guid Instance, long Sequence, byte Lane,
    DateTimeOffset Expires, ImmutableArray<RaidActor> Recipients);

internal sealed partial class AntonRaidBattle
{
    long? fiendwarSupportTimestamp;

    internal AntonRaidBattleChange? RequestFiendwarSupport(RaidActor source, RaidParty party, byte lane)
    {
        lock (gate)
        {
            if (lane > 2 || party is not { Type: 2, Guide: false, Started: { } started } ||
                started.InstanceId != Instance || party.Leader != source.Character || !source.IsCurrent ||
                !party.Members.Any(member => member.Actor == source) || !members.ContainsKey(source) ||
                !FiendwarSupportActive()) return null;
            long timestamp = lukeClock.GetTimestamp();
            if (fiendwarSupportTimestamp is { } previous &&
                lukeClock.GetElapsedTime(previous, timestamp) < TimeSpan.FromSeconds(3)) return null;
            var before = current;
            long sequence = checked(before.Revision + 1);
            var audience = members.Keys.Where(actor => actor.IsCurrent).ToImmutableArray();
            current = before with { Revision = sequence,
                FiendwarSupport = new(Instance, sequence, lane, lukeClock.GetUtcNow().AddSeconds(3), audience) };
            fiendwarSupportTimestamp = timestamp;
            return new(before, current);
        }
    }

    internal bool CanPublishFiendwarSupport(RaidActor recipient, FiendwarSupportNotice notice)
    {
        lock (gate) return recipient.IsCurrent && notice.Instance == Instance &&
            ReferenceEquals(current.FiendwarSupport, notice) && FiendwarSupportActive() &&
            lukeClock.GetUtcNow() < notice.Expires && members.ContainsKey(recipient) && notice.Recipients.Contains(recipient);
    }

    bool FiendwarSupportActive() => Kind == RaidKind.Fiendwar && fiendwarDefinition is { Guide: false } &&
        lukeClockActive && pendingTransition is null && !TerminalRequested(current) && !current.ClockFaulted &&
        current.State == AntonRaidBattleState.Fighting && current.Fiendwar is { Failed: false, Cleared: false } field &&
        lukeClock.GetUtcNow() < field.Deadline;
}
