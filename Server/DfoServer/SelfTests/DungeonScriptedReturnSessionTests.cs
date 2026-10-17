using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Quests;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 合成Session门禁；不模拟任务提交或客户端自然事件。
internal static class DungeonScriptedReturnSessionTests
{
    internal static void RunCore()
    {
        using (var pending = new Fixture()) Verify(pending,["lifecycle", "communication", "quickclear"]);
        using (var card = new Fixture(clear:true)) Verify(card,["card"]);
    }

    static void Verify(Fixture f,string[] kinds)
    {
        foreach (string pending in kinds)
        {
            f.Pending(pending, true);
            f.Unchanged(() => That(f.Session.MoveFormalDungeon(1, 0, 0) is null,
                pending + "未决时首次45不得借普通MoveLayer返回"));
            f.Pending(pending, false);
        }
        var first = f.Session.MoveFormalDungeon(1, 0, 0);
        That(first is { ScriptedReturnId: var id } && id != Guid.Empty,
            "三种测试pending清理后，同一实例只建立一次呈现许可");
        f.Session.ConfirmScriptedReturnPresentation(first!);
        foreach (string pending in kinds)
        {
            f.Pending(pending, true);
            f.Unchanged(() =>
            {
                That(f.Session.CompleteFormalDungeonLoading() is null, pending + "未决时37不得确认呈现");
                That(!f.Session.ConfirmFormalDungeonLayerBase(1, 0), pending + "未决时kind1不得确认");
                That(f.Session.MoveFormalDungeon(1, 0, 0) is null &&
                    f.Session.MoveFormalDungeon(1, 0, 1) is null, pending + "重复45不得绕门禁或落回真实换层");
                That(!f.Session.CanPublishFormalDungeon(first!), pending + "后来出现的pending使原回执不可发布");
            });
            f.Pending(pending, false);
            That(f.Session.CanPublishFormalDungeon(first!), "同一原回执恢复发布资格");
        }
        var loaded = f.Session.CompleteFormalDungeonLoading();
        That(loaded is not null, "相同许可在所有测试pending清除后恢复37");
        f.Session.ConfirmScriptedReturnLoading(loaded!);
        That(f.Session.ConfirmFormalDungeonLayerBase(1, 0) &&
            f.Session.MoveFormalDungeon(1, 0, 0) is { NavigationReplay: true }, "恢复后kind1和重复45仍属呈现确认");
        That(ReferenceEquals(f.OriginalClear,f.Run.ClearFact) && f.Run.Snapshot.CurrentMap == f.Layer &&
            f.Run.Snapshot.Identity == f.OriginalIdentity &&
            f.Session.Selected!.ActiveMainQuests.Single().AcceptanceId == f.Acceptance,
            "本组门禁及恢复不生成死亡、清图、epoch或新接取");
        That(f.Run.CreatureExperiencePoints == 3,"Session呈现确认和未决操作恢复不重复累计宠物房间成长点");
    }
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static Type NestedType(string name) => typeof(GameSession).GetNestedType(name, BindingFlags.NonPublic)!;
    static object Nested(string name, params object?[] args) => NestedType(name).GetConstructors(Instance)
        .Single(ctor => ctor.GetParameters().Length == args.Length).Invoke(args);

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-script-nav-gate-" + Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly AccountOwners owners = new();
        readonly QuestCatalog quests;
        readonly QuestLifecyclePlan frozen;
        readonly QuestQuickClearPlan quick;
        readonly NativeDungeonCombatAuthority authority;
        readonly object dungeon;
        object? settlement;
        DungeonCardReward? card;
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public SinglePlayerDungeonRun Run { get; }
        public DungeonMap Layer { get; }
        public Guid Acceptance { get; } = Guid.NewGuid();
        public DungeonRunIdentity OriginalIdentity { get; }
        public DungeonClearFact? OriginalClear { get; }

