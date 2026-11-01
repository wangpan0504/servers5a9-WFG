using DfoServer.Game.Accounts;
using DfoServer.Game.Currency;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildNpcItemCraftTests
{
    public static void RunCore()
    {
        int before = Count;
        string root = Path.Combine(Path.GetTempPath(), "guild-item-craft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Schema(Path.Combine(root, "schema.db")); Run(Path.Combine(root, "game.db")); }
        finally { TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} guild NPC item craft assertions");
    }

    static void Schema(string path)
    {
        _ = new CharacterRepository(path);
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
        void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
        Sql("DROP TABLE character_guild_npc_item_crafts; PRAGMA user_version=146; CREATE TABLE character_guild_npc_item_crafts(craft_id TEXT)");
        Reject<SqliteException>(() => new CharacterRepository(path), "146升级遇同名损坏生产表拒绝覆盖");
        That(Number("PRAGMA user_version") == 146, "炼金迁移失败不推进版本");
        Sql("DROP TABLE character_guild_npc_item_crafts");
        _ = new CharacterRepository(path);
        That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion && Number("SELECT count(*) FROM character_guild_npc_item_crafts") == 0,
            "146库真实升级新炼金表，不预置生产");
    }

    static void Run(string path)
    {
        const long now = 1700000000;
        var repository = new CharacterRepository(path); var owners = new AccountOwners();
        using var owner = owners.Acquire("craft-master", static () => { });
        var location = new Location(38, 1, 450, 234, 0);
        var character = repository.Create(owner, new("CraftLead", location, location));
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
        void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
        Sql($"""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'炼金测试','craft-test',{character.Id},1);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1);
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES(1,400001065,1,{now+1000});
            UPDATE characters SET gold=1000 WHERE id={character.Id};
            """);
        var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
        var actor = session.CaptureGuildManagement()!; var npcs = GuildNpcTests.Synthetic();
        var definition = new GuildNpcBenefitDefinition(7001, 400001065, 1, false, "crafting item",
            "gold", 100, "none", 0, 60, [490013242, 30]);
        var core = new ItemCore(490013242, ItemKind.Consumable, 30);
        string Key(int key) => key.ToString("x64");
        GuildNpcBenefitCommit Change(int key, uint action = 4, GuildNpcBenefitDefinition? row = null) =>
            repository.ChangeGuildNpcItemCraft(actor, repository.LoadCharacter(owner, character.Slot), npcs,
                row ?? definition, action, Key(key), now, action == 4 ? core : null, current =>
                {
                    var paid = CurrencyService.Spend(current.Inventory, CurrencyKind.Gold, (row ?? definition).FirstAmount);
                    return paid.Accepted ? new(current.Inventory, paid.Plan!.After, "guild_npc_benefit_purchase", new(current)) : null;
                });
        string Snapshot() => string.Join(":", Number("SELECT gold FROM characters WHERE id=" + character.Id),
            Number("SELECT revision FROM characters WHERE id=" + character.Id), Number("SELECT count(*) FROM character_guild_npc_item_crafts"),
            Number("SELECT count(*) FROM guild_npc_receipts"), Number("SELECT count(*) FROM guild_request_claims"));
        string unchanged = Snapshot();
        That(!Change(1, row: definition with { Level = 2 }).Success && Snapshot() == unchanged, "炼金等级不足不扣费、不占请求");
        Sql($"UPDATE guild_npc_progress SET contract_until={now}");
        That(!Change(1).Success && Snapshot() == unchanged, "炼金契约到期不扣费");
        Sql($"UPDATE guild_npc_progress SET contract_until={now+1000}");
        That(Change(1, row: definition with { FirstAmount = 1001 }).Error == 17 && Snapshot() == unchanged, "炼金费用不足无半份生产");
        Sql("CREATE TRIGGER craft_receipt_fail BEFORE INSERT ON guild_npc_receipts BEGIN SELECT RAISE(ABORT,'craft rollback'); END");
        Reject<SqliteException>(() => Change(1), "炼金提交末段回执故障");
        That(Snapshot() == unchanged, "炼金费用、物品核心、倒计时和回执全部回滚");
        Sql("DROP TRIGGER craft_receipt_fail");
        var start = Change(1);
        That(start.Success && start.State!.Individual is [{ BenefitId: 7001, Deadline: now + 60 }] &&
            Number("SELECT gold FROM characters WHERE id=" + character.Id) == 900, "炼金开始真实扣费并恢复个人生产倒计时");
        unchanged = Snapshot();
        That(Change(1).Replayed && Snapshot() == unchanged && !Change(2).Success && Snapshot() == unchanged,
            "炼金重放不重复扣款，同NPC已有生产拒绝另一委托");
        That(repository.CompleteGuildNpcItemCrafts(now + 59).Length == 0 && Number("SELECT count(*) FROM mailbox_messages") == 0,
            "炼金未到期不能提前发产物");
        That(Change(3, 5).Success && Number("SELECT gold FROM characters WHERE id=" + character.Id) == 900,
            "主动取消清生产、不退费用");
        That(Change(4).Success && Change(3, 5).Replayed &&
            Number("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") == 1,
            "旧取消请求重放不能取消后续新生产");
        Sql("CREATE TRIGGER craft_mail_fail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'mail rollback'); END");
        Reject<SqliteException>(() => repository.CompleteGuildNpcItemCrafts(now + 60), "炼金邮件插入故障");
        That(Number("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") == 1 && Number("SELECT count(*) FROM mailbox_messages") == 0,
            "发奖故障保留完整待完成生产");
        Sql("DROP TRIGGER craft_mail_fail; DELETE FROM guild_members; DELETE FROM guilds");
        var reopened = new CharacterRepository(path);
        var completed = reopened.CompleteGuildNpcItemCrafts(now + 60);
        That(completed is [{ Management: null, Mail.Accepted: true }] && Number("SELECT count(*) FROM mailbox_messages WHERE expires_at IS NULL") == 1 &&
            Number("SELECT sum(json_extract(i.item_core,'$.Value')) FROM mailbox_attachments a JOIN character_inventory_items i ON i.item_uid=a.item_uid") == 30,
            "重开、退会及解散后仍向付款角色发永久邮件，完整30份产物不丢失");
        That(reopened.CompleteGuildNpcItemCrafts(now + 61).Length == 0 && Number("SELECT count(*) FROM mailbox_messages") == 1,
            "重复完成不重复发产物或邮件");
        That(Number("SELECT count(*) FROM pragma_foreign_key_check") == 0, "炼金完成后外键完整");
    }
}
