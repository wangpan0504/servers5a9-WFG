using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Town;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;

namespace DfoServer;

internal static class TownSelectionTests
{
    static readonly GoldLimitCatalog Limits = new("[gold limit from level]\n0 500\n[/gold limit from level]");
    static readonly HashSet<int> Quests = [10, 11];

    public static void Run()
    {
        int before = TestCheck.Count;
        Rules();
        Selection();
        SelectionFailures();
        TravelCommits();
        Console.WriteLine($"PASS {TestCheck.Count - before} town rules/selection/receipt assertions");
    }

    static TownCatalog Towns()
    {
        var room = new Location(1, 0, 20, 20, 7);
        var street = new Location(2, 0, 10, 10);
        Link Gate(byte town) => new(new(8, 8, 4, 4), town, 0, [], []);
        TownMap Map(string name, bool isRoom, Link[] links, TownNpc[] npcs) => new(name,
            [new(new(0, 0, 100, 100), [], [])], links, isRoom, npcs);
        TownArea Area(byte town, TownMap map) => new(town, 0, 1, [], [], [], null, map, [], []);
        var portal = Map("portal.map", false, [Gate(1), Gate(2)], []);
        return new(new()
        {
            [(1, 0)] = Area(1, Map("room.map", true, [Gate(2)], [new(100, 1, 1)])),
            [(2, 0)] = Area(2, Map("street.map", false, [Gate(1), Gate(3)], [new(100, 1, 1)])),
            [(3, 0)] = Area(3, portal) with { Level = 10, Quests = [10], AnyQuests = [11, 12], ExceptQuests = [13],
                Phases = [portal with { Error = "stage-unavailable" }], Shifts = [new(0, 99)] },
            [(4, 0)] = Area(4, Map("npc.map", false, [], [new(200, 1, 1)]))
        }, room, street, new(0, [], []), [new(3, 0, new(0, 0, 10, 10), 10, 10)], new Dictionary<uint, uint[]> { [100] = [200] });
    }

    static void Rules()
    {
        var towns = Towns();
        var character = new Character(1, 0, "Rules", 7, towns.Room, towns.InitialReturn) { Level = 20, CompletedQuests = [10, 11] };
        var request = new AreaRequest(3, 0, ushort.MaxValue, 0, 5, 1, 0, 5);
        string original = JsonSerializer.Serialize(character);
        for (int kind = 0; kind <= byte.MaxValue; kind++)
        {
            var plan = TownTravel.Plan(character, request with { PathKind = (byte)kind }, towns, Quests);
            TestCheck.That(plan.Accepted == (kind is not (0 or 6)), "完整kind字节保留菜单/房间返回分流：" + kind);
            if (plan.Accepted)
                TestCheck.That(plan.Position == new Location(3, 0, ushort.MaxValue, 0, 7) && plan.Return == new Location(3, 0, 10, 10),
                    "切图保留u16请求落点和方向，返回地区独立解析");
            else
                TestCheck.That(plan.Reason == "room-return-mismatch" && plan.Position is null, "房间返回拒绝错误地区且无可提交目标");
        }
        TestCheck.That(TownTravel.Plan(character, request with { PreviousTown = 257 }, towns, Quests).Reason == "source-area-mismatch",
            "源区域按完整u16比较，不截断为byte");
        TestCheck.That(TownTravel.Plan(character, request with { Town = 255 }, towns, Quests).Reason == "target-not-in-pvf", "未知目的地拒绝");
        TestCheck.That(TownTravel.Plan(character with { Level = 9 }, request, towns, Quests).Reason == "target-level-required", "保留等级准入");
        TestCheck.That(TownTravel.Plan(character, request, towns, new HashSet<int> { 10 }).Reason == "target-quest-required", "保留OR任务准入");
        TestCheck.That(TownTravel.Plan(character, request, towns, new HashSet<int> { 11 }).Reason == "target-quest-required", "保留AND任务准入");
        TestCheck.That(TownTravel.Plan(character, request, towns, new HashSet<int> { 10, 11, 13 }).Reason == "target-excluded-by-quest", "保留排除任务");
        TestCheck.That(TownTravel.Plan(character, request, towns, new HashSet<int> { 10, 11, 99 }).Reason == "stage-unavailable", "使用当前阶段地图");
        var npc = TownTravel.Plan(character, request with { Town = 4, TargetNpc = 200 }, towns, Quests);
        TestCheck.That(npc.Accepted && npc.Position == new Location(4, 0, ushort.MaxValue, 0, 7) && npc.Return == towns.InitialReturn,
            "非零NPC优先使用NPC路线，保留无房间地区的返回点");
        TestCheck.That(TownTravel.Plan(character, request with { Town = 4, TargetNpc = 999 }, towns, Quests).Reason == "npc-route-not-in-pvf", "未知NPC不转作菜单传送");
        var exit = TownTravel.LeaveRoom(character with { Return = null }, towns, Quests);
        TestCheck.That(exit.Accepted && exit.Position == towns.InitialReturn && exit.Return == towns.InitialReturn, "空body出房解析默认门口");
        TestCheck.That(TownTravel.LeaveRoom(character with { Return = new(255, 0, 0, 0) }, towns, Quests).Reason == "return-gate-unavailable", "无返回门口拒绝");
        var outside = character with { Position = towns.InitialReturn with { Direction = 3 } };
        TestCheck.That(TownTravel.LeaveRoom(outside, towns, Quests).Reason == "not-in-ready-room", "非房间不执行出房");
        var enter = TownTravel.Plan(outside, new(1, 0, 99, 98, 5, 2, 0, 0), towns, Quests);
        TestCheck.That(enter.Accepted && enter.Position == new Location(1, 0, 99, 98, 3) && enter.Return == new Location(2, 0, 10, 10, 3),
            "入房保留请求落点并保存来源地区门口");
        TestCheck.That(TownTravel.Walk(character, new(100, 100, 0, 0, 0, 0), towns, Quests) == new Location(1, 0, 100, 100, 7), "步行接受矩形边界并保留方向");
        TestCheck.That(TownTravel.Walk(character, new(101, 100, 0, 0, 0, 0), towns, Quests) is null, "步行不借切图规则绕过几何");
        TestCheck.That(JsonSerializer.Serialize(character) == original, "全部领域规划保持输入角色不变");
    }

