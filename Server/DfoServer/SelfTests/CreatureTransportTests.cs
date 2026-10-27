using DfoServer.Game.Characters;
using DfoServer.Game.Creatures;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using Microsoft.Data.Sqlite;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class CreatureTransportTests
{
    public static void RunCore()
    {
        That(CreatureTransportRequests.TryRead(Convert.FromHexString("520010000102000000"), out var request) &&
            request == new CreatureTransportRequest(82, 16, 1, 2), "557实收9B保留源、Pet目标、模式及原名册行号");
        That(!CreatureTransportRequests.TryRead(new byte[8], out _) && !CreatureTransportRequests.TryRead(new byte[10], out _),
            "跨界请求必须完整且不接受多余尾部");
        var book = CreatureChangeTests.Book;
        var raw = CreatureChangeTests.Raw with { ActionDeclaration = new("[creature transport stone]", [4012]),
            HasActionUsablePlace = true, DeclaredActionUsablePlaces = ["[seria room]"], DeclaredNeededMaterials = [999,9] };
        var file = new RecoveryConsumableFile(book.Id, "stackable/book.stk", raw, 1000, null, null, null, ["cool time"], []);
        var definition = ActionConsumableDefinition.Parse(file, book)!;
        That(definition is { Label: "[creature transport stone]", Parameters: [4012], CoolTime: 1000 } &&
            !definition.AllowsPlace("[village]"), "宠物跨界按动作解析，赛丽亚地点来自PVF，need material不成为消费成本");
        var clock = new ConsumableUseSessionTests.FakeClock();
        var policy = new CreatureTransportMailPolicy(7);
        var expiry = policy.Create(490705421, 1792512000);
        That(expiry == new CreatureTransportMailExpiry(490705421, 7, 365, 1792512000, 1792512000L + 365L * 86400),
            "跨界邮件策略保留可信SKU、revision和固定365天期限");
        Reject<InvalidDataException>(() => new CreatureTransportMailPolicy(0).Create(490705421, 0), "无效策略版本拒绝");
        Reject<InvalidDataException>(() => policy.Create(0, 0), "无效来源拒绝");
        Reject<InvalidDataException>(() => policy.Create(490705421, -1), "负创建时刻拒绝");
        Reject<OverflowException>(() => policy.Create(490705421, long.MaxValue), "邮件截止溢出拒绝");
        var source = new InventoryItem(77, new(InventoryListType.Main, 82), new(book.Id, ItemKind.Consumable, 2));
        var pet = new InventoryItem(78, new(InventoryListType.Pet, 16), new(7001, ItemKind.Creature, 78)
            { EnchantCardId = 1234, Extension27 = 99, TradeRestriction = 1 })
            { Creature = new() { Name = "Keep", Satiety = 41, ExpireTime = (int)clock.GetUtcNow().ToUnixTimeSeconds()+100 } };
        var character = new Character(1, 0, "Sender", 1, new(1,0,1,1), null) { Inventory = new([source, pet]) };
        var recipient = new CharacterRosterEntry(2, 7, "Receiver", 0, 0, 1, []);
        ItemTemplate Item(int id) => id == book.Id ? book : new(id, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "pet")
            { CreatureSubType = 0 };
        var service = new CreatureTransportService(Item, _ => definition, CreatureSystemTests.Synthetic(), null!, () => policy, clock);
        That(service.Plan(character, recipient, request, "[seria room]", out var plan) == 0 &&
            plan!.Pet == pet && plan.Stone == source && plan.MailExpiry.SourceItemId == source.Core.ItemId &&
            plan.Inventory.After.Find(pet.Position) is null &&
            plan.Inventory.After.Find(source.Position)!.Core.Count == 1 && plan.Inventory.After.ConsumableStates.Length == 1 &&
            character.Inventory.Find(pet.Position) == pet && !plan.Inventory.CharacterPrecondition!.Matches(character, pet.ExpireTime),
            "绑定及有限期成宠可专用跨界，规划保留原实例并同笔扣石头和冷却，最早到期时不可提交");
        That(service.Plan(character, recipient, request, "[village]", out _) == 21 &&
            service.Plan(character, recipient with { Id = character.Id }, request, "[seria room]", out _) == 7 &&
            service.Plan(character, recipient, request with { Mode = 0 }, "[seria room]", out _) == 7,
            "错误地点、自身及非宠物跨界模式不消费");
        foreach (var invalid in new[] { pet with { Position = new(InventoryListType.Equipment,26) },
            pet with { Core = pet.Core with { EquipmentLockId = 1 } }, pet with { Core = pet.Core with { IsRental = true } },
            pet with { Core = pet.Core with { SortLockFlag = 1 } },
            pet with { Creature = pet.Creature! with { ExpireTime = (int)clock.GetUtcNow().ToUnixTimeSeconds() } } })
            That(service.Plan(character with { Inventory = new([source,invalid]) }, recipient, request, "[seria room]", out _) != 0,
                "穿戴、锁定、租赁和到期宠物不能被跨界请求取走");
        var eggService = new CreatureTransportService(id => Item(id) with { CreatureSubType = 1 }, _ => definition,
            CreatureSystemTests.Synthetic(), null!, () => policy, clock);
        That(eggService.Plan(character, recipient, request, "[seria room]", out _) == 7,
            "宠物蛋不能取得成宠跨界资格");
        var unknown = new CreatureTransportService(Item, _ => null, CreatureSystemTests.Synthetic(), null!, () => policy, clock);
        var springSource = source with { Core = source.Core with { ItemId = 490705421 } };
        That(unknown.Plan(character with { Inventory = new([springSource, pet]) }, recipient, request, "[seria room]", out _) == 7,
            "421的365天策略不让缺少真实跨界动作定义的物品取得资格");
        var committed = character with { Inventory = plan!.Inventory.After };
        Bytes(CreatureTransportPackets.Ack(request, new(true,0) { Receipt = new(committed,1,1) }),
            [1,82,0,16,0,1,0,0,0,1], "557成功完整10B，第三个u16为实际剩余量，末项mode1");
        Bytes(CreatureTransportPackets.Ack(request, InventoryResult.Reject(17)), [0,17,0], "557失败使用已确认的u16不存在错误码");
        MailExpiryPersistence();
        Console.WriteLine("PASS 宠物跨界动作、资格、完整实例与557字节合同断言");
    }

    static void MailExpiryPersistence()
    {
        using var fixture = new MailboxPersistenceTests.Fixture();
        var clock = new ConsumableUseSessionTests.FakeClock();
        var policy = new CreatureTransportMailPolicy(7);
        long created = clock.GetUtcNow().ToUnixTimeSeconds();
        var recipient = fixture.Repository.Create(fixture.SenderOwner,
            new("PetTarget", fixture.Sender.Position, null));
        var actual = new Dictionary<int, long>();
        foreach (int sourceId in new[] { 10007991, 490705421 })
        {
            var stone = CreatureChangeTests.Book with { Id = sourceId };
            var definition = new ActionConsumableDefinition(stone, "[creature transport stone]", [4012],
                1000, -sourceId, 0, 0, ["[seria room]"]);
            var sender = fixture.Repository.Create(fixture.SenderOwner,
                new(sourceId == 490705421 ? "SpringPet" : "LegacyPet", fixture.Sender.Position, null)
                {
                    Inventory = new([
                        new(0, new(InventoryListType.Main, 82), new(sourceId, ItemKind.Consumable, 2)),
                        new(0, new(InventoryListType.Pet, 16), new(7001, ItemKind.Creature, 0)
                            { EnchantCardId = 1234, Extension27 = 99, TradeRestriction = 1 })
                        { Creature = new() { Name = "Keep", Satiety = 41, ExpireTime = checked((int)created + 100) } }
                    ])
                });
            var pet = sender.Inventory.Find(new(InventoryListType.Pet, 16))!;
            ItemTemplate Item(int id) => id == sourceId ? stone : new(id, ItemKind.Creature, 26, 1, 0, 1,
                0, 0, 0, "[trade]", ushort.MaxValue, 0, "pet") { CreatureSubType = 0 };
            var service = new CreatureTransportService(Item, id => id == sourceId ? definition : null,
                CreatureSystemTests.Synthetic(), fixture.Repository, () => policy, clock);
            That(service.Plan(sender, CharacterRosterEntry.FromCharacter(recipient), new(82, 16, 1, 1),
                "[seria room]", out var plan) == 0, "真实SKU跨界石通过既有动作与成宠资格规划");
            var frozenPlan = plan!;
            if (sourceId == 490705421)
            {
                foreach (var invalid in new[]
                {
                    plan! with { Stone = plan!.Stone with { Uid = plan.Stone.Uid + 100 } },
                    plan! with { Stone = plan!.Stone with { Position = new(InventoryListType.Main, 83) } },
                    plan! with { Stone = plan!.Stone with { Core = plan.Stone.Core with { ItemId = 10007991 } } },
                    plan! with { MailExpiry = plan!.MailExpiry with { SourceItemId = 10007991 } },
                    plan! with { MailExpiry = plan!.MailExpiry with { PolicyRevision = 8 } },
                    plan! with { MailExpiry = plan!.MailExpiry with { Days = 15 } },
                    plan! with { MailExpiry = plan!.MailExpiry with { Created = created + 1 } },
                    plan! with { MailExpiry = plan!.MailExpiry with { ExpiresAt = plan.MailExpiry.ExpiresAt + 1 } },
                    plan! with { Recipient = plan!.Recipient with { Slot = 23 } },
                    plan! with { Recipient = CharacterRosterEntry.FromCharacter(fixture.Other) },
                    plan! with { Recipient = CharacterRosterEntry.FromCharacter(sender) }
                }) Unchanged(() => service.Commit(fixture.SenderOwner, invalid), "伪造石UID/SKU、邮件期限或目标身份整笔拒绝");
                var unpaid = new InventoryPlan(plan!.Inventory.Before, plan.Inventory.After with
                {
                    Items = plan.Inventory.After.Items.Replace(plan.Inventory.After.Find(plan.Stone.Position)!, plan.Stone)
                }, plan.Inventory.Reason, plan.Inventory.CharacterPrecondition);
                Unchanged(() => service.Commit(fixture.SenderOwner, plan with { Inventory = unpaid }), "未准确扣一颗原石的计划拒绝");
                policy = new(8);
                Unchanged(() => service.Commit(fixture.SenderOwner, plan!), "当前策略revision改变后拒绝旧计划");
                policy = new(7);
                var deadlineClock = new ConsumableUseSessionTests.FakeClock();
                var deadlineService = new CreatureTransportService(Item, _ => definition, CreatureSystemTests.Synthetic(),
                    fixture.Repository, () => policy, deadlineClock);
                That(deadlineService.Plan(sender, plan!.Recipient, new(82, 16, 1, 1), "[seria room]", out var expired) == 0,
                    "有限期宠物在到期前可规划");
                deadlineClock.Advance(TimeSpan.FromSeconds(100));
                Unchanged(() => deadlineService.Commit(fixture.SenderOwner, expired!), "规划后宠物到期不能因365天邮件延长资格");
                var stoneBefore = sender.Inventory.Find(new(InventoryListType.Main, 82))!;
                sender = fixture.Repository.CommitInventory(fixture.SenderOwner, sender,
                    new(sender.Inventory, sender.Inventory with { Items = sender.Inventory.Items.Replace(stoneBefore,
                        stoneBefore with { Core = stoneBefore.Core with { ItemId = 10007991 } }) }, "replace_transport_source"));
                Unchanged(() => service.Commit(fixture.SenderOwner, plan!), "源SKU及revision已经变化后拒绝旧计划");
                var replaced = sender.Inventory.Find(stoneBefore.Position)!;
                sender = fixture.Repository.CommitInventory(fixture.SenderOwner, sender,
                    new(sender.Inventory, sender.Inventory with { Items = sender.Inventory.Items.Replace(replaced,
                        stoneBefore with { Uid = 0 }) }, "replace_transport_uid"));
                That(sender.Inventory.Find(stoneBefore.Position)!.Uid != stoneBefore.Uid, "替换源石取得新的真实UID");
                That(service.Plan(sender, plan!.Recipient, new(82, 16, 1, 1), "[seria room]", out plan) == 0,
                    "合法新源实例按当前revision重新规划");
                frozenPlan = plan!;
                foreach (string table in new[] { "mailbox_attachments", "mailbox_audit_log" })
                {
                    fixture.Execute($"CREATE TRIGGER fail_transport AFTER INSERT ON {table} BEGIN SELECT RAISE(ABORT,'transport rollback'); END");
                    string before = fixture.Snapshot();
                    Reject<SqliteException>(() => service.Commit(fixture.SenderOwner, frozenPlan), "同一冻结计划的附件或审计晚期SQL失败");
                    That(fixture.Snapshot() == before, "晚期SQL失败回滚源石、冷却、宠物UID/详情、revision和全部邮件资产");
                    fixture.Execute("DROP TRIGGER fail_transport");
                }
                clock.Advance(TimeSpan.FromSeconds(1));
            }
            var commit = service.Commit(fixture.SenderOwner, frozenPlan);
            actual[sourceId] = Convert.ToInt64(fixture.Query($"SELECT expires_at FROM mailbox_messages WHERE mail_id={commit.MailId}"));
            var restored = new CharacterRepository(fixture.Path).LoadMailbox(fixture.SenderOwner, recipient.Id, created)
                .Entries.Single(mail => mail.Id == commit.MailId).Attachments.Single().Item;
            That(restored.Uid == pet.Uid && restored.Core == pet.Core && restored.Creature == pet.Creature &&
                restored.Creature!.ExpireTime == created + 100 && commit.Character.Inventory.Find(pet.Position) is null &&
                commit.Character.Inventory.Find(new(InventoryListType.Main, 82))!.Core.Count == 1,
                "跨界托管重开仓储保留原UID、完整Core和宠物自身100秒寿命，仅扣一颗源石");
            That(Convert.ToInt64(fixture.Query($"SELECT created_at FROM mailbox_messages WHERE mail_id={commit.MailId}")) == frozenPlan.Created &&
                actual[sourceId] == frozenPlan.MailExpiry.ExpiresAt,
                "原冻结计划晚期失败回滚后直接延时重提，保存原created和expiry，不换源或重新规划");
            Unchanged(() => service.Commit(fixture.SenderOwner, plan!), "已提交计划重放不再次扣石或寄件");
        }
        That(actual[10007991] == created + 15L * 86400, "既有跨界石邮件保持15天");
        That(actual[490705421] == created + 365L * 86400,
            $"421跨界邮件365天：created={created}, actual={actual[490705421]}, expected={created + 365L * 86400}");
        var takeoverSender = fixture.Repository.Create(fixture.SenderOwner,
            new("OwnerPet", fixture.Sender.Position, null)
            {
                Inventory = new([
                    new(0, new(InventoryListType.Main, 82), new(490705421, ItemKind.Consumable, 1)),
                    new(0, new(InventoryListType.Pet, 16), new(7001, ItemKind.Creature, 0)) { Creature = new() }
                ])
            });
        var takeoverStone = CreatureChangeTests.Book with { Id = 490705421 };
        var takeoverDefinition = new ActionConsumableDefinition(takeoverStone, "[creature transport stone]", [4012], 0, 0, 0, 0, []);
        var takeoverService = new CreatureTransportService(id => id == takeoverStone.Id ? takeoverStone :
            new(id, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "pet") { CreatureSubType = 0 },
            _ => takeoverDefinition, CreatureSystemTests.Synthetic(), fixture.Repository, () => policy, clock);
        var room = fixture.Sender.Position;
        var towns = new TownCatalog(new() { [(room.Town, room.Area)] = new(room.Town, room.Area, 1, [], [], [], room,
            new("synthetic.map", [new(new(0, 0, 1000, 1000), [], [])], [], true, []), [], []) },
            room, room, new(0, [], []), [], new Dictionary<uint, uint[]>());
        var session = new GameSession(fixture.Repository, towns, fixture.SenderOwner);
        var roster = session.PrepareRoster();
        session.MarkRosterPublished(roster);
        session.Select(takeoverSender.Slot);
        session.MarkReady();
        var invalidatingService = new CreatureTransportService(id => id == takeoverStone.Id ? takeoverStone :
            new(id, ItemKind.Creature, 26, 1, 0, 1, 0, 0, 0, "[trade]", ushort.MaxValue, 0, "pet") { CreatureSubType = 0 },
            _ => takeoverDefinition, CreatureSystemTests.Synthetic(), null!, () =>
            {
                session.ReturnToRoster();
                return policy;
            }, clock);
        string selectionBefore = fixture.Snapshot();
        uint recipientIndex = checked((uint)Array.FindIndex(roster.Entries.ToArray(), entry => entry.Id == recipient.Id));
        That(!session.TransportCreature(invalidatingService, new(82, 16, 1, recipientIndex)).Inventory.Accepted &&
            fixture.Snapshot() == selectionBefore, "锁外规划中换角及名册失效后回锁拒绝，资产保持原样");
        takeoverSender = fixture.Repository.LoadCharacter(fixture.SenderOwner, takeoverSender.Slot);
        That(takeoverService.Plan(takeoverSender, CharacterRosterEntry.FromCharacter(recipient), new(82, 16, 1, 1),
            "[seria room]", out var takeoverPlan) == 0, "owner接管前合法计划已完成");
        using var currentOwner = Task.Run(() => fixture.Owners.Acquire(fixture.SenderOwner.Account, () => { })).GetAwaiter().GetResult();
        Unchanged(() => takeoverService.Commit(fixture.SenderOwner, takeoverPlan!), "并发owner接管后旧generation不能提交合法计划");
        var currentCommit = takeoverService.Commit(currentOwner, takeoverPlan!);
        That(currentCommit.Character.Inventory.Find(new(InventoryListType.Main, 82)) is null,
            "新owner仍可按当前源实例提交且最后一颗跨界石正确清槽");

        void Unchanged(Action action, string label)
        {
            string before = fixture.Snapshot();
            Reject<InvalidOperationException>(action, label);
            That(fixture.Snapshot() == before, label + "且全部资产不变");
        }
    }
}
