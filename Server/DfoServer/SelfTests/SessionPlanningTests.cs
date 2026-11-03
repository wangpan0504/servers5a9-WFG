using static DfoServer.Testing.TestCheck;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Auth;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Persistence;
using DfoServer.Network.Parsers;
using DfoServer.Network.Builders;
using Microsoft.Data.Sqlite;
using DfoServer.Testing;

namespace DfoServer;

internal static partial class SessionPlanningTests
{
    static readonly SkillLearningCommand Purchase = new(0, 0, [new(5, false, 1)]);
    static readonly CharacterProfile Profile = new(0, new(1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), [new(5, 1)]);

    public static async Task RunAsync()
    {
        int checks = TestCheck.Count;
        Creation();
        Deletion();
        DeletionCapacity();
        DeletionRosterMapping();
        PublishedRosterIdentity();
        TagEvildomPublishedRoster();
        TagEvildomConfigurationLifecycle();
        await TagEvildomRunSchedulingAsync();
        await TagEvildomPublicationRacesAsync();
        TagEvildomPairSurvivesPrimaryChange();
        ImperialArenaPairSurvivesPrimaryChange();
        SlotChange();
        Initialization();
        ExpertJobStoreLifecycle();
        Learning();
        Layout();
        EnchantmentTownPlanning();
        await CreationRacesAsync();
        await SkillRacesAsync();
        await LayoutRacesAsync();
        Console.WriteLine($"PASS {TestCheck.Count - checks} session planning/commit assertions");
    }

    static SkillLearningDefinition Resolve(byte job, ushort id) => id == 5
        ? new(job, id, "synthetic.skl", SkillType.Active, [5], [10, 10, 10, 10, 10, 10], [10, 10, 10, 10, 10, 10],
            1, 0, [], [0, 0, 0, 0, 0, 0], false, false, [])
        : throw new KeyNotFoundException();