    static void Selection()
    {
        using var f = new Fixture();
        long survivor = f.Original.Inventory.Items.Single(item => item.Core.ExpireTime == 0).Uid;
        f.Session.Select(0, Limits);
        var selected = f.Session.Selected!;
        TestCheck.That(selected.Position == f.Towns.Room && selected.Return == f.Towns.InitialReturn && selected.Revision == 3,
            "选角按出生、期限、金币上限顺序提交实际快照");
        TestCheck.That(!f.Session.Ready && f.Session.Phase == SessionPhase.Unselected && f.Session.SelectionEpoch == 1, "准备完成才安装Selected但尚未Ready");
        TestCheck.That(selected.Inventory.Items.Single().Uid == survivor && selected.Inventory.Gold == 123 && selected.Inventory.GoldCarryLimit == 500,
            "选角清理背包/快捷栏/穿戴期限和锁，保留未到期UID与余额");
        TestCheck.That(Equal(selected, f.Read()) && selected.CompletedQuests.SequenceEqual(f.Original.CompletedQuests), "选角安装SQLite实际结果并保留进度");
        TestCheck.That(f.Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired'") == 3, "期限删除同事务记录三个实际实例");
        f.Session.ReturnToRoster();
        f.Session.Select(0, Limits);
        TestCheck.That(f.Session.Selected!.Revision == selected.Revision && Equal(f.Session.Selected, selected), "重复选角无变化不重写、不重复赠送或清理");
        f.Session.MarkReady();
        TestCheck.That(f.Session.Travel(new(3, 0, 77, 78, 5, 1, 0, 5)), "选角安装的完成任务集参与后续菜单准入");
        f.Session.UpdateProgress(new(CharacterProgressField.QuestRemove, f.Owner.Account, selected.Name, 10, 0));
        f.Session.ReturnToRoster();
        f.Session.Select(0, Limits);
        f.Session.MarkReady();
        TestCheck.That(!f.Session.Travel(new(3, 0, 77, 78, 5, 1, 0, 5)) && f.Session.TravelReason == "target-quest-required",
            "重选后采用新的完成任务快照");
    }

    static void SelectionFailures()
    {
        foreach (string stage in new[] { "spawn", "expiry", "limit" })
        {
            using var f = new Fixture();
            string trigger = stage switch
            {
                "spawn" => "BEFORE UPDATE ON characters",
                "expiry" => "BEFORE INSERT ON inventory_audit_log WHEN NEW.action_name='item_expired'",
                _ => "BEFORE UPDATE ON characters WHEN NEW.gold_carry_limit>OLD.gold_carry_limit"
            };
            f.Execute("CREATE TRIGGER fail_selection " + trigger + " BEGIN SELECT RAISE(ABORT,'selection failure'); END");
            Reject<SqliteException>(() => f.Session.Select(0, Limits), "选角准备阶段失败：" + stage);
            TestCheck.That(f.Session.Selected is null && !f.Session.Ready && f.Session.SelectionEpoch == 0, "准备失败不安装角色和epoch：" + stage);
            var saved = f.Read();
            int completed = stage == "spawn" ? 0 : stage == "expiry" ? 1 : 2;
            TestCheck.That(saved.Revision == completed && saved.Inventory.GoldCarryLimit == 0, "失败只回滚本阶段并保留前序提交：" + stage);
            TestCheck.That(saved.Inventory.Items.Length == (stage == "limit" ? 1 : 4) &&
                f.Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired'") == (stage == "limit" ? 3 : 0),
                "期限实例与审计原子回滚：" + stage);
            f.Execute("DROP TRIGGER fail_selection");
            f.Session.Select(0, Limits);
            TestCheck.That(f.Session.Selected!.Revision == 3 && f.Session.Selected.Inventory.Items.Length == 1 &&
                f.Scalar("SELECT count(*) FROM inventory_audit_log WHERE action_name='item_expired'") == 3, "重新装载后只补未完成阶段：" + stage);
        }
        using var stale = new Fixture();
        using var replacement = stale.Owners.Acquire(stale.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => stale.Session.Select(0, Limits), "旧owner选角拒绝");
        TestCheck.That(stale.Session.Selected is null && stale.Repository.LoadCharacter(replacement, 0).Revision == 0, "旧owner不修改选角数据");
    }

    static void TravelCommits()
    {
        using var f = new Fixture();
        TestCheck.That(!f.Session.LeaveRoom(out var notReady) && notReady is null && f.Session.TravelReason == "not-in-ready-room", "未选角出房无回执");
        f.Session.Select(0, Limits);
        TestCheck.That(!f.Session.Travel(new(2, 0, 10, 10, 5, 1, 0, 0), out _), "入城前不允许切图");
        f.Session.MarkReady();
        var initial = f.Session.Selected!;
        TestCheck.That(f.Session.Move(new(initial.Position.X, initial.Position.Y, 0, 0, 0, 0)) && ReferenceEquals(initial, f.Session.Selected), "重复步行不写库");
        f.Execute("CREATE TRIGGER fail_travel BEFORE UPDATE ON characters BEGIN SELECT RAISE(ABORT,'travel failure'); END");
        Reject<SqliteException>(() => f.Session.LeaveRoom(out _), "切图SQL失败不伪造回执");
        TestCheck.That(ReferenceEquals(initial, f.Session.Selected) && Equal(initial, f.Read()), "切图失败保留内存和持久位置");
        f.Execute("DROP TRIGGER fail_travel");
        TestCheck.That(f.Session.LeaveRoom(out var receipt) && receipt is not null, "切图成功提供提交回执");
        TestCheck.That(ReferenceEquals(receipt!.Character, f.Session.Selected) && Equal(receipt.Character, f.Read()) &&
            receipt.Generation == f.Owner.Generation && receipt.SelectionEpoch == f.Session.SelectionEpoch && f.Session.CanPublishTravel(receipt),
            "旅行回执绑定真实提交和当前会话");
        f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, initial.Name, 21, 0));
        TestCheck.That(f.Session.Selected!.Revision == receipt.Character.Revision, "GM未增加revision");
        Reject<InvalidOperationException>(() => f.Session.CanPublishTravel(receipt), "同选角内过期切图必须终止连接，不能静默继续");
        f.Session.ReturnToRoster();
        f.Session.Select(0, Limits);
        f.Session.MarkReady();
        TestCheck.That(!f.Session.CanPublishTravel(receipt), "另一选角epoch跳过旧旅行发布");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Session.CanPublishTravel(receipt), "旧owner不得发布旅行");
    }

    static bool Equal(Character left, Character right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-town-selection-" + Guid.NewGuid().ToString("N"));
        public TownCatalog Towns { get; } = TownSelectionTests.Towns();
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public GameSession Session { get; }
        public Character Original { get; }
        string PathName => Path.Combine(root, "game.db");

        public Fixture()
        {
            Directory.CreateDirectory(root);
            Owner = Owners.Acquire("selection", () => { });
            Repository = new(PathName);
            InventoryItem Item(InventoryListType list, short slot, int id, ItemKind kind, int expiry, byte lockId = 0) =>
                new(0, new(list, slot), new(id, kind, 1) { ExpireTime = expiry, SortLockFlag = 1, EquipmentLockId = lockId });
            Original = Repository.Create(Owner, new("Selection", new(2, 0, 88, 88, 3), null)
            {
                Level = 20, CompletedQuests = [10, 11],
                Inventory = new(ImmutableArray.Create(Item(InventoryListType.Main, 9, 101, ItemKind.Equipment, 1),
                    Item(InventoryListType.Main, 3, 102, ItemKind.Material, 1), Item(InventoryListType.Equipment, 12, 103, ItemKind.Equipment, 1, 1),
                    Item(InventoryListType.Main, 10, 104, ItemKind.Equipment, 0))) { Gold = 123 }
            });
            Session = new(Repository, Towns, Owner);
        }

        public Character Read() => Repository.LoadCharacter(Owner, 0);
        public long Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + PathName + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return (long)command.ExecuteScalar()!;
        }
        public void Execute(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + PathName + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
