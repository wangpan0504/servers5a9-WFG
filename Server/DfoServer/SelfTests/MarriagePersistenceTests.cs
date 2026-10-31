using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auth;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Marriage;
using DfoServer.GameWorld.Items;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MarriagePersistenceTests
{
    public static void RunCore()
    {
        int checks = Count;
        InventoryDeadlines();
        HomeTravelAfterDivorce();
        using (var f = new Fixture())
        {
            var first = f.First; var second = f.Second;
            var relation = f.NewRelation(first, second);
            var firstPlan = Pay(first, 50); var secondPlan = Pay(second, 20);
            var delivery = f.Delivery("create", second);
            long itemsBefore = f.Count("character_inventory_items");
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, first, firstPlan, null, relation),
                "单人不能新建婚姻关系");
            f.Sql("CREATE TRIGGER fail_marriage_mail AFTER INSERT ON mailbox_audit_log BEGIN SELECT RAISE(ABORT,'marriage mail rollback'); END");
            Reject<SqliteException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
                firstPlan, secondPlan, null, relation, [delivery]), "双方库存和关系写入后的邮件失败");
            That(f.Count("marriage_records") == 0 && f.Count("marriage_active_members") == 0 && f.Count("mailbox_messages") == 0 &&
                f.Count("character_inventory_items") == itemsBefore && f.ReadFirst().Inventory.Gold == 1000 &&
                f.ReadSecond().Inventory.Gold == 1000 && f.ReadFirst().Revision == first.Revision && f.ReadSecond().Revision == second.Revision,
                "末段邮件失败回滚关系、双方索引、金币、实例及角色版本");
            f.Sql("DROP TRIGGER fail_marriage_mail");

            var created = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
                firstPlan, secondPlan, null, relation, [delivery]);
            var createdSecond = created.Second ?? throw new InvalidOperationException("Missing second marriage character");
            That(created.Record.Revision == 1 && created.First.Inventory.Gold == 950 && createdSecond.Inventory.Gold == 980 &&
                created.First.Revision == first.Revision + 1 && createdSecond.Revision == second.Revision + 1 &&
                created.First.Marriage?.Id == relation.Id && createdSecond.Marriage?.Id == relation.Id && f.Count("marriage_active_members") == 2,
                "创建将双方扣款、关系及双方实际快照一次提交");
            That(f.Repository.FindMarriage(first.Id)?.Id == relation.Id && f.Repository.FindMarriage(second.Id)?.Id == relation.Id &&
                f.Repository.FindMarriage(relation.Id)?.ContextId == relation.ContextId &&
                f.Repository.FindMarriageByContext(relation.ContextId)?.Id == relation.Id &&
                f.Repository.FindLatestMarriage(first.Id)?.Id == relation.Id, "双方、唯一上下文、关系标识和历史查询一致，支持高位u32上下文");
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
                firstPlan, secondPlan, null, relation, [delivery]), "提交成功后的创建计划不可重放");
            That(f.ReadFirst().Inventory.Gold == 950 && f.Count("mailbox_messages") == 1, "创建重放不再次扣费或投递");

            var current = f.Repository.FindMarriage(relation.Id)!;
            var next = current with { Revision = 2, PrepareClears = 1 };
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, first, Pay(first, 1), current, next),
                "关系版本正确也不能使用旧角色库存版本");
            var alteredBefore = current with { PrepareClears = 9 };
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, f.ReadFirst(), Noop(f.ReadFirst()), alteredBefore, next),
                "同revision的伪造聚合快照也不能覆盖当前内容");
            var liveFirst = f.ReadFirst();
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, liveFirst, Pay(liveFirst, 1),
                current, next, [delivery with { Gold = delivery.Gold + 1 }]), "已用邮件key不能换资产，失败回滚本次关系及扣款");
            That(f.Repository.FindMarriage(relation.Id)?.Revision == 1 && f.ReadFirst().Inventory.Gold == 950 &&
                f.Count("mailbox_messages") == 1 && f.Count("character_inventory_items") == itemsBefore + 1,
                "陈旧角色、伪造聚合及邮件冲突都不留下部分写入");

            var occupied = f.NewRelation(f.ReadFirst(), f.Guest, context: 42);
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.GuestOwner,
                f.ReadFirst(), f.Guest, Noop(f.ReadFirst()), Noop(f.Guest), null, occupied), "有效成员唯一性拒绝另一段关系");
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.FirstOwner,
                f.ReadFirst(), f.ReadFirst(), Noop(f.ReadFirst()), Noop(f.ReadFirst()), null, relation), "同账号不能建立婚姻");
            That(f.Count("marriage_records") == 1 && f.ReadGuest().Inventory.Gold == 1000, "唯一性失败不占新关系也不影响来宾资产");

            Guid clear = Guid.NewGuid(), gift = Guid.NewGuid();
            var guest = f.ReadGuest();
            var guestUpdate = current with
            {
                Revision = 2,
                Members = current.Members.SetItem(first.Id, new() { LovePoints = 7, LoveDay = 20261026, CapsulesPending = 1 }),
                Tree = new() { Seed = 10002766, Expiry = 5000, Growth = 10, WaterDay = 20261026, Waters = 1 },
                Storage = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty.Add(0, new(10002797, 2)),
                Furniture = ImmutableDictionary<ushort, MarriageFurnitureStack>.Empty.Add(25, new(10002802, 1)),
                Comments = [new(1, guest.Id, guest.Name, 1100, "祝福")], ClearEvents = ImmutableHashSet.Create(clear),
                Wedding = new()
                {
                    Id = Guid.NewGuid(), Type = 0, PayerId = first.Id, PaidGold = 50, ReservedAt = 1000,
                    StartsAt = 1200, EndsAt = 1500, SelectionSeconds = 300, State = 1,
                    InviteRemaining = ImmutableDictionary<ushort, int>.Empty.Add(first.Id, 9).Add(second.Id, 10),
                    Invitations = [new() { CharacterId = guest.Id, InviterId = first.Id, Name = guest.Name, State = 1, SentAt = 1100 }],
                    Gifts = [new() { Id = gift, CharacterId = guest.Id, Name = guest.Name, Gold = 10, SentAt = 1100, Text = "新婚快乐" }]
                }
            };
            var guestCommit = f.Repository.CommitMarriageSingle(f.GuestOwner, guest, Pay(guest, 10), current, guestUpdate);
            That(guestCommit.First.Inventory.Gold == 990 && guestCommit.First.Marriage is null &&
                f.ReadFirst().Inventory.Gold == 950 && f.ReadFirst().Revision == created.First.Revision,
                "来宾礼金只修改来宾库存与共享关系，不改写离线配偶资产快照");
            var guestDeletion = f.Repository.LoadDeletionTarget(f.GuestOwner, guest.Slot);
            That(guestDeletion.MarriageBusy, "未结清礼金的来宾保留回礼及失败退款收件身份");
            Reject<InvalidDataException>(() => f.Repository.Delete(f.GuestOwner, guestDeletion),
                "来宾没有有效婚约时，删角事务仍拒绝丢弃未结清礼金");
            Reject<InvalidOperationException>(() => f.Repository.Delete(f.GuestOwner, guestDeletion with { MarriageBusy = false }),
                "旧删角计划不能绕过事务内礼金占用复验");
            var reopened = new CharacterRepository(f.Path);
            var restored = reopened.FindMarriage(relation.Id)!;
            That(restored.Revision == 2 && restored.Members[first.Id].LovePoints == 7 && restored.Tree?.Growth == 10 &&
                restored.Storage[0] == new MarriageFurnitureStack(10002797, 2) && restored.Furniture[25].ItemId == 10002802 &&
                restored.Comments[0].Text == "祝福" && restored.ClearEvents.SetEquals([clear]) &&
                restored.Wedding is { } restoredWedding && restoredWedding.Gifts[0].Id == gift &&
                restoredWedding.Gifts[0].Text == "新婚快乐" && restoredWedding.InviteRemaining[first.Id] == 9,
                "重开库恢复完整成员、家具、树、留言、清图集合和婚礼支付账本");
            Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.GuestOwner, guest, Pay(guest, 10), current, guestUpdate),
                "原来宾礼金计划重放被关系CAS拒绝");

            var end = restored with { Revision = 3, State = 0, EndedAt = 2000 };
            liveFirst = f.ReadFirst();
            var ended = f.Repository.CommitMarriageSingle(f.FirstOwner, liveFirst, Noop(liveFirst), restored, end,
                [delivery with { Created = 1500 }]);
            That(ended.First.Marriage is null && ended.First.Revision == liveFirst.Revision && f.Count("marriage_active_members") == 0 &&
                f.Repository.FindMarriage(first.Id) is null && f.Repository.FindMarriage(second.Id) is null &&
                f.Repository.FindMarriage(relation.Id) is { State: 0, Revision: 3 } &&
                f.Repository.FindLatestMarriage(first.Id)?.EndedAt == 2000 && f.Count("mailbox_messages") == 1,
                "终止只释放有效成员并保留历史，零资产计划不改角色版本，原内容邮件幂等");
            var duplicateContext = f.NewRelation(f.ReadFirst(), f.ReadSecond()) with { CreatedAt = 3000 };
            Reject<SqliteException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, f.ReadFirst(), f.ReadSecond(),
                Noop(f.ReadFirst()), Noop(f.ReadSecond()), null, duplicateContext), "已结束关系的上下文仍不得复用");
            var remarriage = duplicateContext with { ContextId = 43 };
            var remarried = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, f.ReadFirst(), f.ReadSecond(),
                Noop(f.ReadFirst()), Noop(f.ReadSecond()), null, remarriage);
            That(remarried.Record.Id != relation.Id && f.Count("marriage_records") == 2 && f.Count("marriage_active_members") == 2 &&
                f.Repository.FindLatestMarriage(first.Id)?.Id == remarriage.Id && f.Repository.FindMarriage(relation.Id)?.State == 0,
                "结束后允许建立新关系，历史记录保留并由时间定位最近一次关系");
        }
        PairRollbackAndOwners();
        WeddingQuestIsolation();
        WeddingRecoverySettlement();
        WeddingHallTravel();
        WeddingAdmissionGifts();
        Console.WriteLine($"PASS {Count - checks} marriage persistence transaction assertions");
    }

    static void HomeTravelAfterDivorce()
    {
        using var f = new Fixture();
        var a = f.First; var b = f.Second;
        var relation = f.NewRelation(a, b) with { State = 4, MarriedAt = 1000 };
        var created = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, a, b, Noop(a), Noop(b), null, relation);
        var inside = f.Repository.Save(f.FirstOwner, created.First, new(101, 0, 300, 200), a.Return);
        f.Repository.CommitMarriageSettlement(created.Record, MarriageWeddingRules.EndRelationship(created.Record, 2000).Record);
        Reject<InvalidOperationException>(() => f.Repository.Save(f.FirstOwner, inside, new(101, 0, 301, 200), a.Return),
            "关系已解除而角色revision未变时，旧在线快照仍不能再次写婚房位置");
        That(f.ReadFirst().Revision == inside.Revision && f.ReadFirst().Position == inside.Position,
            "婚房资格在位置事务内拒绝，角色与位置无副作用");
    }

    static void WeddingHallTravel()
    {
        using var f = new Fixture();
        var record = f.NewRelation(f.First, f.Second) with
        {
            State = 2,
            Wedding = new() { Id = Guid.NewGuid(), PayerId = f.First.Id, State = 1, HallArea = 8,
                ServerIndex = 1, ChannelNumber = 10, StartsAt = 2000, EndsAt = 2060,
                Invitations = [new() { CharacterId = f.Guest.Id, InviterId = f.First.Id, Name = f.Guest.Name }] }
        };
        var created = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, f.First, f.Second,
            Noop(f.First), Noop(f.Second), null, record);
        var destination = new DfoServer.GameWorld.Towns.Location(39, 8, 100, 100);
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageHallTravel(f.GuestOwner, f.Guest, record,
            destination, f.Guest.Position, 1500), "只持请柬但没有实际礼金记录的来宾不能绕过入场");
        var entered = f.Repository.CommitMarriageHallTravel(f.FirstOwner, created.First, record, destination, f.First.Position, 1500);
        That(entered.WeddingHall is { } hall && hall.CeremonyId == record.Wedding.Id && hall.RelationshipId == record.Id &&
            f.ReadFirst().Position == destination && f.ReadFirst().Return == f.First.Position,
            "婚礼位置、返城来源和角色资产版本同事务保存，场景只绑定实际入场的婚礼实例");
        var withAssets = f.Repository.Save(f.FirstOwner, entered, entered.Position, entered.Return, Pay(entered, 1));
        That(withAssets.WeddingHall == entered.WeddingHall && withAssets.Inventory.Gold == 999,
            "礼堂内资产提交保留本次选角场景身份，不从SQL重读成未授权场景");
        var withInvitation = f.Repository.CommitMarriageSingle(f.FirstOwner, withAssets, Noop(withAssets), record,
            record with { Revision = 2 });
        That(withInvitation.First.WeddingHall == entered.WeddingHall,
            "请帖等无资产差额的关系提交也保留礼堂实例身份");
        record = withInvitation.Record;
        var cancelled = record with { Revision = 3, State = 1, Wedding = record.Wedding! with { State = 3 } };
        f.Repository.CommitMarriageSettlement(record, cancelled);
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageHallTravel(f.SecondOwner, created.Second!, record,
            destination, f.Second.Position, 1501), "取消后旧预约快照不能提交另一方的位置或场景身份");
        That(f.ReadSecond().Position == f.Second.Position && f.ReadSecond().Revision == created.Second!.Revision,
            "旧预约入场失败不留下位置或版本写入");
    }

    static void WeddingAdmissionGifts()
    {
        using var f = new Fixture();
        var guest = f.Guest;
        var record = f.NewRelation(f.First, f.Second) with
        {
            State = 2,
            Wedding = new()
            {
                Id = Guid.NewGuid(), PayerId = f.First.Id, State = 1, HallArea = 8,
                StartsAt = 2000, EndsAt = 2060,
                Invitations = [new() { CharacterId = guest.Id, InviterId = f.First.Id, Name = guest.Name }],
                Gifts = [new() { Id = Guid.NewGuid(), CharacterId = guest.Id, Gold = 10000 }]
            }
        };
        f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, f.First, f.Second,
            Noop(f.First), Noop(f.Second), null, record);
        var claimed = MarriageWeddingRules.ClaimCeremonyGifts(record, guest.Id, 1500);
        var destination = new DfoServer.GameWorld.Towns.Location(39, 8, 100, 100);
        var plan = new InventoryPlan(guest.Inventory, guest.Inventory with
        {
            Items = guest.Inventory.Items.Add(new(0, new(InventoryListType.Main, 65), new(10002627, ItemKind.Consumable, 1)))
                .Add(new(0, new(InventoryListType.Main, 66), new(10002628, ItemKind.Consumable, 1)))
        }, "marriage");
        f.Sql("CREATE TRIGGER fail_ceremony_gift AFTER UPDATE ON marriage_records BEGIN SELECT RAISE(ABORT,'ceremony gift rollback'); END");
        Reject<SqliteException>(() => f.Repository.CommitMarriageHallTravel(f.GuestOwner, guest, record, destination,
            guest.Position, 1500, plan, claimed), "入场位置与库存保存后，领取凭据末段失败必须回滚");
        That(f.ReadGuest().Position == guest.Position && f.ReadGuest().Revision == guest.Revision &&
            f.ReadGuest().Inventory.Items.IsEmpty && f.Repository.FindMarriage(record.Id)!.Revision == record.Revision,
            "入场礼写入失败没有位置、UID、资产或领取资格残留");
        f.Sql("DROP TRIGGER fail_ceremony_gift");
        var entered = f.Repository.CommitMarriageHallTravel(f.GuestOwner, guest, record, destination,
            guest.Position, 1500, plan, claimed);
        var reopened = new CharacterRepository(f.Path);
        That(entered.Position == destination && entered.WeddingHall?.CeremonyId == record.Wedding.Id &&
            entered.Inventory.Items.Length == 2 && entered.Inventory.Items.All(row => row.Uid > 0) &&
            reopened.FindMarriage(record.Id)!.Wedding!.Invitations[0].CeremonyGiftsReceived,
            "重试成功后发放实际UID，位置和请帖领取凭据一并保存，重开库不丢凭据");
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageHallTravel(f.GuestOwner, guest, record,
            destination, guest.Position, 1500, plan, claimed), "原入场礼计划不能绕过角色与关系CAS重放");
        var repeated = f.Repository.CommitMarriageHallTravel(f.GuestOwner, entered, claimed, destination, guest.Position, 1501);
        That(repeated.Inventory.Items.Select(row => row.Uid).SequenceEqual(entered.Inventory.Items.Select(row => row.Uid)) &&
            repeated.Inventory.Items.All(row => row.Core.Count == 1), "已经领取的来宾重复入场不产生第二套礼炮");
    }

    static void WeddingRecoverySettlement()
    {
        using var f = new Fixture();
        var first = f.First; var second = f.Second;
        var reserved = f.NewRelation(first, second) with
        {
            State = 2,
            Wedding = new() { Id = Guid.NewGuid(), Type = 0, PayerId = first.Id, PaidGold = 50, ReservedAt = 1000,
                StartsAt = 1200, EndsAt = 1500, SelectionSeconds = 300, State = 1 }
        };
        f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second, Noop(first), Noop(second), null, reserved);
        var auth = new AuthenticationService(new AuthenticationRepository(f.Path));
        auth.Create("marriage-fourth", "fixture-password");
        using var fourthOwner = f.Owners.Acquire("marriage-fourth", () => { });
        var fourth = f.Repository.Create(fourthOwner, new("Fourth", new(1, 0, 50, 50), null) { Level = 30 });
        var conflicting = f.NewRelation(f.Guest, fourth, 2) with
        {
            State = 2,
            Wedding = reserved.Wedding! with { Id = Guid.NewGuid(), PayerId = f.Guest.Id }
        };
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageTogether(f.GuestOwner, fourthOwner, f.Guest, fourth,
            Noop(f.Guest), Noop(fourth), null, conflicting), "同服务器频道婚礼区域不能在另一事务内重复占用");
        That(f.Repository.FindMarriage(f.Guest.Id) is null && f.Count("marriage_records") == 1,
            "婚礼区域占用冲突不留下新关系和成员索引");
        var reopened = new CharacterRepository(f.Path);
        That(reopened.FindPendingWeddings() is [{ Id: var id }] && id == reserved.Id,
            "重开仓储按持久婚礼账本找回待恢复的预约");
        var after = reserved with { Revision = 2, State = 1, Wedding = reserved.Wedding! with { State = 3 } };
        var mail = f.Delivery("refund", first);
        f.Sql("CREATE TRIGGER fail_marriage_settlement AFTER INSERT ON mailbox_audit_log BEGIN SELECT RAISE(ABORT,'marriage settlement rollback'); END");
        Reject<SqliteException>(() => reopened.CommitMarriageSettlement(reserved, after, [mail]), "系统结算投递失败需回滚婚礼状态");
        That(reopened.FindPendingWeddings().Count == 1 && f.Count("mailbox_messages") == 0 &&
            f.ReadFirst().Inventory.Gold == 1000 && f.ReadFirst().Revision == first.Revision,
            "恢复结算失败没有半份退款，也不改离线角色资产或版本");
        f.Sql("DROP TRIGGER fail_marriage_settlement");
        reopened.CommitMarriageSettlement(reserved, after, [mail]);
        That(reopened.FindPendingWeddings().Count == 0 && reopened.FindMarriage(reserved.Id)?.Revision == 2 &&
            f.Count("mailbox_messages") == 1 && f.ReadFirst().Revision == first.Revision && f.ReadSecond().Revision == second.Revision,
            "系统恢复同事务提交关系与退款邮件，双方角色库存保持原版本");
        Reject<InvalidOperationException>(() => reopened.CommitMarriageSettlement(reserved, after, [mail]), "恢复的旧计划重放由同一关系CAS拒绝");
        That(f.Count("mailbox_messages") == 1, "恢复重放不重复退款");
        f.Repository.CommitMarriageTogether(f.GuestOwner, fourthOwner, f.Guest, fourth, Noop(f.Guest), Noop(fourth), null, conflicting);
        That(f.Repository.FindPendingWeddings().Count == 1, "原婚礼结算释放区域后允许另一对合法占用");
    }

    static void WeddingQuestIsolation()
    {
        using var f = new Fixture();
        var recipient = f.Repository.FindMarriageRecipient(f.Second.Id);
        That(recipient is not null && recipient.Id == f.Second.Id && recipient.Account == f.SecondOwner.Account &&
            recipient.Name == f.Second.Name && f.Repository.FindMarriageRecipient(ushort.MaxValue) is null,
            "离线收件人按角色ID读取真实账号和名字，缺失角色不猜测");
        var card = f.Repository.FindMarriageCard(f.Second.Id);
        That(card is { Equipment.Count: 13 } && card.Equipment.All(id => id == 0) &&
            card.Recipient == recipient && f.Repository.FindMarriageCard(ushort.MaxValue) is null,
            "离线婚礼卡片保留十三个原生装备槽，空槽为零且缺失角色不伪造卡片");
        SeedWeddingQuests(f, f.First, f.FirstOwner.Account);
        SeedWeddingQuests(f, f.Second, f.SecondOwner.Account);
        SeedWeddingQuests(f, f.Guest, f.GuestOwner.Account);
        var first = f.ReadFirst(); var second = f.ReadSecond();
        var relation = f.NewRelation(first, second);
        f.Sql("CREATE TRIGGER fail_marriage_quest_mail AFTER INSERT ON mailbox_audit_log BEGIN SELECT RAISE(ABORT,'marriage quest rollback'); END");
        Reject<SqliteException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
            Noop(first), Noop(second), null, relation, [f.Delivery("quest", second)]), "关系事务末段失败同时回滚双方准备任务清理");
        That(HasWeddingQuests(f.ReadFirst()) && HasWeddingQuests(f.ReadSecond()) && f.Count("marriage_records") == 0,
            "失败创建保留原有婚姻任务旗标与接取身份");
        f.Sql("DROP TRIGGER fail_marriage_quest_mail");
        var created = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
            Noop(first), Noop(second), null, relation);
        That(CleanWeddingQuests(created.First) && CleanWeddingQuests(created.Second!) && CleanWeddingQuests(f.ReadFirst()) &&
            CleanWeddingQuests(f.ReadSecond()) && HasWeddingQuests(f.ReadGuest()) && f.Count("character_quest_completion_receipts") == 3,
            "新婚约清理双方旧准备任务及答案，保留无关任务、来宾及历史完成回执");
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, first, Noop(first), relation,
            relation with { Revision = 2 }), "即使资产revision未变，婚约清理后的旧任务资格也不可重用");
        SeedWeddingQuests(f, f.First, f.FirstOwner.Account);
        SeedWeddingQuests(f, f.Second, f.SecondOwner.Account);
        first = f.ReadFirst();
        var ended = f.Repository.CommitMarriageSingle(f.FirstOwner, first, Noop(first), relation,
            relation with { Revision = 2, State = 0, EndedAt = 2000 });
        That(CleanWeddingQuests(ended.First) && CleanWeddingQuests(f.ReadFirst()) && CleanWeddingQuests(f.ReadSecond()) &&
            f.Count("character_quest_completion_receipts") == 3 && HasWeddingQuests(f.ReadGuest()),
            "单方结束同事务清理离线配偶任务，只移除当前标志并保留历史回执");
    }

    static bool HasWeddingQuests(Character character) => character.CompletedQuests.Contains(20737) &&
        character.CompletedQuests.Contains(20738) && character.ActiveMainQuests.Any(quest => quest.QuestId == 20737);

    static bool CleanWeddingQuests(Character character) => character.CompletedQuests.SequenceEqual([42]) &&
        character.ActiveMainQuests is [{ QuestId: 43 }] && character.QuestAnswers.Count == 1 && character.QuestAnswers[42] == 2 &&
        character.QuestDailyClaims.Count == 1 && character.QuestDailyClaims[42] == 20261026;

    static void SeedWeddingQuests(Fixture f, Character character, string account)
    {
        string acceptance = Guid.NewGuid().ToString("N");
        // fixture账号是固定英文常量；插入真实历史回执，确认清理不触碰领奖去重账本。
        f.Sql($"""
            INSERT OR REPLACE INTO character_completed_quests(character_id,quest_id,daily_period,answer)
              VALUES({character.Id},42,20261026,2),({character.Id},20737,20261026,3),({character.Id},20738,0,4);
            INSERT OR REPLACE INTO character_active_main_quests(character_id,slot,quest_id,condition_state,acceptance_id)
              VALUES({character.Id},0,20737,5,'{acceptance}'),({character.Id},1,43,1,'{Guid.NewGuid():N}');
            INSERT OR IGNORE INTO character_quest_completion_receipts(character_id,account,acceptance_id,quest_id,
              generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,
              difficulty_key,difficulty_weight,experience_amount,gold_amount,before_level,after_level,before_experience,
              after_experience,before_revision,revision,rewards,condition_type,plan_snapshot)
              SELECT {character.Id},'{account}','{acceptance}',20737,1,'fixture','fixture','fixture',0,0,0,'fixture',0,0,0,
                30,30,0,0,0,1,'[]',2,json_object()
              WHERE NOT EXISTS(SELECT 1 FROM character_quest_completion_receipts WHERE character_id={character.Id});
            """);
    }

    static void PairRollbackAndOwners()
    {
        using var f = new Fixture();
        var before = f.NewRelation(f.First, f.Second, 101);
        var created = f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, f.First, f.Second,
            Noop(f.First), Noop(f.Second), null, before);
        That(created.First.Revision == f.First.Revision && created.Second!.Revision == f.Second.Revision,
            "关系独立CAS不需要凭空推进未修改库存的角色版本");
        var after = before with { Revision = 2, State = 2 };
        var first = f.ReadFirst(); var second = f.ReadSecond();
        f.Sql($"CREATE TRIGGER fail_second_marriage BEFORE UPDATE OF gold ON characters WHEN NEW.id={second.Id} BEGIN SELECT RAISE(ABORT,'second marriage rollback'); END");
        Reject<SqliteException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
            Pay(first, 3), Pay(second, 4), before, after), "第二角色写入失败回滚已提交到同事务的第一角色和关系CAS");
        That(f.ReadFirst().Inventory.Gold == 1000 && f.ReadSecond().Inventory.Gold == 1000 &&
            f.Repository.FindMarriage(before.Id)?.Revision == 1, "双人中途失败没有单边扣款和半份关系");
        f.Sql("DROP TRIGGER fail_second_marriage");
        using var replacement = f.Owners.Acquire(f.FirstOwner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageTogether(f.FirstOwner, f.SecondOwner, first, second,
            Pay(first, 3), Pay(second, 4), before, after), "被接管的任一方owner不能提交旧关系计划");
        Reject<InvalidOperationException>(() => f.Repository.CommitMarriageSingle(f.FirstOwner, first, Noop(first), before, after),
            "单人旧generation同样不能提交");
        That(f.Repository.LoadCharacter(replacement, first.Slot).Inventory.Gold == 1000 &&
            f.Repository.FindMarriage(before.Id)?.Revision == 1, "接管后原计划没有资产或聚合写入");
    }

    static InventoryPlan Noop(Character character) => new(character.Inventory, character.Inventory, "marriage_test");
    static InventoryPlan Pay(Character character, uint gold) => new(character.Inventory,
        character.Inventory with { Gold = checked(character.Inventory.Gold - gold) }, "marriage_test");

    sealed class Fixture : IDisposable
    {
        readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "s5a9-marriage-" + Guid.NewGuid().ToString("N"));
        public string Path { get; }
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease FirstOwner { get; }
        public AccountOwners.Lease SecondOwner { get; }
        public AccountOwners.Lease GuestOwner { get; }
        public CharacterRepository Repository { get; }
        public Character First { get; }
        public Character Second { get; }
        public Character Guest { get; }

        public Fixture()
        {
            Directory.CreateDirectory(directory);
            Path = System.IO.Path.Combine(directory, "game.db");
            var authentication = new AuthenticationService(new AuthenticationRepository(Path));
            foreach (string account in new[] { "marriage-first", "marriage-second", "marriage-guest" }) authentication.Create(account, "fixture-password");
            Repository = new(Path);
            // 该夹具也可在迁移注册接入前单独使用；升级函数必须可重复确认已有精确结构。
            using (var db = Open())
            using (var write = db.BeginWrite("MarriagePersistenceTests.Schema"))
            {
                MarriageSchema.Upgrade(db, write.Transaction);
                MarriageSchema.Upgrade(db, write.Transaction);
                write.Transaction.Commit();
            }
            FirstOwner = Owners.Acquire("marriage-first", () => { });
            SecondOwner = Owners.Acquire("marriage-second", () => { });
            GuestOwner = Owners.Acquire("marriage-guest", () => { });
            First = Create(FirstOwner, "Bride", 1);
            Second = Create(SecondOwner, "Groom", 0);
            Guest = Create(GuestOwner, "Guest", 2);
        }

        Character Create(AccountOwners.Lease owner, string name, byte job) => Repository.Create(owner,
            new(name, new(1, 0, 50, 50), null) { Job = job, Level = 30, Inventory = new([], Gold: 1000) });
        public Character ReadFirst() => Repository.LoadCharacter(FirstOwner, First.Slot);
        public Character ReadSecond() => Repository.LoadCharacter(SecondOwner, Second.Slot);
        public Character ReadGuest() => Repository.LoadCharacter(GuestOwner, Guest.Slot);
        public MarriageRecord NewRelation(Character first, Character second, uint context = 0xf0000001) =>
            new(Guid.NewGuid(), context, 1, first.Id, second.Id, 1)
            {
                Members = ImmutableDictionary<ushort, MarriageMemberState>.Empty.Add(first.Id, new()).Add(second.Id, new()),
                CreatedAt = 1000
            };
        public MailboxDelivery Delivery(string suffix, Character recipient) => new("marriage-test-" + suffix,
            Repository.FindMailRecipient(recipient.Name)!, "System", "Wedding gift", 17,
            [new(0, new(InventoryListType.Main, 121), new(10002609, ItemKind.Material, 1))], 1000);
        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false, ForeignKeys = true }.ToString());
            db.Open();
            return db;
        }
        public long Count(string table)
        {
            using var db = Open(); using var query = db.CreateCommand();
            query.CommandText = "SELECT count(*) FROM " + table;
            return Convert.ToInt64(query.ExecuteScalar());
        }
        public void Sql(string text)
        {
            using var db = Open(); using var write = db.BeginWrite("MarriagePersistenceTests.Sql");
            using var query = db.CreateCommand(); query.Transaction = write.Transaction; query.CommandText = text;
            query.ExecuteNonQuery(); write.Transaction.Commit();
        }
        public void Dispose()
        {
            GuestOwner.Dispose(); SecondOwner.Dispose(); FirstOwner.Dispose();
            SqliteConnection.ClearAllPools(); Directory.Delete(directory, true);
        }
    }
}
