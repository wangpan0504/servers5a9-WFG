using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Titles;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class TitleTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        var real = new TitleCatalog(resources.Archive, resources.Quests);
        var registered = resources.Quests.All.Where(quest => quest.Grade == "[title]").ToArray();
        That(real.Quests.Count == registered.Length, "称号只按quest.lst的有效注册ID装配，不把目录内未注册旧文件作为可用任务: " +
            real.Quests.Count + "/" + registered.Length + "; skipped=" + string.Join(',', registered.Where(quest => !real.Quests.ContainsKey(quest.Id)).Select(quest => quest.Id)));
        That(real.Quests[6501].Quest.Condition == "[clear quest]" && real.Quests[6501].Initial == new TitleCounters(1, 0, 0) &&
            real.Quests[6501].Quest.IntData.SequenceEqual([4303]) && real.RewardSlot(6501, 26596) == (0, 0), "真实6501条件、默认计数和映射合同");
        That(
            real.Slots.Count(pair => pair.Key.Category == 0) == 200 && real.Slots.Count(pair => pair.Key.Category == 1) == 170 &&
            real.Fits(4, 10, 26596) && !real.Fits(4, 0, 26596), "当前PVF称号任务、真实映射、分类容量、自由及关闭活动格合同");
        var definition = resources.Quests.Get(6501);
        var quests = new QuestCatalog(resources.Quests.All.ToDictionary(quest => quest.Id));
        // 三通道组合用合成条件；目标称号模板、称号簿映射和所有其他任务仍取共享原资源。
        definition = definition with { Condition = "[use item]", IntData = [1000],
            Definition = definition.Definition! with { Fields = definition.Definition!.Fields.Where(field => field.Name != "check count").ToImmutableArray()
                .Add(new("check count", [new(0, 3, null), new(0, 2, null), new(0, 4, null)], [])) } };
        quests = new QuestCatalog(quests.All.ToDictionary(quest => quest.Id, quest => quest.Id == 6501 ? definition : quest));
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "titles"), resources: resources, questCatalog: quests);
        var repo = new CharacterRepository(fixture.Database);
        var template = resources.ItemResources.Items.Get(26596);
        var core = InventoryRewardGrantService.Create(template) with { Attr = 7, Durability = 5, Marker16 = 3,
            EnchantCardId = 3185, EnchantUpgrade = 2, Extension97 = 0x12340056, Rune = 4, GuardianGemKeys = 0x10203040 };
        long manualUid;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repo.Create(owner, new("TitleTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 10, AcceptedStoryLevel = 1, CompletedQuests = [3], Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 9), core)], Gold: 500)
            });
            manualUid = created.Inventory.Items.Single().Uid;
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(417, Trigger(6501, 1, 0, 0)); peer.Expect(1, 417, [0, 2, 0, 101, 25, 0, 0]);
            peer.Select(prepared: true);
            That(fixture.Query("SELECT quest_id,remaining1,remaining2,remaining3 FROM character_title_progress WHERE quest_id=6502") is [[6502L, 0L, 0L, 0L]] &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests") is [[0L]] &&
                peer.TitleBooks[0].Body.Length == 45 && U32(peer.TitleBooks[0].Body, 13) == 26597,
                "真实任务3完成在选角补齐6502称号，普通任务槽不增加，完整实体已下发");
            peer.Send(417, Trigger(6501, 1, 0, 0));
            peer.Expect(1, 417, [1, 101, 25, 0, 0, 2, 0, 2, 0, 4, 0]);
            peer.Expect(0, 359); peer.Barrier();
            That(fixture.Query("SELECT remaining1,remaining2,remaining3 FROM character_title_progress WHERE quest_id=6501") is [[2L, 2L, 4L]] &&
                fixture.Query("SELECT count(*) FROM character_title_book_slots") is [[1L]], "部分进度只保存三计数，不提前发奖");
            peer.Send(417, Trigger(6501, 65535, 1, 3));
            peer.Expect(1, 417, [1, 101, 25, 0, 0, 0, 0, 1, 0, 1, 0]); peer.Expect(0, 359);
            peer.Send(417, Trigger(6501, 0, 1, 1));
            peer.Expect(1, 417, [1, 101, 25, 0, 0, 0, 0, 0, 0, 0, 0]);
            var notification = peer.Expect(0, 360).Body;
            That(notification.Length == 18 && U32(notification, 0) == 6501 && U32(notification, 4) == 0 && U32(notification, 8) == 0 &&
                U32(notification, 12) == 26596 && U16(notification, 16) == template.Durability, "当前NOTI360按本版类别、位置、物品和耐久通知完成");
            peer.Expect(0, 359); ReadBooks(peer);
            string afterComplete = Snapshot();
            peer.Send(417, Trigger(6501, 1, 1, 1)); peer.Expect(1, 417, [0, 2, 0, 101, 25, 0, 0]); peer.Barrier();
            That(Snapshot() == afterComplete, "完成后重复触发不新增实例、邮件或revision");
            peer.Send(417, Trigger(3145, 1, 0, 0)); peer.Expect(1, 417, [0, 1, 0, 73, 12, 0, 0]);
            peer.Send(412, Book(0, 9, 26596, 4, 10)); peer.Expect(1, 412, Ack(0, 9, 4, 10)); peer.Expect(0, 14); ReadBooks(peer);
            That(Read(InventoryListType.TitleEvent, 10) is { Uid: var uid, Core: var stored } && uid == manualUid && stored == core &&
                fixture.Query("SELECT count(*) FROM character_inventory_slots WHERE item_uid=$p0", manualUid) is [[0L]], "放入自由展示格保留完整核心和UID，普通位置移除");
            peer.Send(423, Build(writer => writer.Write(peer.Id)));
            for (int category = 0; category < 5; category++)
            {
                var book = peer.Expect(0, 358).Body;
                That(book[0] == 1 && U16(book, 1) == peer.Id && U32(book, 3) == category, "CMD423采用非零被查看角色owner，最后分类完成窗口打开");
            }
            peer.Send(413, Book(0, 10, 26596, 4, 10)); peer.Expect(1, 413, Ack(0, 10, 4, 10)); peer.Expect(0, 14); ReadBooks(peer);
            That(Read(InventoryListType.Main, 10) is { Uid: var returnedUid, Core: var returned } && returnedUid == manualUid && returned == core,
                "取出保留附魔、强化、期限、扩展字段及UID，不按模板重建");
            peer.Send(413, Book(0, 11, 26596, 0, 0)); peer.Expect(1, 413, [0, 5, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
            peer.Send(413, Book(17, 13, 26596, 0, 0)); peer.Expect(1, 413, [0, 2, 0, 17, 0, 0, 0, 0, 0, 0, 0]);
            peer.Send(413, Book(3, 13, 26596, 0, 0)); peer.Expect(1, 413, Ack(3, 13, 0, 0)); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Equipment, 13)?.Core.ItemId == 26596 && fixture.Query("SELECT count(*) FROM character_title_book_slots WHERE list_type=18 AND slot_index=0") is [[0L]],
                "成就称号可以穿戴但不能取包，ACK先移动客户端对象，不发送完整Actor重建");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("titles-restarted");
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            That(Read(InventoryListType.Main, 10)?.Core == core && Read(InventoryListType.Equipment, 13)?.Core.ItemId == 26596 &&
                fixture.Query("SELECT count(*) FROM character_title_progress WHERE quest_id=6501 AND remaining1+remaining2+remaining3=0") is [[1L]],
                "服务重启恢复已穿戴/取出称号及完成计数，不重发奖励");
            peer.Send(413, Book(3, 13, 26597, 0, 1)); peer.Expect(1, 413, Ack(3, 13, 0, 1)); peer.Expect(0, 14); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Equipment, 13)?.Core.ItemId == 26597 && Read(InventoryListType.Main, 9)?.Core.ItemId == 26596 &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=26596") is [[2L]],
                "替换穿戴的旧称号完整回包，两个既有物理实例不因穿戴多一份");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "TCP取放、重启和穿戴后外键完整");
        Console.WriteLine($"PASS {Count - checks} 称号进度、称号簿取放、穿戴和重启TCP断言");

        InventoryItem? Read(InventoryListType list, short slot)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, 0).Inventory.Find(new(list, slot));
        }
        string Snapshot() => JsonSerializer.Serialize(new { Character = fixture.Query("SELECT revision,gold FROM characters"),
            Progress = fixture.Query("SELECT * FROM character_title_progress ORDER BY quest_id"), Items = fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") });
        static byte[] Trigger(int id, ushort first, ushort second, ushort third) => Build(writer =>
            { writer.Write(id); writer.Write(first); writer.Write(second); writer.Write(third); });
        static byte[] Book(int list, int slot, int item, int category, int index) => Build(writer =>
            { writer.Write(list); writer.Write(slot); writer.Write(item); writer.Write(category); writer.Write(index); });
        static byte[] Ack(int list, int slot, int category, int index) => Build(writer =>
            { writer.Write((byte)1); writer.Write(list); writer.Write(slot); writer.Write(category); writer.Write(index); });
        static void ReadBooks(TcpPeer peer)
        {
            for (int category = 0; category < 5; category++)
            {
                var book = peer.Expect(0, 358).Body;
                That(U16(book, 1) == 0 && U32(book, 3) == category && book.Length == 11 + U32(book, 7) * 34, "本人完整称号簿投影不误入他人上下文");
            }
        }
    }
}
