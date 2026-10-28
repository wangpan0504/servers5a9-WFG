using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    bool LukeRejoinOpenLocked() => Kind == RaidKind.Luke && !TerminalRequested(current) &&
        !LukePhaseDueLocked() && pendingTransition is null && dungeonRuns.Count == 0 &&
        lukeRevivalReservations.Count == 0 && !lukeSettlements.ContainsKey(current.Phase);

    internal bool IsLukeWaitingRoom(RaidActor actor)
    {
        lock (gate) return script.WaitingRoom is { } room && actor.Session.ChannelEntry is { Type: 0x20 } channel &&
            channel.Identity == actor.Channel && actor.Session.Selected?.Position is { } position &&
            position.Town == room.Town && position.Area == room.Area;
    }

    internal bool CanCaptureLukeRejoin(RaidActor actor)
    {
        lock (gate) return LukeRejoinOpenLocked() && IsLukeWaitingRoom(actor) && members.ContainsKey(actor) &&
            originalLukeMembers.GetValueOrDefault(actor.Character) == actor;
    }

    internal bool CanRestoreLukeRejoin(LukeRaidRejoinReservation reservation)
    {
        lock (gate) return CanRestoreLukeRejoinLocked(reservation);
    }

    bool CanRestoreLukeRejoinLocked(LukeRaidRejoinReservation reservation) =>
        ReferenceEquals(reservation.Battle, this) && LukeRejoinOpenLocked() && current.Phase == reservation.Phase &&
        originalLukeMembers.GetValueOrDefault(reservation.Original.Character) == reservation.Original &&
        !members.Keys.Any(actor => actor.Character == reservation.Original.Character);

    // owner → Party → RaidRegistry → Battle。回调仅安装已分配的会话绑定，不做SQL/网络/外部用户代码。
    internal bool RestoreLukeMember(LukeRaidRejoinReservation reservation, RaidActor replacement, Func<bool> install)
    {
        lock (gate)
        {
            if (!CanRestoreLukeRejoinLocked(reservation) || !replacement.IsCurrent ||
                replacement.Character != reservation.Original.Character ||
                replacement.Owner.Account != reservation.Original.Owner.Account ||
                reservation.Original.Owner.IsCurrent) return false;
            ImmutableArray<RaidPhaseRewardPolicy> policy = lukeCompletion is { } completion
                ? completion.Phases.Select(p => p.Rewards).ToImmutableArray() : [];
            members.EnsureCapacity(members.Count + 1);
            if (!install()) return false;
            members.Add(replacement, policy);
            originalLukeMembers[replacement.Character] = replacement;
            // 已提交参与、资格、已领kind集合不变；新视图使旧快照失效，但不重新锚定阶段时钟。
            current = current with { Revision = current.Revision + 1 };
            return true;
        }
    }
}
