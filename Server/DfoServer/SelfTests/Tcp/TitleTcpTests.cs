using System.Collections.Immutable;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Titles;
using DfoServer.Game.Quests;
using DfoServer.GameWorld.Quests;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class TitleTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        TitleCloneTests.RunCore();
        var real = new TitleCatalog(resources.Archive, resources.Quests);
        FiendwarFacts(resources, Path.Combine(root, "fiendwar-title-facts"));
        var registered = resources.Quests.All.Where(quest => quest.Grade == "[title]").ToArray();
        That(real.Quests[6583].Quest.Condition == "[seeking]" && real.Quests[6583].Quest.SubType == 25 &&
            real.Quests[6583].Quest.IntData.SequenceEqual([-7, 5]) && real.RewardSlot(6583, 26691) == (1, 73),
            "真实6583要求拥有五个已孵化宠物，奖励绑定原称号槽");
        That(resources.ItemResources.Items.Get(63022).CreatureSubType == 1 &&
            resources.ItemResources.Items.Get(63023).CreatureSubType == 0,
            "真实波比蛋与成宠从各自EQU读取subtype1和0，不从实例SealFlag推定");
        That(real.Quests.Count == registered.Length, "称号只按quest.lst的有效注册ID装配，不把目录内未注册旧文件作为可用任务: " +
            real.Quests.Count + "/" + registered.Length + "; skipped=" + string.Join(',', registered.Where(quest => !real.Quests.ContainsKey(quest.Id)).Select(quest => quest.Id)));
        That(real.Quests[6501].Quest.Condition == "[clear quest]" && real.Quests[6501].Initial == new TitleCounters(1, 0, 0) &&
            real.Quests[6501].Quest.IntData.SequenceEqual([4303]) && real.RewardSlot(6501, 26596) == (0, 0), "真实6501条件、默认计数和映射合同");
        That(
            real.Slots.Count(pair => pair.Key.Category == 0) == 200 && real.Slots.Count(pair => pair.Key.Category == 1) == 170 &&
            real.Fits(4, 10, 26596) && !real.Fits(4, 0, 26596), "当前PVF称号任务、真实映射、分类容量、自由及关闭活动格合同");
        int[][] sectionGroups = [Enumerable.Range(0, 17).ToArray(), [0, 1, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15],
            Enumerable.Range(0, 5).ToArray(), Enumerable.Range(0, 10).ToArray(), Enumerable.Range(0, 10).ToArray()];
        for (int category = 0; category < sectionGroups.Length; category++)
            That(real.Sections.Values.Where(section => section.Category == TitleCatalog.Categories[category])
                .Select(section => section.Index).Order().SequenceEqual(sectionGroups[category]),
                "真实称号簿全部章节按槽位组对应，包含活动保留栏: " + TitleCatalog.Categories[category]);
        That(real.Sections[268].Emotion == 33 && real.Sections[268].PostalItem == 0 &&
            real.SectionQuests[268].Order().SequenceEqual([6761, 6762]) &&
            real.Sections[269].PostalItem == 100331574 && real.Sections[270].PostalItem == 100331746 &&
            real.SectionQuests[270].Order().SequenceEqual([7003, 7004, 7005, 7006, 7007, 7008]) &&
            real.Sections[271].PostalItem == 100331804,
            "觉醒、卢克、超时空和苍穹的章节资格与各自奖励对应，不能串位");
        var erza = resources.ItemResources.Items.Get(2747528);
        var jinErza = resources.ItemResources.Items.Get(2747531);
        That(erza.EquipmentSlot == 13 && erza.CannotBeCloneTitle && erza.CannotBeHiddenByCloneTitle &&
            jinErza.EquipmentSlot == 13 && jinErza.CannotBeCloneTitle && jinErza.CannotBeHiddenByCloneTitle,
            "真实2747528妖精女王与2747531精·妖精女王同时带39(CannotBeCloneTitle)和40(CannotBeHiddenByCloneTitle)");
        foreach (int normalTitleId in new[] { 26596, 26597, 26059, 2676120, 100331179, 400330267 })
        {
            var normalTitle = resources.ItemResources.Items.Get(normalTitleId);
            That(normalTitle.EquipmentSlot == 13 && !normalTitle.CannotBeCloneTitle && !normalTitle.CannotBeHiddenByCloneTitle,
                "普通称号模板不带39或40限制: " + normalTitleId);
        }
        var slot13Ids = PvfLib.PvfScript.List(resources.Archive.ReadText("equipment/equipment.lst"))
            .Where(pair => resources.ItemResources.Items.TryGet(pair.Key, out var t) && t.EquipmentSlot == 13)
            .Select(pair => pair.Key).ToArray();
        var only40Titles = slot13Ids.Where(id => resources.ItemResources.Items.Get(id) is { CannotBeCloneTitle: false, CannotBeHiddenByCloneTitle: true }).Order().ToArray();
        int both39And40 = slot13Ids.Count(id => resources.ItemResources.Items.Get(id) is { CannotBeCloneTitle: true, CannotBeHiddenByCloneTitle: true });
        int only40TitleId = only40Titles.FirstOrDefault(id => resources.ItemResources.Items.Get(id) is { RequiredSkill: 0 } t && t.AllowsUse(95, 0));
        int hiddenTargetId = only40TitleId != 0 ? only40TitleId : jinErza.Id;
        var hiddenTarget = resources.ItemResources.Items.Get(hiddenTargetId);
        Console.WriteLine($"title-clone pvf scan slot13={slot13Ids.Length} only40={only40Titles.Length} usableOnly40={only40TitleId} both={both39And40} hiddenTarget={hiddenTargetId}");
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
        var sealedTemplate = resources.ItemResources.Items.Get(26059);
        That(sealedTemplate.EquipmentSlot == 13 && sealedTemplate.AttachType.Contains("[sealing]", StringComparison.OrdinalIgnoreCase) &&
            real.Fits(4, 10, sealedTemplate.Id), "真实26059为可存活动自由格的封装称号");
        var sealedCore = InventoryRewardGrantService.Create(sealedTemplate) with { SealFlag = 1, Attr = 7, Marker16 = 3,
            EnchantCardId = 3185, EnchantUpgrade = 2, ExpireTime = checked((int)DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds()) };
        var wornCore = InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(26597));
        long sealedUid, wornUid;
        long manualUid;
        ushort awakeningId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repo.Create(owner, new("TitleTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 10, AcceptedStoryLevel = 1, CompletedQuests = [3], Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 9), core)], Gold: 500)
            });
            // 显式准备旧存档：剩余三计数与章节标记彼此独立，标记不能封死未完成任务。
            created = repo.CommitTitleAchievement(owner, new(created, new(created.Inventory, created.Inventory, "old_title_state", new(created)),
                created.TitleProgress.Add(6501, new(new(3, 2, 4))), ImmutableHashSet.Create(0), [], [], []));
            manualUid = created.Inventory.Items.Single().Uid;
            awakeningId = repo.Create(owner, new("AwakeTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 95, GrowType = 33, Skills = resources.Characters.Get(0).Skills
            }).Id;
            foreach (int adults in new[] { 0, 4, 5 })
            {
                var pets = Enumerable.Range(0, 5).Select(index =>
                {
                    var petTemplate = resources.ItemResources.Items.Get(index < adults ? 63023 : 63022);
                    return InventoryRewardGrantService.CreateItem(petTemplate, new(InventoryListType.Pet, (short)index),
                        InventoryRewardGrantService.Create(petTemplate));
                }).ToImmutableArray();
                That(pets.All(item => item.Core.SealFlag == 0), "复现统一授予蛋和成宠的现有SealFlag0，不伪造不同实例字段");
                repo.Create(owner, new("Pets" + adults, resources.Towns.Room, resources.Towns.InitialReturn)
                {
                    Level = 10, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                    Inventory = new(pets)
                });
            }
        }
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var swap = repo.Create(owner, new("TitleSwap", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 10, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([new(0, new(InventoryListType.Main, 9), sealedCore),
                    new(0, new(InventoryListType.Equipment, 13), wornCore)])
            });
            sealedUid = swap.Inventory.Find(new(InventoryListType.Main, 9))!.Uid;
            wornUid = swap.Inventory.Find(new(InventoryListType.Equipment, 13))!.Uid;
            var books = new TitleBookService(real, resources.ItemResources.Items);
            var stored = new InventoryItem(sealedUid, TitleBookService.Slot(4, 10), sealedCore);
            var prepared = swap with { Inventory = swap.Inventory with { Items =
                [stored, swap.Inventory.Find(new(InventoryListType.Equipment, 13))!] } };
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            That(books.CloneTitleError(prepared, 26059, now) == 0 && books.CloneTitleError(prepared, 26597, now) == 0 &&
                books.CloneTitleError(prepared, 0, now) == 0 && books.CloneTitleError(prepared, uint.MaxValue, now) == 2 &&
                books.CloneTitleError(prepared, 26596, now) == 2,
                "动画来源检查本人实际称号；称号簿和穿戴均可选，清除可用，未持有或越界ID拒绝");
            foreach (var invalid in new[] { sealedCore with { ExpireTime = 1 }, sealedCore with { IsRental = true },
                         sealedCore with { Kind = DfoServer.GameWorld.Items.ItemKind.Consumable } })
                That(books.CloneTitleError(prepared with { Inventory = prepared.Inventory with { Items =
                    [stored with { Core = invalid }] } }, 26059, now) == 2,
                    "过期、租赁和非装备实例不能提供称号动画");
            That(books.CloneTitleError(prepared with { Inventory = prepared.Inventory with { Items =
                    [stored with { Position = new(InventoryListType.Main, 9) }] } }, 26059, now) == 0 &&
                books.CloneTitleError(prepared with { Inventory = prepared.Inventory with { Items =
                    [stored with { Position = new(InventoryListType.PersonalCargo, 0) }] } }, 26059, now) == 2,
                "背包称号可作动画来源，仓库实例不冒充当前可选称号");
            var request = new TitleBookRequest(3, 13, 26597, 4, 10);
            foreach (var blocked in new[] { sealedCore with { EquipmentLockId = 1 }, sealedCore with { ExpireTime = 1 } })
                That(!books.Put(prepared with { Inventory = prepared.Inventory with { Items =
                    [stored with { Core = blocked }, prepared.Inventory.Find(new(InventoryListType.Equipment, 13))!] } },
                    request, DateTimeOffset.UtcNow.ToUnixTimeSeconds()).Accepted, "反向穿戴仍拒绝锁定或到期的旧称号");
            var nonSealing = wornCore with { SealFlag = 1 };
            var retained = books.Put(prepared with { Inventory = prepared.Inventory with { Items =
                [stored with { Core = nonSealing }, prepared.Inventory.Find(new(InventoryListType.Equipment, 13))!] } },
                request, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            That(retained.Accepted && retained.Plan!.After.Find(new(InventoryListType.Equipment, 13))!.Core == nonSealing,
                "非sealing模板的既有标记保持，不对所有称号无条件清零");
        }
        fixture.Sql("UPDATE characters SET blacklist_count=201 WHERE name='TitleSwap'");
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Send(417, Trigger(6501, 1, 0, 0)); peer.Expect(1, 417, [0, 2, 0, 101, 25, 0, 0]);
            peer.Select(prepared: true);
            string beforeClone = Snapshot();
            foreach (uint id in new uint[] { 26597, 26596, 0 })
            {
                peer.Send(568, Build(writer => writer.Write(id)));
                peer.Expect(1, 568, Build(writer => { writer.Write((byte)1); writer.Write(id); }));
                ReadAppearance(peer, null, cloneTitle: id); peer.Barrier();
                That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", peer.Id) is [[var saved]] &&
                    Convert.ToUInt32(saved) == id && Snapshot() == beforeClone,
                    "动画选择、替换和清除保存真实ID，不移动称号、不改核心UID、金币、成就或资产revision");
            }
            peer.Send(568, Build(writer => writer.Write(26059u))); peer.Expect(1, 568, [0, 2, 0]); peer.Barrier();
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", peer.Id) is [[0L]] &&
                Snapshot() == beforeClone, "不能借用同账号其它角色的称号，拒绝后连接与原数据保持");
            That(fixture.Query("SELECT remaining1,remaining2,remaining3 FROM character_title_progress WHERE quest_id=6501") is [[3L, 2L, 4L]] &&
                fixture.Query("SELECT section_key FROM character_title_sections WHERE character_id=$p0", peer.Id) is [[0L]],
                "旧的未完成三计数与已存章节标记按原值恢复，不被修复清空或换成其他任务");
            That(fixture.Query("SELECT quest_id,remaining1,remaining2,remaining3 FROM character_title_progress WHERE quest_id=6502") is [[6502L, 0L, 0L, 0L]] &&
                fixture.Query("SELECT count(*) FROM character_active_main_quests") is [[0L]] &&
                peer.TitleBooks[0].Body.Length == 45 && U32(peer.TitleBooks[0].Body, 13) == 26597,
                "真实任务3完成在选角补齐6502称号，普通任务槽不增加，完整实体已下发");
            peer.Send(417, Trigger(6501, 1, 0, 0));
            peer.Expect(1, 417, [1, 101, 25, 0, 0, 2, 0, 2, 0, 4, 0]);
            peer.Expect(0, 359); peer.Barrier();
            That(fixture.Query("SELECT remaining1,remaining2,remaining3 FROM character_title_progress WHERE quest_id=6501") is [[2L, 2L, 4L]] &&
                fixture.Query("SELECT count(*) FROM character_title_book_slots") is [[1L]], "旧未完成任务继续累计三计数，已有章节标记不阻挡任务推进或提前发奖");
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
            peer.Send(413, Book(3, 13, 26596, 0, 0)); peer.Expect(1, 413, Ack(3, 13, 0, 0));
            ReadAppearance(peer, 26596); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Equipment, 13)?.Core.ItemId == 26596 && fixture.Query("SELECT count(*) FROM character_title_book_slots WHERE list_type=18 AND slot_index=0") is [[0L]],
                "成就称号可以穿戴但不能取包，ACK先移动已有对象，再同步本人外观与完整穿戴");
        }
        for (ushort slot = 2; slot <= 4; slot++)
        {
            using var peer = Open(fixture);
            peer.Select(slot: slot);
            string before = Snapshot();
            peer.Send(417, Trigger(6583, 1, 0, 0));
            if (slot < 4)
            {
                peer.Expect(1, 417, [0, 2, 0, 183, 25, 0, 0]); peer.Barrier();
                That(Snapshot() == before, "五蛋或四成宠一蛋拒绝6583，进度、称号奖励、原实例和revision均不变");
            }
            else
            {
                peer.Expect(1, 417, [1, 183, 25, 0, 0, 0, 0, 0, 0, 0, 0]);
                var completed = peer.Expect(0, 360).Body;
                That(U32(completed, 0) == 6583 && U32(completed, 4) == 1 && U32(completed, 8) == 73 && U32(completed, 12) == 26691,
                    "五只真实成宠经417完成并发实际称号奖励");
                peer.Expect(0, 359); ReadBooks(peer); peer.Barrier();
                string after = Snapshot();
                peer.Send(417, Trigger(6583, 1, 0, 0));
                peer.Expect(1, 417, [0, 2, 0, 183, 25, 0, 0]); peer.Barrier();
                That(Snapshot() == after, "成宠称号完成后重放不重复发奖");
            }
        }
        using (var peer = Open(fixture))
        {
            peer.Select(slot: 5);
            peer.Send(412, Book(0, 9, 26059, 4, 10)); peer.Expect(1, 412, Ack(0, 9, 4, 10)); peer.Expect(0, 14); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.TitleEvent, 10, 5) is { Uid: var storedUid, Core: var storedCore } &&
                storedUid == sealedUid && storedCore == sealedCore, "封装称号放入自由格不提前消封且保留UID及完整核心");
            peer.Send(412, Book(3, 13, 26597, 4, 10)); peer.Expect(1, 412, Ack(3, 13, 4, 10));
            ReadAppearance(peer, 26059, 201); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Equipment, 13, 5) is { Uid: var equippedUid, Core: var equippedCore } &&
                equippedUid == sealedUid && equippedCore == sealedCore with { SealFlag = 0 } &&
                Read(InventoryListType.TitleEvent, 10, 5) is { Uid: var oldUid, Core: var oldCore } && oldUid == wornUid && oldCore == wornCore,
                "412同格交换反向穿戴仅清封装，双方UID、期限及其他核心不变");
            string afterSwap = Snapshot();
            peer.Send(412, Book(3, 13, 26597, 4, 10)); peer.Expect(1, 412, [0, 2, 0, 3, 0, 0, 0, 4, 0, 0, 0]); peer.Barrier();
            That(Snapshot() == afterSwap, "重放旧源模板的交换请求拒绝，不能再次换位或改写封装与revision");
            peer.Send(413, Book(0, 9, 26597, 4, 10)); peer.Expect(1, 413, Ack(0, 9, 4, 10)); peer.Expect(0, 14); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Main, 9, 5) is { Uid: var returnedUid, Core: var returnedCore } &&
                returnedUid == wornUid && returnedCore == wornCore, "交换后的原穿戴称号可完整取回背包，不复制实例");
            peer.Send(412, Book(3, 13, 26059, 4, 10)); peer.Expect(1, 412, Ack(3, 13, 4, 10));
            ReadAppearance(peer, null, 201); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Equipment, 13, 5) is null && Read(InventoryListType.TitleEvent, 10, 5)?.Uid == sealedUid,
                "卸下称号也立即发送无称号外观，不依赖重新选角或复制实例");
            peer.Send(413, Book(3, 13, 26059, 4, 10)); peer.Expect(1, 413, Ack(3, 13, 4, 10));
            ReadAppearance(peer, 26059, 201); ReadBooks(peer); peer.Barrier();
            string beforeClone = Snapshot();
            peer.Send(568, Build(writer => writer.Write(26597u)));
            peer.Expect(1, 568, [1, 0xe5, 0x67, 0, 0]);
            ReadAppearance(peer, 26059, 201, 26597); peer.Barrier();
            That(Snapshot() == beforeClone && Read(InventoryListType.Equipment, 13, 5)?.Uid == sealedUid &&
                Read(InventoryListType.Main, 9, 5)?.Uid == wornUid,
                "选择背包称号动画时仍穿戴原称号，双方UID及属性不变");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("titles-restarted");
        using (var peer = Open(fixture))
        {
            peer.Select(slot: 5);
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", peer.Id) is [[26597L]] &&
                CloneFromRoster(peer.Roster, 5) == 26597 && peer.PlayerBody[126] == 1 && U32(peer.PlayerBody, 215) == 26597,
                "既有服务重启路径恢复动画选择到数据库、轻量名册和本人Player，不依赖新增ACK或重新选择动画");
            That(Read(InventoryListType.Equipment, 13, 5) is { Uid: var equippedUid, Core: var equippedCore } &&
                equippedUid == sealedUid && equippedCore == sealedCore with { SealFlag = 0 } &&
                Read(InventoryListType.Main, 9, 5) is { Uid: var returnedUid, Core: var returnedCore } &&
                returnedUid == wornUid && returnedCore == wornCore,
                "服务重启及重选恢复交换消封结果和原称号，不重新封装或重造UID");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(prepared: true);
            That(Read(InventoryListType.Main, 10)?.Core == core && Read(InventoryListType.Equipment, 13)?.Core.ItemId == 26596 &&
                fixture.Query("SELECT count(*) FROM character_title_progress WHERE quest_id=6501 AND remaining1+remaining2+remaining3=0") is [[1L]],
                "服务重启恢复已穿戴/取出称号及完成计数，不重发奖励");
            peer.Send(413, Book(3, 13, 26597, 0, 1)); peer.Expect(1, 413, Ack(3, 13, 0, 1)); peer.Expect(0, 14);
            ReadAppearance(peer, 26597); ReadBooks(peer); peer.Barrier();
            That(Read(InventoryListType.Equipment, 13)?.Core.ItemId == 26597 && Read(InventoryListType.Main, 9)?.Core.ItemId == 26596 &&
                fixture.Query("SELECT count(*) FROM character_inventory_items WHERE json_extract(item_core,'$.ItemId')=26596") is [[2L]],
                "替换穿戴的旧称号完整回包，两个既有物理实例不因穿戴多一份");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(slot: 1);
            That(fixture.Query("SELECT quest_id FROM character_title_progress WHERE character_id=$p0 AND remaining1+remaining2+remaining3=0 ORDER BY quest_id", awakeningId)
                is [[6761L], [6762L]] && fixture.Query("SELECT section_key FROM character_title_sections WHERE character_id=$p0", awakeningId) is [[268L]],
                "二觉角色真实选角只补齐觉醒章节");
            var invitation = resources.Quests.Get(2670);
            var mail = QuestMailPlan.Read(invitation);
            That(mail.Time == 0 && mail.Enclosures.IsEmpty &&
                fixture.Query("SELECT d.quest_id,d.resource_path,m.body,m.gold,m.system_mail FROM mailbox_messages m LEFT JOIN quest_eligibility_mail_dispatches d ON d.mail_id=m.mail_id AND d.recipient_id=m.recipient_id WHERE m.recipient_id=$p0", awakeningId)
                    is [[2670L, var resourcePath, var body, 0L, 1L]] && Equals(resourcePath,invitation.Path) && Equals(body,mail.Body) &&
                fixture.Query("SELECT count(*) FROM mailbox_attachments a JOIN mailbox_messages m ON m.mail_id=a.mail_id WHERE m.recipient_id=$p0", awakeningId) is [[0L]] &&
                fixture.Query("SELECT json_extract(item_core,'$.ItemId') FROM character_inventory_items WHERE character_id=$p0 ORDER BY json_extract(item_core,'$.ItemId')", awakeningId)
                    is [[100331607L], [100331608L]], "二觉选角仅两件原觉醒称号；邮箱仅原2670无附件邀请，不寄超时空或其他章节称号");
        }
        using (var peer = Open(fixture))
        {
            peer.Select(slot: 4);
            That(fixture.Query("SELECT remaining1,remaining2,remaining3 FROM character_title_progress WHERE character_id=$p0 AND quest_id=6583", peer.Id)
                is [[0L, 0L, 0L]] && fixture.Query("SELECT count(*) FROM character_inventory_items WHERE character_id=$p0 AND json_extract(item_core,'$.ItemId')=26691", peer.Id)
                is [[1L]], "重启恢复成宠称号完成状态和唯一真实奖励实例");
            string before = Snapshot();
            peer.Send(417, Trigger(6583, 1, 0, 0)); peer.Expect(1, 417, [0, 2, 0, 183, 25, 0, 0]); peer.Barrier();
            That(Snapshot() == before, "重启后重复成宠称号请求仍不发奖或修改revision");
        }
        ushort cloneRuleSlot;
        ushort cloneRuleCharacterId;
        using (var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { }))
        {
            var created = repo.Create(owner, new("CloneRuleTcp", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = new([
                    new(0, new(InventoryListType.Main, 9), InventoryRewardGrantService.Create(resources.ItemResources.Items.Get(26597))),
                    new(0, new(InventoryListType.Main, 10), InventoryRewardGrantService.Create(jinErza)),
                    ..(only40TitleId != 0
                        ? [new InventoryItem(0, new(InventoryListType.Main, 11), InventoryRewardGrantService.Create(hiddenTarget))]
                        : Array.Empty<InventoryItem>())
                ])
            });
            cloneRuleSlot = created.Slot;
            cloneRuleCharacterId = created.Id;
        }
        using (var peer = Open(fixture))
        {
            byte[] cloneRuleMain = peer.Select(slot: cloneRuleSlot)
                .Single(packet => packet is { Kind: 0, Opcode: 13 } && packet.Body[0] == 0).Body;
            // 13号槽为空时：拒绝含39的2747531，允许普通称号26597；仅40来源只在真实PVF存在时覆盖。
            peer.Send(568, Build(writer => writer.Write(2747531u)));
            peer.Expect(1, 568, [0, 2, 0]); peer.Barrier();
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", cloneRuleCharacterId) is [[0L]],
                "TCP拒绝将带39的2747531设为克隆称号");

            if (only40TitleId != 0)
            {
                peer.Send(568, Build(writer => writer.Write((uint)only40TitleId)));
                peer.Expect(1, 568, Build(writer => { writer.Write((byte)1); writer.Write((uint)only40TitleId); }));
                ReadAppearance(peer, null, cloneTitle: (uint)only40TitleId); peer.Barrier();
                That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", cloneRuleCharacterId) is [[var savedOnly40]] &&
                    Convert.ToUInt32(savedOnly40) == (uint)only40TitleId,
                    "13号槽为空时仅带40不带39的真实称号可作为克隆源");
            }

            peer.Send(568, Build(writer => writer.Write(26597u)));
            peer.Expect(1, 568, Build(writer => { writer.Write((byte)1); writer.Write(26597u); }));
            ReadAppearance(peer, null, cloneTitle: 26597); peer.Barrier();

            // CMD19穿戴带40的称号到13号槽：自动将激活的clone_title_item_id清零并刷新外观。
            byte hiddenFromSlot = (byte)(only40TitleId != 0 ? 11 : 10);
            var cloneSource = Read(InventoryListType.Main, 9, cloneRuleSlot)!;
            var hiddenSource = Read(InventoryListType.Main, hiddenFromSlot, cloneRuleSlot)!;
            string walletsBefore = Wallets();
            peer.Send(19, MoveEquipment(0, hiddenFromSlot, hiddenTargetId, 3, 13, 0));
            peer.Expect(1, 19, MoveEquipmentAck(0, hiddenFromSlot, 3, 13));
            byte[] reloadedMain = peer.Expect(0, 13).Body;
            That(cloneRuleMain[0] == 0 && cloneRuleMain.Length == 5 + U16(cloneRuleMain, 3) * 123 &&
                reloadedMain[0] == 0 && reloadedMain.Length == 5 + U16(reloadedMain, 3) * 123,
                "普通称号穿戴严格接收完整Main13头与123B条目，不回退为1681新物品标记");
            byte[] expectedMain = Build(writer =>
            {
                writer.Write((byte)0); writer.Write(U16(cloneRuleMain, 1));
                writer.Write(checked((ushort)(U16(cloneRuleMain, 3) - 1)));
                for (int index = 0; index < U16(cloneRuleMain, 3); index++)
                {
                    int offset = 5 + index * 123;
                    if (U16(cloneRuleMain, offset) != hiddenFromSlot)
                        writer.Write(cloneRuleMain.AsSpan(offset, 123));
                }
            });
            That(reloadedMain.SequenceEqual(expectedMain),
                "Main完整重载仅移除已穿戴源槽，槽9动画来源、其他实体和全部虚拟钱包逐字节保留");
            ReadAppearance(peer, hiddenTargetId, cloneTitle: 0); peer.Barrier();
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", cloneRuleCharacterId) is [[0L]] &&
                Read(InventoryListType.Main, hiddenFromSlot, cloneRuleSlot) is null &&
                Read(InventoryListType.Main, 9, cloneRuleSlot) == cloneSource &&
                Read(InventoryListType.Equipment, 13, cloneRuleSlot) ==
                    hiddenSource with { Position = new(InventoryListType.Equipment, 13), Core = hiddenSource.Core with { SealFlag = 0 } } &&
                Wallets() == walletsBefore,
                "TCP穿戴带40称号清零DB与外观动画，保留双方UID及除穿戴消封外完整核心，角色及账号钱包不变");

            // 13号槽穿戴带40称号期间：CMD568拒绝选择26597，但允许0清空。
            peer.Send(568, Build(writer => writer.Write(26597u)));
            peer.Expect(1, 568, [0, 2, 0]);
            peer.Send(568, Build(writer => writer.Write(0u)));
            peer.Expect(1, 568, [1, 0, 0, 0, 0]); peer.Barrier();

            if (real.Fits(4, 10, hiddenTargetId))
            {
                // 称号簿CMD412将13号槽带40称号放入自由格(4,10)，再激活26597克隆，随后CMD413穿回13号槽：自动清零。
                peer.Send(412, Book(3, 13, hiddenTargetId, 4, 10));
                peer.Expect(1, 412, Ack(3, 13, 4, 10));
                ReadAppearance(peer, null, cloneTitle: 0); ReadBooks(peer); peer.Barrier();

                peer.Send(568, Build(writer => writer.Write(26597u)));
                peer.Expect(1, 568, Build(writer => { writer.Write((byte)1); writer.Write(26597u); }));
                ReadAppearance(peer, null, cloneTitle: 26597); peer.Barrier();

                peer.Send(413, Book(3, 13, hiddenTargetId, 4, 10));
                peer.Expect(1, 413, Ack(3, 13, 4, 10));
                ReadAppearance(peer, hiddenTargetId, cloneTitle: 0); ReadBooks(peer); peer.Barrier();
                That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", cloneRuleCharacterId) is [[0L]],
                    "称号簿CMD413穿戴带40称号到13号槽时自动清零clone_title_item_id");
            }
        }
        // 模拟历史存档在13槽穿戴带40称号时残留非零clone_title_item_id=26597，验证选角/登录自动清零并同步DB与外观包。
        fixture.Sql("UPDATE characters SET clone_title_item_id=26597 WHERE id=$p0", cloneRuleCharacterId);
        using (var peer = Open(fixture))
        {
            That(CloneFromRoster(peer.Roster, cloneRuleSlot) == 0, "登录名册自动将违反40规则的历史clone_title_item_id投影为0");
            peer.Select(slot: cloneRuleSlot);
            That(fixture.Query("SELECT clone_title_item_id FROM characters WHERE id=$p0", cloneRuleCharacterId) is [[0L]] &&
                U32(peer.PlayerBody, 127 + peer.PlayerBody[126] * 88) == 0,
                "选角加载违反40规则的历史存档时自动清零DB与本人Player外观");
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "TCP取放、重启和穿戴后外键完整");
        Console.WriteLine($"PASS {Count - checks} 称号进度、称号簿取放、穿戴和重启TCP断言");

        InventoryItem? Read(InventoryListType list, short slot, ushort characterSlot = 0)
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            return repo.LoadCharacter(owner, characterSlot).Inventory.Find(new(list, slot));
        }
        string Snapshot() => JsonSerializer.Serialize(new { Character = fixture.Query("SELECT revision,gold FROM characters"),
            Progress = fixture.Query("SELECT * FROM character_title_progress ORDER BY quest_id"),
            Items = fixture.Query("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid"),
            InventorySlots = fixture.Query("SELECT * FROM character_inventory_slots ORDER BY item_uid"),
            BookSlots = fixture.Query("SELECT * FROM character_title_book_slots ORDER BY item_uid") });
        string Wallets()
        {
            using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
            var inventory = repo.LoadCharacter(owner, cloneRuleSlot).Inventory;
            return JsonSerializer.Serialize(new { inventory.Gold, inventory.ReviveCoins, inventory.WinPoints, inventory.AccountWallet });
        }
        static byte[] Trigger(int id, ushort first, ushort second, ushort third) => Build(writer =>
            { writer.Write(id); writer.Write(first); writer.Write(second); writer.Write(third); });
        static byte[] Book(int list, int slot, int item, int category, int index) => Build(writer =>
            { writer.Write(list); writer.Write(slot); writer.Write(item); writer.Write(category); writer.Write(index); });
        static byte[] Ack(int list, int slot, int category, int index) => Build(writer =>
            { writer.Write((byte)1); writer.Write(list); writer.Write(slot); writer.Write(category); writer.Write(index); });
        static byte[] MoveEquipment(byte fromList, short fromSlot, int fromId, byte toList, short toSlot, int toId) => Build(writer =>
            { writer.Write(fromList); writer.Write(fromSlot); writer.Write(fromId); writer.Write(1);
              writer.Write(toList); writer.Write(toSlot); writer.Write(toId); writer.Write(0); writer.Write(-1); writer.Write((ushort)0); });
        static byte[] MoveEquipmentAck(byte fromList, short fromSlot, byte toList, short toSlot) => Build(writer =>
            { writer.Write((byte)1); writer.Write(fromList); writer.Write(fromSlot); writer.Write(1); writer.Write(toList); writer.Write(toSlot); });
        static void ReadAppearance(TcpPeer peer, int? title, int blacklistCount = 0, uint cloneTitle = 0)
        {
            byte[] actor = peer.Expect(0, 2).Body;
            int count = checked(133 + (int)U32(actor, 123));
            That(actor[0] == 0 && U16(actor, 121) == peer.Id && actor[count - 1] == 0 && actor[count] == (title is null ? 0 : 1),
                "称号簿穿脱使用同一角色和城镇状态刷新本人，不靠切图重建");
            if (title is { } item)
                That(actor[count + 1] == 13 && U32(actor, count + 2) == item, "Actor实际外观表包含本次已提交的13槽称号");
            That(U32(actor, count + 1 + actor[count] * 25) == cloneTitle,
                "Actor独立动画字段与选择一致，实际穿戴仍在原13槽");
            int pet = count + 1 + actor[count] * 25 + 22;
            int score = checked(pet + 11 + (int)U32(actor, pet + 4));
            That(U32(actor, score) == blacklistCount, "称号刷新保留独立被拉黑计分，不用装备ID覆盖惩罚字段");
            byte[] player = peer.Expect(0, 2).Body;
            That(player[0] == 1 && U16(player, 22) == peer.Id && player[126] == (title is null ? 0 : 1) &&
                (title is null || player[127] == 13 && U32(player, 128) == title),
                "Actor后补完整Player穿戴，不发送列表3重复创建已由ACK移动的称号");
            // 本夹具的普通称号没有变长词条，每行88字节；动画位于列表之后而不是物品核心内。
            That(U32(player, 127 + player[126] * 88) == cloneTitle, "后发Player保留同一动画，不把Actor刚更新的选择清零");
        }
        static uint CloneFromRoster(byte[] roster, ushort slot)
        {
            int at = 15;
            for (int row = 0; row < roster[1]; row++)
            {
                ushort current = U16(roster, at); at += 2;
                at += checked(4 + (int)U32(roster, at)) + 15;
                int equipment = roster[at++];
                at += equipment * 25;
                uint clone = checked((uint)U32(roster, at));
                if (current == slot) return clone;
                at += 48;
            }
            throw new InvalidDataException("名册没有目标称号测试角色");
        }
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
