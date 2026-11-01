using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Guild;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Guilds;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildExperienceGiftTests
{
    public static void RunCore()
    {
        int before = Count;
        var template = new ItemTemplate(987655, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "fixture");
        var raw = new StackableFile(template.Id, "stackable/custom/guild-exp.stk", new("[town and dungeon]", []), 1000,
            null, null, 1, null, [])
        {
            ActionDeclaration = new("[increase guild exp fund]", [1, 500]), DeclaredUseActionPacket = 1,
            HasUseActionPacket = true, HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[village]", "[seria room]"]
        };
        That(GuildExperienceGiftDefinition.Parse(raw, template) is { Amount: 500 } && GuildExperienceGiftDefinition.Action == 145,
            "公会经验书145只接受本版声明类型1与正经验值");
        That(GuildExperienceGiftDefinition.Parse(raw with { ActionDeclaration = new("[increase guild exp fund]", [0, 500]) }, template) is null &&
            GuildExperienceGiftDefinition.Parse(raw with { ActionDeclaration = new("[add guildmileage]", [500]) }, template) is null &&
            GuildExperienceGiftDefinition.Parse(raw with { HasActionUsablePlace = false }, template) is null,
            "未证基金类型、其它服145积分及缺场所资源不混入公会经验消费");
        Repository(template);
        Console.WriteLine($"PASS {Count - before} guild experience item assertions");
    }
    public static void RunResources(ItemCatalog items)
    {
        int before = Count;
        That(items.GuildExperienceGift(10006240) is { Amount: 500 } && items.GuildExperienceGift(490000177) is { Amount: 2000 },
            "纯净资源两种实际公会经验书按500/2000经验接入");
        That(items.GuildExperienceGift(1200) is null, "公会据点战积分物品1200不是可用经验书");
        Console.WriteLine($"PASS {Count - before} guild experience item resource assertions");
    }
    static void Repository(ItemTemplate template)
    {
        string directory = Path.Combine(Path.GetTempPath(), "guild-exp-item-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path); var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-exp", static () => { });
            var location = new Location(38, 1, 450, 234, 0); var slot = new InventorySlot(InventoryListType.Main, 68);
            var character = repository.Create(owner, new("ExpBook", location, location)
                { Inventory = new([new(0, slot, new(template.Id, ItemKind.Consumable, 3))]) });
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,level,tier,experience,created_at_unix)
                VALUES(1,'经验公会','exp-guild',{character.Id},1,0,900,1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
                """);
            var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
            var actor = session.CaptureGuildManagement()!; var parameters = GuildParameterTests.Synthetic();
            const long now = 1700000000;
            string Key(int value) => value.ToString("x64");
            GuildExperienceGiftPlan Prepare()
            {
                var expected = repository.LoadCharacter(owner, character.Slot); var source = expected.Inventory.Find(slot)!;
                var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
                return new(expected, source, new(template, 500),
                    new(expected.Inventory, deletion.Plan!.After, "guild_experience_gift", new(expected, requirePosition: true)),
                    repository.ReadGuildExperienceMembership(actor)!);
            }
            GuildExperienceCommit Commit(GuildExperienceGiftPlan plan, int key) =>
                repository.CommitGuildExperienceGift(actor, plan, parameters, Key(key), now);
            var plan = Prepare();
            Sql($"INSERT INTO guild_request_claims VALUES('{Key(99)}',4,'{owner.Account}',{character.Id})");
            That(Commit(plan, 99).Error == 14 && Number("SELECT experience FROM guilds WHERE guild_id=1") == 900 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 3,
                "公会NPC已经占用的请求不能再消费经验书");
            Sql("CREATE TRIGGER guild_exp_item_fail BEFORE INSERT ON guild_growth_item_receipts BEGIN SELECT RAISE(ABORT,'guild exp item rollback'); END");
            Reject<SqliteException>(() => Commit(plan, 1), "公会经验书末段回执SQL失败");
            That(Number("SELECT experience FROM guilds WHERE guild_id=1") == 900 &&
                Number("SELECT count(*) FROM guild_growth_item_receipts") == 0 &&
                Number($"SELECT count(*) FROM guild_request_claims WHERE request_id='{Key(1)}'") == 0 &&
                SkillRepositoryTests.Equal(repository.LoadCharacter(owner, character.Slot), plan.Expected),
                "经验书扣件、角色revision、公会等级经验、回执及统一claim整体回滚");
            Sql("DROP TRIGGER guild_exp_item_fail");
            var first = Commit(plan, 1);
            That(first.Success && first.State?.Guild is { Experience: 1400, Level: 10, Tier: 0 } &&
                first.Character?.Inventory.Find(slot)?.Core.Count == 2 &&
                Number("SELECT public_funds FROM guilds WHERE guild_id=1") == 0,
                "消耗一件真实UID书增加500公会经验并推进档内等级，保持tier且不增加公共积分");
            That(Commit(plan, 1).Replayed &&
                new CharacterRepository(path).ReplayGuildExperienceGift(actor, Key(1), slot.Index, now)?.Replayed == true &&
                repository.ReplayGuildExperienceGift(actor, Key(1), 69, now)?.Error == 14 &&
                Commit(plan with { Definition = new(template, 501) }, 1).Error == 14 &&
                Number("SELECT experience FROM guilds WHERE guild_id=1") == 1400 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 2,
                "经验书精确重放及重开不扣第二件，换源槽或更改金额不能复用回执");
            var second = Prepare();
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={character.Id}");
            That(Commit(second, 2).Error == 14 && repository.ReplayGuildExperienceGift(actor, Key(1), slot.Index, now)?.Error == 14 &&
                Number("SELECT experience FROM guilds WHERE guild_id=1") == 1400,
                "准备后退会重入不能把原书计划或旧回执转给新会籍");
            Sql($"UPDATE guild_members SET joined_at_unix=1000 WHERE character_id={character.Id}");
            var overflow = Prepare();
            Sql($"UPDATE guilds SET experience={uint.MaxValue - 100L} WHERE guild_id=1");
            That(Commit(overflow, 3).Error == 22 && Number("SELECT experience FROM guilds WHERE guild_id=1") == uint.MaxValue - 100L &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 2,
                "公会经验uint总量不足承接整本时拒绝，不截断奖励也不吞书");
            Sql($"DELETE FROM guild_members WHERE character_id={character.Id}");
            That(repository.ReadGuildExperienceMembership(actor) is null && Commit(second, 4).Error == 34 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 2,
                "无会籍成员不消耗经验书且没有平行角色或经验状态");
            session.End();
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(directory, true); }
    }
}