        public Fixture(bool clear = false)
        {
            path = Path.Combine(root, "game.db");
            var point = new Location(14, 1, 50, 50);
            var townMap = new TownMap("fixture/nav.map", [new(new(0, 0, 2000, 1000), [], [])], [], false, []);
            var area = new TownArea(14, 1, 1, [], [], [], point, townMap, [], []);
            var towns = new TownCatalog(new() { [(14, 1)] = area }, point, point, new(0, [], []), [],
                new Dictionary<uint, uint[]>());
            var quest = QuestEligibilityTests.Quest(6032);
            quests = new(new() { [6032] = quest });
            Repository = new(path, quests);
            Owner = owners.Acquire("script-nav-gate", () => { });
            var created = Repository.Create(Owner, new("NavGate", point, point)
            {
                ActiveMainQuests = [new(0, 6032, 1) { AcceptanceId = Acceptance }],
                DungeonState = new(10, 0, 0, 0, 0)
            });
            Session = new(Repository, towns, Owner, questCatalog: quests);
            Session.Select(created.Slot, resumeTown: true); Session.MarkReady();
            var expected = Session.Selected!;
            var start = new DungeonMap("map", new(0, 0), 59911, "map/start.map");
            var source = new DungeonMap("map", new(1, 0), 59913, "map/source.map");
            var boss = new DungeonMap("boss", new(2, 0), 59914, "map/terminal.map");
            Layer = boss with { Kind = "layered", Position = source.Position };
            var maze = new DungeonMaze(0, 3, 1, "BBEE", [start.Position], [boss.Position], [start, source, boss], [])
                { LayeredMaps = [Layer] };
            var entry = new DungeonEntryPlan(expected.Id, expected.Revision + 1, 99, 6032, 0, 0, maze, start, boss)
            {
                HasExplicitClearMap = true, QuestAcceptanceId = Acceptance, QuestAcceptances = [.. expected.ActiveMainQuests],
                QuestClearMap = Layer, TerminalLayerMap = Layer, TerminalLayerQuestId = 6032,
                TerminalLayerQuestSlot = 0, TerminalLayerAcceptanceId = Acceptance
            };
            var declaration = new DungeonScriptedLayerReturn(Layer.Id, Layer.Path, 0, 109000009,
                "passiveobject/event.obj", 0, "passiveobject/action/6032_3.act", 1, 0, 406, 209, 1);
            DungeonMonsterPlan Plan(DungeonMap map, ushort key) => DungeonMonsterPlan.FromRoom(new(map,
                map == Layer || map == boss ? [new(1, [1, 0, 20, 0, 0, 0, 1, 1], "[fixed]", "[boss]", "monster/fixture.mob")] : [],
                map == Layer ? [new(109000009, [109000009, 0, 0, 0], "passiveobject/event.obj")] : [], [])
                { DeclaredType = map == Layer || map == boss ? "[boss]" : "[normal]", ScriptedLayerReturn = map == Layer ? declaration : null }, expected.Id, key);
            var route = new DungeonFixedRoute([Plan(start, 10), Plan(source, 15), Plan(boss, 20), Plan(Layer, 30)],
                [new(start.Position, source.Position), new(source.Position, boss.Position)]);
            authority = new NativeDungeonCombatAuthority();
            That(SinglePlayerDungeonRun.TryCreate(entry, 1, Session.SelectionEpoch, route, authority, out var run, out _) &&
                authority.BindTo(run!), "合成Run使用正式combat authority但不生成死亡");
            Run = run!;
            dungeon = Nested("FormalDungeon", entry, route, Run, authority, 1u,
                new DungeonGroundState(Run, route.Rooms, [], ""), new Dictionary<int, DungeonExplicitDropPool>(),
                (Func<int, int>)(_ => 0), null, ImmutableDictionary<ushort, DungeonDropGenerationPlan>.Empty);
            var prepared = Nested("PreparedFormalDungeon", expected, Session.SelectionEpoch, dungeon, 31u);
            That(typeof(GameSession).GetMethod("InstallFormalDungeon", Instance)!.Invoke(Session, [prepared]) is not null,
                "合成实例仍经正式attempt/key预留安装");
            That(Session.CompleteFormalDungeonLoading() is not null && Session.MoveFormalDungeon(1, 0) is not null &&
                Session.CompleteFormalDungeonLoading() is not null && Session.MoveFormalDungeon(1, 0, 1) is not null &&
                Session.CompleteFormalDungeonLoading() is not null, "真实Session加载并进入未清末层");
            That(Run.Snapshot.CurrentMap == Layer && Run.ClearFact is null, "门禁测试起点没有伪造终末死亡");
            frozen = QuestLifecyclePlan.PayImageCommunication(Session.Selected!, 6032, Acceptance, 0, quests, DateTimeOffset.UtcNow);
            // 正常构造真实plan；空scope仅用于测试pending布尔门禁，不提交跳过事务。
            quick = (QuestQuickClearPlan)typeof(QuestQuickClearPlan).GetConstructors(Instance)
                .Single(ctor => ctor.GetParameters().Length == 11).Invoke([Session.Selected!, Guid.NewGuid(), 1,
                    ImmutableArray<StoryQuest>.Empty, ImmutableDictionary<int, byte>.Empty, ImmutableArray<int>.Empty,
                    quests, null, null, null, null]);
            if (clear)
            {
                var death = authority.ValidateAndIssueScripted(Run,Run.Snapshot.Identity,new(30,0,true,new string('A',64)));
                That(death.Death is not null && Run.AcceptConfirmedDeath(death.Death).Outcome == DungeonRunOutcome.Accepted &&
                    Run.ClearFact is not null,"翻牌门禁先经原权威合成报告建立真实领域ClearFact");
                typeof(GameSession).GetMethod("SetState",Instance)!.Invoke(Session,[SessionPhase.Settlement]);
                card = new(0,null,"fixture-card","fixture/card",0);
                var settlementPlan = Run.PrepareSettlement(Run.ClearFact!,new byte[] { 1 },new(1,0,1,1,"fixture-clear","fixture/clear"),card);
                That(settlementPlan.Pending is not null && Run.ConfirmCommittedSettlement(settlementPlan.Pending) == DungeonRunOutcome.Accepted,
                    "门禁夹具正常构造并确认原结算准备，不注入空Pending");
                settlement = Nested("FormalSettlement",settlementPlan.Pending,new DungeonSettlementPresentation(0,0,0,0),Session.Selected!);
                settlement.GetType().GetProperty("Committed",Instance)!.SetValue(settlement,true);
                dungeon.GetType().GetProperty("Settlement",Instance)!.SetValue(dungeon,settlement);
            }
            OriginalIdentity = Run.Snapshot.Identity;
            OriginalClear = Run.ClearFact;
        }

