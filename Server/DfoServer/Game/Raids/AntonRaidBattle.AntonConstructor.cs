using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    readonly Dictionary<Guid, AntonConstructorDeath> antonConstructorDeaths = [];

    internal AntonDungeonObjectScope? CaptureAntonConstructorScope(RaidActor actor, SinglePlayerDungeonRun source)
    {
        lock (gate)
        {
            var snapshot = source.Snapshot;
            if (!antonPhaseProgress || Kind != RaidKind.Anton || !actor.IsCurrent || pendingTransition is not null ||
                TerminalRequested(current) || LukePhaseDueLocked() || current.Phase != 1 || current.State != AntonRaidBattleState.Fighting ||
                snapshot.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
                source.LastLoadingFact is not { } loading || loading.Map != snapshot.CurrentMap ||
                loading.Identity.Epoch > snapshot.Identity.Epoch ||
                loading.Identity with { Epoch = snapshot.Identity.Epoch } != snapshot.Identity ||
                !dungeonRuns.TryGetValue(snapshot.Identity.InstanceId, out var run) || !run.Installed ||
                source.DungeonId != run.Dungeon || AntonConstructorDefinition.Find(run.Dungeon, snapshot.CurrentMap.Id) is null || !run.Active.Contains(actor) ||
                !(CurrentObjective(run) || run.Cleared && current.DungeonGenerations.GetValueOrDefault(checked((int)run.Dungeon)) == run.Generation &&
                    current.Dungeons.GetValueOrDefault(checked((int)run.Dungeon)) == RaidDungeonState.Close) ||
                !entries.TryGetValue((run.Id, actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                entry.Identity.CharacterId != snapshot.Identity.CharacterId || entry.Identity.QuestId != snapshot.Identity.QuestId ||
                entry.Identity.TaskAttempt != snapshot.Identity.TaskAttempt || snapshot.Identity.Epoch < entry.Identity.Epoch) return null;
            // Boss死亡推进业务Epoch，不能清掉同次房间访问中先到的Last。
            return new(Instance, run.Id, run.Generation, loading.Identity.Epoch, actor.Character, actor.Epoch);
        }
    }

    internal AntonConstructorDeath? RecordAntonConstructorDeath(RaidActor actor, SinglePlayerDungeonRun source, ushort key)
    {
        lock (gate)
        {
            var snapshot = source.Snapshot;
            if (CaptureAntonConstructorScope(actor, source) is not { } scope ||
                AntonConstructorDefinition.Find(source.DungeonId, snapshot.CurrentMap.Id) is not { } definition ||
                !source.CommittedMonitorDeath(key, out int template, out _) || template != definition.Monster ||
                snapshot.LivingMonsters.Contains(key)) return null;
            if (antonConstructorDeaths.TryGetValue(scope.Run, out var previous))
                return previous.MonsterKey == key && previous.RoomEpoch == scope.RoomEpoch && previous.Definition == definition ? previous : null;
            if (antonConstructorDeaths.Count >= 5) return null;
            // 感染条件属于这次死亡的Last；后续报文不重新选择感染目标。
            var death = new AntonConstructorDeath(Instance, scope.Run, scope.Generation, scope.RoomEpoch, definition, key,
                current.Symbols.GetValueOrDefault("[infection dungeon index]") == definition.Dungeon);
            antonConstructorDeaths.Add(scope.Run, death);
            return death;
        }
    }

    internal bool ConfirmAntonConstructorEffect(RaidActor actor, SinglePlayerDungeonRun source, AntonConstructorEffectFact fact, out bool changed)
    {
        changed = false;
        lock (gate)
        {
            if (CaptureAntonConstructorScope(actor, source) is not { } scope || scope != fact.Scope ||
                !antonConstructorDeaths.TryGetValue(scope.Run, out var death) || death != fact.Death ||
                death.Generation != scope.Generation || death.RoomEpoch != scope.RoomEpoch ||
                fact.EventId != AntonConstructorLedger.EventId(death.MonsterKey, fact.Effect) || !Enum.IsDefined(fact.Effect) ||
                fact.Effect == AntonConstructorEffect.InfectionEnergy && !death.Infected) return false;
            var key = new AntonRaidEventKey(Instance, scope.Run, scope.Generation, current.Phase, death.Definition.Dungeon, fact.EventId);
            if (antonEvents.Contains(key, AntonRaidEventKind.ObjectDestroyed)) return true;
            if (!antonEvents.Activate(key) || !antonEvents.TryCommit(key, AntonRaidEventKind.ObjectDestroyed)) return false;
            var before = current;
            if (fact.Effect == AntonConstructorEffect.VolcanoSignal)
                CommitBattleCandidate(before, before with { AntonConstructorSignals = before.AntonConstructorSignals.SetItem(death.Definition.Signal, 1), Revision = before.Revision + 1 });
            else
            {
                int barrier = before.Symbols["[black volcano barrier]"];
                int remaining = Math.Max(0, barrier - (fact.Effect == AntonConstructorEffect.Energy ? 1000 : 500));
                if (remaining == 0 && barrier != 0 && antonChargeAnchor is null) antonChargeAnchor = lukeClock.GetTimestamp();
                CommitBattleCandidate(before, Evaluate(before with { Symbols = before.Symbols.SetItem("[black volcano barrier]", remaining) },
                    null, "[black volcano barrier]", null));
            }
            changed = true;
            return true;
        }
    }

    void RemoveAntonConstructorRun(Guid run) => antonConstructorDeaths.Remove(run);
}
