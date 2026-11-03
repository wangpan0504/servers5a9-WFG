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

internal static class GuildNpcTeraniumCraftTests
{
    public static void RunCore()
    {
        int before = Count;
        var alchemy = new GuildNpcBenefitDefinition(7001, GuildNpcCatalog.Alchemist, 1, false, "crafting item",
            "gold", 100, "none", 0, 60, [490013242, 30]);
        var teranium = new GuildNpcBenefitDefinition(2009, GuildNpcCatalog.Spirit, 5, false, "purified teranium craft",
            "gold", 400, "none", 0, 60, [10158964, 16, 1]);
        That(GuildNpcBenefitService.IndividualItemCraft(alchemy) && !GuildNpcBenefitService.WeeklyItemCraft(alchemy) &&
            GuildNpcBenefitService.IndividualItemCraft(teranium) && GuildNpcBenefitService.WeeklyItemCraft(teranium) &&
            !GuildNpcBenefitService.IndividualItemCraft(teranium with { Effect = "pure will crystal craft" }) &&
            !GuildNpcBenefitService.IndividualItemCraft(teranium with { NpcId = GuildNpcCatalog.Guardian }) &&
            !GuildNpcBenefitService.IndividualItemCraft(teranium with { Parameters = [10158964, 16, 2] }),
            "个人生产只认炼金与守护精灵时空石，周限仅第三参数为1");
        string root = Path.Combine(Path.GetTempPath(), "guild-teranium-craft-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { Schema(Path.Combine(root, "schema.db")); Run(Path.Combine(root, "game.db"), teranium, alchemy); }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(root, true); }
        Console.WriteLine($"PASS {Count - before} guild NPC teranium craft assertions");
    }

    static void Schema(string path)
    {
        _ = new CharacterRepository(path);
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"); db.Open();
        void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
        using (var tx = db.BeginTransaction())
        {
            SqliteMigrations.ReplaceTableConstraint(db, tx, "character_guild_npc_item_crafts",
                "CHECK(npc_id IN(400001065,400001061))", "CHECK(npc_id=400001065)");
            SqliteMigrations.ReplaceTableConstraint(db, tx, "character_guild_npc_item_crafts",
                "CHECK(benefit_id>0)", "CHECK(benefit_id BETWEEN 7001 AND 7005)");
            SqliteMigrations.ReplaceTableConstraint(db, tx, "guild_request_claims",
                "CHECK(kind BETWEEN 1 AND 7)", "CHECK(kind BETWEEN 1 AND 6)");
            tx.Commit();
        }
        Sql("DROP TABLE character_guild_npc_craft_weeks; DROP TABLE guild_dungeon_scroll_receipts; PRAGMA user_version=192");
        That(Number("PRAGMA user_version") == 192, "192回退保留旧炼金约束并卸下周次与净化回执");
        _ = new CharacterRepository(path);
        That(Number("PRAGMA user_version") == SqliteMigrations.CurrentVersion &&
            Number("SELECT count(*) FROM character_guild_npc_craft_weeks") == 0 &&
            Number("SELECT count(*) FROM guild_dungeon_scroll_receipts") == 0,
            "192库真实升级193：周次表、净化回执与放宽后的NPC/福利约束");
        That(Number("""
            SELECT instr(sql,'CHECK(npc_id IN(400001065,400001061))')>0
            FROM sqlite_schema WHERE type='table' AND name='character_guild_npc_item_crafts'
            """) == 1, "193把个人生产NPC约束扩到守护精灵");
    }

    static void Run(string path, GuildNpcBenefitDefinition teranium, GuildNpcBenefitDefinition alchemy)
    {
        const long now = 1700000000;
        var repository = new CharacterRepository(path); var owners = new AccountOwners();
        using var owner = owners.Acquire("spirit-master", static () => { });
        var location = new Location(38, 1, 450, 234, 0);
        var character = repository.Create(owner, new("SpiritLead", location, location));
        using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
        void Sql(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        long Number(string sql) { using var cmd = db.CreateCommand(); cmd.CommandText = sql; return Convert.ToInt64(cmd.ExecuteScalar()); }
        Sql($"""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix) VALUES(1,'精灵测试','spirit-test',{character.Id},1);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1);
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES(1,400001061,1,{now+1000});
            INSERT INTO guild_npc_progress(guild_id,npc_id,level,contract_until) VALUES(1,400001065,1,{now+1000});
            UPDATE characters SET gold=1000 WHERE id={character.Id};
            """);
        var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
        var actor = session.CaptureGuildManagement()!; var npcs = GuildNpcTests.Synthetic();
        var core = new ItemCore(10158964, ItemKind.Consumable, 16);
        var alchemyCore = new ItemCore(490013242, ItemKind.Consumable, 30);
        var allowed = teranium with { Level = 1 };
        string Key(int key) => key.ToString("x64");
        GuildNpcBenefitCommit Change(int key, uint action = 4, GuildNpcBenefitDefinition? row = null, ItemCore? reward = null) =>
            repository.ChangeGuildNpcItemCraft(actor, repository.LoadCharacter(owner, character.Slot), npcs,
                row ?? allowed, action, Key(key), now, action == 4 ? reward ?? core : null, current =>
                {
                    var paid = CurrencyService.Spend(current.Inventory, CurrencyKind.Gold, (row ?? allowed).FirstAmount);
                    return paid.Accepted ? new(current.Inventory, paid.Plan!.After, "guild_npc_benefit_purchase", new(current)) : null;
                });
        string Snapshot() => string.Join(":", Number("SELECT gold FROM characters WHERE id=" + character.Id),
            Number("SELECT count(*) FROM character_guild_npc_item_crafts"),
            Number("SELECT count(*) FROM character_guild_npc_craft_weeks"),
            Number("SELECT count(*) FROM guild_request_claims"));
        string unchanged = Snapshot();
        That(!Change(1, row: teranium).Success && Snapshot() == unchanged, "守护精灵等级不足时时空石生产回14且不扣费");
        var start = Change(1);
        That(start.Success && start.State!.Individual.Any(row => row.BenefitId == 2009 && row.Deadline == now + 60) &&
            Number("SELECT gold FROM characters WHERE id=" + character.Id) == 600 &&
            Number("SELECT npc_id FROM character_guild_npc_craft_weeks") == 400001061,
            "达到配方等级后开始2009扣费、写入个人倒计时并占本周");
        unchanged = Snapshot();
        That(Change(1).Replayed && Snapshot() == unchanged && !Change(2).Success && Snapshot() == unchanged,
            "时空石重放不重复扣款，同周第二次开始拒绝");
        That(Change(3, 5).Success && Number("SELECT gold FROM characters WHERE id=" + character.Id) == 600 &&
            Number("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") == 0 &&
            Number("SELECT count(*) FROM character_guild_npc_craft_weeks") == 1,
            "取消清生产、不退费、不退周次");
        That(!Change(4).Success && Number("SELECT count(*) FROM character_guild_npc_craft_weeks") == 1,
            "取消后本周仍不能再委托时空石");
        Sql($"UPDATE character_guild_npc_craft_weeks SET week_start={GuildContributionRules.WeekStart(now) - 7 * 86400}");
        var next = Change(5);
        That(next.Success && Number("SELECT gold FROM characters WHERE id=" + character.Id) == 200 &&
            Number("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") == 1,
            "跨周后可以再次委托");
        long weeks = Number("SELECT count(*) FROM character_guild_npc_craft_weeks");
        That(Change(6, 4, alchemy, alchemyCore).Success &&
            Number("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") == 2 &&
            Number("SELECT count(*) FROM character_guild_npc_craft_weeks") == weeks,
            "炼金与时空石可并行；炼金不占守护精灵周次");
        Sql("CREATE TRIGGER teranium_mail_fail BEFORE INSERT ON mailbox_messages BEGIN SELECT RAISE(ABORT,'mail rollback'); END");
        Reject<SqliteException>(() => repository.CompleteGuildNpcItemCrafts(now + 60), "时空石邮件插入故障");
        That(Number("SELECT count(*) FROM character_guild_npc_item_crafts WHERE state=0") == 2 &&
            Number("SELECT count(*) FROM mailbox_messages") == 0, "发奖故障保留待完成时空石与炼金生产");
        Sql("DROP TRIGGER teranium_mail_fail");
        var completed = repository.CompleteGuildNpcItemCrafts(now + 60);
        That(completed.Length == 2 && completed.All(row => row.Mail.Accepted) &&
            Number("SELECT count(*) FROM mailbox_messages WHERE sender_name='公会守护精灵' AND expires_at IS NULL") == 1 &&
            Number("SELECT count(*) FROM mailbox_messages WHERE sender_name='公会炼金术师' AND expires_at IS NULL") == 1 &&
            Number("""
                SELECT json_extract(i.item_core,'$.Value') FROM mailbox_messages m
                JOIN mailbox_attachments a ON a.mail_id=m.mail_id
                JOIN character_inventory_items i ON i.item_uid=a.item_uid
                WHERE m.sender_name='公会守护精灵'
                """) == 16 &&
            Number("""
                SELECT json_extract(i.item_core,'$.ItemId') FROM mailbox_messages m
                JOIN mailbox_attachments a ON a.mail_id=m.mail_id
                JOIN character_inventory_items i ON i.item_uid=a.item_uid
                WHERE m.sender_name='公会守护精灵'
                """) == 10158964,
            "到期分别以公会守护精灵与公会炼金术师发永久邮件，16份时空石不丢失");
        session.End();
    }
}
