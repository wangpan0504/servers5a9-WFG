using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.GameWorld.Dungeons;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class TournamentSettlementTests
{
    static void VerifyResourceRounds(PvfArchive pvf)
    {
        var catalog = global::DfoServer.Testing.SelfTestCatalogs.Dungeons(pvf);
        var rooms = global::DfoServer.Testing.SelfTestCatalogs.Rooms(pvf);
        var character = new Character(1, 0, "Tournament", 0, new(38, 1, 1, 1), null) { Level = 95 };
        var observed = new HashSet<int>();
        foreach (int dungeon in new[] { 120, 121, 122, 123, 124 })
        {
            var definition = catalog.Get(dungeon);
            byte difficulty = definition.TryGetDesignatedDifficulty(out var fixedDifficulty) ? fixedDifficulty : (byte)0;
            That(DungeonEntryPlan.TryCreate(catalog, character, (uint)dungeon, 0, difficulty, out var entry, out var reason),
                $"真实大会创建路线：{dungeon}/{reason}");
            var room = rooms.Get(entry!.StartMap, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: false);
            var source = rooms.ReadTournamentSource(entry.StartMap);
            // 黄龙两个报告对象分别固定命中，避免随机一次入场漏过其中一名。
            int[] targets = dungeon is 120 or 123 ? [56461, 59021] : [0];
            foreach (int target in targets)
            {
                uint seed = 1;
                DungeonMonsterPlan plan;
                do
                {
                    plan = DungeonTournamentPlan.CreateRoomPlan(rooms, room, source, seed++, character.Id, 100,
                        entry.BasisLevel!.Value, entry.PlayerTeam);
                } while (target != 0 && !plan.Tournament!.Opponents.Any(row => row.Code == target) && seed <= 256);
                That(target == 0 || plan.Tournament!.Opponents.Any(row => row.Code == target),
                    $"真实大会固定种子命中报告对象：{dungeon}/{target}");
                var authority = new NativeDungeonCombatAuthority();
                That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, new([plan], []), authority, out var run, out reason) &&
                    authority.BindTo(run!) && run!.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted,
                    $"真实大会四轮实例加载：{dungeon}/{reason}");
                var active = run!;
                var opponents = plan.Tournament!.Opponents;
                NativeDungeonDeathResult Report(TournamentOpponent row) => authority.ValidateAndIssue(active, active.Snapshot.Identity,
                    new(row.ObjectKey, character.Id, 0x0111, character.Id, 0, 0, 0, row.Level, 0, 0, 0, true, new string('A', 64)));
                That(Report(opponents[^1]).Outcome == DungeonRunOutcome.Rejected && active.ClearFact is null,
                    $"真实大会不能提前报告末轮：{dungeon}");
                foreach (var opponent in opponents)
                {
                    observed.Add(opponent.Code);
                    var issued = Report(opponent);
                    That(issued is { Outcome: DungeonRunOutcome.Accepted, Death: not null },
                        $"真实大会按386的轮次key签发：{dungeon}/{opponent.Round}/{opponent.Code}");
                    var pending = active.PrepareConfirmedDeath(issued.Death!);
                    That(pending is { Outcome: DungeonRunOutcome.Accepted, Pending: not null } &&
                        pending.Pending.Result.CulledMonsterKeys.Count == 0,
                        $"真实大会逐轮不清退未来对手：{dungeon}/{opponent.Round}");
                    active.ConfirmCommittedDeath(pending.Pending!);
                    That((opponent.Round == 4) == (active.ClearFact is not null) &&
                        Report(opponent).Outcome == DungeonRunOutcome.Duplicate,
                        $"真实大会只在四轮提交后通关，重复死亡幂等：{dungeon}/{opponent.Round}");
                }
            }
        }
        That(observed.Contains(56461) && observed.Contains(59021), "刘俊与文英均经过真实资源四轮死亡路径");
    }
}