    static void Creation()
    {
        using var f = new Fixture();
        var draft = CharacterCreation.Prepare("Draft", Profile, f.Towns.Room, f.Towns.InitialReturn, Resolve);
        TestCheck.That(draft.Job == 0 && draft.AcceptedStoryLevel == 1 && draft.ActiveMainQuests.Length == 0,
            "建角保留职业，不按固定编号自动接取任务");
        TestCheck.That(!ReferenceEquals(draft.Skills, Profile.Skills) && draft.Skills.SequenceEqual(Profile.Skills), "建角复制初始技能数组");
        TestCheck.That(draft.InitialLearning.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0 && tree.Skills.Single() == new LearnedSkill(0, 5, 1)),
            "建角双树绑定原技能且不赠点");
        var special = CharacterCreation.Prepare("Special", Profile with { Job = 9 }, f.Towns.Room, f.Towns.InitialReturn, Resolve);
        TestCheck.That(special.InitialLearning.Initialized && special.Skills.SequenceEqual(Profile.Skills) &&
            special.InitialLearning.Trees.All(tree => tree.Skills.Single().Slot == 150 && tree.Sp == 0 && tree.Tp == 0),
            "特殊职业建角完整初始化，普通子技能使用连招备用区且不赠点");
        var created = f.Session.CreateCharacter("Created", Profile, Resolve);
        TestCheck.That(created.Id > 0 && created.Learning.Initialized && f.Session.Selected is null && f.Repository.List(f.Owner).Length == 2,
            "建角返回持久结果且不隐式选择角色");
        Reject<SqliteException>(() => f.Session.CreateCharacter("Created", Profile, Resolve), "重名建角仍由唯一约束拒绝");
        TestCheck.That(f.Repository.List(f.Owner).Length == 2, "重名失败没有部分角色");
        f.Select();
        Reject<InvalidDataException>(() => f.Session.CreateCharacter("Selected", Profile, (_, _) => throw new Exception("不应解析")),
            "选角后建角在资源读取前拒绝");
    }

    static void ExpertJobStoreLifecycle()
    {
        using var f = new Fixture();
        var id = f.Read().Id;
        f.Sql($"INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES({id},'planning',3,51)");
        f.Select();
        var store = new DfoServer.Game.ExpertJob.ExpertJobStoreRecord(id, 3, 0, "", 25, 0, 0, 0, 1, 300);
        That(f.Session.OpenExpertJobStore(), "副职业生命周期夹具开店");
        f.Session.QuoteExpertJobStore(store);
        That(f.Session.MatchesExpertJobStoreQuote(store), "进入保存原店铺报价");
        f.Session.ReturnToRoster(); f.Select();
        That(!f.Session.ExpertJobStoreOpen && !f.Session.MatchesExpertJobStoreQuote(store), "小退重选不复活旧店铺或报价");
        That(f.Session.OpenExpertJobStore(), "重新开店明确授权新生命周期");
        f.Session.QuoteExpertJobStore(store);
        That(f.Session.LeaveRoom() && !f.Session.ExpertJobStoreOpen && !f.Session.MatchesExpertJobStoreQuote(store),
            "离开场景同时清除开店资格和旧报价");
        var repriced = new DfoServer.Game.ExpertJob.ExpertJobStoreRecord(id, 3, 0, "", 50, 0, 0, 0, 1, 300);
        f.Session.QuoteExpertJobStore(store);
        That(!f.Session.MatchesExpertJobStoreQuote(repriced) && f.Session.MatchesExpertJobStoreQuote(store with { Endurance = 299 }),
            "改价更换实例而消耗耐久不改变原报价身份");
        using var customerLease = f.Owners.Acquire("store-customer", () => { });
        var created = f.Repository.Create(customerLease, new("StoreBuyer", f.Towns.Room, f.Towns.InitialReturn)
        { Level = 10, Inventory = new([new(0, new(InventoryListType.Main, 121), new ItemCore(10014751, ItemKind.Material, 2))]) });
        var customerSession = new GameSession(f.Repository, f.Towns, customerLease);
        customerSession.Select(created.Slot); customerSession.MarkReady();
        var beforeCustomer = customerSession.Selected!;
        var beforeOwner = f.Session.Selected!;
        var item = beforeCustomer.Inventory.Items.Single();
        var plan = new DfoServer.Game.ExpertJob.ExpertJobTradePlan(beforeCustomer, beforeOwner,
            new(beforeCustomer.Inventory, beforeCustomer.Inventory with { Items = beforeCustomer.Inventory.Items.Replace(item,
                item with { Core = item.Core with { Value = 1 } }) }, "trade-race"),
            new(beforeOwner.Inventory, beforeOwner.Inventory, "trade-race-owner"),
            beforeOwner.ExpertJobExperience + 1, beforeOwner.ExpertMachineEndurance - 1, 0);
        That(f.Session.OpenExpertJobStore(), "竞态测试重新开店");
        f.Session.CloseExpertJobStore();
        That(GameSession.CommitExpertJobTrade(customerSession, f.Session, plan, () => true) is null &&
            f.Read().Revision == beforeOwner.Revision, "规划完成后关店在双锁提交前拒绝，双方不消费");
        f.Session.OpenExpertJobStore();
        var board = new DfoServer.Game.ExpertJob.ExpertJobStoreBoard(); board.Open(store);
        customerSession.QuoteExpertJobStore(store);
        board.Open(repriced);
        That(GameSession.CommitExpertJobTrade(customerSession, f.Session, plan, () =>
            board.TryGet(id, out var live) && ReferenceEquals(live, store) && customerSession.MatchesExpertJobStoreQuote(live)) is null &&
            f.Repository.LoadCharacter(customerLease, created.Slot).Revision == beforeCustomer.Revision,
            "规划完成后改价拒绝旧报价且不扣顾客源");
        f.Session.ExpertJobStoreClosed = actor => board.TryClose(actor, out _);
        using var replacementOwner = f.Owners.Acquire(f.Owner.Account, () => { });
        var replacementSession = new GameSession(f.Repository, f.Towns, replacementOwner);
        replacementSession.Select(0); replacementSession.MarkReady(); replacementSession.OpenExpertJobStore();
        var replacementStore = repriced with { Instance = Guid.NewGuid() }; board.Open(replacementStore);
        f.Session.End();
        That(board.TryGet(id, out var survivingStore) && ReferenceEquals(survivingStore, replacementStore) &&
            replacementSession.ExpertJobStoreOpen, "旧generation迟到End只清旧会话，不触发回调关闭新generation同角色店铺");
        Reject<InvalidOperationException>(() => GameSession.CommitExpertJobTrade(customerSession, f.Session, plan, () => true),
            "规划完成后店主断线使旧租约双锁提交失败");
        That(f.Repository.LoadCharacter(customerLease, created.Slot).Inventory.Items.Single().Core.Value == 2,
            "断线拒绝保留顾客原物品数量");
    }

    static void Initialization()
    {
        using var f = new Fixture(initialized: false);
        f.Select(ready: false);
        var before = f.Session.Selected!;
        TestCheck.That(f.Session.InitializeLearning(Resolve) == SkillLearningFailure.None, "入城前允许初始化学习绑定");
        var current = f.Session.Selected!;
        TestCheck.That(!f.Session.Ready && current.Revision == before.Revision + 1 && current.Learning.Experience == 2035 &&
            current.Learning.Trees.All(tree => tree.Sp == 0 && tree.Tp == 0), "初始化只提交绑定，保持经验和选角阶段");
        TestCheck.That(Equal(current, f.Read()), "初始化回读实际提交快照");
        TestCheck.That(f.Session.InitializeLearning((_, _) => throw new Exception("不应解析")) == SkillLearningFailure.None &&
            ReferenceEquals(current, f.Session.Selected), "重复初始化不解析资源也不写库");

        var empty = before with { Skills = [] };
        TestCheck.That(SkillLearning.Initialize(empty, Resolve).Failure == SkillLearningFailure.Unsupported, "无技能旧角色不伪造初始化");
        TestCheck.That(SkillLearning.Initialize(before, (_, _) => throw new KeyNotFoundException()).Failure == SkillLearningFailure.MissingSkill &&
            !before.Learning.Initialized, "初始化缺资源保留原经验与未初始化状态");

        using var failed = new Fixture(initialized: false);
        failed.Select(ready: false);
        var snapshot = failed.Session.Selected;
        failed.FailSkillWrite();
        Reject<SqliteException>(() => failed.Session.InitializeLearning(Resolve), "初始化后段SQL失败向上传递");
        TestCheck.That(ReferenceEquals(snapshot, failed.Session.Selected) && Equal(snapshot!, failed.Read()), "初始化SQL失败整笔回滚且不替换Selected");
    }

    static void Deletion()
    {
        using var f = new Fixture();
        var original = f.Read();
        var later = f.Repository.Create(f.Owner, new("Later", f.Towns.Room, f.Towns.InitialReturn));
        var highest = f.Repository.Create(f.Owner, new("Highest", f.Towns.Room, f.Towns.InitialReturn));
        var pieces = new EpicPieceCatalog("[piece list]\n2001 3001\n[/piece list]");
        var paid = original.Inventory with
        {
            AccountWallet = original.Inventory.AccountWallet.Set(CurrencyKind.Cera, 77).Set(CurrencyKind.RentalPoint, 19),
            AccountCargo = new(0, 8, 9), EpicPieces = EpicPieceBook.Empty.Set(pieces, 0, 12),
            ShopPurchases = [new(1, 1001, true, false, 0, 2), new(1, 1001, false, true, 1, 3)],
            Items = [new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 3)),
                new(0, new(InventoryListType.AccountCargo, 0), new(1001, ItemKind.Material, 7))]
        };
        original = f.Repository.CommitInventory(f.Owner, original, new(original.Inventory, paid, "delete-fixture"));
        f.SeedDeletionGraph(original.Id);
        var expected = f.Repository.LoadDeletionTarget(f.Owner, original.Slot);
        string shared = f.SharedSnapshot();
        string audits = f.Rows("SELECT * FROM inventory_audit_log WHERE character_id=" + original.Id + " ORDER BY audit_id");
        string before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Repository.Delete(f.Owner, expected with { Revision = expected.Revision - 1 }),
            "删除拒绝旧revision且不触碰原存档");
        TestCheck.That(f.Snapshot() == before, "过期删除不改变角色、私有和账号记录");
        f.Select();
        Reject<InvalidDataException>(() => f.Session.DeleteCharacter(original.Slot), "已选角会话不能删除任何角色");
        f.Session.ReturnToRoster();
        long epoch = f.Session.SelectionEpoch;
        f.Sql("CREATE TRIGGER deletion_final_abort AFTER DELETE ON characters BEGIN SELECT RAISE(ABORT,'deletion rollback'); END");
        before = f.Snapshot();
        Reject<SqliteException>(() => f.Session.DeleteCharacter(original.Slot), "删除末段真实SQL失败向上传递");
        TestCheck.That(f.Snapshot() == before && f.Session.SelectionEpoch == epoch && f.Session.Selected is null,
            "末段失败回滚全部私有依赖、账号来源和资产，不推进epoch或返回成功");
        f.Sql("DROP TRIGGER deletion_final_abort; CREATE TABLE deletion_extension(character_id INTEGER REFERENCES characters(id) ON DELETE CASCADE); INSERT INTO deletion_extension VALUES(" + original.Id + ")");
        before = f.Snapshot();
        Reject<InvalidDataException>(() => f.Session.DeleteCharacter(original.Slot), "未知扩展外键拒绝删除，不能依靠CASCADE移除不归本模块的记录");
        TestCheck.That(f.Snapshot() == before && f.Session.SelectionEpoch == epoch, "未知依赖拒绝后全库和Session保持原样");
        f.Sql("DROP TABLE deletion_extension");
        var actual = f.Session.DeleteCharacter(original.Slot);
        TestCheck.That(actual.Id == original.Id && actual.Slot == original.Slot && f.Session.SelectionEpoch == epoch + 1 &&
            f.Session.Phase == SessionPhase.Unselected && f.Session.Selected is null && !f.Session.Ready,
            "成功删除返回实际原身份并仅推进名册epoch，保留未选角会话");
        TestCheck.That(f.PrivateRows(original.Id).All(value => value == 0) && f.SharedSnapshot() == shared &&
            f.Scalar("SELECT character_id IS NULL FROM account_completed_quests") is 1L &&
            f.Rows("SELECT * FROM inventory_audit_log WHERE character_id=" + original.Id + " ORDER BY audit_id") == audits,
            "删除全部角色私有表，保留账号资产、完成事实和无外键资产审计");
        TestCheck.That(f.Repository.ListRoster(f.Owner).Select(row => row.Slot).SequenceEqual(new ushort[] { 1, 2 }),
            "删除名册不重新编号剩余角色槽位");
        Reject<InvalidDataException>(() => f.Session.DeleteCharacter(original.Slot), "重复已删槽拒绝");
        var created = f.Session.CreateCharacter(original.Name, Profile);
        TestCheck.That(created.Slot == 0 && created.Id > highest.Id && created.Name == original.Name &&
            created.AccountCompletedQuests.SequenceEqual(new[] { 900 }) && f.SharedSnapshot() == shared,
            "同会话立即重用姓名和最小空槽，创建新ID且恢复账号任务事实");
        Reject<InvalidOperationException>(() => f.Repository.Delete(f.Owner, expected), "旧删除身份不能删除重建的同槽角色");
        f.Session.DeleteCharacter(later.Slot);
        var middle = f.Session.CreateCharacter("Middle", Profile);
        TestCheck.That(middle.Slot == 1 && f.Repository.ListRoster(f.Owner).Select(row => row.Slot).SequenceEqual(new ushort[] { 0, 1, 2 }),
            "存在高槽角色时连续建角正确填补中间空槽");
        f.Session.DeleteCharacter(highest.Slot);
        var fresh = f.Session.CreateCharacter("Fresh", Profile);
        fresh = f.Repository.CommitInventory(f.Owner, fresh, new(fresh.Inventory, fresh.Inventory with
        {
            Items = fresh.Inventory.Items.Add(new(0, new(InventoryListType.Main, 121), new(1001, ItemKind.Material, 1)))
        }, "delete-uid-check"));
        TestCheck.That(fresh.Id > middle.Id && fresh.Inventory.Items.Single(item => item.Position.List == InventoryListType.Main).Uid >
            original.Inventory.Items.Single(item => item.Position.List == InventoryListType.Main).Uid,
            "删除最高角色不回绕AUTOINCREMENT身份或复用历史UID");
        var reopened = new CharacterRepository(f.Database);
        TestCheck.That(reopened.ListRoster(f.Owner).Select(row => row.Id).SequenceEqual(new[] { created.Id, middle.Id, fresh.Id }) &&
            reopened.LoadCharacter(f.Owner, created.Slot).AccountCompletedQuests.SequenceEqual(new[] { 900 }) &&
            f.Scalar("PRAGMA integrity_check") is "ok" && f.Scalar("SELECT count(*) FROM pragma_foreign_key_check") is 0L,
            "重开恢复新名册、账号任务与完整外键，无删除角色或私有依赖复活");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Session.DeleteCharacter(created.Slot), "旧generation不能删除");
        TestCheck.That(f.Snapshot() == before && reopened.ListRoster(replacement).Length == 3, "旧generation不改存档或释放新owner");
    }

    static void DeletionCapacity()
    {
        using var f = new Fixture();
        for (int slot = 1; slot < 32; slot++) f.Session.CreateCharacter("Capacity" + slot, Profile);
        string before = f.Snapshot();
        Reject<InvalidDataException>(() => f.Session.CreateCharacter("Full", Profile), "32槽满时拒绝且不创建半份角色");
        TestCheck.That(f.Snapshot() == before, "满槽建角保持全部存档和sequence");
        f.Session.DeleteCharacter(17);
        var created = f.Session.CreateCharacter("Capacity17", Profile);
        TestCheck.That(created.Slot == 17 && f.Repository.ListRoster(f.Owner).Length == 32,
            "满名册删除后同连接立即重建空槽和原姓名");
    }

    static void DeletionRosterMapping()
    {
        using var f = new Fixture();
        var middle = f.Session.CreateCharacter("映射中间", Profile);
        var highest = f.Session.CreateCharacter("映射高槽", Profile);
        f.Session.MarkRosterPublished(f.Session.PrepareRoster());
        byte[] body = [1, 0, 8, 0, 0, 0, 0xD3, 0xB3, 0xC9, 0xE4, 0xD6, 0xD0, 0xBC, 0xE4];
        var parsed = CharacterRequests.Delete(body);
        TestCheck.That(parsed.RosterIndex == 1 && parsed.ConfirmationName == middle.Name, "删除Parser独立CP936样本保留行索引与完整确认名");
        foreach (byte[] invalid in new[] { body[..1], body[..6], body.Concat(new byte[] { 0 }).ToArray(),
            new byte[] { 0, 0, 1, 0, 0, 0, 0x81 }, new byte[] { 0, 0, 13, 0, 0, 0 } })
            Reject<InvalidDataException>(() => CharacterRequests.Delete(invalid), "删除Parser拒绝截断、未知尾、无效CP936或越界姓名");
        string before = f.Snapshot();
        var rejected = f.Session.DeleteCharacterFromRoster(1, "Wrong");
        TestCheck.That(!rejected.Accepted && CharacterPackets.Deleted(rejected).AsSpan().SequenceEqual(new byte[] { 0, 123, 0 }) &&
            f.Snapshot() == before, "确认名失配仅回复已证静默收尾123，存档不变");
        rejected = f.Session.DeleteCharacterFromRoster(31, "Missing");
        TestCheck.That(!rejected.Accepted && f.Snapshot() == before, "无名册行拒绝，不把行索引当角色ID");
        var result = f.Session.DeleteCharacterFromRoster(parsed.RosterIndex, parsed.ConfirmationName);
        var receipt = result.Receipt!;
        TestCheck.That(result.Accepted && receipt.Deleted.Id == middle.Id && receipt.Deleted.Slot == 1 &&
            receipt.Roster.Entries.Select(row => row.Slot).SequenceEqual(new ushort[] { 0, 2 }) &&
            CharacterPackets.Deleted(result).AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0 }) && f.Session.CanPublishDeletion(receipt),
            "中间行删除ACK回显行索引，提交名册保留高槽且原owner/epoch可发布");
        f.Session.MarkRosterPublished(receipt.Roster);
        f.Session.SelectFromRosterIndex(1);
        TestCheck.That(f.Session.Selected is { } selected && selected.Id == highest.Id && selected.Slot == 2 && !f.Session.CanPublishDeletion(receipt),
            "选角wire同样按当前名册行索引映射高槽，不能误选或当缺槽拒绝");
        f.Session.ReturnToRoster();
        f.Session.MarkRosterPublished(f.Session.PrepareRoster());
        result = f.Session.DeleteCharacterFromRoster(1, highest.Name);
        TestCheck.That(result.Receipt is { } highDeleted && highDeleted.Deleted.Id == highest.Id && highDeleted.Deleted.Slot == 2 &&
            CharacterPackets.Deleted(result).AsSpan().SequenceEqual(new byte[] { 1, 0, 1, 0 }) && !f.Session.CanPublishDeletion(receipt),
            "名册有缺口时行1映射持久槽2，后续删除使旧名册回执失效");
        receipt = result.Receipt!;
        var creation = f.Session.CreateCharacterForClient("Next", Profile);
        TestCheck.That(!f.Session.CanPublishDeletion(receipt) && f.Session.CanPublishCreation(creation) &&
            creation.Roster.Entries.Select(row => row.Name).SequenceEqual(new[] { "Fixture", "Next" }),
            "同owner成功建角推进epoch，旧删除名册不能覆盖新角色，创建回执携带实际名册");
        f.Select();
        TestCheck.That(!f.Session.CanPublishCreation(creation), "选角后创建回执不能迟到发布ACK或旧名册");
        before = f.Snapshot();
        rejected = f.Session.DeleteCharacterFromRoster(0, "Fixture");
        TestCheck.That(!rejected.Accepted && f.Snapshot() == before, "选角后网络删除为业务拒绝，不提交或误删当前角色");
        f.Session.ReturnToRoster();
        f.Session.MarkRosterPublished(f.Session.PrepareRoster());
        result = f.Session.DeleteCharacterFromRoster(1, "Next");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Session.CanPublishDeletion(result.Receipt!), "接管后旧generation不能发布删除ACK或名册");
    }

    static void PublishedRosterIdentity()
    {
        using var f = new Fixture();
        var shown = f.Repository.Create(f.Owner, new("Shown", f.Towns.Room, f.Towns.InitialReturn));
        Reject<InvalidDataException>(() => f.Session.SelectFromRosterIndex(0), "没有实际发布名册时不能选择索引");
        TestCheck.That(!f.Session.DeleteCharacterFromRoster(0, "Fixture").Accepted, "没有已发布映射时删除拒绝");
        f.Repository.Delete(f.Owner, f.Repository.LoadDeletionTarget(f.Owner, 0));
        var roster = f.Session.PrepareRoster();
        f.Session.MarkRosterPublished(roster);
        // 同owner的直接存储写模拟未发布的新名册；线上用例必须沿Session的epoch推进。
        var unseen = f.Repository.Create(f.Owner, new("Unseen", f.Towns.Room, f.Towns.InitialReturn));
        f.Session.SelectFromRosterIndex(0);
        TestCheck.That(f.Session.Selected is { } selected && selected.Id == shown.Id && selected.Slot == shown.Slot,
            "外部填补空槽后按最近已发布名册索引选择原角色，不用新SQL行序解释旧请求");
        f.Session.ReturnToRoster();
        f.Session.MarkRosterPublished(roster);
        Reject<InvalidDataException>(() => f.Session.SelectFromRosterIndex(0), "状态变化后的旧名册不能标记已发布或用于选择");
        var current = f.Session.PrepareRoster();
        f.Session.MarkRosterPublished(current);
        f.Repository.Delete(f.Owner, f.Repository.LoadDeletionTarget(f.Owner, unseen.Slot));
        var replacement = f.Repository.Create(f.Owner, new("Unseen", f.Towns.Room, f.Towns.InitialReturn));
        string before = f.Snapshot();
        Reject<InvalidOperationException>(() => f.Session.SelectFromRosterIndex(0), "同槽同名外部重建后选角核对原发布ID，不能选中新角色");
        Reject<InvalidOperationException>(() => f.Session.DeleteCharacterFromRoster(0, "Unseen"), "删除同样拒绝已发布身份被同槽同名替换");
        TestCheck.That(f.Snapshot() == before && replacement.Id > unseen.Id && f.Session.Selected is null,
            "发布身份失配不会保存出生、删除新角色或安装在线状态");
    }

    static void SlotChange()
    {
        var parsed = CharacterRequests.ChangeSlot([2, 0, 0, 0, 7, 0, 0, 0]);
        That(parsed.IndexA == 2 && parsed.IndexB == 7, "重排Parser按两个u32名册行索引解读，不是持久槽位");
        foreach (byte[] invalid in new[] { Array.Empty<byte>(), new byte[] { 1, 0, 0, 0 }, new byte[7], new byte[9] })
            Reject<InvalidDataException>(() => CharacterRequests.ChangeSlot(invalid), "重排Parser拒绝非8字节正文");

        using var f = new Fixture();
        var original = f.Read();
        var second = f.Session.CreateCharacter("Second", Profile);
        var third = f.Session.CreateCharacter("Third", Profile);
        f.Session.DeleteCharacter(second.Slot);
        f.Session.MarkRosterPublished(f.Session.PrepareRoster());
        long epoch = f.Session.SelectionEpoch;
        string before = f.Snapshot();
        var same = f.Session.ChangeCharacterSlots(1, 1);
        That(same.Accepted && same.Reason == "same-slot" && same.SelectionEpoch == epoch && f.Snapshot() == before,
            "同索引重排不写库，仍回带当前名册供客户端复位本地行序");
        // 删除中间槽位后名册只剩两行：索引2越界；旧实现按持久槽位解释时这里会错误交换槽位0/2。
        var absent = f.Session.ChangeCharacterSlots(0, 2);
        That(absent.Accepted && absent.Reason == "roster-mismatch" && absent.SelectionEpoch == epoch && f.Snapshot() == before,
            "越界名册索引不写库，回推当前名册");
        var invalidIndex = f.Session.ChangeCharacterSlots(0, 65536);
        That(invalidIndex.Accepted && invalidIndex.Reason == "roster-mismatch" && invalidIndex.SelectionEpoch == epoch && f.Snapshot() == before,
            "超出名册长度的索引不写库，回报成功并重新同步名册");
        long originalRevision = Convert.ToInt64(f.Scalar("SELECT revision FROM characters WHERE id=" + original.Id));
        long thirdRevision = Convert.ToInt64(f.Scalar("SELECT revision FROM characters WHERE id=" + third.Id));
        // 行索引1对应持久槽位2：有空洞时必须按名册行序映射，不能把正文直接当槽位。
        var result = f.Session.ChangeCharacterSlots(0, 1);
        var receipt = result.Receipt!;
        That(result.Accepted && result.Reason == "committed" && result.SelectionEpoch == epoch + 1 &&
            receipt.Roster.SelectionEpoch == epoch + 1 && receipt.Roster.Entries.Select(row => (row.Slot, row.Id))
                .SequenceEqual(new[] { (original.Slot, third.Id), (third.Slot, original.Id) }) &&
            f.Session.CanPublishSlotChange(receipt), "空洞名册按行索引交换正确角色，回执携带最新名册和原owner/epoch");
        That(f.Scalar("SELECT slot FROM characters WHERE id=" + original.Id) is 2L &&
            f.Scalar("SELECT slot FROM characters WHERE id=" + third.Id) is 0L &&
            Convert.ToInt64(f.Scalar("SELECT revision FROM characters WHERE id=" + original.Id)) > originalRevision &&
            Convert.ToInt64(f.Scalar("SELECT revision FROM characters WHERE id=" + third.Id)) > thirdRevision,
            "互换后两个角色落到对方持久槽位并使旧revision失效");
        f.Session.MarkRosterPublished(receipt.Roster);
        var back = f.Session.ChangeCharacterSlots(0, 1);
        var backReceipt = back.Receipt!;
        That(back.Accepted && back.SelectionEpoch == epoch + 2 && !f.Session.CanPublishSlotChange(receipt) &&
            backReceipt.Roster.Entries.Select(row => (row.Slot, row.Id)).SequenceEqual(new[] { (original.Slot, original.Id), (third.Slot, third.Id) }) &&
            f.Session.CanPublishSlotChange(backReceipt), "再次互换推进epoch，旧回执不能覆盖新名册");
        f.Session.MarkRosterPublished(backReceipt.Roster);
        f.Session.SelectFromRosterIndex(0);
        That(f.Session.Selected is { } selected && selected.Id == original.Id && selected.Slot == original.Slot,
            "重排后选角wire按新名册行序映射回持久槽位");
        long selectedEpoch = f.Session.SelectionEpoch;
        before = f.Snapshot();
        var duringSelection = f.Session.ChangeCharacterSlots(0, 1);
        That(!duringSelection.Accepted && duringSelection.Receipt is null && duringSelection.Reason == "selected" &&
            f.Session.SelectionEpoch == selectedEpoch && f.Snapshot() == before, "已选角会话拒绝重排，不写库也不回包");
        f.Session.ReturnToRoster();
        f.Session.MarkRosterPublished(f.Session.PrepareRoster());
        f.Session.CreateCharacterForClient("Fourth", Profile);
        long createdEpoch = f.Session.SelectionEpoch;
        before = f.Snapshot();
        var stale = f.Session.ChangeCharacterSlots(0, 1);
        That(stale.Accepted && stale.Reason == "roster-mismatch" && stale.SelectionEpoch == createdEpoch && f.Snapshot() == before,
            "建角推进epoch后旧名册请求不写库，只回推新名册");

        using var failed = new Fixture();
        var added = failed.Session.CreateCharacter("SwapFail", Profile);
        failed.Session.MarkRosterPublished(failed.Session.PrepareRoster());
        failed.Sql("CREATE TRIGGER fail_slot_swap BEFORE UPDATE OF slot ON characters BEGIN SELECT RAISE(ABORT,'slot swap rollback'); END");
        long failEpoch = failed.Session.SelectionEpoch;
        string snapshot = failed.Snapshot();
        Reject<SqliteException>(() => failed.Session.ChangeCharacterSlots(0, 1), "重排SQL失败向上传递");
        That(failed.Snapshot() == snapshot && failed.Session.SelectionEpoch == failEpoch,
            "重排失败整笔回滚，不推进epoch也不返回成功");

        using var full = new Fixture();
        full.Sql("INSERT INTO account_wallets(account,character_slots) VALUES('planning',62) ON CONFLICT(account) DO UPDATE SET character_slots=62");
        for (int slot = 1; slot < 62; slot++) full.Session.CreateCharacter("Full" + slot, Profile);
        var rows = full.Repository.ListRoster(full.Owner);
        That(rows.Length == 62 && rows[0].Slot == 0 && rows[^1].Slot == 61, "重排满槽夹具覆盖全部62个合法槽位");
        full.Session.MarkRosterPublished(full.Session.PrepareRoster());
        var fullResult = full.Session.ChangeCharacterSlots(0, 61);
        var fullReceipt = fullResult.Receipt!;
        ushort[] swappedIds = rows.Select(row => row.Id).ToArray();
        (swappedIds[0], swappedIds[61]) = (swappedIds[61], swappedIds[0]);
        That(fullResult.Accepted && fullResult.Reason == "committed" &&
            fullReceipt.Roster.Entries.Select(row => row.Id).SequenceEqual(swappedIds) &&
            full.Scalar("SELECT slot FROM characters WHERE id=" + rows[0].Id) is 61L &&
            full.Scalar("SELECT slot FROM characters WHERE id=" + rows[^1].Id) is 0L &&
            full.Scalar("SELECT count(*) FROM characters WHERE account='planning' AND slot BETWEEN 0 AND 61") is 62L,
            "62槽全占时经临时-1槽完成互换，CHECK临时放行后名册无空缺");
        Reject<SqliteException>(() => full.Sql("UPDATE characters SET slot=99 WHERE id=" + rows[0].Id), "全槽互换后CHECK约束已恢复");
    }

    static void Learning()
    {
        using var f = new Fixture();
        f.Select(ready: false);
        TestCheck.That(f.Session.LearnSkills(Purchase, (_, _) => throw new Exception("不应解析")).Failure == SkillLearningFailure.Unsupported,
            "未Ready时拒绝学习且不读取资源");
        f.Session.MarkReady();
        var before = f.Session.Selected!;
        var result = f.Session.LearnSkills(Purchase, Resolve);
        var receipt = result.Receipt!;
        TestCheck.That(result.Success && result.State!.Trees[0].Sp == 35 && result.Changes.Single() == new LearnedSkill(3, 5, 2),
            "学习扣一次费用并保留稳定槽位");
        TestCheck.That(ReferenceEquals(receipt.Character, f.Session.Selected) && ReferenceEquals(result.State, receipt.Character.Learning) &&
            receipt.Generation == f.Owner.Generation && receipt.SelectionEpoch == f.Session.SelectionEpoch && f.Session.CanPublishLearning(receipt),
            "成功回执绑定已安装状态和原generation/epoch");
        TestCheck.That(Equal(receipt.Character, f.Read()) && receipt.Character.Revision == before.Revision + 1, "回执为实际SQLite提交结果");
        var missing = f.Session.LearnSkills(new(0, 0, [new(99, false, 1)]), Resolve);
        TestCheck.That(!missing.Success && missing.Receipt is null && ReferenceEquals(receipt.Character, f.Session.Selected), "失败学习无回执且不安装快照");
        Reject<IOException>(() => f.Session.LearnSkills(Purchase, (_, _) => throw new IOException("资源读取失败")), "资源读取异常不提交");
        TestCheck.That(Equal(f.Session.Selected!, f.Read()) && f.Session.CanPublishLearning(receipt), "规划异常保持原发布资格");
        f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, before.Name, 11, 0));
        TestCheck.That(f.Session.Selected!.Revision == receipt.Character.Revision && !f.Session.CanPublishLearning(receipt),
            "同revision的GM快照替换也使回执失效");
        result = f.Session.LearnSkills(Purchase, Resolve);
        f.Session.LearnSkills(Purchase, Resolve);
        TestCheck.That(!f.Session.CanPublishLearning(result.Receipt!), "后续提交使旧学习回执失效");
        result = f.Session.LearnSkills(Purchase, Resolve);
        f.Session.ReturnToRoster();
        TestCheck.That(!f.Session.CanPublishLearning(result.Receipt!), "返回列表后不能发布旧学习");
        f.Select();
        TestCheck.That(!f.Session.CanPublishLearning(result.Receipt!) && Equal(f.Session.Selected!, f.Read()), "同角色重选重读已提交数据且不复用旧回执");
        using var replacement = f.Owners.Acquire(f.Owner.Account, () => { });
        Reject<InvalidOperationException>(() => f.Session.CanPublishLearning(result.Receipt!), "旧generation不能发布学习回执");

        using var failed = new Fixture();
        failed.Select();
        var snapshot = failed.Session.Selected!;
        failed.FailSkillWrite();
        Reject<SqliteException>(() => failed.Session.LearnSkills(Purchase, Resolve), "学习后段SQL失败向上传递");
        TestCheck.That(ReferenceEquals(snapshot, failed.Session.Selected) && Equal(snapshot, failed.Read()), "学习后段SQL失败回滚点数、绑定和revision");
    }

    static async Task CreationRacesAsync()
    {
        using (var f = new Fixture())
        {
            await RaceAsync(f, definition => f.Session.CreateCharacter("Racing", Profile, definition), () =>
            {
                f.Select();
                f.Session.ReturnToRoster();
            });
            TestCheck.That(f.Session.Selected is null && f.Repository.List(f.Owner).Length == 1,
                "建角规划期间选角再回列表，旧epoch不得创建角色");
        }
        using (var f = new Fixture())
        {
            await RaceAsync(f, definition => f.Session.CreateCharacter("AfterDelete", Profile, definition), () => f.Session.DeleteCharacter(0));
            TestCheck.That(f.Repository.ListRoster(f.Owner).Length == 0, "建角锁外规划期间删除角色使旧epoch计划失效");
            var fresh = f.Session.CreateCharacter("Fixture", Profile);
            TestCheck.That(fresh.Slot == 0 && fresh.Id > 1, "删除后的新建角规划可继续且不复用旧身份");
        }
        using (var f = new Fixture())
        {
            AccountOwners.Lease? replacement = null;
            try
            {
                await RaceAsync(f, definition => f.Session.CreateCharacter("Takeover", Profile, definition),
                    () => replacement = f.Owners.Acquire(f.Owner.Account, () => { }));
                TestCheck.That(f.Repository.List(replacement!).Length == 1, "建角规划期间接管不产生旧owner角色");
            }
            finally { replacement?.Dispose(); }
        }
    }

    static async Task SkillRacesAsync()
    {
        foreach (bool initialized in new[] { false, true })
        foreach (string change in new[] { "gm", "reselect", "takeover", "end", "commit" })
        {
            using var f = new Fixture(initialized);
            f.Select(ready: initialized);
            var original = f.Session.Selected!;
            AccountOwners.Lease? replacement = null;
            try
            {
                await RaceAsync(f, definition =>
                {
                    if (initialized)
                        f.Session.LearnSkills(Purchase, definition);
                    else
                        f.Session.InitializeLearning(definition);
                }, () =>
                {
                    switch (change)
                    {
                        case "gm":
                            f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, original.Name, 11, 0));
                            break;
                        case "reselect":
                            f.Session.ReturnToRoster();
                            f.Select(ready: initialized);
                            break;
                        case "takeover":
                            replacement = f.Owners.Acquire(f.Owner.Account, () => { });
                            break;
                        case "end":
                            f.Session.End();
                            break;
                        case "commit":
                            if (initialized)
                                f.Session.LearnSkills(Purchase, Resolve);
                            else
                                f.Session.InitializeLearning(Resolve);
                            break;
                    }
                });
                var saved = f.Repository.LoadCharacter(replacement ?? f.Owner, original.Slot);
                TestCheck.That(saved.Revision == original.Revision + (change == "commit" ? 1 : 0), "过期规划不追加revision：" + initialized + "/" + change);
                TestCheck.That(saved.Level == (change == "gm" ? 11 : original.Level), "过期规划不覆盖GM等级：" + initialized + "/" + change);
                TestCheck.That(saved.Learning.Experience == original.Learning.Experience &&
                    (initialized ? saved.Learning.Trees[0].Sp == (change == "commit" ? 35 : 40) : saved.Learning.Initialized == (change == "commit")),
                    "过期规划不重复扣费或初始化：" + initialized + "/" + change);
            }
            finally { replacement?.Dispose(); }
        }
    }

    static void Layout()
    {
        using var f = new Fixture();
        f.Select(ready: false);
        var move = new SkillSlotChangeCommand(0, 3, 6, -1, 0);
        TestCheck.That(f.Session.MoveSkillSlot(move, (_, _) => throw new Exception("不应解析")).Failure == SkillLearningFailure.Unsupported,
            "未Ready不解析或提交槽位");
        f.Session.MarkReady();
        var before = f.Session.Selected!;
        var result = f.Session.MoveSkillSlot(move, Resolve);
        TestCheck.That(result.Success && result.State!.Trees[0].Skills.Single() == new LearnedSkill(6, 5, 1) &&
            result.State.Trees[1].Skills.Single() == new LearnedSkill(7, 5, 1) && result.State.Experience == 2035 &&
            result.State.Trees[0].Sp == 40 && result.State.Trees[0].Tp == 4, "会话换槽保持全部余额、等级、另一树与经验");
        TestCheck.That(Equal(f.Session.Selected!, f.Read()) && f.Session.Selected!.Revision == before.Revision + 1 &&
            f.Session.CanPublishLearning(result.Receipt!), "换槽返回实际提交且可发布的快照");
        var unchanged = f.Session.Selected;
        var noop = f.Session.MoveSkillSlot(new(0, 6, 6, -1, 0), Resolve);
        TestCheck.That(noop.Success && ReferenceEquals(unchanged, f.Session.Selected), "同槽确认不写库或换快照");
        Reject<InvalidDataException>(() => f.Repository.CommitSkillLayout(f.Owner, unchanged!, unchanged!.Learning with
        {
            Trees = Array.AsReadOnly(unchanged.Learning.Trees.Select(tree => tree with { Sp = (ushort)(tree.Sp - 1) }).ToArray())
        }), "换槽仓储拒绝偷改SP");
        f.FailSkillWrite();
        Reject<SqliteException>(() => f.Session.MoveSkillSlot(new(0, 6, 0, -1, 0), Resolve), "晚期SQL失败传播");
        TestCheck.That(ReferenceEquals(unchanged, f.Session.Selected) && Equal(unchanged!, f.Read()), "换槽SQL失败回滚绑定、余额及revision");
    }

    static async Task LayoutRacesAsync()
    {
        foreach (string change in new[] { "gm", "reselect", "takeover", "commit" })
        {
            using var f = new Fixture();
            f.Select();
            var original = f.Session.Selected!;
            AccountOwners.Lease? replacement = null;
            try
            {
                await RaceAsync(f, definition => f.Session.MoveSkillSlot(new(0, 3, 6, -1, 0), definition), () =>
                {
                    if (change == "gm") f.Session.UpdateProgress(new(CharacterProgressField.Level, f.Owner.Account, original.Name, 11, 0));
                    if (change == "reselect") { f.Session.ReturnToRoster(); f.Select(); }
                    if (change == "takeover") replacement = f.Owners.Acquire(f.Owner.Account, () => { });
                    if (change == "commit") f.Session.LearnSkills(Purchase, Resolve);
                });
                var saved = f.Repository.LoadCharacter(replacement ?? f.Owner, original.Slot);
                TestCheck.That(saved.Learning.Trees[0].Skills.Single().Slot == 3 && saved.Revision == original.Revision + (change == "commit" ? 1 : 0),
                    "过期换槽规划不能覆盖GM、重选、接管或学习结果：" + change);
            }
            finally { replacement?.Dispose(); }
        }
    }

    static async Task RaceAsync(Fixture f, Action<Func<byte, ushort, SkillLearningDefinition>> operation, Action mutate)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task work = Task.Run(() => operation((job, id) =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(15)))
                throw new TimeoutException("资源规划未释放");
            return Resolve(job, id);
        }));
        Task competing = Task.CompletedTask;
        try
        {
            TestCheck.That(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))), "进入锁外资源规划");
            competing = Task.Run(() =>
            {
                using var unrelated = f.Owners.Acquire("unrelated", () => { });
                TestCheck.That(unrelated.Execute(() => true), "资源规划不阻塞其他账号owner操作");
                mutate();
            });
            await competing.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            // 即使断言失败也收回两项工作，不让迟到提交碰到下一用例或已删除的临时库。
            try { await Task.WhenAll(work, competing).WaitAsync(TimeSpan.FromSeconds(20)); }
            catch (InvalidOperationException) { }
        }
        try
        {
            await work;
        }
        catch (InvalidOperationException)
        {
            TestCheck.That(true, "状态变化后拒绝原规划，不自动重跑");
            return;
        }
        throw new InvalidOperationException("过期规划未被拒绝");
    }

    static bool Equal(Character a, Character b) => JsonSerializer.Serialize(a) == JsonSerializer.Serialize(b);

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-session-planning-" + Guid.NewGuid().ToString("N"));
        public AccountOwners Owners { get; } = new();
        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public TownCatalog Towns { get; }
        public GameSession Session { get; }
        readonly ushort slot;
        public string Database => Path.Combine(root, "game.db");

        public Fixture(bool initialized = true)
        {
            Directory.CreateDirectory(root);
            var room = new Location(1, 0, 10, 10);
            var street = new Location(2, 0, 10, 10);
            TownArea Area(Location point, bool isRoom, Location target) => new(point.Town, point.Area, 1, [], [], [], point,
                new("synthetic.map", [new(new(0, 0, 20, 20), [], [])], [new(new(0, 0, 20, 20), target.Town, target.Area, [], [])], isRoom, []), [], []);
            Towns = new(new() { [(1, 0)] = Area(room, true, street), [(2, 0)] = Area(street, false, room) },
                room, street, new(0, [], []), [], new Dictionary<uint, uint[]>());
            Repository = new(Path.Combine(root, "game.db"));
            Owner = Owners.Acquire("planning", () => { });
            CharacterLearningState learning = initialized
                ? new(2035, Array.AsReadOnly<CharacterSkillTree>([new(0, 40, 4, Array.AsReadOnly<LearnedSkill>([new(3, 5, 1)])),
                    new(1, 30, 2, Array.AsReadOnly<LearnedSkill>([new(7, 5, 1)]))]))
                : CharacterLearningState.Uninitialized with { Experience = 2035 };
            slot = Repository.Create(Owner, new("Fixture", room, street) { Level = 10, Skills = [new(5, 1)], InitialLearning = learning }).Slot;
            Session = new(Repository, Towns, Owner);
        }

        public void Select(bool ready = true)
        {
            Session.Select(slot);
            if (ready)
                Session.MarkReady();
        }

        public Character Read() => Repository.LoadCharacter(Owner, slot);

        public void Sql(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Foreign Keys=True;Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        public object? Scalar(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Foreign Keys=True;Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }

        public string Rows(string sql)
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Foreign Keys=True;Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount];
                reader.GetValues(values);
                rows.Add(values.Select(value => value is DBNull ? null : value).ToArray());
            }
            return JsonSerializer.Serialize(rows);
        }

        string[] Tables()
        {
            using var db = new SqliteConnection("Data Source=" + Database + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name";
            using var reader = command.ExecuteReader();
            var tables = new List<string>();
            while (reader.Read()) tables.Add(reader.GetString(0));
            return tables.ToArray();
        }

        // 辅助连接不入池，避免清理临时目录的句柄异常掩盖业务断言。
        public string Snapshot() => Scalar("PRAGMA user_version") + ":" +
            Rows("SELECT name,sql FROM sqlite_schema WHERE sql IS NOT NULL ORDER BY name") + ":" +
            string.Join('|', Tables().Select(table => Rows("SELECT rowid,* FROM \"" + table + "\" ORDER BY rowid")));

        // 两表虽带character_前缀，仍是按账号保留的完成/额度事实，不随收件角色删除。
        static bool RetainedStrongway2019AccountFact(string table) => table is
            "character_strongway2019_completions" or "character_strongway2019_match_facts";

        public string SharedSnapshot() => string.Join('|', Tables().Where(table => (table.StartsWith("account", StringComparison.Ordinal) ||
            RetainedStrongway2019AccountFact(table)) &&
            table != "account_completed_quests").Select(table => Rows("SELECT rowid,* FROM \"" + table + "\" ORDER BY rowid"))) +
            Rows("SELECT rowid,account,quest_id,acceptance_id,daily_period,answer FROM account_completed_quests ORDER BY rowid") +
            Rows("SELECT * FROM character_inventory_items WHERE character_id IS NULL ORDER BY item_uid") +
            Rows("SELECT * FROM character_inventory_slots WHERE character_id IS NULL ORDER BY item_uid") +
            Rows("SELECT * FROM shop_purchase_counts WHERE character_id=0 ORDER BY rowid");

        public long[] PrivateRows(ushort id) => Tables().Where(table => table.StartsWith("character_", StringComparison.Ordinal) &&
            !RetainedStrongway2019AccountFact(table))
            .Select(table => Convert.ToInt64(Scalar("SELECT count(*) FROM \"" + table + "\" WHERE " +
                (table == "character_social_links" ? "owner_id=" + id + " OR target_id=" : "character_id=") + id))).ToArray();

        public void SeedDeletionGraph(ushort id)
        {
            new AuthenticationService(new AuthenticationRepository(Database)).Create(Owner.Account, "fixture-password");
            string training=JsonSerializer.Serialize(new DfoServer.Game.Pvp.PvpTrainingSkills(
                new(0,[new(0,0,0,[]),new(1,0,0,[])]),0,Guid.Empty));
            string fiendwarPayment = JsonSerializer.Serialize(new DfoServer.Game.Raids.FiendwarPaymentSnapshot(
                [new(0, new(InventoryListType.Main, 121), new ItemCore(10306760, ItemKind.Material, 1))]));
            Sql($$"""
                INSERT INTO account_wallets(account) VALUES('planning') ON CONFLICT(account) DO NOTHING;
                INSERT INTO account_material_balances VALUES('planning',10308807,120,740000,150,'{"{{id}}":150}');
                INSERT INTO account_material_audit(account,character_id,generation,action_name,item_id,before_state,after_state)
                  VALUES('planning',{{id}},1,'fixture',10308807,'{}','{}');
                INSERT INTO account_client_settings VALUES('planning',zeroblob(204));
                INSERT INTO account_card_memberships(account,black_diamond_expires,gold_card_expires,revision) VALUES('planning',9999999999,9999999999,7);
                INSERT INTO account_expert_contracts VALUES('planning',9999999999);
                INSERT INTO account_completed_quests VALUES('planning',900,{{id}},'1234567890abcdef1234567890abcdef',0,2);
                INSERT INTO character_completed_quests VALUES({{id}},10,0,1);
                INSERT INTO character_active_main_quests VALUES({{id}},0,30,1,'1234567890abcdef1234567890abcdef');
                INSERT INTO character_skill_point_sources VALUES({{id}},1);
                INSERT INTO character_tp_sources VALUES({{id}},2);
                INSERT INTO character_permanent_stats VALUES({{id}},'planning',250,250,50,50,50,50,10,10);
                INSERT INTO character_skill_commands VALUES({{id}},0,5,X'0102');
                INSERT INTO character_skill_combos VALUES({{id}},0,5,X'');
                INSERT INTO character_client_settings VALUES({{id}},'planning',zeroblob(512));
                INSERT INTO character_game_settings VALUES({{id}},'planning',zeroblob(204));
                INSERT INTO character_quest_notify VALUES({{id}},'planning',X'02000000000000000000000000000000');
                INSERT INTO character_privilege_service VALUES({{id}},1000,2000000000,1,1,739916,5,739916,1);
                INSERT INTO character_dungeon_state(character_id,account,next_object_key,last_attempt,
                  capsule_experience,proficiency_level,proficiency_experience) VALUES({{id}},'planning',256,1,0,0,0);
                INSERT INTO character_adventure_credits VALUES({{id}},'planning',10,20);
                INSERT INTO character_fatigue VALUES({{id}},'planning',0,156,0,156,0,0);
                INSERT INTO character_fatigue_acceleration VALUES({{id}},'planning',1);
                INSERT INTO character_fatigue_reset_days VALUES({{id}},739916);
                INSERT INTO character_pcroom_claims VALUES({{id}},'planning',20261031);
                INSERT INTO character_teleport_cooldowns(character_id,account,potion_until,aura_until) VALUES({{id}},'planning',2000000000000,2000000420000);
                INSERT INTO character_exp_bonus VALUES({{id}},'planning',2000007200,1000);
                INSERT INTO character_consumable_states VALUES({{id}},'planning',0,10087825,2000000000000,7,5);
                INSERT INTO character_boss_selection_buff VALUES({{id}},'planning','{"ItemId":490705256,"Remaining":5,"UntilMilliseconds":2000000000000,"Definition":{"BuffId":300,"DungeonId":7191,"Entries":5,"Weights":[{"MapId":92689,"Weight":40},{"MapId":92690,"Weight":40},{"MapId":92697,"Weight":20}]} }');
                INSERT INTO character_expert_job(character_id,account,job_type,expertness) VALUES({{id}},'planning',1,0);
                INSERT INTO character_expert_meta VALUES({{id}},'planning',0,1,300);
                INSERT INTO character_assault_daily VALUES({{id}},'planning',740000,2,1);
                INSERT INTO character_luke_normal_drop_claims VALUES({{id}},'planning',20261026,56063,'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb');
                INSERT INTO character_luke_normal_progress VALUES({{id}},'planning',20261026,3);
                INSERT INTO character_crack_daily(character_id,account,day,clear_count,daily_reward) VALUES({{id}},'planning',740000,1,0);
                INSERT INTO character_growup_change(character_id,account,change_count) VALUES({{id}},'planning',2);
                INSERT INTO character_weekly_dungeon(character_id,account,week,slot,used) VALUES({{id}},'planning',20261022,0,3);
                INSERT INTO account_grief_tower VALUES('planning',20261023,1);
                INSERT INTO character_grief_tower VALUES({{id}},'planning',20261023,0,1,'1234567890abcdef1234567890abcdef',1);
                INSERT INTO character_grief_tower_entries VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',1,20261023,1,5115,0,1,1,1);
                INSERT INTO character_grief_tower_clears VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','1234567890abcdef1234567890abcdef',1,20261023,1,5115,'{}',NULL,1);
                INSERT INTO character_dungeon_fatigue_receipts VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',1,1,1,1,'test',0,1,'{}','{}');
                INSERT INTO character_gold_card_uses VALUES({{id}},'planning',20261011,2);
                INSERT INTO character_quest_daily_clear VALUES({{id}},'planning',13200,20261011,'1234567890abcdef1234567890abcdef',1);
                INSERT INTO mailbox_sent_gold VALUES({{id}},20261011,12);
                INSERT INTO character_quest_lifecycle_receipts VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','Accept',30,
                  '1234567890abcdef1234567890abcdef',0,1,'[]','[]','[]',NULL,NULL,NULL);
                INSERT INTO character_quest_lifecycle_receipts VALUES({{id}},'planning','fedcba0987654321fedcba0987654321','Condition',30,
                  '1234567890abcdef1234567890abcdef',1,2,'[]','[]','[]','{}',NULL,NULL);
                INSERT INTO character_dungeon_scene_clear_receipts(character_id,account,operation_id,source_type,instance_id,quest_id,
                  task_attempt,before_epoch,epoch,dungeon_id,map_id,template_id,source_ordinal,fingerprint,death_events,generation,revision)
                  VALUES({{id}},'planning','fedcba0987654321fedcba0987654321','PassiveObject','instance',30,
                    1,1,2,1,1,69291,0,'0000000000000000000000000000000000000000000000000000000000000000',
                    X'1234567890ABCDEF1234567890ABCDEF',1,2);
                INSERT INTO character_quest_skip_receipts VALUES({{id}},'planning',NULL,NULL,NULL,0,1,'{}','abcdef1234567890abcdef1234567890','Manual');
                INSERT INTO character_quest_effect_receipts VALUES({{id}},'abcdef1234567890abcdef1234567890',0,1,'[]','[]');
                INSERT INTO character_quest_special_reward_claims VALUES({{id}},30,'abcdef1234567890abcdef1234567890');
                INSERT INTO character_dungeon_growth_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,combat_sequence,generation,amount,rule_id,resource_source,monster_id,monster_level,before_level,after_level,before_experience,after_experience,revision) VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','instance',0,1,1,1,1,1,1,1,'test','test',1,1,1,1,0,1,1);
                INSERT INTO character_dungeon_loot_receipts VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','instance',1,1,1,1,'test','test',1,'{}',0,0,0);
                INSERT INTO character_dungeon_pickup_receipts VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',1,'instance',1,1,0,121,1,'{}',1);
                INSERT INTO character_dungeon_drop_batches(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,map_id,monster_key,rule_id,resource_source,object_count)
                  VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','instance',0,1,1,1,1,'test','test',1);
                INSERT INTO character_dungeon_drop_objects VALUES({{id}},'1234567890abcdef1234567890abcdef','instance',0,2,NULL,1,0,0,0);
                INSERT INTO character_dungeon_drop_pickups(character_id,account,event_id,ground_key,instance_id,task_attempt,epoch,destination_slot,item_uid,after_core,currency_kind,amount,after_balance,account_revision,revision)
                  VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',2,'instance',1,1,NULL,NULL,NULL,0,1,1,0,1);
                INSERT INTO character_dungeon_passive_pickups VALUES({{id}},'planning',3,'1234567890abcdef1234567890abcdef',
                  1,1,NULL,1,NULL,NULL,NULL,0,1,1,0,1);
                INSERT INTO character_quest_drop_receipts VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',0,1,'{}','[]');
                INSERT INTO character_quest_clear_receipts(character_id,account,acceptance_id,quest_id,quest_slot,event_id,
                  instance_id,task_attempt,epoch,dungeon_id,map_id,generation,revision,source_type,growth_event_id,scene_operation_id)
                  VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',30,0,
                    '1234567890abcdef1234567890abcdef','instance',1,1,1,1,1,1,
                    'MonsterDeath','1234567890abcdef1234567890abcdef',NULL);
                INSERT INTO character_quest_completion_receipts(character_id,account,acceptance_id,quest_id,event_id,instance_id,task_attempt,epoch,
                  generation,rule_id,resource_source,quest_source,native_index,base_experience,raw_additional_percent,difficulty_key,difficulty_weight,
                  experience_amount,gold_amount,before_level,after_level,before_experience,after_experience,before_revision,revision,rewards,clear_acceptance_id)
                  VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',30,'1234567890abcdef1234567890abcdef','instance',1,1,
                    1,'test','test','test',0,1,0,'normal',1,1,0,1,1,0,1,0,1,'[]','1234567890abcdef1234567890abcdef');
                INSERT INTO character_operation_hope_reward_receipts(character_id,account,instance_id,acceptance_id,task_attempt,
                  maze_ordinal,quest_id,clear_epoch,generation,before_revision,revision,before_level,after_level,
                  before_experience,requested_experience,after_experience,plan_snapshot,rewards,changed_items,created_at)
                  VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','1234567890abcdef1234567890abcdef',1,
                    0,6177,1,1,0,1,1,1,0,0,0,'{}','[]','[]',1);
                INSERT INTO character_quest_stage_witnesses VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',30,1,13,1,'Granted','{}');
                INSERT INTO character_quest_stage_claims VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','1234567890abcdef1234567890abcdef',13,1,2,1,1);
                INSERT INTO character_dungeon_settlement_receipts(character_id,account,event_id,instance_id,quest_id,task_attempt,epoch,
                  dungeon_id,map_id,death_events,report_body,generation,base_experience,rank_experience,total_experience,
                  rank_bonus_index,rule_id,resource_source,before_level,after_level,before_experience,after_experience,revision)
                  VALUES({{id}},'planning','settlement','instance',0,1,1,1,1,zeroblob(16),X'01',1,
                  1,0,1,-1,'test','test',1,1,0,1,1);
                INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
                  VALUES({{id}},'planning','abcdef1234567890abcdef1234567890',1,'fixture/delete',1,0);
                INSERT INTO character_raid_runs(character_id,account,instance_id,raid_type,source,admitted_at,state)
                  VALUES({{id}},'planning','fedcba1234567890abcdef1234567890',2,'fixture/delete-fiendwar',1,0);
                INSERT INTO character_fiendwar_recovery(character_id,instance_id,owner_generation,selection_epoch,state,payment_json)
                  VALUES({{id}},'fedcba1234567890abcdef1234567890',1,1,0,'{{fiendwarPayment}}');
                INSERT INTO character_fiendwar_title_facts(character_id,account,quest_id,total,applied)
                  VALUES({{id}},'planning',7007,5,2);
                INSERT INTO character_fiendwar_counter_stream VALUES({{id}},'planning','fixture-room',19);
                INSERT INTO character_raid_reward_receipts(character_id,account,instance_id,phase,kind,card,week_key,final_phase,plan_json,mail_ids,created_at)
                  VALUES({{id}},'planning','1234567890abcdef1234567890abcdef',1,0,2,20261022,1,'{}','[]',1);
                INSERT INTO character_dungeon_card_rewards(character_id,account,instance_id,plan_json,reward_json,mail_id,revision) VALUES({{id}},'planning','instance','{}','{}',NULL,1);
                INSERT INTO character_dungeon_card_options VALUES({{id}},'planning','instance','{}',NULL,NULL,NULL,NULL,NULL,NULL,NULL);
                INSERT INTO character_dungeon_tournament_rewards VALUES({{id}},'planning','instance','{}',0,0,'[{},{}]',NULL,1);
                INSERT INTO character_tutorial_state VALUES({{id}},'planning',7115,0,0);
                INSERT INTO character_tutorial_receipts VALUES({{id}},'planning','abcdef1234567890abcdef1234567890','skip',
                  '1234567890abcdef1234567890abcdef',0,0,0,0,0,0,1,'{}');
                INSERT INTO character_inventory_items(item_uid,character_id,account,item_core)
                  VALUES(95000,{{id}},'planning','{"ItemId":26596,"Kind":1,"Value":1}');
                INSERT INTO character_title_book_slots VALUES({{id}},'planning',18,0,95000);
                INSERT INTO character_inventory_items(item_uid,character_id,account,item_core)
                  VALUES(95001,{{id}},'planning','{"ItemId":2001,"Kind":1,"Value":1}');
                INSERT INTO character_inventory_slots(character_id,account,list_type,slot_index,item_uid)
                  VALUES({{id}},'planning',0,9,95001);
                INSERT INTO character_skill_switch_inventory(character_id,account,skill_id) VALUES({{id}},'planning',5);
                INSERT INTO character_skill_switch_inventory_items(character_id,equipment_part,item_uid,item_id)
                  VALUES({{id}},4,95001,2001);
                INSERT INTO character_title_progress VALUES({{id}},'planning',6501,1,2,3,0);
                INSERT INTO character_title_sections VALUES({{id}},'planning',0,1);
                INSERT INTO character_epic_production VALUES({{id}},101000747,1000,1,2,1,0,0,0,0);
                INSERT INTO character_epic_wishes VALUES({{id}},'planning','[5000,0,7000,0,0]');
                INSERT INTO character_pvp_training_skills VALUES({{id}},'{{training}}','1234567890abcdef1234567890abcdef',0);
                INSERT INTO character_pvp_mission_facts VALUES({{id}},'planning',0,0,0,'1234567890abcdef1234567890abcdef');
                INSERT INTO character_pvp_mission_rewards VALUES({{id}},'planning',1,'[]');
                INSERT INTO character_pvp_daily_missions VALUES({{id}},'planning',1,'{"Day":1,"Entries":[]}');
                INSERT OR IGNORE INTO pvp_arena_match_results VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa','{}',0);
                INSERT INTO character_evildom_war_rewards VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','1234567890abcdef1234567890abcdef',1,5422,1,0,'{}','[]',1);
                INSERT INTO character_evildom_war_hidden_rewards VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','{}','[]');
                INSERT INTO evildom_war_battles VALUES('1234567890abcdef1234567890abcdef',0,9999999999,'{}');
                INSERT INTO character_evildom_war_seats VALUES({{id}},'planning','1234567890abcdef1234567890abcdef','1234567890abcdef1234567890abcdef',0,NULL,'{}','1234567890abcdef1234567890abcdef');
                INSERT INTO character_pvp_ratings VALUES({{id}},'planning',1,'{}');
                INSERT INTO character_pvp_rating_claims(match_id,character_id,account,rating_mode,outcome,receipt_json)
                  VALUES('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',{{id}},'planning',0,0,'{}');
                INSERT INTO character_collectbox_state VALUES({{id}},'planning',2);
                INSERT INTO character_collectbox_slots VALUES({{id}},'planning',2,3,104);
                INSERT INTO character_collectbox_expirations VALUES({{id}},'planning',2,2000000000);
                INSERT INTO character_social_links(owner_id,target_id,kind,created,memo)
                  SELECT {{id}},id,0,0,'' FROM characters WHERE id<>{{id}} ORDER BY id LIMIT 1;
                INSERT INTO character_dungeon_incount VALUES({{id}},'planning',400000016,20261020,1,20261015,1);
                INSERT INTO character_dungeon_incount_extra VALUES({{id}},'planning',5428,20261020,1,20261015,1);
                INSERT INTO character_ekern_cycle VALUES({{id}},'planning',739917,3,1);
                INSERT INTO character_rename_pending VALUES({{id}},'planning',95001);
                INSERT INTO character_name_history VALUES({{id}},'planning',0,'FormerName',1000);
                INSERT INTO character_beast_dungeon_entries VALUES({{id}},'planning',20261020,310,1);
                INSERT INTO character_beast_dungeon_reservations VALUES({{id}},'planning','abcdef1234567890abcdef1234567890',1,1,20261020,310,0,0);
                INSERT INTO character_beast_stage_rewards VALUES({{id}},'planning','abcdef1234567890abcdef1234567890',0,'abcdef1234567890abcdef1234567891',400363,1,10303240);
                INSERT INTO character_guild_hongbao_purchases(character_id,day_start_unix,cera) VALUES({{id}},2000000000,1);
                INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
                  SELECT 65000,'删除福利夹具','deletion-benefits',id,1 FROM characters WHERE id<>{{id}} ORDER BY id LIMIT 1;
                INSERT INTO character_guild_npc_benefits(character_id,guild_id,joined_at,effect,benefit_id,remaining,deadline,selected)
                  VALUES({{id}},65000,1,'guild trainer attack raid buff',5001,0,0,1);
                INSERT INTO character_guild_npc_item_crafts(craft_id,character_id,guild_id,joined_at,npc_id,benefit_id,deadline,item_core)
                    VALUES(printf('%064d',{{id}}),{{id}},65000,1,400001065,7001,2000000000,'{}');
                INSERT INTO character_guild_npc_craft_weeks(character_id,npc_id,week_start,craft_id)
                    VALUES({{id}},400001061,2000000000,printf('%064d',{{id}}));
                INSERT INTO character_server_item_usage VALUES({{id}},'{}');
                INSERT INTO character_prey_normal_progress VALUES({{id}},'planning',20261031,3,4);
                INSERT INTO character_anton_normal_progress VALUES({{id}},'planning',20261031,3,4);
                INSERT INTO character_strongway2019_statistics VALUES('planning',{{id}},'abcdef1234567890abcdef1234567890',1,1,5359,0,1000,'{}');
                INSERT INTO account_strongway2019_rewards VALUES('planning','abcdef1234567890abcdef1234567890',{{id}},1,1,5359,0,20261102,20261028,1,'{}','{}',1);
                INSERT INTO character_strongway2019_completions(account,instance_id,character_id,task_attempt,run_epoch,
                  dungeon_id,bucket,day,week,created_at,reward_json,fact_json,state,mail_id)
                  VALUES('planning','10000000000000000000000000000001',{{id}},2,1,5359,0,20261102,20261028,1,'{}','{}',2,NULL);
                INSERT INTO character_strongway2019_match_facts(account,character_id,instance_id,competition_id,bucket,
                  day,week,created_at,state,snapshot_json)
                  VALUES('planning',{{id}},'10000000000000000000000000000002','10000000000000000000000000000003',1,
                    20261102,20261028,1,0,'{}');
                """);
            TestCheck.That(Scalar("SELECT count(*) FROM character_strongway2019_completions WHERE account='planning' AND character_id=" + id) is 1L &&
                Scalar("SELECT count(*) FROM character_strongway2019_match_facts WHERE account='planning' AND character_id=" + id) is 1L,
                "删除夹具同时保存额度已满完成和未消费匹配权益，由共享快照核对删角及重建后保留");
            TestCheck.That(PrivateRows(id).All(value => value > 0), "删除夹具覆盖当前全部角色私有表和外键账本");
        }

        public void FailSkillWrite()
        {
            using var db = new SqliteConnection("Data Source=" + Path.Combine(root, "game.db") + ";Pooling=False");
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TRIGGER fail_skill_write BEFORE INSERT ON character_skills BEGIN SELECT RAISE(ABORT,'skill write failure'); END";
            command.ExecuteNonQuery();
        }

        public void Dispose()
        {
            Owner.Dispose();
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }
}
