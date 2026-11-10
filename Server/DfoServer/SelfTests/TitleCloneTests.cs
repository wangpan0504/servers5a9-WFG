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
        }
        finally
        {
            TestFiles.DeleteDirectory(root, true);
        }
    }

    static void Check(byte[] zero, byte[] actual, int offset, uint id, string consumer)
    {
        That(U32(zero, offset) == 0, consumer + "缺省动画为0");
        // 在独立计算的字段偏移写期望值，完整比较确保字段之外的装备、属性和尾部均不变。
        BinaryPrimitives.WriteUInt32LittleEndian(zero.AsSpan(offset), id);
        Bytes(actual, zero, consumer + "只替换独立动画u32，不移位或覆盖其它字段");
    }
}
