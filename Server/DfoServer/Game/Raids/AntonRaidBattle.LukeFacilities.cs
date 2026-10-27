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
            var before = current;
            var candidate = Evaluate(before with { Symbols = before.Symbols.SetItem(name, checked(before.Symbols[name] + 1)) }, null, name, null);
            // 规则失败不消费；成功后沿原pending阶段/资格SQL屏障，不在这里发奖或另开事务。
            CommitBattleCandidate(before, candidate);
            run.ConsumedFacilities.Add(binding);
            return new(LukeFacilityReportOutcome.Applied, new(before, current));
        }
    }
}
