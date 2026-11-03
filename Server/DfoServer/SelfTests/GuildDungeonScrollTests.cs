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

internal static class GuildDungeonScrollTests
{
    public static void RunCore()
    {
        int before = Count;
        var template = new ItemTemplate(987656, ItemKind.Consumable, 0, 1, 0, 1000, 0, 2, 1, "[trade]", ushort.MaxValue, 0, "fixture");
        var raw = new StackableFile(template.Id, "stackable/custom/guild-clear.stk", new("[town and dungeon]", []), 1000,
            null, null, 1, null, [])
        {
            ActionDeclaration = new("[guild dungeon clear count]", [101, 0, 1]), DeclaredUseActionPacket = 1,
            HasUseActionPacket = true, HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[village]", "[seria room]"]
        };
        That(GuildDungeonClearScrollDefinition.Parse(raw, template) is { DungeonId: 101, Increment: 1 } &&
            GuildDungeonClearScrollDefinition.Action == 167,
            "净化卷轴167只接受正副本编号与正增量");
        That(GuildDungeonClearScrollDefinition.Parse(raw with { ActionDeclaration = new("[guild dungeon clear count]", [0, 0, 1]) }, template) is null &&
            GuildDungeonClearScrollDefinition.Parse(raw with { ActionDeclaration = new("[increase guild exp fund]", [1, 500]) }, template) is null &&
            GuildDungeonClearScrollDefinition.Parse(raw with { HasActionUsablePlace = false }, template) is null &&
            GuildDungeonClearScrollDefinition.Parse(raw with { DeclaredActionUsablePlaces = ["[dungeon]"] }, template) is null,
            "零图号、其它动作及非城镇场所不混入净化卷轴");
        Repository(template);
        Console.WriteLine($"PASS {Count - before} guild dungeon scroll assertions");
    }

    public static void RunResources(ItemCatalog items)
    {
        int before = Count;
        foreach (var (id, dungeon, increment) in new (int Id, uint Dungeon, int Increment)[]
        {
            (10161078, 8504, 1), (10161079, 8507, 1), (10161080, 8505, 1),
            (10161081, 8503, 1), (10161082, 8508, 1), (10161083, 8500, 1),
            (490007011, 8504, 14), (490007012, 8507, 20), (490007013, 8505, 20),
            (490007014, 8503, 14), (490007015, 8508, 34), (490007016, 8500, 7),
            (10160833, 8504, 1), (10160834, 8507, 1), (10160835, 8505, 1),
            (10160836, 8503, 1), (10160837, 8508, 1), (10160838, 8500, 1),
        })
            That(items.GuildDungeonClearScroll(id) is { } scroll && scroll.DungeonId == dungeon &&
                scroll.Increment == increment,
                "公会净化卷轴按[guild dungeon clear count]标签接入，不写死商店编号: " + id);
        That(items.GuildDungeonClearScroll(10155510) is null && items.GuildDungeonClearScroll(10155681) is null,
            "公会地下城延长券148不是167净化卷轴");
        That(items.GuildDungeonClearScroll(10310211) is null && items.GuildDungeonClearScroll(10006240) is null,
            "缺use packet的etc模板与经验书不是净化卷轴");
        Console.WriteLine($"PASS {Count - before} guild dungeon scroll resource assertions");
    }

