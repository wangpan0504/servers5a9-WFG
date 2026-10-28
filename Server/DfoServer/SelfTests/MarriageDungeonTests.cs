using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Dungeons;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MarriageDungeonTests
{
    public static void RunCore()
    {
        int checks = Count;
        var definition = Definition();
        var record = Relation(1, 2);
        var admission = MarriageDungeonAdmission.Prepare(definition, record, [1, 2], 0, 2, false);
        That(admission is { Ticket: { ItemId: 10002609, Count: 3, Third: 1 } }, "夫妻副本保留资源票数三与消耗标记");
        foreach (ushort[] members in new ushort[][] { [1], [1, 1], [1, 3], [1, 2, 3] })
            That(MarriageDungeonAdmission.Prepare(definition, record, members, 0, 2, false) is null,
                "夫妻副本只允许同一婚姻的恰好两名不同角色");
        That(MarriageDungeonAdmission.Prepare(definition, record with { State = 3 }, [1, 2], 0, 2, false) is null &&
            MarriageDungeonAdmission.Prepare(definition, record, [1, 2], 1, 2, false) is null &&
            MarriageDungeonAdmission.Prepare(definition, record, [1, 2], 0, 0, false) is null &&
            MarriageDungeonAdmission.Prepare(definition, record, [1, 2], 0, 2, true) is null,
            "未完婚、任务入口、错误指定难度和特殊模式不能借夫妻副本入场");
        That(!admission!.Matches(record with { Revision = 2 }, [1, 2]) && !admission.Matches(record with { State = 0 }, [1, 2]),
            "冻结资格在关系版本改变或离婚后失效");
        That(TownCatalog.HasMarriageDungeonEntryRole("[role]\n`[item shop]` 1208 `[enter wedding dungeon]` 3541\n[/role]") &&
            !TownCatalog.HasMarriageDungeonEntryRole("[role]\n`[enter wedding dungeon]` 3542\n[/role]") &&
            !TownCatalog.HasMarriageDungeonEntryRole("[role]\n`[enter wedding dungeon]` 3541 `[enter wedding dungeon]` 3541\n[/role]"),
            "夫妻副本入口按唯一NPC角色与目标3541识别，错目标和重复角色拒绝");
        PairTransaction();
        SingleRoom();
        Console.WriteLine($"PASS {Count - checks} marriage dungeon core assertions");
    }

    static void SingleRoom()
    {
        var point = new DungeonPoint(0, 0);
        var maze = new DungeonMaze(0, 1, 1, "BB", [point], [point], null, []);
        var definition = Definition();
        const string path = "map/chn_wedding_couple_dungeon/chn_wedding_couple_01.map";
        var entry = new DungeonMapPoolEntry(123, path, [3541], "[boss]", 2);
        var resolved = new DungeonMapPool([entry]).Resolve(definition, maze, _ => 0);
        That(resolved is { WeddingCoupleRoom.Id: 123, MapSpecification.Count: 1 } &&
            resolved.StaticTopology is { Supported: true, Rooms: [{ ExitMask: 0 }], Links.Count: 0 },
            "夫妻BB只装配真实反向归属唯一Boss图，单房不生成越界出口");
        foreach (var pool in new DungeonMapPoolEntry[][] { [], [entry with { Dungeons = [3542] }],
            [entry with { Kind = "[normal]" }], [entry with { Layouts = 4 }], [entry, entry with { Id = 124 }],
            [entry with { Path = "map/unrelated.map" }], [entry with { Dungeons = [3541, 3542] }] })
            That(new DungeonMapPool([.. pool]).Resolve(definition, maze, _ => 0).WeddingCoupleRoom is null,
                "夫妻图缺失、错归属、错类型、错出口、歧义及其它资源均不借单房豁免");
        That(!maze.StaticTopology.Supported &&
            new DungeonMapPool([entry]).Resolve(definition, maze with { Width = 2 }, _ => 0).WeddingCoupleRoom is null,
            "原始未装配BB与畸形迷宫不能直接放行");
    }

    public static void RunResources(DungeonCatalog catalog, TownCatalog? towns = null)
    {
        var definition = catalog.Get(3541);
        That(MarriageDungeonAdmission.ResourceMatches(definition), "实际夫妻副本类型、双人上限、禁练习与无普通经济标记完整");
        if (towns is not null)
            That(towns.MarriageDungeonEntryNpcs.Contains(20013) && towns.HasMarriageDungeonEntrance(new(39, 0, 2306, 156), new HashSet<int>()) &&
                !towns.HasMarriageDungeonEntrance(new(101, 0, 474, 234), new HashSet<int>()),
                "实际Father NPC提供夫妻副本入口，婚房不能冒充NPC入口场景");
        var actor = new Character(1, 0, "Couple", 0, new(39, 0, 400, 300), null) { Level = 30 };
        That(DungeonEntryPlan.TryCreate(catalog, actor, 3541, 0, 2, out var plan, out _, draw: _ => 0) &&
            plan is { EntryItem: { ItemId: 10002609, Count: 3, Third: 1 }, DisableClearReward: true,
                DisableMonsterAndClearExperience: true, UsesFatigue: false, HasFreeCombatConsumption: false, ExperienceWeight: 0 },
            "实际3541复用正式地图计划，扣三张票、无疲劳无普通经验无翻牌，战斗消耗仍真实");
    }

    static void PairTransaction()
    {
        string root = Path.Combine(Path.GetTempPath(), "ServerS5A9-marriage-dungeon-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var auth = new AuthenticationService(new AuthenticationRepository(path));
            auth.Create("couple-entry-a", "fixture-password"); auth.Create("couple-entry-b", "fixture-password");
            var owners = new AccountOwners();
            using var firstOwner = owners.Acquire("couple-entry-a", () => { });
            using var secondOwner = owners.Acquire("couple-entry-b", () => { });
            Character Create(AccountOwners.Lease owner, string name) => repository.Create(owner,
                new NewCharacter(name, new(39, 0, 400, 300), null)
                {
                    DungeonState = CharacterDungeonState.NewOrdinary,
                    Inventory = new([new(0, new(InventoryListType.Main, 129), new(10002609, ItemKind.Material, 6))])
                });
            var first = Create(firstOwner, "CoupleA"); var second = Create(secondOwner, "CoupleB");
            var record = Relation(first.Id, second.Id);
            repository.CommitMarriageTogether(firstOwner, secondOwner, first, second,
                new(first.Inventory, first.Inventory, "fixture"), new(second.Inventory, second.Inventory, "fixture"), null, record);
            first = repository.LoadCharacter(firstOwner, 0)!; second = repository.LoadCharacter(secondOwner, 0)!;
            var admission = MarriageDungeonAdmission.Prepare(Definition(), record, [first.Id, second.Id], 0, 2, false)!;
            Guid instance = Guid.NewGuid();
            PartyDungeonReservation Reserve(AccountOwners.Lease owner, Character actor) => new(owner, actor, 20,
                new(InventoryDeleteService.Plan(actor.Inventory, new(InventoryListType.Main, 129), 10002609, 3).Plan,
                    0, new(actor.Id, 0, actor.DungeonState!.LastAttempt + 1, instance, 1), 1)
                { Marriage = admission, DungeonId = 3541 });
            var reservations = new[] { Reserve(firstOwner, first), Reserve(secondOwner, second) };
            void Sql(string text)
            {
                using var db = new SqliteConnection("Data Source=" + path); db.Open();
                using var command = db.CreateCommand(); command.CommandText = text; command.ExecuteNonQuery();
            }
            Sql($"CREATE TRIGGER fail_couple_entry BEFORE UPDATE ON character_dungeon_state WHEN NEW.character_id={second.Id} BEGIN SELECT RAISE(ABORT,'couple entry rollback'); END");
            Reject<SqliteException>(() => repository.CommitPartyDungeonEntry(reservations), "第二成员入场写入失败");
            That(repository.LoadCharacter(firstOwner, 0)!.Inventory.Items.Single().Core.Count == 6 &&
                repository.LoadCharacter(secondOwner, 0)!.Inventory.Items.Single().Core.Count == 6 &&
                repository.LoadCharacter(firstOwner, 0)!.DungeonState!.LastAttempt == 0,
                "夫妻入场末段失败回滚双方三张票与尝试号");
            Sql("DROP TRIGGER fail_couple_entry");
            Reject<InvalidOperationException>(() => repository.CommitDungeonEntry(firstOwner, first, 20, reservations[0].Charge),
                "单人仓储入口不能提交夫妻副本费用");
            var committed = repository.CommitPartyDungeonEntry(reservations);
            That(committed.All(row => row.Character.Inventory.Items.Single().Core.Count == 3 && row.Character.DungeonState!.LastAttempt == 1),
                "双方门票各扣资源要求的三张并原子推进各自尝试号");
            Reject<InvalidOperationException>(() => repository.CommitPartyDungeonEntry(reservations), "夫妻副本旧入场计划不可重放");
            first = committed[0].Character; second = committed[1].Character;
            repository.CommitMarriageSingle(firstOwner, first, new(first.Inventory, first.Inventory, "fixture"), record,
                record with { Revision = 2 });
            Reject<InvalidOperationException>(() => repository.CommitPartyDungeonEntry([Reserve(firstOwner, first), Reserve(secondOwner, second)]),
                "角色资产未变但关系版本变化仍拒绝旧双人资格");
            That(repository.LoadCharacter(firstOwner, 0)!.Inventory.Items.Single().Core.Count == 3 &&
                repository.LoadCharacter(secondOwner, 0)!.Inventory.Items.Single().Core.Count == 3, "关系冲突不再次扣任一方门票");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    static MarriageRecord Relation(ushort first, ushort second) => new(Guid.NewGuid(), 7, 1, first, second, 4)
    { Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty.Add(first, new()).Add(second, new()), CreatedAt = 1000 };

    static DungeonDefinition Definition() => new(3541, "dungeon/china/chn_wedding_couple.dgn", 30, 50, [],
        [new("required item", [new(0, 10002609), new(0, 3), new(0, 1)]), new("no fatigue", []),
            new("disable exp", []), new("disable clear reward", []), new("limit party count", [new(0, 2)]),
            new("prohibit practice", [new(0, 1)]), new("designate dungeon difficulty", [new(0, 2)])])
    { DungeonType = "wedding couple", DisableFatigueConsumption = true };
}
