using DfoServer.Game.Accounts;
using DfoServer.GameWorld.Channels;

namespace DfoServer.Game.Raids;

// 断线记录只保存原团身份，不复制角色资产，也不代替未证明的正式恢复包序。
internal sealed record AntonRaidDisconnectFact(
    string Account, ushort Character, long Epoch, AccountOwners.Lease OldOwner, uint Key, Guid Instance,
    ChannelIdentity Channel, byte Slot, byte Squad, Guid? Run, bool WasLoading)
{
    public bool Attempted { get; set; }
    internal LukeRaidRejoinReservation? Luke { get; init; }
    internal AntonRaidRejoinReservation? Anton { get; init; }
}

internal sealed record AntonRaidRejoinDecision(
    bool Matched, string Reason, uint? Key, long? OldEpoch, long? NewEpoch, byte? Slot, byte? Squad,
    bool OldSessionReleased, string OwnerReplace, string LoadingBarrier, string Unknown)
{
    public static AntonRaidRejoinDecision Reject(string reason, uint? key = null) =>
        new(false, reason, key, null, null, null, null, false, "not-applied", "not-applied", "unknown");
}

// 进程内保留原 Battle 仍存活时的断线身份。没有证明保留时长，不发明计时器。
internal sealed class AntonRaidRejoin(bool enabled, bool lukeCandidatesEnabled = false)
{
    readonly object gate = new();
    readonly Dictionary<ushort, AntonRaidDisconnectFact> byCharacter = [];
    public bool Enabled { get; } = enabled;
    internal bool LukeCandidatesEnabled { get; } = enabled || lukeCandidatesEnabled;

    internal LukeRaidDisconnectCandidate? DescribeLukeCandidate(ushort character)
    {
        lock (gate)
        {
            if (!byCharacter.TryGetValue(character, out var fact) || fact.Luke is not { } reservation) return null;
            return new(fact.Instance, fact.Key, fact.Slot, fact.Squad, reservation.OriginalParty.Id,
                reservation.PartySlot, reservation.Phase, !fact.OldOwner.IsCurrent);
        }
    }

    public bool HasCandidate(ushort character)
    {
        lock (gate) return Enabled && byCharacter.ContainsKey(character);
    }

    public bool Record(AntonRaidDisconnectFact fact)
    {
        if (fact.Luke is null ? !Enabled : !LukeCandidatesEnabled) return false;
        lock (gate)
        {
            // 旧连接的迟到收尾不能覆盖后来代次的恢复能力；同代也不能用缺少上下文的记录降级完整候选。
            if (byCharacter.TryGetValue(fact.Character, out var current) && current.Account == fact.Account &&
                (current.OldOwner.Generation > fact.OldOwner.Generation ||
                 ReferenceEquals(current.OldOwner, fact.OldOwner) && current.Anton is not null && fact.Anton is null)) return false;
            byCharacter[fact.Character] = fact;
            return true;
        }
    }

    public void DropParty(uint key)
    {
        lock (gate)
        {
            foreach (var row in byCharacter.Where(row => row.Value.Key == key).Select(row => row.Key).ToArray())
                byCharacter.Remove(row);
        }
    }

    public void DropCharacter(ushort character)
    {
        lock (gate) byCharacter.Remove(character);
    }

    public AntonRaidDisconnectFact? Peek(ushort character)
    {
        lock (gate) return byCharacter.GetValueOrDefault(character);
    }

    // 835 只在同频道、原 Battle 仍存活且旧租约已释放时下发。尾部 u8/u16 语义 unknown，不在这里填业务值。
    public AntonRaidRejoinDecision DescribeNotice(RaidActor actor, bool battleAlive, bool alreadyMember)
    {
        lock (gate)
        {
            if (!Enabled || !byCharacter.TryGetValue(actor.Character, out var fact) || fact.Account != actor.Owner.Account)
                return AntonRaidRejoinDecision.Reject("none");
            if (fact.Luke is not null) return AntonRaidRejoinDecision.Reject("luke-rejoin-wire-unavailable", fact.Key);
            if (fact.Attempted) return AntonRaidRejoinDecision.Reject("already-attempted", fact.Key);
            if (fact.Channel != actor.Channel) return AntonRaidRejoinDecision.Reject("channel-mismatch", fact.Key);
            if (alreadyMember) return AntonRaidRejoinDecision.Reject("already-member", fact.Key);
            if (!battleAlive)
            {
                byCharacter.Remove(actor.Character);
                return AntonRaidRejoinDecision.Reject("battle-not-alive", fact.Key);
            }
            if (fact.OldOwner.IsCurrent) return AntonRaidRejoinDecision.Reject("old-session-not-released", fact.Key);
            return new(true, "ready", fact.Key, fact.Epoch, actor.Epoch, fact.Slot, fact.Squad,
                true, "not-applied", Barrier(fact), "u8+u16");
        }
    }

    // 668 只带原团 key。成员、epoch 与旧租约由服务端记录核对。校验通过仍不提交 owner 替换。
    public AntonRaidRejoinDecision Evaluate(RaidActor actor, uint key, bool battleAlive, bool alreadyMember)
    {
        lock (gate)
        {
            if (!Enabled) return AntonRaidRejoinDecision.Reject("formal-raid-rejoin-unavailable", key);
            if (!byCharacter.TryGetValue(actor.Character, out var fact) || fact.Account != actor.Owner.Account)
                return AntonRaidRejoinDecision.Reject("no-rejoin-record", key);
            if (fact.Key != key) return AntonRaidRejoinDecision.Reject("key-mismatch", key);
            if (fact.Luke is not null) return AntonRaidRejoinDecision.Reject("luke-rejoin-wire-unavailable", fact.Key);
            if (fact.Attempted) return AntonRaidRejoinDecision.Reject("duplicate-rejoin", key);
            if (fact.Channel != actor.Channel) return AntonRaidRejoinDecision.Reject("channel-mismatch", key);
            if (ReferenceEquals(actor.Owner, fact.OldOwner) && actor.Epoch == fact.Epoch)
                return AntonRaidRejoinDecision.Reject("epoch-still-current", key);
            if (fact.OldOwner.IsCurrent) return AntonRaidRejoinDecision.Reject("old-session-not-released", key);
            if (!actor.IsCurrent) return AntonRaidRejoinDecision.Reject("actor-not-current", key);
            if (alreadyMember) return AntonRaidRejoinDecision.Reject("already-member", key);
            if (!battleAlive)
            {
                byCharacter.Remove(actor.Character);
                return AntonRaidRejoinDecision.Reject("battle-not-alive", key);
            }
            if (fact.Anton is null) fact.Attempted = true;
            return new(true, "recovery-sequence-unknown", key, fact.Epoch, actor.Epoch, fact.Slot, fact.Squad,
                !fact.OldOwner.IsCurrent, "withheld", Barrier(fact), "recovery-sequence-unknown");
        }
    }

    static string Barrier(AntonRaidDisconnectFact fact) =>
        fact.WasLoading || fact.Run is not null ? "withheld" : "not-applicable";
}
