using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.TagEvildom;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Towns;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class TagEvildomTcpTests
{
    static void ResourceRoutes(TcpTestResources resources, Character first, Character second)
    {
        var stadium = resources.Towns.Areas[(62, 0)];
        That(stadium.DungeonWorldMap is null && stadium.Map(new HashSet<int>()).TagDungeonPopup &&
            stadium.Map(new HashSet<int>()).DungeonGates.Length == 0,
            "当前Stadium有TAG popup但没有普通worldmap或dungeon gate，不伪造普通选图资格");
        That(TownCatalog.HasTagDungeonPopup("[popup window areas]\n[window area]\n[popup window]\n`enter tagdungeon`\n[area]\n46 3 120 50\n[/window area]\n[/popup window areas]") &&
            !TownCatalog.HasTagDungeonPopup("[popup window areas]\n[window area]\n[popup window]\n`enter dungeon`\n[area]\n46 3 120 50\n[/window area]\n[/popup window areas]"),
            "popup能力只识别已证TAG脚本，不借普通入口文字");
        var catalog = SelfTestCatalogs.Dungeons(resources.Archive);
        var rooms = SelfTestCatalogs.Rooms(resources.Archive);
        var configuration = new TagEvildomConfiguration(1, 0, [new(first.Id, second.Id), default, default]);
        var unique = new HashSet<int>();
        int routes = 0;
        foreach (int id in new[] { 9608, 400000095 })
        {
            var definition = catalog.Get(id);
            That(TagEvildomEntryPolicy.Supports(definition), $"本版TAG{id}资源准入声明完整");
            foreach (var source in definition.Mazes)
            {
                var admission = new TagEvildomAdmission(configuration, first, second, id, new string('a', 64), 1)
                    { MazeOrdinal = source.SourceOrdinal };
                That(DungeonEntryPlan.TryCreate(catalog, first, (uint)id, 0, 0, out var entry, out var reason,
                    draw: _ => throw new InvalidOperationException("15已冻结迷宫，16不得重抽"),
                    quests: SelfTestCatalogs.Quests(resources.Archive), tagEvildom: admission, tagMaze: source.SourceOrdinal),
                    $"TAG{id}候选{source.SourceOrdinal}可据原ordinal批准：{reason}");
                var maze = entry!.Maze;
                That(maze.MapSpecification!.Count == 5 && maze.StaticTopology.Links!.Count == 4 &&
                    entry.StartMap.Position == new DungeonPoint(0, 0) && entry.BossMap.Position == new DungeonPoint(4, 0),
                    "五房四边互反拓扑及真实显式终点，不靠放宽Run几何");
                uint next = 256;
                var plans = new List<DungeonMonsterPlan>();
                foreach (var map in maze.MapSpecification)
                {
                    unique.Add(map.Id);
                    var plan = DungeonMonsterPlan.FromRoom(rooms.Get(map, requirePassiveDefinitions: false), first.Id,
                        checked((ushort)next), entry.BasisLevel, playerTeam: entry.PlayerTeam, companionRows: []);
                    plans.Add(plan);
                    if (plan.Monsters.Count > 0 || plan.SpecialObjects.Count > 0) next = plan.NextObjectKey;
                }
                var route = new DungeonFixedRoute(plans, maze.StaticTopology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray());
                var authority = new NativeDungeonCombatAuthority();
                That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, route, authority, out var run, out reason) && authority.BindTo(run!),
                    $"TAG实际房间演员和脚本能由现有Run核准：{reason}");
                That(run!.InstallTagEvildom(new(first, second, 1), admission), "实际资源Run在初始加载绑定固定同队资料");
                for (int x = 0; x < 5; x++)
                {
                    That(run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted, "真实五房逐一确认加载");
                    var monsters = plans.Single(plan => plan.Room.Map == run.Snapshot.CurrentMap).Monsters
                        // 存活集合还包含友方原生AI与非战斗对象，不能当作普通死亡目标。
                        .Where(monster => monster.CanReportDeath && run.Snapshot.LivingMonsters.Contains(monster.ObjectKey))
                        .OrderBy(monster => monster.IsBoss).ToArray();
                    foreach (var monster in monsters)
                    {
                        var observed = new NativeDungeonDeathObservation(monster.ObjectKey, first.Id, 0x111, first.Id,
                            600, 1, 0, (uint)monster.CandidateLevel, 500, 0, 1, true, new string('a', 64));
                        var fact = authority.ValidateAndIssue(run, run.Snapshot.Identity, observed);
                        That(fact.Outcome == DungeonRunOutcome.Accepted && fact.Death is not null,
                            $"TAG{id}候选{source.SourceOrdinal}房{x}地图{run.Snapshot.CurrentMap.Id}演员{monster.MonsterId}/key{monster.ObjectKey}签发真实死亡事实");
                        var prepared = run.PrepareConfirmedDeath(fact.Death!);
                        That(prepared.Pending is not null, "实际批准怪物沿原生死亡权威形成待确认事实");
                        if (prepared.Pending!.TagEvildomClear is not null)
                        {
                            That(x == 4 && run.HasPendingDeath && run.ClearFact is null,
                                "仅真实末房Boss产生TAG待结算，不伪造普通ClearFact或在资源测试发奖");
                            break;
                        }
                        That(run.ConfirmCommittedDeath(prepared.Pending).Outcome == DungeonRunOutcome.Accepted, "非终点死亡无经济确认");
                    }
                    if (x < 4)
                        That(run.Move(run.Snapshot.Identity, Guid.NewGuid(), new(x + 1, 0)).Outcome == DungeonRunOutcome.Accepted,
                            "合法清房后按原互反边推进，未绕过怪物存活或门墙");
                }
                That(run.HasPendingDeath, "每条实际路线都抵达独立TAG终点待结算");
                routes++;
            }
        }
        That(routes == 10 && unique.Count == 30, "定向覆盖两难度十条路线、三十份实际MAP，不冒称全资源验证");
    }
}
