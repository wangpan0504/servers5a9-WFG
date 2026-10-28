using System.Collections.Immutable;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Raids;

namespace DfoServer.Game.Raids;

// 本服显式兼容合同：按当前场景解释原662报告，不是服务端伤害权威。默认不装配。
internal sealed record LukeFacilityReportPolicy(string Source, bool AcceptAuthenticatedClientActionReports)
{
    public bool Valid => !string.IsNullOrWhiteSpace(Source) && AcceptAuthenticatedClientActionReports;
}
internal enum LukeFacilityReportOutcome { Rejected, Applied, Duplicate }
internal sealed record LukeFacilityLedgerMismatch(Guid Run, int Phase, uint Dungeon, string Symbol, int Expected, int Actual, long ExpectedWriteEpoch, long ActualWriteEpoch);
internal sealed record LukeFacilityReportResult(LukeFacilityReportOutcome Outcome, AntonRaidBattleChange? Change = null);

internal sealed partial class AntonRaidBattle
{
    readonly LukeFacilityReportPolicy? lukeFacilities;
    internal bool AcceptsLukeFacilityReports => lukeFacilities is not null;
    LukeObjectBinding? CreateFacilityRoom(RaidDungeonRun run, ImmutableArray<LukeObjectBinding>? bindings)
    {
        if (lukeFacilities is null || bindings is not [var binding] || !SupportedFacility(binding)) return null;
        return binding;
    }

    static string? FacilitySymbol(LukeObjectBinding binding) => (binding.Room.Dungeon, binding.ObjectId, binding.Symbol,
        binding.ActionOrdinal, binding.ActionPath) switch
    {
        (5054, 64249, 117, 0, "passiveobject/actionobject/map/luke_raid/phase1/sanctum_of_birth/action/luke_mainmchine_destroy_raid.act") => "[birth sanctum object]",
        (5055, 64251, 118, 0, "passiveobject/actionobject/map/luke_raid/phase1/sanctum_of_extinction/action/mainmchine_destroy_raid.act") => "[extinction sanctum object]",
        (5056, 64252, 119, 1, "passiveobject/actionobject/map/luke_raid/phase1/sanctum_of_spread/action/censor_machine_broken_front_raid.act") => "[spread sanctum object]",
        _ => null
    };
    bool SupportedFacility(LukeObjectBinding binding) => binding.Ordinal >= 0 && FacilitySymbol(binding) is { } name &&
        script.Symbols.GetValueOrDefault(name, -1) == binding.Symbol;

    sealed record FacilityLedger(string Symbol, int Baseline, int Expected, int Delta, long WriteEpoch);
    AntonRaidBattleView StampLukeFacilityWrite(AntonRaidBattleView state, string symbol) =>
        lukeFacilities is not null && symbol is "[birth sanctum object]" or "[extinction sanctum object]" or "[spread sanctum object]"
            ? state with { LukeFacilityWriteEpochs = state.LukeFacilityWriteEpochs.SetItem(symbol,
                checked(state.LukeFacilityWriteEpochs.GetValueOrDefault(symbol) + 1)) } : state;
    LukeFacilityLedgerMismatch? lastFacilityLedgerMismatch;
    // 每个Battle只保留最后一条类型化失配，不累积历史，也不伪造原生错误码。
    internal LukeFacilityLedgerMismatch? LastFacilityLedgerMismatch { get { lock (gate) return lastFacilityLedgerMismatch; } }
    void RecordFacilityMismatch(RaidDungeonRun run, FacilityLedger ledger, AntonRaidBattleView state)
    {
        run.FacilityLedgerAmbiguous = true;
        lastFacilityLedgerMismatch = new(run.Id, run.Phase, run.Dungeon, ledger.Symbol, ledger.Expected, state.Symbols[ledger.Symbol],
            ledger.WriteEpoch, state.LukeFacilityWriteEpochs.GetValueOrDefault(ledger.Symbol));
    }

    // 仅撤回本服兼容报告在未完成Run内的增量，不解释原生reset sanctum object。
    // 其它机制覆盖计数后不再猜测归属；保留当前权威值并允许强制退出清理。
    AntonRaidBattleView RevertLukeFacilityReports(RaidDungeonRun run)
    {
        if (lukeFacilities is null || run.FacilityLedger is not { } ledger) return current;
        int actual = current.Symbols[ledger.Symbol];
        if (run.FacilityLedgerAmbiguous || actual != ledger.Expected ||
            current.LukeFacilityWriteEpochs.GetValueOrDefault(ledger.Symbol) != ledger.WriteEpoch)
        {
            RecordFacilityMismatch(run, ledger, current);
            return current;
        }
        return current with { Symbols = current.Symbols.SetItem(ledger.Symbol, ledger.Baseline) };
    }

