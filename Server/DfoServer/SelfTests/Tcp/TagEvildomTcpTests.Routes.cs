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
                        checked((ushort)next), entry.BasisLevel, playerTeam: entry.PlayerTeam, companionRows: [],
                        suppressCompanions: entry.IsTagSystem);
                    plans.Add(plan);
                    if (plan.Monsters.Count > 0 || plan.SpecialObjects.Count > 0) next = plan.NextObjectKey;
                }
                That(plans.SelectMany(plan => plan.Monsters).All(monster =>
                    monster.AiCharacter is not { Id: 5409, Faction: "[character]" }),
                    "再战魔界资源路线不发布旧友方5409支援兵，由新DLL接管");
                var route = new DungeonFixedRoute(plans, maze.StaticTopology.Links!.Select(link => new DungeonRouteConnection(link.First, link.Second)).ToArray());
                var authority = new NativeDungeonCombatAuthority();
                That(SinglePlayerDungeonRun.TryCreate(entry, 1, 1, route, authority, out var run, out reason) && authority.BindTo(run!),
                    $"TAG实际房间演员和脚本能由现有Run核准：{reason}");
                That(run!.InstallTagEvildom(new(first, second, 1), admission), "实际资源Run在初始加载绑定固定同队资料");
                for (int x = 0; x < 5; x++)
                {
                    That(run.CompleteLoading(run.Snapshot.Identity, Guid.NewGuid()).Outcome == DungeonRunOutcome.Accepted, "真实五房逐一确认加载");
                    var monsters = plans.Single(plan => plan.Room.Map == run.Snapshot.CurrentMap).Monsters
                        // 对存活集合中的非战斗演员保留显式无死亡事实及无副作用核验。
                        .Where(monster => run.Snapshot.LivingMonsters.Contains(monster.ObjectKey))
                        .OrderBy(monster => monster.IsBoss).ToArray();
                    foreach (var monster in monsters)
                    {
                        var observed = new NativeDungeonDeathObservation(monster.ObjectKey, first.Id, 0x111, first.Id,
                            600, 1, 0, (uint)monster.CandidateLevel, 500, 0, 1, true, new string('a', 64));
                        var before = run.Snapshot;
                        var fact = authority.ValidateAndIssue(run, before.Identity, observed);
                        if (!monster.CanReportDeath)
                        {
                            That(monster is { MonsterId: 5409, IsFriendly: true, IsBlocking: false, AiCharacter.Faction: "[character]" } &&
                                fact.Outcome == DungeonRunOutcome.Accepted && fact.Death is null,
                                "旧玩家资源原生友方5409不取得战斗死亡事实，不能借资源遍历生成奖励或清图");
                            var after = run.Snapshot;
                            That(after.Identity == before.Identity && after.Phase == before.Phase && after.CurrentMap == before.CurrentMap &&
                                after.LivingMonsters.SequenceEqual(before.LivingMonsters) && !run.HasPendingDeath && run.ClearFact is null,
                                "非战斗演员报告不改变原TAG实例、房间、存活集合或待结算状态");
                            continue;
                        }
                        That(fact.Outcome == DungeonRunOutcome.Accepted && fact.Death is not null,
                            "当前真实敌人必须由原生权威签发非空死亡事实");
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
