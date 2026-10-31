using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class CharacterRenameCardTests
{
    public static void RunCore()
    {
        int before = Count;
        That(CharacterRequests.TryNameHistory([0x34, 0x12, 5], out var historyTarget) && historyTarget == 0x1234 &&
            !CharacterRequests.TryNameHistory([0x34, 0x12, 3], out _), "CMD8模式5请求曾用名，模式3仍为角色资料");
        Reject<InvalidDataException>(() => CharacterRequests.TryNameHistory([1, 0, 5, 0], out _), "曾用名不吞额外尾部");
        Bytes(CharacterNameHistoryPackets.List([new("旧名", 0x12345678)], 2),
            [1, 4, 0, 0, 0, 0xBE, 0xC9, 0xC3, 0xFB, 2, 0x78, 0x56, 0x34, 0x12], "736原生行数、GBK名字、服索引及Unix时间");
        Bytes(CharacterNameHistoryPackets.List([], 0), [0], "无真实历史也清空旧窗口内容");
        That(CharacterRenameRequests.Use([65, 0]) == 65, "509读取原生Main槽u16");
        var body = Body(2, "旧名字", "新名字");
        That(CharacterRenameRequests.Complete(body) == new CharacterRenameRequest(2, "旧名字", "新名字", 1),
            "135依次保留名册行、旧名、新名及原因");
        foreach (byte[] invalid in new byte[][] { [], [1], body[..^1], [.. body, 0] })
            Reject<InvalidDataException>(() => CharacterRenameRequests.Complete(invalid), "改名正文截断和多余尾部拒绝");
        foreach (byte[] invalid in new byte[][] { [], [1], [1, 0, 0] })
            Reject<InvalidDataException>(() => CharacterRenameRequests.Use(invalid), "更名卡不猜测额外容器或模板");
        string root = Path.Combine(Path.GetTempPath(), "S5A9-rename-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "game.db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("rename-test", () => { });
            using var other = owners.Acquire("rename-other", () => { });
            var role = repository.Create(owner, new NewCharacter("OldName", new(38, 1, 450, 234), null)
            { Inventory = new([new(0, new(InventoryListType.Main, 65), new(2660698, ItemKind.Consumable, 3))]) });
            repository.Create(other, new("TakenName", role.Position, null));
            string initial = Snapshot();
            Sql("DROP TABLE character_rename_pending; DROP TABLE character_name_history; PRAGMA user_version=155");
            repository = new(path);
            That(Snapshot() == initial && !repository.ListRoster(owner).Single().RenamePending && repository.ReadNameHistory(owner, role.Id).IsEmpty,
                "v155升级建立空资格和曾用名表，不伪造历史，保留角色与资产");
            Sql("CREATE TRIGGER rename_activate_abort BEFORE INSERT ON character_rename_pending BEGIN SELECT RAISE(ABORT,'activate rollback'); END");
            Reject<SqliteException>(() => repository.ActivateRenameCard(owner, role, Plan(role)), "资格写入晚期失败");
            That(Snapshot() == initial, "扣卡与revision在资格SQL失败后全部回滚");
            Sql("DROP TRIGGER rename_activate_abort");
            Reject<InvalidDataException>(() => repository.ActivateRenameCard(other, role, Plan(role)), "禁止跨账号扣更名卡");
            var unexpected = Plan(role);
            Reject<InvalidDataException>(() => repository.ActivateRenameCard(owner, role,
                unexpected.WithAfter(unexpected.After with { Expansion = 16 })), "资格计划不能捎带扩容等无关资产变化");
            role = repository.ActivateRenameCard(owner, role, Plan(role))!;
            That(role.Inventory.Items.Single().Core.Count == 2 && repository.ListRoster(owner).Single().RenamePending,
                "激活恰扣一张并持久化待改名资格");
            string pending = Snapshot();
            That(repository.ActivateRenameCard(owner, role, Plan(role)) is null && Snapshot() == pending,
                "已有待改名资格不能重扣第二张卡");
            That(repository.CompleteRenameCard(owner, role, "TakenName") == CharacterRenameFailure.Duplicate &&
                repository.CompleteRenameCard(owner, role, "非法!") == CharacterRenameFailure.InvalidName && Snapshot() == pending,
                "全局重名和非法名称保留原名与资格");
            Sql("CREATE TRIGGER rename_complete_abort BEFORE DELETE ON character_rename_pending BEGIN SELECT RAISE(ABORT,'complete rollback'); END");
            Reject<SqliteException>(() => repository.CompleteRenameCard(owner, role, "NewName"), "清资格晚期SQL失败");
            That(Snapshot() == pending, "清资格失败回滚新名、revision与全部资格");
            That(repository.ReadNameHistory(owner, role.Id).IsEmpty, "失败改名不能留下曾用名");
            Sql("DROP TRIGGER rename_complete_abort");
            var stale = role;
            role = repository.Save(owner, role, role.Position, role.Return);
            That(repository.CompleteRenameCard(owner, stale, "NewName") == CharacterRenameFailure.Unavailable,
                "旧revision不能消费最新资格");
            That(repository.CompleteRenameCard(owner, role, "NewName") == CharacterRenameFailure.None,
                "持久资格允许一次正式改名");
            var saved = repository.LoadCharacter(owner, role.Slot);
            That(saved.Name == "NewName" && saved.Id == role.Id && JsonSerializer.Serialize(saved.Inventory) == JsonSerializer.Serialize(role.Inventory) &&
                !repository.ListRoster(owner).Single().RenamePending, "改名保留角色ID、槽位及原资产并清资格");
            That(repository.ReadNameHistory(owner, role.Id) is [{ Name: "OldName", ChangedAt: > 0 }] &&
                repository.ReadNameHistory(other, role.Id).IsEmpty, "更名卡保存真实旧名并按账号隔离查询");
            That(repository.CompleteRenameCard(owner, saved, "Again") == CharacterRenameFailure.Unavailable,
                "无资格不能再次改名");
            saved = repository.Rename(owner, saved, "中文名");
            var history = repository.ReadNameHistory(owner, role.Id);
            That(history.Select(row => row.Name).SequenceEqual(["NewName", "OldName"]), "GM改名同样记历史，按revision逆序而非秒级时间排序");
            saved = repository.Rename(owner, saved, "中文名");
            That(repository.ReadNameHistory(owner, role.Id).SequenceEqual(history), "同名GM操作不制造虚假历史");
            Sql("CREATE TRIGGER rename_history_abort BEFORE INSERT ON character_name_history BEGIN SELECT RAISE(ABORT,'history rollback'); END");
            Reject<SqliteException>(() => repository.Rename(owner, saved, "Rollback"), "曾用名写入失败须回滚GM改名");
            That(repository.LoadCharacter(owner, saved.Slot).Name == "中文名" && repository.ReadNameHistory(owner, role.Id).SequenceEqual(history), "历史与名称原子提交");
            Sql("DROP TRIGGER rename_history_abort");
            role = repository.ActivateRenameCard(owner, saved, Plan(saved))!;
            repository = new(path);
            That(repository.ListRoster(owner).Single().RenamePending && repository.ReadNameHistory(owner, role.Id).SequenceEqual(history), "重开仓储恢复待改名资格与历史");
            repository.Delete(owner, repository.LoadDeletionTarget(owner, role.Slot));
            That(!repository.ListRoster(owner).Any() && repository.ReadNameHistory(owner, role.Id).IsEmpty, "删除角色一并删除待改名资格和历史，不留外键孤儿");
            VerifySession();
            Sql("DROP TABLE character_rename_pending; CREATE TABLE character_rename_pending(unknown INTEGER)");
            Reject<InvalidDataException>(() => _ = new CharacterRepository(path), "启动拒绝同名损坏资格表");

            string Snapshot() => JsonSerializer.Serialize(repository.LoadCharacter(owner, role.Slot)) +
                JsonSerializer.Serialize(repository.ListRoster(owner));
            void Sql(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path }.ToString());
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }

            void VerifySession()
            {
                var point = new Location(10, 4, 10, 10);
                var map = new TownMap("rename-fixture.map", [new(new(0, 0, 100, 100), [], [])], [], false, []);
                var towns = new TownCatalog(new() { [(10, 4)] = new(10, 4, 1, [], [], [], point, map, [], []) },
                    point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
                using var first = owners.Acquire("rename-session", () => { });
                var candidate = repository.Create(first, new NewCharacter("SessionOld", point, point)
                { Inventory = new([new(0, new(InventoryListType.Main, 65), new(2660698, ItemKind.Consumable, 1))]) });
                repository.ActivateRenameCard(first, candidate, Plan(candidate));
                var session = new GameSession(repository, towns, first);
                That(session.CompleteCharacterRename(0, "SessionOld", "SessionNew", 1) == CharacterRenameFailure.Unavailable,
                    "未发布名册不得用任意索引消费资格");
                var original = session.PrepareRoster();
                session.MarkRosterPublished(original);
                That(session.RenameCardSelectionBlocked(0) &&
                    session.CompleteCharacterRename(0, "SessionOld", "SessionNew", 2) == CharacterRenameFailure.Unavailable,
                    "资格阻止直接选角，其他更名原因不借用卡资格");
                using var replacement = owners.Acquire(first.Account, () => { });
                Reject<InvalidOperationException>(() => session.CompleteCharacterRename(0, "SessionOld", "SessionNew", 1),
                    "接管后旧owner不得提交改名");
                var fresh = new GameSession(repository, towns, replacement);
                var published = fresh.PrepareRoster();
                fresh.MarkRosterPublished(published);
                That(fresh.CompleteCharacterRename(0, "SessionOld", "SessionNew", 1) == CharacterRenameFailure.None &&
                    !fresh.CanPublishRoster(published), "新owner从持久资格恢复，成功改名令旧名册epoch失效");
                That(fresh.CompleteCharacterRename(0, "SessionOld", "SessionAgain", 1) == CharacterRenameFailure.Unavailable,
                    "旧行重放不能重新改名");
                fresh.MarkRosterPublished(fresh.PrepareRoster());
                That(!fresh.RenameCardSelectionBlocked(0), "刷新名册只解除已成功改名角色的选角限制");
                fresh.SelectFromRosterIndex(0);
                That(fresh.Selected?.Name == "SessionNew" &&
                    fresh.CompleteCharacterRename(0, "SessionNew", "TownRename", 1) == CharacterRenameFailure.Unavailable,
                    "改名后选中原角色，已选角阶段不能再执行135");
                repository.Delete(replacement, repository.LoadDeletionTarget(replacement, candidate.Slot));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
        Console.WriteLine($"PASS {Count - before} 角色更名卡解析、资格事务及迁移断言");
    }

    static InventoryPlan Plan(Character role)
    {
        var deleted = InventoryDeleteService.Plan(role.Inventory, new(InventoryListType.Main, 65), 2660698, 1).Plan!;
        return new(role.Inventory, deleted.After, "character_rename_card", new(role));
    }

    internal static byte[] Body(byte index, string oldName, string newName, byte reason = 1) =>
        [index, .. Name(oldName), .. Name(newName), reason];
}
