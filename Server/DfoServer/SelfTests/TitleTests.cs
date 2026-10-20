using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class TitleTests
{
    static void CheckHatchedCreature()
    {
        foreach (string declaration in new[] { "", "[sub type]\n", "[sub type]\n0 1", "[sub type]\n0\n[sub type]\n0",
            "[sub type]\n-1", "[sub type]\n256", "[sub type]\n2147483648" })
            That(ItemCatalog.ReadCreatureSubType(new(declaration)) == -1, "宠物类型缺失、重复、越界或非单整数不补为成宠");
        That(ItemCatalog.ReadCreatureSubType(new("[sub type]\n0")) == 0 &&
            ItemCatalog.ReadCreatureSubType(new("[sub type]\n1")) == 1 &&
            ItemCatalog.ReadCreatureSubType(new("[sub type]\n2")) == 2, "保留实际宠物类型，不把未知非零类型归一为成宠");
        var template = new ItemTemplate(63023, ItemKind.Creature, 26, 1, 0, 1, 0, 1, 1, "[trade]", ushort.MaxValue, 0, "成宠")
        { CreatureSubType = 0 };
        var item = new InventoryItem(1, new(InventoryListType.Pet, 0), new(63023, ItemKind.Creature, 1))
        { Creature = new() { ExpireTime = 1000 } };
        That(TitleAchievementService.IsHatchedCreature(item, template, 999) &&
            !TitleAchievementService.IsHatchedCreature(item with { Core = item.Core with { SealFlag = 2 } }, template, 999),
            "成宠资格同时核对模板并保留原非零实例类型拒绝边界");
        foreach (int subtype in new[] { -1, 1, 2, 255 })
            That(!TitleAchievementService.IsHatchedCreature(item, template with { CreatureSubType = subtype }, 999),
                "即使SealFlag为0，蛋及未知模板类型也不能计入成宠数量");
        That(!TitleAchievementService.IsHatchedCreature(item, template, 1000) &&
            !TitleAchievementService.IsHatchedCreature(item, template with { Id = 63022 }, 999) &&
            !TitleAchievementService.IsHatchedCreature(item, template with { Kind = ItemKind.Equipment }, 999) &&
            !TitleAchievementService.IsHatchedCreature(item with { Core = item.Core with { Kind = ItemKind.Equipment } }, template, 999),
            "精确到期、模板身份不符及非宠物不能计入成宠数量");
    }

    static void CheckAppearance()
    {
        var title = new InventoryItem(1, new(InventoryListType.Equipment, 13), new(26071, ItemKind.Equipment, 1));
        var creature = new InventoryItem(2, new(InventoryListType.Equipment, 26), new(63000, ItemKind.Creature, 2))
        { Creature = new() { Name = "宠物" } };
        foreach (string name in new[] { "Title", "称号测试" })
        foreach (bool withCreature in new[] { false, true })
        {
            var character = new Character(1, 0, name, 0, new(1, 0, 10, 10), null)
            { Inventory = new(withCreature ? [title, creature] : [title]) };
            byte[] baseline = TownPackets.Actor(character, utcSeconds: 1700000000);
            int count = 133 + NameWidth(name);
            That(baseline[count + 1] == 13 && U32(baseline, count + 2) == 26071,
                "装备称号始终位于外观表13槽，不误用旧端10槽或惩罚计分字段");
            int pet = count + 1 + baseline[count] * 25 + 22;
            int score = checked(pet + 11 + (int)U32(baseline, pet + 4));
            foreach (int value in new[] { 0, 99, 100, 199, 200, 299, 300, int.MaxValue })
            {
                var expected = (byte[])baseline.Clone();
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(expected.AsSpan(score), value);
                Bytes(TownPackets.Actor(character with { BlacklistCount = value }, utcSeconds: 1700000000), expected,
                    "被拉黑计分完整投影u32，阈值选择归客户端，不改装备、宠物、网吧或黑钻字段");
            }
            Reject<InvalidDataException>(() => TownPackets.Actor(character with { BlacklistCount = -1 }),
                "负数计分不能进入客户端的有符号阈值判断");
        }
    }

    public static void RunCore()
    {
        int start = Count;
        CheckHatchedCreature();
        CheckAppearance();
        var characterLevel = new Character(1, 0, "TitleLevel", 0, new(1, 0, 10, 10), null) { Level = 4 };
        var titleLevel = QuestEligibilityTests.Quest(6501) with { Grade = "[title]", MaximumLevel = 3 };
        var levelCatalog = new QuestCatalog(new[] { titleLevel }.ToDictionary(quest => quest.Id));
        That(!DfoServer.Game.Quests.QuestEligibility.Evaluate(characterLevel, titleLevel, levelCatalog).Eligible &&
            DfoServer.Game.Quests.QuestEligibility.Evaluate(characterLevel, titleLevel, levelCatalog, allowAboveMaximum: true).Eligible,
            "只有称号专用资格可忽略最高等级，原有普通任务默认上限保持");
        That(!DfoServer.Game.Quests.QuestEligibility.Evaluate(characterLevel,
            titleLevel with { MinimumLevel = 5, MaximumLevel = 99 }, levelCatalog, allowAboveMaximum: true).Eligible &&
            !DfoServer.Game.Quests.QuestEligibility.Evaluate(characterLevel with { CompletedQuests = [6501] },
                titleLevel, levelCatalog, allowAboveMaximum: true).Eligible,
            "称号仍检查最低等级和完成事实，不借放宽上限重复发奖");
        var delta = new TitleCounters(7, 2, 9).Subtract(new(ushort.MaxValue, 1, 3));
        That(delta == new TitleCounters(0, 1, 6) && !delta.Complete && delta.Subtract(new(0, 2, 10)).Complete,
            "三个剩余计数独立饱和相减，不能只看第一通道完成");
        var request = TitleRequests.Trigger(Build(writer => { writer.Write(6501); writer.Write((ushort)7); writer.Write((ushort)2); writer.Write((ushort)9); }));
        That(request == new TitleTriggerRequest(6501, new(7, 2, 9)), "CMD417为完整i32+三个u16");
        Reject<InvalidDataException>(() => TitleRequests.Trigger(new byte[9]), "不截断三计数包");
        Reject<InvalidDataException>(() => TitleRequests.Book(new byte[21]), "取放拒绝多余尾部");
        Reject<InvalidDataException>(() => TitleRequests.Book(Build(writer =>
            { writer.Write(0); writer.Write(9); writer.Write(26596); writer.Write(-1); writer.Write(0); })), "分类负数不能进入客户端ACK数组消费者");
        var bookRequest = TitleRequests.Book(Build(writer => { writer.Write(3); writer.Write(13); writer.Write(26596); writer.Write(0); writer.Write(2); }));
        That(bookRequest == new TitleBookRequest(3, 13, 26596, 0, 2), "CMD412/413保持五个i32原顺序");
        Bytes(TitlePackets.Trigger(new(true, 0, 6501, new(7, 2, 9))), [1, 101, 25, 0, 0, 7, 0, 2, 0, 9, 0], "ACK417成功11B");
        Bytes(TitlePackets.Trigger(TitleAchievementResult.Reject(6501, 1)), [0, 1, 0, 101, 25, 0, 0], "ACK417失败u16错误+questID共7B");
        That(TitlePackets.BookAck(bookRequest, new(true, 0)).Length == 17 && TitlePackets.BookAck(bookRequest, InventoryResult.Reject(11)).Length == 11,
            "取放成功四个i32，失败两个i32且错误为u16");
        var core = new ItemCore(26596, ItemKind.Equipment, 987654321)
        {
            Attr = 7, Durability = 30, SealFlag = 1, EnchantCardId = 3185, EnchantUpgrade = 2,
            AmplifyType = 3, AmplifyValue = 120, Marker16 = 123, Extension97 = 0x12340056,
            Random0 = new(1, 2, 3), RandomState = 1, RandomChangedIndex = 255, Rune = 4,
            GuardianGemKeys = 0x12345678, SortLockFlag = 1
        };
        var item = new InventoryItem(5, TitleBookService.Slot(0, 2), core);
        var empty = new Character(1, 0, "TitleCore", 0, new(1, 0, 10, 10), null);
        That(TitlePackets.Progress(empty).Length == 20, "空成就表仍保留16B尾部");
        var book = TitlePackets.Book(new([item]), 0);
        That(book.Length == 45 && U16(book, 1) == 0 && U32(book, 3) == 0 && U32(book, 7) == 1 && U16(book, 11) == 2 &&
            U32(book, 13) == 26596 && U32(book, 17) == core.Value && U32(book, 33) == 4 && U32(book, 37) == core.Extension97 && U32(book, 41) == core.Marker16,
            "NOTI358本人owner0、22B行头、4B RAW及独立尾i32，完整字段消费不漂移");
        var categories = TitleCatalog.Categories.Select((name, index) => new PvfLib.TitleBookCategory(name, 1,
            [new(0, index == 0 ? 0 : -1, index == 0 ? 6501 : -1, index == 0 ? [26596] : [])])).ToImmutableArray();
        var catalog = new TitleCatalog(new PvfLib.TitleBookFile(categories, [], []), new QuestCatalog([]));
        var complete = TitlePackets.Complete(6501, catalog, new([item with { Position = TitleBookService.Slot(0, 0) }]));
        That(complete.Length == 18 && U32(complete, 12) == 26596 && U16(complete, 16) == 30,
            "NOTI360最后u16进入物品构造耐久字段，不是质量百分比");
        var character = empty with { TitleProgress = ImmutableDictionary<int, TitleProgress>.Empty
            .Add(6501, new(default, 2)).Add(6502, new(new(1, 2, 3))).Add(6503, new(default, 5)) };
        var progress = TitlePackets.Progress(character);
        That(progress.Length == 50 && U32(progress, 34) == 6503 && U32(progress, 38) == 6501 && U32(progress, 42) == 0 && U32(progress, 46) == 0,
            "进度表按ID排序，尾部按真实完成revision保存最多四ID");
        SectionMapping();
        Persist(core);
        Console.WriteLine($"PASS {Count - start} 称号三计数、称号簿字节及原子资产/邮件断言");
    }

    static void SectionMapping()
    {
        var categories = TitleCatalog.Categories.Select(name => new PvfLib.TitleBookCategory(name, 50,
            Enumerable.Range(0, 50).Select(index => name == "specific" && index is 0 or 10 or 40
                ? new PvfLib.TitleBookRow(index, 0, 6501 + index, [26596 + index])
                : name == "event" && index >= 10
                    ? new PvfLib.TitleBookRow(index, 1, -1, [-1])
                    : new PvfLib.TitleBookRow(index, -1, -1, [])).ToImmutableArray())).ToImmutableArray();
        var sections = new PvfLib.TitleBookSection[]
        {
            new("specific", 0, "", 10, 0, 0, "", ""),
            new("specific", 1, "", 11, 0, 0, "", ""),
            new("specific", 2, "", 0, 1000, 1, "System", "Later chapter"),
            new("event", 0, "Closed", 0, 0, 0, "", ""),
            new("event", 1, "", 0, 0, 0, "", "")
        }.ToImmutableArray();
        var catalog = new TitleCatalog(new PvfLib.TitleBookFile(categories, sections, []), new QuestCatalog([]));
        That(catalog.Sections[260].PostalItem == 1000 && catalog.SectionQuests[260].SequenceEqual([6541]),
            "章节顺序跨过两组空槽后，奖励绑定实际40组而非20组");
        That(!catalog.Sections.ContainsKey(258) && !catalog.Sections.ContainsKey(259) &&
            catalog.Sections[256].Emotion == 10 && catalog.Sections[257].Emotion == 11,
            "空槽组不挤占章节，空位前的章节奖励保持");
        That(catalog.Sections[1024].LockMessage == "Closed" && !catalog.Fits(4, 0, 26596) && catalog.Fits(4, 10, 26596),
            "活动关闭栏保留原组号，不能因全空而把锁移到自由展示栏");
    }

    static void Persist(ItemCore core)
    {
        string root = Path.Combine(Path.GetTempPath(), "s5a9-titles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(path);
            using var owner = new AccountOwners().Acquire("title-test", () => { });
            var current = repo.Create(owner, new("TitleStore", new(1, 0, 10, 10), null));
            var partial = Plan(new(2, 1, 3));
            current = repo.CommitTitleAchievement(owner, partial);
            That(current.Revision == 1 && current.TitleProgress[6501].Remaining == new TitleCounters(2, 1, 3) && current.Inventory.Items.IsEmpty,
                "仅进度有变化也提交revision，不创建称号或独立资产事务");
            var target = TitleBookService.Slot(0, 0);
            var final = Plan(default) with
            {
                Inventory = new(current.Inventory, current.Inventory with { Items = [new(0, target, core)] }, "title_test", new(current)),
                Completed = [6501], Sections = ImmutableHashSet.Create(0),
                Postals = [new(1000, 1, "System", "Title reward", "title-test-mail")],
                PostalItems = [new(0, new(InventoryListType.Main, 65), new(1000, ItemKind.Consumable, 1))]
            };
            using var db = new SqliteConnection("Data Source=" + path + ";Pooling=False;Foreign Keys=True"); db.Open();
            Execute("CREATE TRIGGER fail_title_mail BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'late title failure'); END");
            Reject<SqliteException>(() => repo.CommitTitleAchievement(owner, final), "邮件附件晚期失败回滚整个称号事务");
            That(Query("SELECT revision FROM characters") is 1L && Query("SELECT count(*) FROM character_inventory_items") is 0L &&
                Query("SELECT count(*) FROM character_title_sections") is 0L && Query("SELECT remaining1 FROM character_title_progress") is 2L &&
                Query("SELECT count(*) FROM mailbox_messages") is 0L, "失败不留下称号UID、完成计数、章节或半份邮件");
            Execute("DROP TRIGGER fail_title_mail");
            current = repo.CommitTitleAchievement(owner, final);
            var title = current.Inventory.Find(target)!;
            That(title.Uid > 0 && title.Core == core && current.TitleProgress[6501].Remaining.Complete &&
                Query("SELECT count(*) FROM mailbox_attachments") is 1L && Query("SELECT count(*) FROM character_title_book_slots") is 1L,
                "完成、完整实例、真实UID、章节和邮件同一事务持久化");
            Reject<InvalidOperationException>(() => repo.CommitTitleAchievement(owner, final), "旧版本不能再次发奖");
            var loaded = new CharacterRepository(path).LoadCharacter(owner, 0);
            That(loaded.TitleProgress.SequenceEqual(current.TitleProgress) && loaded.TitleSections.SetEquals(current.TitleSections) &&
                loaded.Inventory.Find(target) == title, "重开库按角色装载完整称号及独立进度，不抢占普通任务槽");
            Reject<SqliteException>(() => Execute($"INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid) VALUES({current.Id},'title-test',0,9,{title.Uid})"),
                "同UID不能同时进入称号簿与普通背包");
            var moved = current.Inventory with { Items = [title with { Position = new(InventoryListType.Equipment, 13) }] };
            current = repo.CommitInventory(owner, current, new(current.Inventory, moved, "title_move"));
            That(current.Inventory.Items.Single().Uid == title.Uid && current.Inventory.Items.Single().Core == core && Query("SELECT count(*) FROM character_title_book_slots") is 0L,
                "称号穿戴移动只转移位置，保留完整核心，不复制永久资产");
            That(Query("PRAGMA integrity_check") is "ok" && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L, "称号与邮件所有权外键完整");
            current = repo.CommitInventory(owner, current, new(current.Inventory,
                current.Inventory with { Items = [title] }, "title_move"));
            var deleted = repo.Delete(owner, repo.LoadDeletionTarget(owner, current.Slot));
            That(deleted.Id == current.Id && Query("SELECT count(*) FROM characters") is 0L &&
                Query("SELECT count(*) FROM character_title_book_slots") is 0L && Query("SELECT count(*) FROM character_title_progress") is 0L &&
                Query("SELECT count(*) FROM character_title_sections") is 0L && Query("SELECT count(*) FROM character_inventory_items") is 0L &&
                Query("SELECT count(*) FROM mailbox_messages") is 0L && Query("SELECT count(*) FROM pragma_foreign_key_check") is 0L,
                "删除角色先清称号位置、进度与章节，再清完整物品和个人奖励邮件，不遗留未知外键");

            TitleAchievementPlan Plan(TitleCounters counters) => new(current, new(current.Inventory, current.Inventory, "title_test", new(current)),
                current.TitleProgress.SetItem(6501, new(counters, counters.Complete ? current.Revision + 1 : 0)), current.TitleSections, [], [], []);
            void Execute(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
            object? Query(string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
        }
        finally { Directory.Delete(root, true); }
    }
}
