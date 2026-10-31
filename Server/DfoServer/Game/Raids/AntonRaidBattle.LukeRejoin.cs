using System.Collections.Immutable;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Dictionary<ushort, long> lukeDisconnectAnchors = [];
    internal GameWorld.Raids.RaidWaitingRoom? LukeWaitingRoom => Kind is RaidKind.Luke or RaidKind.Fiendwar ? script.WaitingRoom : null;
    bool LukeRejoinOpenLocked() => Kind is RaidKind.Luke or RaidKind.Fiendwar && current.State != AntonRaidBattleState.Completed &&
        !current.ClockFaulted && !current.PhaseExpired && !LukePhaseDueLocked();

    internal void ReserveLukeDisconnect(RaidActor actor)
    { lock (gate) { lukeDisconnectAnchors[actor.Character] = lukeClock.GetTimestamp(); ScheduleLukeClockLocked(); } }

    TimeSpan? LukeRejoinRemainingLocked()
    {
        foreach (var row in lukeDisconnectAnchors.Where(row => lukeClock.GetElapsedTime(row.Value) >= TimeSpan.FromSeconds(300)).ToArray())
            lukeDisconnectAnchors.Remove(row.Key);
        return lukeDisconnectAnchors.Count == 0 ? null : lukeDisconnectAnchors.Values
            .Select(anchor => TimeSpan.FromSeconds(300) - lukeClock.GetElapsedTime(anchor)).Min();
    }

    internal bool IsLukeWaitingRoom(RaidActor actor)
    {
        lock (gate) return script.WaitingRoom is { } room && actor.Session.ChannelEntry is { } channel &&
            RaidTypes.FromChannelType(channel.Type) == (byte)Kind &&
            channel.Identity == actor.Channel && actor.Session.Selected?.Position is { } position &&
            position.Town == room.Town && position.Area == room.Area;
    }

    internal bool CanCaptureLukeRejoin(RaidActor actor)
    {
        lock (gate) return LukeRejoinOpenLocked() && members.ContainsKey(actor) &&
            originalLukeMembers.GetValueOrDefault(actor.Character) == actor;
    }

    internal bool CanRestoreLukeRejoin(LukeRaidRejoinReservation reservation)
    {
        lock (gate) return CanRestoreLukeRejoinLocked(reservation);
    }

    bool CanRestoreLukeRejoinLocked(LukeRaidRejoinReservation reservation) =>
        ReferenceEquals(reservation.Battle, this) && LukeRejoinOpenLocked() && pendingTransition is null &&
        !dungeonRuns.Values.Any(run => !run.Installed) && lukeRevivalReservations.Count == 0 && current.Phase >= reservation.Phase &&
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
            ImmutableArray<RaidPhaseRewardPolicy> policy = originalLukePolicies.GetValueOrDefault(replacement.Character,
                lukeCompletion is { } completion ? completion.Phases.Select(p => p.Rewards).ToImmutableArray() : []);
            members.EnsureCapacity(members.Count + 1);
            if (!install()) return false;
            members.Add(replacement, policy);
            if (Kind == RaidKind.Fiendwar && current.Fiendwar is { } field)
            {
                int area = reservation.FiendwarArea ?? FiendwarBaseArea;
                if (area != FiendwarBaseArea && (current.FiendwarPositions.Values.Count(value => value == area) >= 4 ||
                    fiendwarEncounters.Values.Any(encounter => encounter.Area == area))) area = FiendwarBaseArea;
                current = ProjectFiendwar(current with { FiendwarPositions = current.FiendwarPositions.SetItem(replacement.Character, area) },
                    fiendwarEngine!.Occupy(field, area, true, lukeClock.GetUtcNow()).State);
            }
            lukeDisconnectAnchors.Remove(replacement.Character);
            originalLukeMembers[replacement.Character] = replacement;
            // 已提交参与、资格、已领kind集合不变；新视图使旧快照失效，但不重新锚定阶段时钟。
            current = current with { Revision = current.Revision + 1 };
            ScheduleLukeClockLocked();
            return true;
        }
    }
}
