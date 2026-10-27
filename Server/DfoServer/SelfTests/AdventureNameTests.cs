using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Adventure;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class AdventureNameTests
{
    public static void RunCore()
    {
        int checks = Count;
        foreach (string name in new[] { "Group_1", "测试团", new string('测', 16) })
            That(AdventureName.IsValid(name), "冒险团名称保留中文、字母、数字与16字符边界");
        foreach (string name in new[] { "", " ", "A\0B", "<Group>", new string('a', 17), "\uD83D\uDE00" })
            That(!AdventureName.IsValid(name), "空白、控制字符、注入符号及超界名称拒绝");
        That(AdventureRequests.Name([2, 0, 0, 0, 0xD6, 0xD0]) == "中", "命名请求使用CP936字节长度");
        That(AdventureRequests.Rename([1, 0, 0, 0, (byte)'A', 255, 0, 1, 0]) == ("A", (ushort)255, (ushort)1),
            "改名保留完整u16槽位与模式，不将免费模式冒充道具槽");
        Reject<InvalidDataException>(() => AdventureRequests.Name([2, 0, 0, 0, 0xD6]), "命名不吞缺失字节");
        Reject<InvalidDataException>(() => AdventureRequests.Rename([1, 0, 0, 0, (byte)'A', 65, 0]), "改名不吞缺失模式");
        Reject<InvalidDataException>(() => AdventureRequests.Name([1, 0, 0, 0, 0x81]), "命名不替换错误编码");
        Resource();
        ActorName();
        Persistence();
        Migration();
        Console.WriteLine($"PASS {Count - checks} 冒险团命名、重名占用、改名消费与迁移断言");
    }

    static void ActorName()
    {
        var character = new Character(1, 0, "NameActor", 0, new(0, 0, 0, 0), null) { Emotion = 300 };
        byte[] empty = TownPackets.Actor(character);
        byte[] named = TownPackets.Actor(character, adventureName: "トリニティ");
        byte[] field = new byte[17];
        CharacterName.Encoding.GetBytes("トリニティ").CopyTo(field, 0);
        Bytes(named.AsSpan(56, 17), field, "Actor在5B记录头之后的RAW偏移51传入独立CP936昵称，末尾保留NUL");
        Bytes(named.AsSpan(0, 56), empty.AsSpan(0, 56), "昵称不改身份或RAW前部字段");
        Bytes(named.AsSpan(73), empty.AsSpan(73), "昵称不覆盖后续字段、心情、角色名或资产外观");
        byte[] longest = TownPackets.Actor(character, adventureName: new string('A', 16));
        That(longest.AsSpan(56, 16).ToArray().All(value => value == 'A') && longest[72] == 0,
            "16个ASCII字节与终止字节不能覆盖紧邻字段");
        byte[] wide = TownPackets.Actor(character, adventureName: "A" + new string('测', 8));
        That(CharacterName.Encoding.GetString(wide, 56, 15) == "A" + new string('测', 7) && wide[71] == 0 && wide[72] == 0,
            "窄头顶字段按完整字符投影前缀，不拆开CP936双字节或收窄持久名称资格");
    }

    static void Resource()
    {
        var raw = new StackableFile(2000, "stackable/fixture.stk", new("[waste]", [0]), 2, 0, 0, 1, null, [])
        {
            ActionDeclaration = new("[represent account name change]", []), DeclaredUsableJobs = ["[all]"],
            DeclaredActionUsablePlaces = ["[village]", "[dont use assult]"]
        };
        var template = new ItemTemplate(2000, ItemKind.Consumable, 0, 1, 0, 2, 0, 2, 1, "account", ushort.MaxValue, 0, "Ticket", "waste");
        That(AdventureNameTicketDefinition.Parse(raw, template) is { TemplateId: 2000 } ticket && ticket.Allows(1, 15),
            "券资格来自完整动作和使用地点，不按名称、图标或物品ID猜测");
        That(AdventureNameTicketDefinition.Parse(raw with { ActionDeclaration = new("[quest clear]", []) }, template) is null &&
            AdventureNameTicketDefinition.Parse(raw with { ActionDeclaration = new("[represent account name change]", [1]) }, template) is null &&
            AdventureNameTicketDefinition.Parse(raw with { DeclaredActionUsablePlaces = ["[village]"] }, template) is null,
            "其他动作、未确认参数和未闭合场景声明不取得改名资格");
    }

    static void Persistence()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-adventure-names-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var catalog = AdventureCoreTests.SyntheticCatalog();
            var now = DateTimeOffset.UtcNow;
            var service = new AdventureNameService(catalog, id => id == 2000 ? new(2000, 1, 95, ushort.MaxValue) : null);
            var repository = new CharacterRepository(path);
            var authentication = new AuthenticationService(new AuthenticationRepository(path));
            authentication.Create("names", "fixture-password"); authentication.Create("other", "fixture-password");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("names", () => { });
            using var other = owners.Acquire("other", () => { });
            repository.InitializeAdventure(owner, catalog, now); repository.InitializeAdventure(other, catalog, now);
            var initial = repository.ReadAdventure(owner, catalog, now);
            That(!initial.State.NameInitialized && repository.ReadAdventureName(owner) == "",
                "新账号保留独立首次命名资格，不将回退名称投影为已命名昵称");
            var first = service.Prepare(initial, null, "FirstGroup", true, 0, 0, now).Plan!;
            var committed = repository.CommitAdventureName(owner, first, catalog, now);
            That(committed.Accepted && committed.Snapshot!.State is { Name: "FirstGroup", NameInitialized: true, Revision: 1 },
                "首次命名与资格消耗同事务，不选择角色或推进角色状态");
            That(repository.ReadAdventureName(owner) == "FirstGroup" && repository.ReadAdventureName(other) == "",
                "昵称只读查询使用本账号实际提交名称，不加载其他账号或免费账户回退名");
            That(service.Prepare(committed.Snapshot!, null, "Bypass", true, 0, 0, now).Error == 3,
                "已有名称不能用首次命名请求绕过改名券");
            var same = service.Prepare(committed.Snapshot!, null, "FirstGroup", true, 0, 0, now).Plan!;
            using var db = Open(path);
            using (var writer = db.BeginTransaction(deferred: false))
            {
                That(repository.AdventureNameExists(other, "firstgroup") && !repository.AdventureNameExists(owner, "FirstGroup") &&
                    repository.CommitAdventureName(owner, same, catalog, now).Snapshot!.State.Revision == 1 &&
                    repository.ReadAdventureName(owner) == "FirstGroup",
                    "只读重名检查和同名重放可在外部writer下完成，不重复增加revision");
                writer.Rollback();
            }
            var otherBefore = repository.ReadAdventure(other, catalog, now);
            var collision = service.Prepare(otherBefore, null, "FIRSTGROUP", true, 0, 0, now).Plan!;
            That(repository.CommitAdventureName(other, collision, catalog, now) is { Accepted: false, Error: 20 } &&
                !repository.ReadAdventure(other, catalog, now).State.NameInitialized, "提交时复验跨账号重名，不能只信预检查");
            var actor = repository.Create(owner, new("NameActor", new(1, 0, 10, 10), null)
            {
                Inventory = new([new(0, new(InventoryListType.Main, 65), new(2000, ItemKind.Consumable, 2))])
            });
            var before = repository.ReadAdventure(owner, catalog, now);
            foreach (var invalid in new[] { actor with { Inventory = InventorySnapshot.Empty }, actor with
                { Inventory = actor.Inventory with { Items = actor.Inventory.Items.Select(item => item with
                    { Core = item.Core with { EquipmentLockId = 1 } }).ToImmutableArray() } }, actor with
                { Inventory = actor.Inventory with { Items = actor.Inventory.Items.Select(item => item with
                    { Core = item.Core with { ExpireTime = 1 } }).ToImmutableArray() } } })
                That(service.Prepare(before, invalid, "Renamed", false, 65, 0, now).Plan is null, "缺券、安全锁及已到期实例拒绝");
            That(service.Prepare(before, actor, "Renamed", false, 255, 1, now).Plan is null,
                "免费改名模式不能伪造未授予的权益");
            var rename = service.Prepare(before, actor, "Renamed", false, 65, 0, now).Plan!;
            string original = JsonSerializer.Serialize(actor);
            Execute(db, "CREATE TRIGGER reject_name_audit BEFORE INSERT ON account_adventure_receipts BEGIN SELECT RAISE(ABORT,'late name failure'); END");
            Reject<SqliteException>(() => repository.CommitAdventureName(owner, rename, catalog, now), "名称审计晚期失败回滚券与名称");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner, actor.Slot)) == original &&
                repository.ReadAdventure(owner, catalog, now).State.Name == "FirstGroup", "失败不留下扣券、名称或角色revision");
            Execute(db, "DROP TRIGGER reject_name_audit");
            var renamed = repository.CommitAdventureName(owner, rename, catalog, now);
            That(renamed.Accepted && renamed.Character!.Inventory.Items.Single() is { Core.Count: 1 } retained &&
                retained.Uid == actor.Inventory.Items.Single().Uid && renamed.Snapshot!.State.Name == "Renamed",
                "实际消费一张，剩余券保留完整UID，名称与费用原子提交");
            That(repository.CommitAdventureName(owner, rename, catalog, now) is { Accepted: false }, "旧名称revision不能重复扣券");
            That(new CharacterRepository(path).ReadAdventure(owner, catalog, now).State.Name == "Renamed", "重开恢复已提交名称与首次资格");
            Execute(db, "UPDATE account_adventures SET name='Renamed',name_initialized=1 WHERE account='other'");
            var retainedName = repository.ReadAdventure(owner, catalog, now);
            var retainedPlan = service.Prepare(retainedName, null, "Renamed", true, 0, 0, now).Plan!;
            That(repository.CommitAdventureName(owner, retainedPlan, catalog, now) is { Accepted: true },
                "旧库已有重名不阻断本账号原名的零变化回放，不产生新名称占用");
            authentication.Create("race-a", "fixture-password"); authentication.Create("race-b", "fixture-password");
            using var raceA = owners.Acquire("race-a", () => { });
            using var raceB = owners.Acquire("race-b", () => { });
            repository.InitializeAdventure(raceA, catalog, now); repository.InitializeAdventure(raceB, catalog, now);
            var planA = service.Prepare(repository.ReadAdventure(raceA, catalog, now), null, "RaceGroup", true, 0, 0, now).Plan!;
            var planB = service.Prepare(repository.ReadAdventure(raceB, catalog, now), null, "racegroup", true, 0, 0, now).Plan!;
            var raced = Task.WhenAll(Task.Run(() => repository.CommitAdventureName(raceA, planA, catalog, now)),
                Task.Run(() => repository.CommitAdventureName(raceB, planB, catalog, now))).GetAwaiter().GetResult();
            That(raced.Count(result => result.Accepted) == 1 && raced.Count(result => result.Error == 20) == 1,
                "两个账号同时占用大小写同名时只有一个提交成功，另一账号明确拒绝");
            using var replacement = owners.Acquire("names", () => { });
            Reject<InvalidOperationException>(() => repository.CommitAdventureName(owner, rename, catalog, now), "旧generation不能改写新owner的名称");
        }
        finally { Directory.Delete(root, true); }
    }

    static void Migration()
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-name-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (bool fail in new[] { false, true })
            {
                string path = Path.Combine(root, fail + ".db");
                _ = new CharacterRepository(path);
                new AuthenticationService(new AuthenticationRepository(path)).Create("legacy", "fixture-password");
                using (var db = Open(path))
                {
                    DatabaseSchemaTests.RemoveAdventureNames(db);
                    DatabaseSchemaTests.RewindRaidLifecycle(db);
                    Execute(db, "PRAGMA user_version=37; INSERT INTO account_adventures VALUES('legacy',7,'LegacyGroup',20261011,1,0,'0',0,0,zeroblob(20),202610)");
                    if (fail) Execute(db, "CREATE INDEX account_adventure_name ON characters(name)");
                }
                if (fail)
                {
                    Reject<SqliteException>(() => new CharacterRepository(path), "索引冲突使v38整步回滚");
                    using var db = Open(path);
                    That(Query(db, "PRAGMA user_version") is 37L && Query(db, "SELECT count(*) FROM pragma_table_info('account_adventures') WHERE name='name_initialized'") is 0L,
                        "失败保留旧结构与版本，不留下半份首次命名资格");
                }
                else
                {
                    _ = new CharacterRepository(path);
                    using var db = Open(path);
                    That(Query(db, "SELECT name||':'||revision||':'||name_initialized FROM account_adventures") is "LegacyGroup:7:1",
                        "旧库保留名字和revision，不自动发放免费改名或重置命名资格");
                    _ = new CharacterRepository(path);
                    That(Query(db, "PRAGMA integrity_check") is "ok", "重开后迁移与名称索引完整");
                }
            }
        }
        finally { Directory.Delete(root, true); }
    }

    static SqliteConnection Open(string path) { var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open(); return db; }
    static void Execute(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static object? Query(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
}