        public void Pending(string kind, bool enabled)
        {
            if (kind == "card")
            {
                var selected = Session.Selected!;
                var claim = enabled ? new DungeonCardClaimPreparation(selected,0,
                    new(new(selected.Inventory,selected.Inventory,"fixture-card"),[card!],[]),DateTimeOffset.UtcNow) : null;
                settlement!.GetType().GetProperty("Claim",Instance)!.SetValue(settlement,claim);
                return;
            }
            string field = kind switch { "lifecycle" => "pendingQuestLifecycle", "communication" => "pendingCommunication", _ => "pendingQuestQuickClear" };
            object? value = null;
            if (enabled)
            {
                if (kind == "lifecycle")
                    value = Nested("PendingQuestLifecycle", frozen, Session.SelectionEpoch, Session.Phase, Run.Snapshot.Identity, null);
                else if (kind == "communication")
                {
                    var element = NestedType("CommunicationSource");
                    var sources = typeof(ImmutableArray<>).MakeGenericType(element).GetField("Empty", BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
                    var scene = Nested("CommunicationScene", Session.Selected!.Id, Owner.Generation, Session.SelectionEpoch,
                        0L, (byte)14, (byte)1, "fixture/nav.map", sources);
                    value = Nested("PendingCommunication", frozen, scene);
                }
                else value = Nested("PendingQuestQuickClear", quick, Session.SelectionEpoch, Session.Phase,
                    Run.Snapshot.Identity, ImmutableArray<QuestSkipAnswer>.Empty, true, null, false);
            }
            // 单线程core门禁夹具注入真实私有记录，不声称这里生成了真实通讯/跳过事件。
            typeof(GameSession).GetField(field, Instance)!.SetValue(Session, value);
        }

        public void Unchanged(Action check)
        {
            var identity = Run.Snapshot.Identity;
            var map = Run.Snapshot.CurrentMap;
            var selected = Session.Selected;
            string database = JsonSerializer.Serialize(Repository.LoadCharacter(Owner, selected!.Slot));
            var clear = Run.ClearFact;
            check();
            That(identity == Run.Snapshot.Identity && map == Run.Snapshot.CurrentMap && ReferenceEquals(clear, Run.ClearFact) &&
                ReferenceEquals(selected, Session.Selected) && database == JsonSerializer.Serialize(Repository.LoadCharacter(Owner, selected.Slot)) &&
                Session.Selected!.ActiveMainQuests.Single().AcceptanceId == Acceptance,
                "拒绝不改变角色、存档、原接取或Run身份/地图/清图");
        }
        public void Dispose() { Session.End(); Owner.Dispose(); Directory.Delete(root, true); }
    }
}

