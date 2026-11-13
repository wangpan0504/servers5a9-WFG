using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class NpcFavorMaxGmTests
{
    public static void RunCore()
    {
        int start = Count;
        var request = DevelopmentRequest.Parse("account other npc-favor-max Favor", "unused");
        That(request.Writes && request.Action == DevelopmentAction.NpcFavorMax && request.Account == "other", "好感度GM写命令保留明确账号");
        Reject<InvalidDataException>(() => DevelopmentRequest.Parse("npc-favor-max Favor extra", "other"), "额外参数拒绝");
        var catalog = new NpcFavorCatalog(id => id is 17 or 18 ? new(17, 40000) : id == 20 ? new(20, 50000) : null, ids: [17,18,20,99]);
        var targets = catalog.All();
        That(targets.Length == 2 && targets[0].OwnerId == 17 && targets[1].Maximum == 50000, "目录去重共享形态，排除不可培养NPC且保留不同上限");
        var previous = new NpcFavorPoint(17, 100) { GiftDay = 700000, GiftCount = 5, Colors = 0x04030201 };
        var role = new Character(1, 0, "Favor", 0, new(1,0,10,10), null)
            { Inventory = new([]) { NpcFavor = [previous, new(99, 3)] } };
        var plan = InventoryNpcFavorMaxService.Plan(role, targets);
        That(plan.Accepted && plan.Plan!.After.NpcFavor.SequenceEqual([previous with { Points = 40000 }, new(20,50000), new(99,3)]) &&
            role.Inventory.NpcFavor[0] == previous, "只改有效owner点数，新增缺项并保留赠礼、染色、未知旧行及输入");
        var again = InventoryNpcFavorMaxService.Plan(role with { Inventory = plan.Plan!.After }, targets);
        That(ReferenceEquals(again.Plan!.Before, again.Plan.After), "重复拉满不生成库存变化");
        That(!InventoryNpcFavorMaxService.Plan(role, []).Accepted &&
            !InventoryNpcFavorMaxService.Plan(role, [new(17,40000),new(17,50000)]).Accepted, "空资源或重复owner拒绝，不能部分成功");
        var full = role with { Inventory = new([]) { NpcFavor = [.. Enumerable.Range(1,255).Select(id => new NpcFavorPoint(id,1))] } };
        That(!InventoryNpcFavorMaxService.Plan(full, [new(300,40000)]).Accepted, "合并后超255行整批拒绝");
        string path = Path.Combine(Path.GetTempPath(), "npc-favor-max-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var repository = new CharacterRepository(path);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("favor-max", static () => { });
            var before = repository.Create(owner, new NewCharacter("Favor", new(1,0,10,10), new(1,0,10,10)));
            var result = InventoryNpcFavorMaxService.Plan(before, targets);
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            db.Open();
            using var sql = db.CreateCommand();
            sql.CommandText = "CREATE TRIGGER fail_favor_max AFTER UPDATE OF npc_favor ON characters BEGIN SELECT RAISE(ABORT,'favor rollback'); END";
            sql.ExecuteNonQuery();
            Reject<SqliteException>(() => InventoryService.Commit(repository, owner, before, result), "GM全量晚失败");
            That(JsonSerializer.Serialize(repository.LoadCharacter(owner,0)) == JsonSerializer.Serialize(before), "失败回滚全部点数及revision");
            sql.CommandText = "DROP TRIGGER fail_favor_max"; sql.ExecuteNonQuery();
            var committed = InventoryService.Commit(repository, owner, before, result);
            That(committed.Inventory.NpcFavor.SequenceEqual([new NpcFavorPoint(17,40000),new(20,50000)]) &&
                repository.LoadCharacter(owner,0).Revision == committed.Revision, "真实库存事务落库，重新加载保留全量好感");
            var unchanged = InventoryService.Commit(repository, owner, committed, InventoryNpcFavorMaxService.Plan(committed, targets));
            That(unchanged.Revision == committed.Revision, "重复操作不推进角色版本");
            Reject<InvalidOperationException>(() => InventoryService.Commit(repository, owner, before, result), "旧版本不能覆盖提交");
        }
        finally { foreach (string file in new[] { path,path+"-wal",path+"-shm" }) if (File.Exists(file)) TestFiles.DeleteFile(file); }
        Console.WriteLine($"PASS {Count-start} NPC favor max GM assertions");
    }
}
