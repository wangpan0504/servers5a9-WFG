using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.PcRoom;
using DfoServer.Game.Session;
using DfoServer.Game.Settings;
using DfoServer.GameWorld.PcRoom;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PcRoomTests
{
    public static void RunCore()
    {
        Bytes(PcRoomPackets.Login(false), [0, 0, 0, 0, 0], "1517独立DWORD保留0与未领取BYTE");
        Bytes(PcRoomPackets.Login(true), [0, 0, 0, 0, 1], "1517只切换已领取位，不借首DWORD编码错误");
        Bytes(PcRoomPackets.Rental(false), [0], "1639未领取为单BYTE");
        Bytes(PcRoomPackets.Rental(true), [1], "1639已领取为单BYTE");
        Bytes(PcRoomPackets.Claim(0), [1, 0, 0, 0, 0], "1550和1637成功为公共成功头后完整result32");
        Bytes(PcRoomPackets.Claim(3), [0, 3, 0, 3, 0, 0, 0], "失败先公共error16再专用result32，包满不能使用截断5B");
        Bytes(PcRoomPackets.Weapons(0), [1, 0], "493成功旧物扣除数为0，不能误删刚发的新装备");
        Bytes(PcRoomPackets.Weapons(19), [0, 19, 0], "493失败使用本版公共u16错误码");
        PcRoomRequests.Empty([]);
        PcRoomRequests.Empty(new byte[13]);
        foreach (int size in new[] { 1, 12, 14 })
            Reject<InvalidDataException>(() => PcRoomRequests.Empty(new byte[size]), "空正文领奖拒绝未知尾部");
        Bytes(PcRoomRequests.Weapons([2, 0, 1, 0, 3]), [1, 3], "租赁只解析候选位置，不接受客户端物品编号");
        Bytes(PcRoomRequests.Weapons([.. new byte[13], 1, 0, 2]), [2], "493兼容原生独立13B预留区");
        foreach (byte[] body in new byte[][] { [], [0], [1, 1, 0], [2, 0, 1], [2, 0, 1, 0, 1], [1, 0, 0, 0] })
            Reject<InvalidDataException>(() => PcRoomRequests.Weapons(body), "租赁非法分类、重复候选及坏长度拒绝");
        var before = new DateTimeOffset(2026, 10, 28, 21, 59, 59, TimeSpan.Zero);
        That(PcRoomDay.At(before) + 1 == PcRoomDay.At(before.AddSeconds(1)) &&
            PcRoomDay.Next(before) == before.AddSeconds(1).ToUnixTimeSeconds(), "网吧次数日界固定UTC+8 06:00");
        That(PcRoomDay.At(before.AddHours(-6)) == PcRoomDay.At(before), "UTC+8零点不提前刷新网吧每日次数");
    }

    // 使用真实资源与同一个临时库验证资格/日账本，TCP只保留端到端时序和原生开盒链。
    public static async Task RunResourcesAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options, Path.Combine(root, "pcroom-rules"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var items = resources.ItemResources;
        var service = new PcRoomRewardService(new PcRoomCatalog(resources.Archive), items.Items, items.GoldLimits, items.EpicPieces);
        var clock = new Clock(new(2026, 10, 28, 21, 59, 59, TimeSpan.Zero));
        var settings = new GameDebugSettings { PremiumPcRoom = true };
        // 事务用例直接取得租约，先建立真实账号外键；登录认证另由TCP入口验证。
        That(new AuthenticationRepository(fixture.Database).TryCreate(fixture.Options.DevelopmentAccount,
            new DfoServer.Game.Auth.PasswordCredential([1], [2], 100000)), "网吧事务夹具准备实际账号归属");
        using var owner = new AccountOwners().Acquire(fixture.Options.DevelopmentAccount, () => { });
        var first = Create("PcRoomRuleA");
        var second = Create("PcRoomRuleB");
        var session = new GameSession(repository, resources.Towns, owner, debugSettings: settings,
            timeProvider: clock, items: items.Items, pieces: items.EpicPieces);
        session.ConfigurePcRoom(service.Catalog);
        Select(first.Slot);
        var initial = session.ReadPcRoomView()!;
        That(initial.Active && initial.Claims == default(PcRoomClaimState), "全局网吧开启时首次投影未领奖，不凭等级补历史领取");
        var login = session.ClaimPcRoom(false, service);
        That(login.Accepted && login.Receipt is not null && login.Receipt.Character.Inventory.Items.Any(item =>
            item.Uid > 0 && item.Core.ItemId == service.Catalog.Premium.LoginItemId), "登录礼盒产生真实物理UID并安装已提交快照");
        string before = Snapshot();
        That(session.ClaimPcRoom(false, service) is { Accepted: false, Error: 2 } && Snapshot() == before,
            "同日重复登录领奖无保存、无新UID且返回已领取");
        That(!session.CanPublishPcRoom(initial), "发奖替换快照后不再发布原角色面板视图");
        session.ReturnToRoster();
        Select(second.Slot);
        That(session.ReadPcRoomView()!.Claims.LoginClaimed && session.ClaimPcRoom(false, service).Error == 2,
            "同账号不同角色共享登录奖日次数");
        var secondRental = session.ClaimPcRoom(true, service);
        That(secondRental.Accepted && secondRental.Receipt!.Character.Inventory.Items.Single(item =>
            item.Core.ItemId == service.Catalog.Premium.RentalItemId) is { Uid: > 0, Core.IsRental: true } rental &&
            rental.ExpireTime == clock.GetUtcNow().ToUnixTimeSeconds() + 86400,
            "租赁礼盒为完整租赁实例及领取后24小时绝对期限");
        session.ReturnToRoster();
        Select(first.Slot);
        That(!session.ReadPcRoomView()!.Claims.RentalClaimed && session.ClaimPcRoom(true, service).Accepted,
            "租赁礼盒角色之间独立，不误用账号登录奖次数");
        That(session.PlanAndCommitInventory(character => InventoryRewardGrantService.TryGrantBatch(character,
            [new(490007908, 1), new(490007909, 1)], items.Items, items.GoldLimits, items.EpicPieces, clock.GetUtcNow())).Accepted,
            "在原测试角色准备两种真实网吧药剂实例");
        var recovery = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == 490007908);
        var fatigueRecovery = session.Selected.Inventory.Items.Single(item => item.Core.ItemId == 490007909);
        var consumables = new InventoryConsumableUseService(items.RecoveryConsumables, clock);
        var fatigueConsumables = new FatigueConsumableService(items.Items, clock);
        var burning = session.ChangeFatigueAcceleration(true)!;
        That(burning.Active && session.CanPublishFatigueAcceleration(burning, requireCurrentState: true),
            "有效网吧环境可保存并发布燃烧选择");

        settings.PremiumPcRoom = false;
        That(session.CanPublishFatigueAcceleration(burning) &&
            !session.CanPublishFatigueAcceleration(burning, requireCurrentState: true),
            "保存偏好后网吧资格失效仍可确认原请求，但不能发布旧燃烧开启状态");
        before = Snapshot();
        var beforePotions = session.Selected;
        That(session.UseConsumable(new(recovery.Position, 0, recovery.Core.ItemId, 0), consumables) is
            { Outcome: ConsumableUseOutcome.Rejected, Reason: "pcroom-required" } && Snapshot() == before &&
            ReferenceEquals(session.Selected, beforePotions),
            "490007908专用消耗入口在环境关闭后拒绝，库存、疲劳、revision及Selected保持");
        That(session.UseFatigueConsumable(fatigueRecovery.Position, fatigueConsumables) is
            { Outcome: ConsumableUseOutcome.Rejected, Reason: "pcroom-required" } && Snapshot() == before &&
            ReferenceEquals(session.Selected, beforePotions),
            "490007909专用疲劳入口在环境关闭后拒绝，库存、疲劳、revision及Selected保持");
        That(session.ClaimPcRoom(false, service) is { Accepted: false, Error: 1 } && Snapshot() == before,
            "没有网吧环境时拒绝领奖，不消耗或重置原日账本");
        var booster = new InventoryBoosterService(items.Items, items.Boosters, items.GoldLimits, items.EpicPieces,
            clock: clock, contracts: items.Contracts, avatars: items.AvatarPackages);
        var box = session.Selected!.Inventory.Items.Single(item => item.Core.ItemId == service.Catalog.Premium.LoginItemId);
        var deniedBox = session.PlanAndCommitBooster(character => booster.Open(character,
            new(box.Position.Index, 1), out _, clock.GetUtcNow()), booster.UsePlaces(box.Core.ItemId), box.Core.ItemId);
        That(!deniedBox.Accepted && Snapshot() == before, "must use at pcbang礼盒在环境关闭后仍不能通过原开盒入口消费");

        session.ReturnToRoster();
        // 与既有疲劳药用例一致，只在测试准备中写入可完整恢复50点的原疲劳行。
        fixture.Sql("UPDATE character_fatigue SET used=50,remaining=maximum-50 WHERE character_id=$p0", first.Id);
        first = repository.LoadCharacter(owner, first.Slot);
        first = repository.CommitInventory(owner, first, new(first.Inventory, first.Inventory with
        {
            AccountWallet = first.Inventory.AccountWallet.SetContractExpiry(128, clock.GetUtcNow().ToUnixTimeSeconds() + 600)
        }, "pcroom_paid_test"));
        Select(first.Slot);
        That(session.PremiumPcRoom, "免费开关关闭后真实有效128仍提供网吧环境");
        That(session.UseConsumable(new(recovery.Position, 0, recovery.Core.ItemId, 0), consumables).Outcome ==
            ConsumableUseOutcome.Committed, "恢复真实有效128后，同一普通网吧药剂入口正常提交消费");
        var fatigueBefore = session.Selected!.Fatigue!;
        That(fatigueBefore.Used == 50 && fatigueBefore.Remaining == fatigueBefore.Maximum - 50,
            "原角色实际疲劳行已准备为可完整恢复50点");
        var usedFatigue = session.UseFatigueConsumable(fatigueRecovery.Position, fatigueConsumables);
        That(usedFatigue.Outcome == ConsumableUseOutcome.Committed &&
            session.Selected!.Inventory.Items.All(item => item.Uid != fatigueRecovery.Uid) &&
            session.Selected.Fatigue == fatigueBefore with { Used = 0, Remaining = fatigueBefore.Maximum } &&
            repository.LoadCharacter(owner, first.Slot).Fatigue == session.Selected.Fatigue,
            "恢复真实有效128后，原疲劳药入口实际消费909并持久恢复完整50点，再继续打开登录礼盒");
        var paidOpen = session.PlanAndCommitBooster(character => booster.Open(character,
            new(box.Position.Index, 1), out _, clock.GetUtcNow()), booster.UsePlaces(box.Core.ItemId), box.Core.ItemId);
        That(paidOpen.Accepted && session.Selected!.Inventory.Items.All(item => item.Uid != box.Uid),
            $"真实付费128独立满足must use at pcbang，免费开关关闭时原礼盒仍可正常打开：error={paidOpen.Error}, reason={paidOpen.RejectReason}, place={booster.UsePlaces(box.Core.ItemId).PendingRule}");
        clock.Now = clock.Now.AddSeconds(1);
        That(!session.ReadPcRoomView()!.Claims.LoginClaimed, "06:00按真实日戳开放新日，不改昨日资产");
        fixture.Sql("CREATE TRIGGER fail_pcroom_claim AFTER INSERT ON inventory_audit_log BEGIN SELECT RAISE(ABORT,'pcroom late rollback'); END");
        before = Snapshot();
        var selected = session.Selected;
        Reject<SqliteException>(() => session.ClaimPcRoom(false, service), "网吧奖励真实库存写入后的SQL故障不返回成功");
        That(Snapshot() == before && ReferenceEquals(session.Selected, selected) && !session.ReadPcRoomView()!.Claims.LoginClaimed,
            "晚SQL失败完整回滚日次数、物品、UID序列、revision及Selected");
        fixture.Sql("DROP TRIGGER fail_pcroom_claim");
        That(session.ClaimPcRoom(false, service).Accepted, "相同请求在晚SQL失败后可正常重试且只领一份");

        // 把同模板每一格都准备到堆叠上限，满包拒绝不能先占新业务日。
        clock.Now = clock.Now.AddDays(1);
        settings.PremiumPcRoom = true;
        session.ReturnToRoster();
        second = repository.LoadCharacter(owner, second.Slot);
        var template = items.Items.Get(service.Catalog.Premium.LoginItemId);
        var range = ItemSlotBoundService.Range(template.Kind, second.Inventory.Expansion);
        var filled = second.Inventory.Items.Where(item => item.Position.List != InventoryListType.Main ||
            item.Position.Index < range.Start || item.Position.Index > range.End).ToList();
        foreach (int slot in Enumerable.Range(range.Start, range.End - range.Start + 1))
            filled.Add(new(0, new(InventoryListType.Main, checked((short)slot)), InventoryRewardGrantService.Create(template, template.StackLimit, clock.GetUtcNow())));
        second = repository.CommitInventory(owner, second, new(second.Inventory,
            second.Inventory with { Items = [.. filled] }, "pcroom_full_bag_test"));
        Select(second.Slot);
        before = Snapshot();
        That(session.ClaimPcRoom(false, service) is { Accepted: false, Error: 17 } && Snapshot() == before &&
            !session.ReadPcRoomView()!.Claims.LoginClaimed,
            "满包拒绝不提前占账号新日次数或改变任何资产");
        string accountClaims = fixture.Snapshot("SELECT * FROM account_pcroom_claims ORDER BY account");
        repository.Delete(owner, repository.LoadDeletionTarget(owner, first.Slot));
        That(fixture.Query("SELECT count(*) FROM character_pcroom_claims WHERE character_id=$p0", first.Id) is [[0L]] &&
            fixture.Query("SELECT count(*) FROM character_fatigue_acceleration WHERE character_id=$p0", first.Id) is [[0L]] &&
            fixture.Snapshot("SELECT * FROM account_pcroom_claims ORDER BY account") == accountClaims,
            "删除角色清除其租赁账本和燃烧偏好，保留账号登录奖次数");
        session.End();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "网吧日账本和完整资产库保持外键及完整性");

        Character Create(string name) => repository.Create(owner, CharacterCreation.Prepare(name,
            resources.Characters.Get(0), resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get) with
            { Level = 95, AcceptedStoryLevel = 1 });
        void Select(ushort slot) { session.Select(slot, items.GoldLimits); session.MarkReady(); }
        string Snapshot() => string.Concat(new[] { "characters", "character_inventory_items", "character_inventory_slots",
            "character_fatigue", "account_currency_balances", "account_pcroom_claims", "character_pcroom_claims", "inventory_audit_log", "sqlite_sequence" }
            .Select(table => fixture.Snapshot("SELECT * FROM " + table + " ORDER BY rowid")));
    }

    sealed class Clock(DateTimeOffset origin) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = origin;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
