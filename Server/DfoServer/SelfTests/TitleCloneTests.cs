using System.Buffers.Binary;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Towns;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using PvfLib;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static class TitleCloneTests
{
    public static void RunCore()
    {
        That(TitleRequests.CloneTitle([0x1c, 0xf1, 0xfa, 0x05]) == 100331804 &&
            TitleRequests.CloneTitle([0, 0, 0, 0]) == 0 &&
            TitleRequests.CloneTitle([255, 255, 255, 255]) == uint.MaxValue,
            "动画请求保留完整4字节ID，0清除；模板是否合法由领域资格处理");
        foreach (int length in new[] { 0, 3, 5 })
            Reject<InvalidDataException>(() => TitleRequests.CloneTitle(new byte[length]), "动画请求拒绝截断与多余尾部");
        Bytes(TitlePackets.CloneTitle(100331804), [1, 0x1c, 0xf1, 0xfa, 0x05], "动画成功回执是成功头与实际选择ID，共5字节");
        Bytes(TitlePackets.CloneTitle(0), [1, 0, 0, 0, 0], "清除动画明确回零，不沿用旧选择");
        Bytes(TitlePackets.CloneTitleFailure(0x1234), [0, 0x34, 0x12], "动画失败沿公共u16错误头，不追加来源槽位或ID");

        var stats = new CharacterStats(8550, 5150, 123, 45, 67, 89, -10, 20, -30, 40,
            480000, 525, 800, 100, 100, 6000, 100, 600);
        var now = DateTimeOffset.FromUnixTimeSeconds(1700000000);
        foreach (string name in new[] { "CloneTitle", "称号动画" })
        {
            var original = new Character(1, 0, name, 0, new(1, 0, 10, 10), null);
            foreach (uint id in new uint[] { 100331804, uint.MaxValue })
            {
                var changed = original with { CloneTitleItemId = id };
                Check(TownPackets.Actor(original, 0, utcSeconds: now.ToUnixTimeSeconds()),
                    TownPackets.Actor(changed, 0, utcSeconds: now.ToUnixTimeSeconds()), 134 + NameWidth(name), id, "Actor");
                Check(TownPackets.Player(original, stats, 1, utcNow: now),
                    TownPackets.Player(changed, stats, 1, utcNow: now), 127, id, "本人Player");
                Check(TownPackets.OtherPlayer(original, stats, 1, default, now),
                    TownPackets.OtherPlayer(changed, stats, 1, default, now), 119, id, "他人公开资料");
                var roster = CharacterRosterEntry.FromCharacter(changed);
                That(roster.CloneTitleItemId == id, "轻量名册保留动画选择，不要求重新加载完整背包");
                Check(CharacterPackets.Roster([CharacterRosterEntry.FromCharacter(original)]),
                    CharacterPackets.Roster([roster]), 37 + NameWidth(name), id, "名册尾部");
            }
        }
        CheckRestrictions();
    }

    static void CheckRestrictions()
    {
        var docNone = new PvfDocument("[item category]\n`title`\n[/item category]");
        var doc39 = new PvfDocument("[item category]\n`cannot be clone title`\n[/item category]");
        var doc40 = new PvfDocument("[item category]\n`cannot be hidden by clone title`\n[/item category]");
        var docBoth = new PvfDocument("[item category]\n`cannot be clone title`\n`cannot be hidden by clone title`\n[/item category]");
        That(!docNone.ContainsString("item category", "cannot be clone title") &&
            !docNone.ContainsString("item category", "cannot be hidden by clone title") &&
            doc39.ContainsString("item category", "cannot be clone title") &&
            !doc39.ContainsString("item category", "cannot be hidden by clone title") &&
            !doc40.ContainsString("item category", "cannot be clone title") &&
            doc40.ContainsString("item category", "cannot be hidden by clone title") &&
            docBoth.ContainsString("item category", "cannot be clone title") &&
            docBoth.ContainsString("item category", "cannot be hidden by clone title"),
            "PVF[item category]独立识别39(cannot be clone title)与40(cannot be hidden by clone title)");

        const int NormalA = 26596, NormalB = 26597, Only39 = 2747501, Only40 = 2747502, Both39And40 = 2747531;
        var items = new ItemCatalog(null!, null!, null!, 0);
        items.PrefillTemplates(new Dictionary<int, ItemTemplate>
        {
            [NormalA] = new(NormalA, ItemKind.Equipment, 13, 1, 0, 1, 0, 1, 1, "[free]", ushort.MaxValue, 0, "普通称号A"),
            [NormalB] = new(NormalB, ItemKind.Equipment, 13, 1, 0, 1, 0, 1, 1, "[free]", ushort.MaxValue, 0, "普通称号B"),
            [Only39] = new(Only39, ItemKind.Equipment, 13, 1, 0, 1, 0, 1, 1, "[free]", ushort.MaxValue, 0, "禁克隆源称号")
            {
                CannotBeCloneTitle = true
            },
            [Only40] = new(Only40, ItemKind.Equipment, 13, 1, 0, 1, 0, 1, 1, "[free]", ushort.MaxValue, 0, "禁被克隆隐藏称号")
            {
                CannotBeHiddenByCloneTitle = true
            },
            [Both39And40] = new(Both39And40, ItemKind.Equipment, 13, 1, 0, 1, 0, 1, 1, "[free]", ushort.MaxValue, 0, "精·妖精女王")
            {
                CannotBeCloneTitle = true,
                CannotBeHiddenByCloneTitle = true
            }
        });
        var categories = TitleCatalog.Categories.Select(name => new PvfLib.TitleBookCategory(name, 20,
            Enumerable.Range(0, 20).Select(index => name == "event" && index >= 10
                ? new PvfLib.TitleBookRow(index, 1, -1, [-1])
                : new PvfLib.TitleBookRow(index, -1, -1, [])).ToImmutableArray())).ToImmutableArray();
        var titleCatalog = new TitleCatalog(new PvfLib.TitleBookFile(categories, [], []), new QuestCatalog([]));
        var books = new TitleBookService(titleCatalog, items);
        long now = 1700000000;

        static InventoryItem Item(long uid, InventorySlot slot, int id) =>
            new(uid, slot, new(id, ItemKind.Equipment, (int)uid));
        static InventoryMoveRequest MoveRequest(InventorySlot source, int sourceId, InventorySlot dest, int destId) =>
            new(source, sourceId, 1, dest, destId, 0, -1, 0, 0);

        var bagNormalB = Item(1, new(InventoryListType.Main, 9), NormalB);
        var bagOnly39 = Item(2, new(InventoryListType.Main, 10), Only39);
        var bagOnly40 = Item(3, new(InventoryListType.Main, 11), Only40);
        var bagBoth = Item(4, new(InventoryListType.Main, 12), Both39And40);
        var baseActor = new Character(1, 0, "CloneRule", 0, new(1, 0, 10, 10), null)
        {
            Level = 85,
            Inventory = new([bagNormalB, bagOnly39, bagOnly40, bagBoth])
        };
        CheckOrphanSources(baseActor, items, NormalB, now);

        // 13号槽为空时：允许普通称号与仅含40的称号作为克隆源，拒绝含39的称号。
        That(books.CloneTitleError(baseActor, NormalB, now) == 0 &&
            books.CloneTitleError(baseActor, Only40, now) == 0 &&
            books.CloneTitleError(baseActor, 0, now) == 0 &&
            books.CloneTitleError(baseActor, Only39, now) == 2 &&
            books.CloneTitleError(baseActor, Both39And40, now) == 2,
            "13号槽为空时允许合法源与仅带40的源，拒绝带39的源");

        // 13号槽穿戴普通称号时：允许普通称号与仅含40的称号作为克隆源，拒绝含39的称号。
        var wearingNormal = baseActor with
        {
            Inventory = new([Item(5, new(InventoryListType.Equipment, 13), NormalA), bagNormalB, bagOnly39, bagOnly40, bagBoth])
        };
        That(books.CloneTitleError(wearingNormal, NormalB, now) == 0 &&
            books.CloneTitleError(wearingNormal, Only40, now) == 0 &&
            books.CloneTitleError(wearingNormal, Only39, now) == 2 &&
            books.CloneTitleError(wearingNormal, Both39And40, now) == 2,
            "13号槽穿戴普通称号时允许仅带40的源称号，拒绝带39的源称号");

        // 13号槽穿戴带40的称号时：拒绝任何非零克隆源，0清除始终允许。
        foreach (int worn40 in new[] { Only40, Both39And40 })
        {
            var wearing40 = baseActor with
            {
                CloneTitleItemId = NormalB,
                Inventory = new([Item(5, new(InventoryListType.Equipment, 13), worn40), bagNormalB, bagOnly39])
            };
            That(books.CloneTitleError(wearing40, NormalB, now) == 2 &&
                books.CloneTitleError(wearing40, (uint)worn40, now) == 2 &&
                books.CloneTitleError(wearing40, 0, now) == 0,
                "13号槽穿戴带40称号时拒绝任何非零克隆选择，但允许0清空");
        }

        // 普通装备移动穿戴、称号簿取出穿戴、称号簿同格交换穿戴，以及选角恢复路径校验。
        string root = Path.Combine(Path.GetTempPath(), "s5a9-clone-title-rules-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string dbPath = Path.Combine(root, "game.db");
            var repo = new CharacterRepository(dbPath);
            var settings = new ClientSettingsService(new ClientSettingsRepository(dbPath));
            var point = new Location(38, 1, 100, 100);
            var towns = new TownCatalog(new(), point, point, new(0, [], []), [], new Dictionary<uint, uint[]>());
            using var owner = new AccountOwners().Acquire("clone-rule-acc", () => { });
            var created = repo.Create(owner, new("CloneRule", towns.Room, towns.InitialReturn)
            {
                Level = 85,
                Inventory = new([
                    Item(0, new(InventoryListType.Equipment, 13), NormalA),
                    Item(0, new(InventoryListType.Main, 9), NormalB),
                    Item(0, new(InventoryListType.Main, 10), Both39And40),
                    Item(0, new(InventoryListType.Main, 11), Only39),
                    Item(0, TitleBookService.Slot(4, 10), Only40)
                ])
            });

            var session = new GameSession(repo, towns, owner, items: items);
            session.Select(created.Slot);
            session.MarkReady();

            // CMD 568 会话入口：拒绝39，允许仅40与普通称号。
            That(session.SelectCloneTitle(books, settings, Only39, now).Error == 2 &&
                session.SelectCloneTitle(books, settings, Both39And40, now).Error == 2 &&
                session.SelectCloneTitle(books, settings, Only40, now) is { Error: 0, Changed: true, Character.CloneTitleItemId: Only40 } &&
                session.SelectCloneTitle(books, settings, NormalB, now) is { Error: 0, Changed: true, Character.CloneTitleItemId: NormalB },
                "CMD568会话校验拒绝39源，允许仅40源与普通源");

            // 1. 普通背包CMD19把带40的称号(2747531)穿戴到13号槽：不拒绝穿戴，且同事务自动清零CloneTitleItemId。
            var move40 = session.PlanAndCommitInventory(c =>
                InventoryMoveService.Plan(c, MoveRequest(new(InventoryListType.Main, 10), Both39And40,
                    new(InventoryListType.Equipment, 13), NormalA), items, now));
            That(move40.Accepted && move40.Plan!.CloneTitleItemIdAfter == 0 &&
                session.Selected!.CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0 &&
                session.Selected.Inventory.Find(new(InventoryListType.Equipment, 13))?.Core.ItemId == Both39And40,
                "普通移动穿戴带40称号到13号槽时自动清零克隆称号并同步落库");

            // 13号槽穿戴2747531期间，CMD568拒绝选择非零克隆称号，允许0清除。
            That(session.SelectCloneTitle(books, settings, NormalB, now).Error == 2 &&
                session.SelectCloneTitle(books, settings, 0, now).Error == 0,
                "13号槽穿戴带40称号期间拒绝CMD568非零选择，允许0清空");

            // 换回普通称号A到13号槽后，重新激活NormalB克隆称号。
            var moveBackNormal = session.PlanAndCommitInventory(c =>
                InventoryMoveService.Plan(c, MoveRequest(new(InventoryListType.Main, 10), NormalA,
                    new(InventoryListType.Equipment, 13), Both39And40), items, now));
            That(moveBackNormal.Accepted && session.SelectCloneTitle(books, settings, NormalB, now).Error == 0 &&
                session.Selected!.CloneTitleItemId == NormalB,
                "13号槽换回普通称号后可再次设置克隆称号");

            // 2. 称号簿CMD413直接穿戴带40的称号(Only40)到13号槽：自动清零CloneTitleItemId。
            var bookEquip40 = session.PlanAndCommitInventory(c =>
                books.Get(c, new(3, 13, Only40, 4, 10), now));
            That(bookEquip40.Accepted && bookEquip40.Plan!.CloneTitleItemIdAfter == 0 &&
                session.Selected!.CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0 &&
                session.Selected.Inventory.Find(new(InventoryListType.Equipment, 13))?.Core.ItemId == Only40,
                "称号簿Get穿戴带40称号到13号槽时自动清零克隆称号并同步落库");

            // 将Only40放回称号簿自由格(4,10)，把NormalA穿回13号槽，激活NormalB克隆称号，再测CMD412同格交换把Only40换入13号槽。
            That(session.PlanAndCommitInventory(c => books.Put(c, new(3, 13, Only40, 4, 10), now)).Accepted &&
                session.PlanAndCommitInventory(c => InventoryMoveService.Plan(c, MoveRequest(new(InventoryListType.Main, 12), NormalA,
                    new(InventoryListType.Equipment, 13), 0), items, now)).Accepted &&
                session.SelectCloneTitle(books, settings, NormalB, now).Error == 0 &&
                session.Selected!.CloneTitleItemId == NormalB,
                "准备称号簿Put与13号槽交换前恢复NormalA穿戴与NormalB克隆");

            // 3. 称号簿CMD412从13号槽与称号簿中带40的称号(Only40)同格交换：自动清零CloneTitleItemId。
            var bookSwap40 = session.PlanAndCommitInventory(c =>
                books.Put(c, new(3, 13, NormalA, 4, 10), now));
            That(bookSwap40.Accepted && bookSwap40.Plan!.CloneTitleItemIdAfter == 0 &&
                session.Selected!.CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0 &&
                session.Selected.Inventory.Find(new(InventoryListType.Equipment, 13))?.Core.ItemId == Only40,
                "称号簿Put与13号槽同格交换带40称号时自动清零克隆称号并同步落库");

            // 4. 历史存档残留非法clone_title_item_id（情况A：13槽穿带40称号且clone非零；情况B：13槽穿普通称号但clone源带39）：
            // 名册回放与选角Prepare均自动清理为0并持久化到DB。
            var settingsRepo = new ClientSettingsRepository(dbPath);
            That(settingsRepo.SaveCloneTitle(owner, created.Id, NormalB) == true &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == NormalB,
                "模拟历史存档在13槽穿带40称号时残留非零clone_title_item_id");
            var reconnect40 = new GameSession(repo, towns, owner, items: items);
            That(reconnect40.Roster().Single().CloneTitleItemId == 0 &&
                reconnect40.PrepareRoster().Entries.Single().CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == NormalB,
                "名册与登录投影屏蔽违反40规则的历史克隆称号，读路径不改库存档");
            reconnect40.Select(created.Slot);
            That(reconnect40.Selected!.CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0,
                "选角Prepare自动清理违反40规则的历史克隆称号并写回数据库");

            // 将13槽换回普通称号NormalA，再模拟历史存档残留带39的源称号(Both39And40)。
            reconnect40.MarkReady();
            That(reconnect40.PlanAndCommitInventory(c => books.Put(c, new(3, 13, Only40, 4, 10), now)).Accepted &&
                reconnect40.Selected!.Inventory.Find(new(InventoryListType.Equipment, 13))?.Core.ItemId == NormalA,
                "13号槽换回普通称号NormalA");
            That(settingsRepo.SaveCloneTitle(owner, created.Id, Both39And40) == true &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == Both39And40,
                "模拟历史存档残留带39的clone_title_item_id");
            var reconnect39 = new GameSession(repo, towns, owner, items: items);
            That(reconnect39.Roster().Single().CloneTitleItemId == 0 &&
                reconnect39.PrepareRoster().Entries.Single().CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == Both39And40,
                "名册与登录投影屏蔽违反39规则的历史克隆称号，读路径不改库存档");
            reconnect39.Select(created.Slot);
            That(reconnect39.Selected!.CloneTitleItemId == 0 &&
                repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0,
                "选角Prepare自动清理违反39规则的历史克隆称号并写回数据库");

            reconnect39.MarkReady();
            That(reconnect39.SelectCloneTitle(books, settings, NormalB, now).Error == 0 &&
                reconnect39.Roster().Single().CloneTitleItemId == NormalB &&
                reconnect39.PrepareRoster().Entries.Single().CloneTitleItemId == NormalB,
                "合法来源在Main时，只有装备快照的轻量名册不能误清动画");
            var duplicateSlot = new InventorySlot(InventoryListType.Main, 14);
            That(reconnect39.PlanAndCommitInventory(c => new(true, 0,
                new(c.Inventory, c.Inventory with { Items = c.Inventory.Items.Add(Item(0, duplicateSlot, NormalB)) },
                    "clone_duplicate_fixture", new(c)))).Accepted,
                "准备另一个同模板但不同UID的合法来源");
            That(reconnect39.PlanAndCommitInventory(c => InventoryDeleteService.Plan(c.Inventory,
                    new(InventoryListType.Main, 9), NormalB, 1)).Accepted &&
                reconnect39.Selected!.CloneTitleItemId == NormalB,
                "删除一个来源时另一合法实例仍可提供动画，不能按单个UID误清");
            var beforeDelete = reconnect39.Selected!;
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TRIGGER fail_clone_orphan BEFORE UPDATE OF clone_title_item_id ON characters " +
                    "WHEN NEW.clone_title_item_id=0 BEGIN SELECT RAISE(ABORT,'clone orphan rollback'); END";
                command.ExecuteNonQuery();
                Reject<Microsoft.Data.Sqlite.SqliteException>(() => reconnect39.PlanAndCommitInventory(c =>
                    InventoryDeleteService.Plan(c.Inventory, duplicateSlot, NormalB, 1)),
                    "最后来源删除与动画清理在同一事务，后段SQL失败必须整体回滚");
                var rolledBack = repo.LoadCharacter(owner, created.Slot);
                That(ReferenceEquals(reconnect39.Selected, beforeDelete) && rolledBack.CloneTitleItemId == NormalB &&
                    rolledBack.Revision == beforeDelete.Revision && rolledBack.Inventory.Find(duplicateSlot) == beforeDelete.Inventory.Find(duplicateSlot),
                    "失败保留原在线快照、最后来源、动画选择与revision");
                command.CommandText = "DROP TRIGGER fail_clone_orphan";
                command.ExecuteNonQuery();
            }
            var deleted = reconnect39.PlanAndCommitInventory(c => InventoryDeleteService.Plan(c.Inventory, duplicateSlot, NormalB, 1));
            That(deleted is { Accepted: true, Plan.CloneTitleItemIdAfter: 0 } &&
                reconnect39.Selected!.CloneTitleItemId == 0 && repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0 &&
                reconnect39.Selected.Inventory.Find(duplicateSlot) is null,
                "最后合法来源删除时，同一次库存提交清零在线与持久动画");
            That(settingsRepo.SaveCloneTitle(owner, created.Id, NormalB) == true, "准备旧存档的无来源残留动画");
            var orphanReconnect = new GameSession(repo, towns, owner, items: items);
            orphanReconnect.Select(created.Slot);
            That(orphanReconnect.Selected!.CloneTitleItemId == 0 && repo.LoadCharacter(owner, created.Slot).CloneTitleItemId == 0,
                "选角从完整库存清理历史无来源动画，不依赖重新设置请求");
            orphanReconnect.MarkReady();
            That(orphanReconnect.PlanAndCommitInventory(c => new(true, 0,
                new(c.Inventory, c.Inventory with { Items = c.Inventory.Items.Add(Item(0, duplicateSlot, NormalB)) },
                    "clone_reissued_fixture", new(c)))).Accepted && orphanReconnect.Selected!.CloneTitleItemId == 0,
                "后来重新发放同模板实例不会继承已清除的幽灵动画");
            CheckDiscardCommit(repo, dbPath, towns, items, NormalB, NormalA, now);
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }
    }

    static void CheckDiscardCommit(CharacterRepository repo, string dbPath, TownCatalog towns, ItemCatalog items,
        int sourceId, int otherTitleId, long now)
    {
        var owners = new AccountOwners();
        using var owner = owners.Acquire("clone-drop-acc", () => { });
        var sourceSlot = new InventorySlot(InventoryListType.Main, 9);
        var created = repo.Create(owner, new("CloneDrop", towns.Room, towns.InitialReturn)
        {
            Level = 85, DungeonState = CharacterDungeonState.NewOrdinary,
            Inventory = new([new(0, sourceSlot, new(sourceId, ItemKind.Equipment, 17))])
        });
        var settings = new ClientSettingsRepository(dbPath);
        settings.SaveCloneTitle(owner, created.Id, checked((uint)sourceId));
        var expected = repo.LoadCharacter(owner, created.Slot);

        DfoServer.Game.Dungeons.DungeonInventoryDiscardPreparation Prepare(Character character)
        {
            var request = new InventoryDiscardRequest(400, 250, 0, 9, 1, 0);
            var planned = InventoryDiscardPlan.Prepare(character, request, items.Get(sourceId), now).Plan!;
            var cleaned = InventoryCloneAppearanceService.WithOrphanCloneTitleCleanup(character, new(true, 0, planned.Inventory), now);
            planned = planned with { Inventory = cleaned.Plan! };
            var state = character.DungeonState!;
            uint key = state.NextObjectKey == character.Id ? state.NextObjectKey + 1 : state.NextObjectKey;
            var identity = new DfoServer.Game.Dungeons.DungeonRunIdentity(character.Id, 0, state.LastAttempt, Guid.NewGuid(), 1);
            var map = new DfoServer.GameWorld.Dungeons.DungeonMap("clone-drop", new(0, 0), 1, "map/clone-drop.map");
            var ground = new DfoServer.Game.Dungeons.DungeonGroundObject(Guid.NewGuid(), identity,
                new(character.Id, key, map, request.X, request.Y, 0), planned.GroundCore)
            {
                ResourceIdentity = "inventory", InventoryOrigin = new(planned.Source, request.Count, character.Revision)
            };
            return new(character, planned, ground);
        }

        var stale = Prepare(expected);
        var advanced = InventoryService.Commit(repo, owner, expected, new(true, 0,
            new(expected.Inventory, expected.Inventory with { Items = expected.Inventory.Items.Add(
                new(0, new(InventoryListType.Main, 10), new(otherTitleId, ItemKind.Equipment, 18))) }, "clone_drop_revision", new(expected))));
        Reject<InvalidOperationException>(() => repo.CommitDungeonDiscard(owner, stale),
            "来源清理计划不能用旧revision删除新库存");
        var prepared = Prepare(advanced);
        settings.SaveCloneTitle(owner, created.Id, 0);
        Reject<InvalidOperationException>(() => repo.CommitDungeonDiscard(owner, prepared),
            "规划后动画选择变化也须拒绝，不能用旧清理计划覆盖新选择");
        settings.SaveCloneTitle(owner, created.Id, checked((uint)sourceId));
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + dbPath))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_clone_drop AFTER UPDATE OF next_object_key ON character_dungeon_state " +
                "BEGIN SELECT RAISE(ABORT,'clone drop rollback'); END";
            command.ExecuteNonQuery();
            Reject<Microsoft.Data.Sqlite.SqliteException>(() => repo.CommitDungeonDiscard(owner, prepared),
                "地面对象key末段失败时，已写库存和动画必须一并回滚");
            var restored = repo.LoadCharacter(owner, created.Slot);
            That(restored.Revision == advanced.Revision && restored.CloneTitleItemId == sourceId &&
                restored.Inventory.Find(sourceSlot) == advanced.Inventory.Find(sourceSlot) && restored.DungeonState == advanced.DungeonState,
                "地面丢弃回滚保留源完整核心UID、动画、revision和key高水位");
            command.CommandText = "DROP TRIGGER fail_clone_drop";
            command.ExecuteNonQuery();
        }
        var committed = repo.CommitDungeonDiscard(owner, prepared);
        That(committed.CloneTitleItemId == 0 && committed.Inventory.Find(sourceSlot) is null &&
            committed.Revision == advanced.Revision + 1,
            "正式丢弃更换审计action后仍保存原计划的同事务动画清零字段");
        var replayed = repo.CommitDungeonDiscard(owner, prepared);
        That(replayed.CloneTitleItemId == 0 && replayed.Revision == committed.Revision && replayed.DungeonState == committed.DungeonState,
            "返回结果丢失后沿同一准备恢复清零结果，不重复扣物或推进revision/key");
        settings.SaveCloneTitle(owner, created.Id, checked((uint)otherTitleId));
        Reject<InvalidDataException>(() => repo.CommitDungeonDiscard(owner, prepared),
            "结果不明恢复不能仅凭库存相同接受后来改变的动画状态");
        settings.SaveCloneTitle(owner, created.Id, 0);
        using var replacement = owners.Acquire("clone-drop-acc", () => { });
        Reject<InvalidOperationException>(() => repo.CommitDungeonDiscard(owner, prepared),
            "账号已接管时旧owner不能重放来源清理");
        That(repo.LoadCharacter(replacement, created.Slot).CloneTitleItemId == 0,
            "旧owner失败后新owner的已提交结果保持");
    }

    static void CheckOrphanSources(Character character, ItemCatalog items, int sourceId, long now)
    {
        var source = character.Inventory.Find(new(InventoryListType.Main, 9))!;
        var selected = character with { CloneTitleItemId = checked((uint)sourceId), Inventory = new([source]) };
        foreach (var position in new[] { source.Position, new InventorySlot(InventoryListType.Equipment, 13), TitleBookService.Slot(4, 10) })
            That(InventoryCloneAppearanceService.PlanCleanup(selected with { Inventory = new([source with { Position = position }]) }, items, now)
                .Plan!.CloneTitleItemIdAfter is null, "Main、穿戴13和称号簿的合法来源均保留动画");
        foreach (var invalid in new[]
        {
            source with { Position = new(InventoryListType.PersonalCargo, 0) },
            source with { Core = source.Core with { IsRental = true } },
            source with { Core = source.Core with { ExpireTime = checked((int)now) } },
            source with { Core = source.Core with { Kind = ItemKind.Consumable } }
        })
            That(InventoryCloneAppearanceService.PlanCleanup(selected with { Inventory = new([invalid]) }, items, now)
                .Plan!.CloneTitleItemIdAfter == 0, "仓库、租赁、到期或错误Kind不能撑住动画来源");
        That(InventoryCloneAppearanceService.PlanCleanup(selected with { Inventory = new([]) }, items, now).Plan!.CloneTitleItemIdAfter == 0,
            "最后合法来源完全消失时生成同事务清零计划");
        That(!InventoryCloneAppearanceService.ShouldClearCloneTitle(checked((uint)sourceId), new([]), items),
            "轻量名册限制检查不把缺少Main和称号簿快照当成来源消失");
    }

    static void Check(byte[] zero, byte[] actual, int offset, uint id, string consumer)
    {
        That(U32(zero, offset) == 0, consumer + "缺省动画为0");
        // 在独立计算的字段偏移写期望值，完整比较确保字段之外的装备、属性和尾部均不变。
        BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(offset), id);
        Bytes(actual, zero, consumer + "只替换独立动画u32，不移位或覆盖其它字段");
    }
}
