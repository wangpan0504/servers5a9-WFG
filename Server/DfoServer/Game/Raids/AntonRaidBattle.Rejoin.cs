using System.Collections.Immutable;
using DfoServer.Game.Dungeons;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 只保留原聚合的能力引用。票、战况和奖励仍由原Battle及既有持久账本负责。
    internal sealed class AntonMemberReservation
    {
        readonly AntonRaidBattle battle;
        readonly ImmutableArray<RaidPhaseRewardPolicy> policy;
        readonly DungeonBinding? entry;
        readonly RaidActor original;
        readonly int phase;
        private AntonMemberReservation(AntonRaidBattle battle, RaidActor actor,
            ImmutableArray<RaidPhaseRewardPolicy> policy, DungeonBinding? entry)
        { this.battle = battle; original = actor; this.policy = policy; this.entry = entry; phase = battle.current.Phase; }

        internal static AntonMemberReservation? Capture(AntonRaidBattle battle, RaidActor actor, Guid? run)
        {
            lock (battle.gate)
            {
                if (battle.Kind != RaidKind.Anton || !battle.members.TryGetValue(actor, out var policy) ||
                    !battle.RejoinOpenLocked()) return null;
                var entry = run is { } id ? battle.entries.GetValueOrDefault((id, actor.Character)) : null;
                if (run is not null && entry?.Run is not { Installed: true }) return null;
                return new(battle, actor, policy, entry);
            }
        }

        internal bool CanRestore()
        {
            lock (battle.gate) return CanRestoreLocked();
        }

        internal bool MatchesRunProgress(ushort character, long epoch, long disconnectedEpoch, bool completedLoading)
        {
            lock (battle.gate)
            {
                if (entry?.Run is not { } run || disconnectedEpoch < entry.Identity.Epoch) return false;
                var participant = run.Actors.FirstOrDefault(a => a.Actor.Character == character);
                if (participant.Actor is null || epoch < participant.Identity.Epoch) return false;
                long progress = disconnectedEpoch - entry.Identity.Epoch;
                return (!completedLoading || progress < long.MaxValue) &&
                    epoch - participant.Identity.Epoch == progress + (completedLoading ? 1 : 0);
            }
        }

        bool CanRestoreLocked() => battle.RejoinOpenLocked() && phase == battle.current.Phase &&
            !original.Owner.IsCurrent && !battle.members.Keys.Any(a => a.Character == original.Character) &&
            (battle.antonRewards is null || battle.originalAntonMembers.GetValueOrDefault(original.Character) == original) &&
            (entry is null || entry.Run is { } run && battle.dungeonRuns.GetValueOrDefault(run.Id) == run &&
                run.Active.Count > 0 && run.Phase == phase && (battle.CurrentObjective(run) ||
                    battle.current.State == AntonRaidBattleState.Reward && run.Cleared));

        // owner → Party → RaidRegistry → Battle；install只安装已准备的Session/Run，不执行SQL或网络。
        internal bool Restore(RaidActor replacement, Func<bool> install)
        {
            lock (battle.gate)
            {
                if (!CanRestoreLocked() || !replacement.IsCurrent || replacement.Character != original.Character ||
                    replacement.Owner.Account != original.Owner.Account) return false;
                battle.members.EnsureCapacity(battle.members.Count + 1);
                if (!install()) return false;
                battle.members.Add(replacement, policy);
                if (battle.antonRewards is not null) battle.originalAntonMembers[replacement.Character] = replacement;
                if (entry?.Run is { } run)
                {
                    run.Active.Add(replacement);
                    int at = Array.FindIndex(run.Actors, a => a.Actor == original);
                    if (at >= 0) run.Actors[at] = (replacement, entry.Identity);
                    if (run.ClearFacts.Remove(original, out var fact)) run.ClearFacts[replacement] = fact;
                    if (run.MapEpochs.Remove(original, out var epoch)) run.MapEpochs[replacement] = epoch;
                    battle.entries[(run.Id, replacement.Character)] = entry;
                }
                battle.current = battle.current with { Revision = battle.current.Revision + 1 };
                return true;
            }
        }
    }

    bool RejoinOpenLocked() => Kind == RaidKind.Anton && current.State is AntonRaidBattleState.Fighting or AntonRaidBattleState.Reward &&
        !current.ClockFaulted && !current.PhaseExpired && pendingTransition is null;
}
