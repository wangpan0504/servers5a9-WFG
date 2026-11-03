using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

internal sealed partial class AntonRaidBattle
{
    // 本服兼容合同：现场218终末117后上报110减3000；只消费已确认ClearFact的共享Run一次。
    // 9B没有对象身份，不能用此合同接小电池、孵化怪或证明跨Run延迟重放可区分。
    internal bool ConfirmAntonEnergy(RaidActor actor, DungeonRunSnapshot snapshot)
    {
        lock (gate)
        {
            if (!antonPhaseProgress || Kind != RaidKind.Anton || !actor.IsCurrent || pendingTransition is not null ||
                current.Phase != 1 || current.State != AntonRaidBattleState.Fighting || TerminalRequested(current) || LukePhaseDueLocked() ||
                snapshot.Phase != SinglePlayerDungeonPhase.Settlement ||
                !dungeonRuns.TryGetValue(snapshot.Identity.InstanceId, out var run) || !run.Installed ||
                run.Phase != current.Phase || run.Dungeon != 218 || !run.Active.Contains(actor) ||
                checked(run.Generation + 1) != current.DungeonGenerations.GetValueOrDefault(218) ||
                !run.ClearFacts.Values.Any(fact => fact.Identity.InstanceId == run.Id && fact.ClearMap == snapshot.CurrentMap &&
                    fact.Identity.Epoch == snapshot.Identity.Epoch) ||
                !entries.TryGetValue((run.Id, actor.Character), out var entry) || !ReferenceEquals(entry.Run, run) ||
                entry.Identity.CharacterId != snapshot.Identity.CharacterId || entry.Identity.QuestId != snapshot.Identity.QuestId ||
                entry.Identity.TaskAttempt != snapshot.Identity.TaskAttempt ||
                snapshot.Identity.Epoch < entry.Identity.Epoch ||
                current.Dungeons.GetValueOrDefault(218) != RaidDungeonState.Open ||
                !current.Symbols.TryGetValue("[black volcano barrier]", out int barrier)) return false;
            if (run.AntonEnergyConfirmed) return true;
            var eventKey = new AntonRaidEventKey(Instance, run.Id, run.Generation, run.Phase, 218, 1);
            if (antonEvents.Contains(eventKey, AntonRaidEventKind.EnergySettled))
            {
                run.AntonEnergyConfirmed = true;
                return true;
            }
            if (!antonEvents.Activate(eventKey) || !antonEvents.TryCommit(eventKey, AntonRaidEventKind.EnergySettled)) return false;
            var before = current;
            int remaining = Math.Max(0, barrier - 3000);
            if (remaining == 0 && barrier != 0 && antonChargeAnchor is null) antonChargeAnchor = lukeClock.GetTimestamp();
            CommitBattleCandidate(before, Evaluate(before with { Symbols = before.Symbols.SetItem("[black volcano barrier]", remaining) },
                null, "[black volcano barrier]", null));
            run.AntonEnergyConfirmed = true;
            return true;
        }
    }

    long? antonChargeAnchor;
    TimeSpan? AntonChargeRemainingLocked()
    {
        if (Kind != RaidKind.Anton || current.Phase != 1 || current.State != AntonRaidBattleState.Fighting ||
            current.Dungeons.GetValueOrDefault(219) != RaidDungeonState.Open || current.PhaseExpired ||
            current.Symbols.GetValueOrDefault("[black volcano barrier]") >= 10000)
        { antonChargeAnchor = null; return null; }
        if (antonChargeAnchor is not { } anchor) return null;
        var remaining = TimeSpan.FromSeconds(1) - lukeClock.GetElapsedTime(anchor);
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    void ChargeAntonEnergyLocked()
    {
        if (AntonChargeRemainingLocked() != TimeSpan.Zero) return;
        var rates = script.Headers.SingleOrDefault(row => row.Tag == "[shield charge rate]");
        if (rates is null || rates.Values.Length != 2 || rates.Values.Any(row => row.Number is <= 0 or > 10000))
        { antonChargeAnchor = null; return; }
        // 沿86每秒一次、首次归零后充电至满的合同；延迟回调只执行一次，不补无限tick。
        int rate = rates.Values[current.Symbols.GetValueOrDefault("[is exist infection dungeon]") != 0 ? 1 : 0].Number;
        var before = current;
        int value = Math.Min(10000, before.Symbols["[black volcano barrier]"] + rate);
        antonChargeAnchor = value == 10000 ? null : lukeClock.GetTimestamp();
        CommitBattleCandidate(before, Evaluate(before with { Symbols = before.Symbols.SetItem("[black volcano barrier]", value) },
            null, "[black volcano barrier]", null));
    }
}
