using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 稳定事件只来自被冻结许可的生成与销毁身份，客户端662不提供事件身份或扣减数值。
    internal bool ConfirmAntonObjectEnergy(RaidActor actor, DungeonRunSnapshot snapshot,
        AntonDungeonObjectDestructionFact fact, out bool changed)
    {
        changed = false;
        lock (gate)
        {
            if (!antonPhaseProgress || TerminalRequested(current) || LukePhaseDueLocked() ||
                CaptureAntonObjectScope(actor, snapshot) is not { } scope || scope != fact.Scope ||
                fact.Room != snapshot.CurrentMap.Position || fact.Resource is not { ObjectId: 18865, SpawnMode: 1 } ||
                fact.Resource.Map != fact.Room || fact.SourceOrdinal < 0 || fact.SelectionOrdinal < 0 ||
                fact.EventId != AntonDungeonObjectLedger.EventId(fact.SourceOrdinal, fact.SelectionOrdinal) ||
                !current.Symbols.TryGetValue("[black volcano barrier]", out int barrier)) return false;
            var key = new AntonRaidEventKey(Instance, scope.Run, scope.Generation, current.Phase, 218, fact.EventId);
            if (antonEvents.Contains(key, AntonRaidEventKind.ObjectDestroyed)) return true;
            if (!antonEvents.Activate(key) || !antonEvents.TryCommit(key, AntonRaidEventKind.ObjectDestroyed)) return false;
            var before = current;
            int remaining = Math.Max(0, barrier - 100);
            if (remaining == 0 && barrier != 0 && antonChargeAnchor is null) antonChargeAnchor = lukeClock.GetTimestamp();
            CommitBattleCandidate(before, Evaluate(before with
            { Symbols = before.Symbols.SetItem("[black volcano barrier]", remaining) }, null, "[black volcano barrier]", null));
            changed = true;
            return true;
        }
    }

    internal AntonDungeonObjectScope? CaptureAntonObjectScope(RaidActor actor, DungeonRunSnapshot snapshot)
    {
        lock (gate)
        {
            if (Kind != RaidKind.Anton || !actor.IsCurrent || pendingTransition is not null ||
                current.Phase != 1 || current.State != AntonRaidBattleState.Fighting ||
                snapshot.Phase != SinglePlayerDungeonPhase.Dungeon ||
                !dungeonRuns.TryGetValue(snapshot.Identity.InstanceId, out var run) || !run.Installed ||
                run.Dungeon != 218 || run.Cleared || !run.Active.Contains(actor) || !CurrentObjective(run) ||
                !entries.TryGetValue((run.Id, actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                entry.Identity.CharacterId != snapshot.Identity.CharacterId || entry.Identity.QuestId != snapshot.Identity.QuestId ||
                entry.Identity.TaskAttempt != snapshot.Identity.TaskAttempt || snapshot.Identity.Epoch < entry.Identity.Epoch)
                return null;
            return new(Instance, run.Id, run.Generation, snapshot.Identity.Epoch, actor.Character, actor.Epoch);
        }
    }
}