    static void Repository(ItemTemplate template)
    {
        string directory = Path.Combine(Path.GetTempPath(), "guild-dungeon-scroll-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "game.db");
            var repository = new CharacterRepository(path); var owners = new AccountOwners();
            using var owner = owners.Acquire("guild-scroll", static () => { });
            var location = new Location(38, 1, 450, 234, 0); var slot = new InventorySlot(InventoryListType.Main, 68);
            var character = repository.Create(owner, new("ScrollUser", location, location)
                { Inventory = new([new(0, slot, new(template.Id, ItemKind.Consumable, 3))]) });
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            void Sql(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            long Number(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            Sql($"""
                INSERT INTO guilds(guild_id,name,address,master_character_id,level,tier,experience,created_at_unix)
                VALUES(1,'净化公会','scroll-guild',{character.Id},1,0,0,1000);
                INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000);
                """);
            var towns = new TownCatalog([], location, location, new(0, [], []), [], new Dictionary<uint, uint[]>());
            var session = new GameSession(repository, towns, owner); session.Select(character.Slot); session.MarkReady();
            var actor = session.CaptureGuildManagement()!;
            var catalog = GuildDungeonProgressTests.Fixture();
            const long now = 1700000000;
            string Key(int value) => value.ToString("x64");
            GuildDungeonClearScrollDefinition Definition(uint dungeon = 101, int increment = 1) => new(template, dungeon, increment);
            GuildDungeonScrollPlan Prepare(GuildDungeonClearScrollDefinition? definition = null)
            {
                var expected = repository.LoadCharacter(owner, character.Slot); var source = expected.Inventory.Find(slot)!;
                var deletion = InventoryDeleteService.Plan(expected.Inventory, slot, source.Core.Value, 1);
                return new(expected, source, definition ?? Definition(),
                    new(expected.Inventory, deletion.Plan!.After, "guild_dungeon_scroll", new(expected, requirePosition: true)),
                    repository.ReadGuildDungeonScrollMembership(actor)!);
            }
            GuildDungeonScrollCommit Commit(GuildDungeonScrollPlan plan, int key) =>
                repository.CommitGuildDungeonScroll(actor, plan, catalog, Key(key), now);
            var plan = Prepare();
            Sql($"INSERT INTO guild_request_claims VALUES('{Key(99)}',4,'{owner.Account}',{character.Id})");
            That(Commit(plan, 99).Error == 14 && Number("SELECT count(*) FROM guild_dungeon_progress") == 0 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 3,
                "公会NPC已经占用的请求不能再消费净化卷轴");
            Sql("CREATE TRIGGER guild_scroll_fail BEFORE INSERT ON guild_dungeon_scroll_receipts BEGIN SELECT RAISE(ABORT,'guild scroll rollback'); END");
            Reject<SqliteException>(() => Commit(plan, 1), "净化卷轴末段回执SQL失败");
            That(Number("SELECT count(*) FROM guild_dungeon_progress") == 0 &&
                Number("SELECT count(*) FROM guild_dungeon_scroll_receipts") == 0 &&
                Number($"SELECT count(*) FROM guild_request_claims WHERE request_id='{Key(1)}'") == 0 &&
                SkillRepositoryTests.Equal(repository.LoadCharacter(owner, character.Slot), plan.Expected),
                "净化卷轴扣件、通关次数、有效次数、回执及统一claim整体回滚");
            Sql("DROP TRIGGER guild_scroll_fail");
            var first = Commit(plan, 1);
            That(first.Success && first.Progress?.Counts.Single() is { DungeonId: 101, Clears: 1 } &&
                first.Character?.Inventory.Find(slot)?.Core.Count == 2 &&
                Number("SELECT clear_count FROM guild_dungeon_progress WHERE dungeon_id=101") == 1 &&
                Number("SELECT effective_clear_units FROM guild_dungeon_effective_progress WHERE dungeon_id=101") == 1,
                "消耗一件卷轴给伤城夹具图增加1次历史通关与1个有效单位");
            That(Commit(plan, 1).Replayed &&
                new CharacterRepository(path).ReplayGuildDungeonScroll(actor, Key(1), slot.Index, now)?.Replayed == true &&
                repository.ReplayGuildDungeonScroll(actor, Key(1), 69, now)?.Error == 14 &&
                Commit(plan with { Definition = Definition(increment: 2) }, 1).Error == 14 &&
                Number("SELECT clear_count FROM guild_dungeon_progress WHERE dungeon_id=101") == 1 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 2,
                "净化卷轴精确重放及重开不扣第二件，换源槽或更改增量不能复用回执");
            var second = Prepare();
            Sql($"UPDATE guild_members SET joined_at_unix=1001 WHERE character_id={character.Id}");
            That(Commit(second, 2).Error == 14 && repository.ReplayGuildDungeonScroll(actor, Key(1), slot.Index, now)?.Error == 14 &&
                Number("SELECT clear_count FROM guild_dungeon_progress WHERE dungeon_id=101") == 1,
                "准备后退会重入不能把原卷计划或旧回执转给新会籍");
            Sql($"UPDATE guild_members SET joined_at_unix=1000 WHERE character_id={character.Id}");
            That(Commit(Prepare(Definition(999)), 3).Error == 19 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 2,
                "未知副本编号拒绝且不吞卷");
            Sql($"DELETE FROM guild_members WHERE character_id={character.Id}");
            That(repository.ReadGuildDungeonScrollMembership(actor) is null && Commit(second, 6).Error == 34 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 2,
                "无会籍成员不消耗净化卷轴且没有平行角色或进度");
            Sql($"INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix) VALUES(1,{character.Id},1,1000)");
            Sql("""
                INSERT INTO guild_dungeon_progress(guild_id,dungeon_id,clear_count,last_clear_unix) VALUES(1,101,65,1000)
                ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET clear_count=65;
                INSERT INTO guild_dungeon_effective_progress(guild_id,dungeon_id,effective_clear_units,decay_anchor_unix)
                VALUES(1,101,65,1700000000)
                ON CONFLICT(guild_id,dungeon_id) DO UPDATE SET effective_clear_units=65,decay_anchor_unix=1700000000;
                """);
            var partial = Commit(Prepare(Definition(increment: 7)), 4);
            That(partial.Success &&
                Number("SELECT clear_count FROM guild_dungeon_progress WHERE dungeon_id=101") == 72 &&
                Number("SELECT effective_clear_units FROM guild_dungeon_effective_progress WHERE dungeon_id=101") == 67 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 1,
                "剩余净化空间不足时有效次数夹到满档，历史通关仍加完整增量");
            Sql("""
                UPDATE guild_dungeon_progress SET clear_count=67;
                UPDATE guild_dungeon_effective_progress SET effective_clear_units=67,decay_anchor_unix=1700000000;
                """);
            That(Commit(Prepare(), 5).Error == 22 && Number("SELECT clear_count FROM guild_dungeon_progress WHERE dungeon_id=101") == 67 &&
                repository.LoadCharacter(owner, character.Slot).Inventory.Find(slot)?.Core.Count == 1,
                "已达100%净化时拒绝，不截断也不吞卷");
            session.End();
        }
        finally { SqliteConnection.ClearAllPools(); TestFiles.DeleteDirectory(directory, true); }
    }
}