    bool FacilityContext(RaidActor actor, DungeonRunSnapshot snapshot, out RaidDungeonRun run)
    {
        run = null!;
        if (lukeFacilities is null || !actor.IsCurrent ||
            snapshot.Phase is not (SinglePlayerDungeonPhase.Dungeon or SinglePlayerDungeonPhase.Settlement) ||
            !dungeonRuns.TryGetValue(snapshot.Identity.InstanceId, out var found) || !found.Installed || found.Cleared ||
            found.Phase != 0 || found.Phase != current.Phase || !found.Active.Contains(actor) ||
            current.DungeonGenerations.GetValueOrDefault(checked((int)found.Dungeon)) != found.Generation || found.FacilityRoom is not { } room ||
            !entries.TryGetValue((found.Id, actor.Character), out var entry) || !ReferenceEquals(entry.Run, found) ||
            snapshot.Identity.CharacterId != actor.Character || snapshot.Identity.QuestId != entry.Identity.QuestId ||
            snapshot.Identity.TaskAttempt != entry.Identity.TaskAttempt ||
            !found.MapEpochs.TryGetValue(actor, out long epoch) || snapshot.Identity.Epoch != epoch ||
            room.Room.Kind != snapshot.CurrentMap.Kind || room.Room.Position != snapshot.CurrentMap.Position ||
            room.Room.Map != snapshot.CurrentMap.Id || room.Room.Path != snapshot.CurrentMap.Path ||
            snapshot.CurrentMap.IsConditional || snapshot.CurrentMap.LayerIndex >= 0 || snapshot.CurrentMap.StoryStageIndex >= 0 ||
            snapshot.CurrentMap.DummyStageIndex >= 0 || snapshot.CurrentMap.CandidateIds.Count > 1) return false;
        run = found;
        return true;
    }

    // snapshot由Session在原共享实例owner组内读取；请求不提供room/epoch/HP。
    // 包内无场景身份，不能声称完全相同的跨房在途/重放报告可被区分。
    internal LukeFacilityReportResult ConfirmLukeFacility(RaidActor actor, DungeonRunSnapshot snapshot,
        int symbol, uint value, byte operation)
    {
        lock (gate)
        {
            if (value != 1 || operation != 1 || !FacilityContext(actor, snapshot, out var run))
                return new(LukeFacilityReportOutcome.Rejected);
            var binding = run.FacilityRoom!;
            if (symbol != binding.Symbol) return new(LukeFacilityReportOutcome.Rejected);
            if (run.ConsumedFacilities.Contains(binding)) return new(LukeFacilityReportOutcome.Duplicate);
            if (pendingTransition is not null || current.State != AntonRaidBattleState.Fighting || !CurrentObjective(run))
                return new(LukeFacilityReportOutcome.Rejected);
            // 只给资源已证birth/extinction设置怪物条件，spreadfront绝不新增清房门槛。
            if (binding.ObjectId is 64249 or 64251 && snapshot.LivingMonsters.Count != 0)
                return new(LukeFacilityReportOutcome.Rejected);
            string name = FacilitySymbol(binding)!;
            var previous = run.FacilityLedger;
            if (previous is not null && (run.FacilityLedgerAmbiguous || current.Symbols[name] != previous.Expected ||
                current.LukeFacilityWriteEpochs.GetValueOrDefault(name) != previous.WriteEpoch))
            {
                RecordFacilityMismatch(run, previous, current);
                return new(LukeFacilityReportOutcome.Rejected);
            }
            var before = current;
            var ledger = new FacilityLedger(name, previous?.Baseline ?? before.Symbols[name],
                checked(before.Symbols[name] + 1), checked((previous?.Delta ?? 0) + 1), before.LukeFacilityWriteEpochs.GetValueOrDefault(name));
            var candidate = Evaluate(before with { Symbols = before.Symbols.SetItem(name, checked(before.Symbols[name] + 1)) }, null, name, null);
            // 规则失败不消费；成功后沿原pending阶段/资格SQL屏障，不在这里发奖或另开事务。
            CommitBattleCandidate(before, candidate);
            run.ConsumedFacilities.Add(binding);
            run.FacilityLedger = ledger;
            if (candidate.Symbols[name] != ledger.Expected || candidate.LukeFacilityWriteEpochs.GetValueOrDefault(name) != ledger.WriteEpoch)
                RecordFacilityMismatch(run, ledger, candidate);
            return new(LukeFacilityReportOutcome.Applied, new(before, current));
        }
    }
}
