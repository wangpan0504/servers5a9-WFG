using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;

namespace DfoServer;

/// <summary>
/// CMD 184 `AUCTION_REGIST_CANCEL`（取消上架）：请求解析、应答字节、业务校验、**同事务**提交与回滚，
/// 以及「挂牌保存的条目快照 → 物品核心」的往返一致性（道具要原样回到背包，靠的就是它）。
///
/// 五组：
///   1. <see cref="Parsing"/>：实测样本逐字段、长度不足 9B 逐个长度拒绝、尾部字节容错；
///   2. <see cref="Wire"/>：`flag + mode(0)` 成功、`flag + errcode` 失败、白名单外的码拒发，
///      以及「每个领域错误都能映射到白名单码」；
///   3. <see cref="Snapshot"/>：`ItemEntrySnapshot` ↔ `ItemCoreFromEntry` 对全字段核心往返相等（字节级也相等）；
///   4. <see cref="Commit"/>：取消成功后 **state=2 + 道具回背包 + 审计** 同一事务、在售口径立即消失、
///      非本人/重复取消/背包满/陈旧计划全部拒绝且**不动挂牌状态**；
///   5. <see cref="Rollback"/>：挂牌状态写入失败时背包改动整体回滚（不留半成品）。
///
/// 模板用 `ItemCatalog.PrefillTemplates` 预置，数据库是真实文件库 ⇒ revision、审计、事务回滚都是真的。
/// </summary>
internal static class AuctionCancelTests
{
    /// <summary>实测样本：前导 0 + 挂牌 id 5 + 到期 0x6A99D40A。</summary>
    const string Sample = "00050000000AD4996A";
    const int SampleListingId = 5;
    const long SampleExpires = 0x6A99D40A;
    /// <summary>可上架装备（样本 10400）。</summary>
    const int SampleTemplate = 10400;

    public static void Run()
    {
        int checks = Count;
        Parsing();
        Wire();
        Snapshot();
        Commit();
        Commit(restricted: true);
        Rollback();
        Console.WriteLine($"PASS {Count - checks} auction cancel assertions");
    }

    /// <summary>请求解析：9 字节 = 前导 0 + 挂牌 id(i32@1) + 到期(i32@5)；不足 9B 一律拒绝。</summary>
    static void Parsing()
    {
        byte[] body = Convert.FromHexString(Sample);
        That(body.Length == AuctionCancelRequest.BodySize && AuctionCancelRequest.BodySize == 9,
            "实测样本正好 9 字节（= AuctionCancelRequest.BodySize）");
        var request = AuctionCancelRequest.Parse(body);
        That(request.Leading == 0, "样本 [0] 前导 0");
        That(request.ListingId == SampleListingId, "样本 [1..4] = 挂牌 id 5（小端 i32，定位用）");
        That(request.ExpiresAtUnix == SampleExpires, "样本 [5..8] = 到期时间 0x6A99D40A（辅助字段，不参与定位）");

        // 长度容错：不足 9 字节逐个长度拒绝（不按残缺字段去动资产），多出的尾部字节被忽略。
        for (int length = 0; length < AuctionCancelRequest.BodySize; length++)
        {
            int captured = length;
            Reject<InvalidDataException>(() => AuctionCancelRequest.Parse(body.AsSpan(0, captured).ToArray()),
                $"不足 9 字节（{captured}B）时拒绝");
        }
        var padded = AuctionCancelRequest.Parse([.. body, 0xAA, 0xBB]);
        That(padded.ListingId == SampleListingId && padded.ExpiresAtUnix == SampleExpires,
            "尾部多出的字节不参与解析（长度按客户端版本容错）");
        // 挂牌 id 为 0：解析层不拦（它只是字段），由 PlanCancel 按 InvalidRequest 拒绝。
        That(AuctionCancelRequest.Parse(new byte[9]).ListingId == 0, "全 0 请求解析出挂牌 id 0（由规划层拒绝）");
    }

    /// <summary>应答字节与错误码白名单。</summary>
    static void Wire()
    {
        Bytes(AuctionPackets.CancelSuccess(), [0x01, 0x00],
            "★ 取消成功应答 = flag(01) + mode(00)：帧体首字节必须是 flag，第二字节让客户端刷新 type7 面板");
        Bytes(AuctionPackets.CancelFailure(0xd2), [0x00, 0xd2, 0], "取消失败使用完整u16错误码");
        Bytes(AuctionPackets.CancelFailure(0x72), [0x00, 0x72, 0, 0], "取消114保留未知分支继续读取的模式0");
        foreach (byte code in AuctionRegisterErrors.Whitelist)
            That(AuctionPackets.CancelFailure(code)[1] == code, $"白名单码 0x{code:X2} 可以下发（184 复用 183 的白名单）");
        foreach (byte code in new byte[] { 0x00, 0x01, 0x7f, 0xd1, 0xd3, 0xff })
            Reject<InvalidDataException>(() => AuctionPackets.CancelFailure(code), $"旧领域取消入口不扩大白名单0x{code:X2}");

        foreach (var error in Enum.GetValues<AuctionCancelError>().Where(value => value != AuctionCancelError.None))
        {
            That(AuctionRegisterErrors.Whitelist.Contains(AuctionCancelErrors.ToClientCode(error)),
                $"取消上架的领域错误 {error} 映射到客户端白名单内的错误码");
        }
        Reject<InvalidDataException>(() => AuctionCancelErrors.ToClientCode(AuctionCancelError.None), "成功没有错误码");
        That(AuctionCancelErrors.ToClientCode(AuctionCancelError.NotFound) == AuctionCancelErrors.Silent &&
            AuctionCancelErrors.ToClientCode(AuctionCancelError.NotOwner) == AuctionCancelErrors.Silent &&
            AuctionCancelErrors.ToClientCode(AuctionCancelError.AlreadyInactive) == AuctionCancelErrors.Silent,
            "「已经不在/不是你的」映射到静默码 0x7e（与 183 的「道具已不在背包」同款）");
        That(AuctionCancelErrors.ToClientCode(AuctionCancelError.InventoryRejected) == AuctionCancelErrors.Generic &&
            AuctionCancelErrors.ToClientCode(AuctionCancelError.Unavailable) == AuctionCancelErrors.Generic &&
            AuctionCancelErrors.ToClientCode(AuctionCancelError.InvalidRequest) == AuctionCancelErrors.Generic,
            "背包放不下等必须让玩家看到提示 ⇒ 通用码 0xd2");
    }

    /// <summary>条目快照 ↔ 物品核心的往返：全字段核心必须逐字段（且逐字节）读回来。</summary>
    static void Snapshot()
    {
        // 全字段核心：每个位置都填非默认值，任何一个字段读错都会在相等断言里暴露。
        var rich = new ItemCore(101010912, ItemKind.Equipment, 999_999_998)
        {
            Attr = 0x1D,
            Durability = 45,
            SealFlag = 1,
            EnchantCardId = 4001,
            EnchantUpgrade = 3,
            AmplifyType = 2,
            AmplifyValue = 77,
            Marker16 = 16,
            Extension26 = 0x26,
            Extension27 = 0x27272727,
            Extension31 = 0x31313131,
            Extension35 = 0x35353535,
            Chronicle0 = new(11, 1, 2, 3, 4),
            Chronicle1 = new(12, 5, 6, 7, 8),
            ExpireTime = 1_700_000_000,
            EmblemSocketCount = 2,
            Emblem1 = 21,
            Emblem2 = 22,
            Rune = 23,
            Random0 = new(1, 2, 3),
            Random1 = new(4, 5, 6),
            Random2 = new(7, 8, 9),
            RandomState = 10,
            RandomChangedIndex = 11,
            RandomChangeState = 12,
            RandomChange = new(13, 14, 15),
            GenuineUpgrade = 16,
            EmancipateLevel = 17,
            TradeRestriction = 18,
            Extension90 = 19,
            Extension92 = 20,
            Extension93 = 21,
            RemainUseCount = 22,
            SortLockFlag = 1,
            EquipmentLockId = 23,
            Extension97 = 0x97979797,
            GuardianGemKeys = 0x1122334455667788,
            Extension109 = 109,
            Extension110 = 0x11011011,
            Extension114 = new(114, 0x11411411),
            Extension119 = 119,
            Extension120 = 120,
            Extension121 = 121
        };
        var entry = AuctionPackets.ItemEntrySnapshot(rich);
        That(entry.Length == 123, "条目快照仍是物品模块的 123B 定长编码（不新增第二套物品编码）");
        var read = AuctionPackets.ItemCoreFromEntry(entry, ItemKind.Equipment);
        That(read == rich, "★ 全字段核心往返相等：itemId/数量/强化/耐久/封印/附魔/增幅/编年史/期限/徽章/随机/锁 全部读回");
        That(Convert.ToHexString(AuctionPackets.ItemEntrySnapshot(read)) == Convert.ToHexString(entry),
            "★ 读回来的核心再编码得到逐字节相同的快照（写入端与读取端没有偏移漂移）");

        // 堆叠类：数量在 Value 里；无编年史/无随机时两个计数字节为 0 也必须能读回。
        var stack = new ItemCore(10014751, ItemKind.Material, 5);
        That(AuctionPackets.ItemCoreFromEntry(AuctionPackets.ItemEntrySnapshot(stack), ItemKind.Material) == stack,
            "堆叠材料（数量 5、无编年史与随机）往返相等");
        var defaulted = InventoryRewardGrantService.Create(
            new ItemTemplate(10001, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "粗布衬衫"));
        That(AuctionPackets.ItemCoreFromEntry(AuctionPackets.ItemEntrySnapshot(defaulted), ItemKind.Equipment) == defaulted,
            "普通新建实例（新角色的默认装备形态）往返相等");

        // 只有 Chronicle0 非零（Chronicle1 为空）也要一致：Validate 只允许这一种单值形态。
        var single = rich with { Chronicle1 = default, Random1 = default, Random2 = default };
        That(AuctionPackets.ItemCoreFromEntry(AuctionPackets.ItemEntrySnapshot(single), ItemKind.Equipment) == single,
            "单编年史 / 单随机属性的实例往返相等（计数字节按实际非零个数校验）");

        // 坏快照必须抛异常（调用方据此拒绝取消，而不是拿半个核心去发放）。
        Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(new byte[122], ItemKind.Equipment), "长度不足 123B 的快照拒绝");
        Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(new byte[124], ItemKind.Equipment), "长度超过 123B 的快照拒绝");
        var tampered = (byte[])entry.Clone();
        tampered[2 + 4 + 4 + 1 + 2 + 1 + 4 + 1 + 1 + 2 + 4 + 1 + 4 + 4 + 4] = 9; // 编年史计数字节
        Reject<InvalidDataException>(() => AuctionPackets.ItemCoreFromEntry(tampered, ItemKind.Equipment),
            "计数字节与实际内容不符的快照拒绝（宁可拒绝发放，也不把半个核心还进背包）");
    }

    /// <summary>取消上架的完整业务：同事务的 state=2 + 道具回背包 + 审计；各类拒绝都不动挂牌状态。</summary>
    static void Commit(bool restricted = false)
    {
        using var f = new Fixture(restricted);
        var expectedCore = f.Character.Inventory.Find(SourceSlot)!.Core;
        // ① 先把一件装备挂上去（走真实上架路径：扣背包 + 写挂牌同一事务）。
        f.RegisterAndTrack();
        That(f.Listing("state") == 0L && f.Character.Inventory.Find(SourceSlot) is null, "上架成功后挂牌在售、背包源槽已空");

        // ② 取消：只读规划 → 会话级提交（这里直接调存储合同的写路径，与 Handler 同一条）。
        var listing = f.Service.FindListing(f.ListingId);
        That(listing is not null && listing.State == AuctionListingState.Active, "按挂牌 id 读到在售行");
        var decision = f.Service.PlanCancel(f.Character, listing, f.Request(), f.Now);
        That(decision.Options is not null && decision.Error == AuctionCancelError.None, "取消规划成功");
        var plan = decision.Options!;
        That(plan.ListingId == f.ListingId && plan.ItemTemplateId == SampleTemplate && plan.ReturnCount == 1,
            "计划带回挂牌 id、模板与要还回的数量");
        That(plan.Inventory.Reason == "auction_cancel" && plan.Inventory.ChangedSlots.Length == 1 &&
            plan.Inventory.ChangedSlots[0].List == InventoryListType.Main,
            "库存计划复用发放入口（reason=auction_cancel）且落点在主背包（具体槽位由发放服务的放置规则选）");
        var cancelled = f.Service.Cancel(f.Owner, f.Character, plan);
        That(cancelled.Character is not null && cancelled.Error == AuctionCancelError.None, "取消提交成功并返回提交后的角色快照");
        var after = cancelled.Character!;
        That(after.Revision == f.Character.Revision + 1, "取消使 revision 递增一次（与背包写入同一个提交）");

        // ③ 三条后果：挂牌 state=2、道具回到背包（属性原样）、审计留下 auction_cancel。
        That(f.Listing("state") == (long)AuctionListingState.Cancelled, "★ 独立读库确认挂牌已置为 state=2（已下架）");
        var returned = after.Inventory.Items.Single(item => item.Core.ItemId == SampleTemplate);
        That(returned.Core == expectedCore && f.Repository.LoadCharacter(f.Owner, after.Slot).Inventory.Items
            .Single(item => item.Core.ItemId == SampleTemplate).Core == expectedCore,
            "拍卖取消实际落库保持完整核心及实例限制 " + restricted);
        That(returned.Core.Attr == 2 && returned.Core.Durability == 45 && returned.Core.Count == 1,
            "★ 道具回到背包且强化/耐久等实例字段原样保留（不是重新创建的白板实例）");
        That(returned!.Uid != f.SourceUid, "还回来的是新 UID 的实例（上架时那条物品行已被删除，这是同一份核心的新行）");
        That(f.QueryLong("SELECT count(*) FROM inventory_audit_log WHERE action_name='auction_cancel'") == 1,
            "审计里留下 auction_cancel 记录（与挂牌状态同一事务）");
        // ④ 在售口径立即消失：搜索与「我的上架」都不再有它，拍卖历史里仍在。
        That(f.Service.Search(new(0, null, AuctionCategoryFilter.All), null, f.Now).TotalCount == 0, "★ 取消后该挂牌从在售搜索里消失");
        That(f.Service.SellerListings(f.Character.Id, f.Now, includeInactive: false).IsEmpty, "「我的上架」不再显示它");
        That(f.Service.SellerListings(f.Character.Id, f.Now, includeInactive: true).Length == 1, "「拍卖历史」仍能看到这条已下架的挂牌");
        // ⑤ 重复取消：拒绝且不再还一次道具（否则一件道具会被复制）。
        var again = f.Service.PlanCancel(after, f.Service.FindListing(f.ListingId), f.Request(), f.Now);
        That(again.Error == AuctionCancelError.AlreadyInactive && again.Options is null, "★ 已下架的挂牌再次取消被拒绝（不产生第二次发放）");
        That(f.InventoryCount() == after.Inventory.Items.Length, "独立读库确认道具没有被还两次");
    }

    /// <summary>拒绝路径：非本人 / 不存在 / 背包满 / 陈旧计划，全部**不动挂牌状态**。</summary>
    static void Rollback()
    {
        // ① 不是本人的挂牌（机器人）：只能读、不能取消。
        using var robot = new Fixture();
        robot.SeedRobotListing();
        var robotListing = robot.Service.FindListing(robot.ListingId);
        var notMine = robot.Service.PlanCancel(robot.Character, robotListing, robot.Request(robot.ListingId), robot.Now);
        That(notMine.Error == AuctionCancelError.NotOwner && notMine.Options is null, "机器人挂牌（seller 为 NULL）不可取消");
        That(robot.Listing("state") == 0L, "非本人挂牌被拒绝后仍是 state=0");

        // ② 挂牌不存在。
        using var missing = new Fixture();
        That(missing.Service.PlanCancel(missing.Character, missing.Service.FindListing(9999), missing.Request(9999), missing.Now).Error
            == AuctionCancelError.NotFound, "挂牌不存在时拒绝");
        That(missing.Service.PlanCancel(missing.Character, null, missing.Request(0), missing.Now).Error
            == AuctionCancelError.InvalidRequest, "挂牌 id 非正时按请求非法拒绝");

        // ③ 背包满：把源槽占满后取消 → 拒绝，且挂牌**保持 state=0**（道具还在挂牌上，玩家可以清背包再取消）。
        using var full = new Fixture();
        full.RegisterAndTrack();
        full.FillBag();
        var blocked = full.Service.PlanCancel(full.Character, full.Service.FindListing(full.ListingId), full.Request(), full.Now);
        That(blocked.Error == AuctionCancelError.InventoryRejected && blocked.Options is null && blocked.Detail is not null,
            "★ 背包放不下时拒绝取消（复用发放服务的判定），并带上原因进日志");
        That(AuctionRegisterErrors.Whitelist.Contains(AuctionCancelErrors.ToClientCode(blocked.Error)),
            "背包满的拒绝码在白名单内（玩家能看到提示）");
        That(full.Listing("state") == 0L, "★ 背包满被拒绝后挂牌仍是 state=0：道具没有丢，清出空间还能再取消");

        // ④ 陈旧计划：规划之后角色被别的提交改动过，提交必须整体拒绝。
        using var stale = new Fixture();
        stale.RegisterAndTrack();
        var staleListing = stale.Service.FindListing(stale.ListingId);
        var staleDecision = stale.Service.PlanCancel(stale.Character, staleListing, stale.Request(), stale.Now).Options!;
        stale.Character = stale.Repository.CommitInventory(stale.Owner, stale.Character,
            new(stale.Character.Inventory, stale.Character.Inventory with { Gold = 7 }, "other_mutation"));
        Reject<InvalidOperationException>(() => stale.Service.Cancel(stale.Owner, stale.Character, staleDecision),
            "陈旧计划在写事务内被 revision/快照复核拒绝");
        That(stale.Listing("state") == 0L && stale.InventoryCount() == stale.Character.Inventory.Items.Length,
            "★ 陈旧计划既没有改挂牌状态，也没有多发一件道具");
    }

    /// <summary>上架时用的源槽（夹具把样本装备放在主背包 27）。</summary>
    static readonly InventorySlot SourceSlot = new(InventoryListType.Main, 27);

    /// <summary>真实文件库 + 一个真实角色 + 一次真实上架所需的最小夹具（不需要 PVF）。</summary>
    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-cancel-" + Guid.NewGuid().ToString("N"));
        readonly AccountOwners owners = new();

        public AccountOwners.Lease Owner { get; }
        public CharacterRepository Repository { get; }
        public AuctionService Service { get; }
        public Character Character { get; set; }
        public long Now { get; } = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        public int ListingId { get; private set; }
        public long SourceUid { get; private set; }

        public Fixture(bool restricted = false)
        {
            Directory.CreateDirectory(root);
            string path = Path.Combine(root, "game.db");
            Owner = owners.Acquire("auction-cancel", () => { });
            Repository = new(path);
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            Service = new AuctionService(Repository, new AuctionCategoryResolver(catalog), catalog,
                new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]"),
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            // 源槽 27 放样本装备（Attr/耐久非默认值，用来验证「还回来的是同一个实例」）；其余格子留空给「背包满」用例。
            var inventory = new InventorySnapshot([
                new(0, SourceSlot, new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45,
                    DisjointForbidden = restricted, DestructionSoulForbidden = restricted })
            ]);
            Character = Repository.Create(Owner, new("Canceller", new(38, 1, 450, 234, 0), null) { Level = 95, Inventory = inventory with { Gold = 1_000_000 } });
            SourceUid = Character.Inventory.Find(SourceSlot)!.Uid;
        }

        /// <summary>样本上架请求（10400 × 1、一口价 17000、24 小时）。</summary>
        public static byte[] SourceBody()
        {
            var body = new byte[AuctionRegisterRequest.BodySize];
            body[2] = 27;
            BitConverter.GetBytes(SampleTemplate).CopyTo(body, 4);
            BitConverter.GetBytes(1).CopyTo(body, 8);
            BitConverter.GetBytes(-1).CopyTo(body, 12);
            BitConverter.GetBytes(17_000).CopyTo(body, 16);
            BitConverter.GetBytes(17_000).CopyTo(body, 20);
            BitConverter.GetBytes(24).CopyTo(body, 24);
            return body;
        }

        /// <summary>规划一次上架并记住挂牌 id（提交后由调用方安装快照）。</summary>
        public AuctionRegisterPlan PlanRegister()
        {
            var decision = Service.PlanRegister(Character, AuctionRegisterRequest.Parse(SourceBody()), Now);
            That(decision.Options is not null, "夹具的上架计划一定成功");
            return decision.Options!;
        }

        /// <summary>走真实上架路径挂上一件（扣背包 + 写挂牌同一事务），并记住新挂牌 id 与提交后的快照。</summary>
        public AuctionRegisterPlan RegisterAndTrack()
        {
            var plan = PlanRegister();
            var registered = Repository.RegisterListing(Owner, Character, plan);
            That(registered.Character is not null, "夹具的上架提交一定成功");
            Character = registered.Character!;
            ListingId = (int)QueryLong("SELECT max(listing_id) FROM auction_listings");
            return plan;
        }

        /// <summary>取消请求：只有挂牌 id 参与定位，到期时间字段按真实挂牌填（用于日志对照）。</summary>
        public AuctionCancelRequest Request(int? listingId = null)
        {
            var target = listingId ?? ListingId;
            var listing = Service.FindListing(target);
            var body = new byte[AuctionCancelRequest.BodySize];
            BitConverter.GetBytes(target).CopyTo(body, 1);
            if (listing is not null)
                BitConverter.GetBytes((int)listing.ExpiresAtUnix).CopyTo(body, 5);
            return AuctionCancelRequest.Parse(body);
        }

        /// <summary>插入一条机器人挂牌（seller 为 NULL），用于「不是本人」的用例。</summary>
        public void SeedRobotListing()
        {
            SeedRobot();
            ListingId = (int)QueryLong("SELECT max(listing_id) FROM auction_listings");
        }

        void SeedRobot()
        {
            var core = new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 };
            Repository.InsertListings([new(null, "拍卖机器人甲", SampleTemplate, (byte)ItemKind.Equipment, 1,
                AuctionPackets.ItemEntrySnapshot(core), 12_000, 17_000, Now, Now + 86_400)]);
        }

        /// <summary>把该物品类别可用的主背包槽**全部**占满，让发放入口判定「放不下」。</summary>
        public void FillBag()
        {
            var range = ItemSlotBoundService.Range(ItemKind.Equipment, Character.Inventory.Expansion);
            var items = Character.Inventory.Items.ToBuilder();
            for (short slot = range.Start; slot <= range.End; slot++)
            {
                var position = new InventorySlot(InventoryListType.Main, slot);
                if (Character.Inventory.Find(position) is not null)
                    continue;
                items.Add(new(0, position, new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 }));
            }
            Character = Repository.CommitInventory(Owner, Character,
                new(Character.Inventory, Character.Inventory with { Items = items.ToImmutable() }, "fill_bag"));
        }

        public long Listing(string column) =>
            QueryLong($"SELECT {column} FROM auction_listings WHERE listing_id={ListingId}");

        public long QueryLong(string sql)
        {
            using var db = Open();
            using var command = db.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(command.ExecuteScalar());
        }

        public long InventoryCount() => QueryLong("SELECT count(*) FROM character_inventory_items");

        SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(root, "game.db"),
                Pooling = false,
                ForeignKeys = true
            }.ToString());
            db.Open();
            return db;
        }

        /// <summary>样本装备：`[free]` 可自由交易，商店价 5000（与取消无关，只是让模板完整）。</summary>
        static readonly Dictionary<int, ItemTemplate> Templates = new()
        {
            [SampleTemplate] = new(SampleTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心")
            {
                ShopPrice = new(5_000, 1_000, [], null)
            }
        };

        public void Dispose()
        {
            Owner.Dispose();
            Directory.Delete(root, true);
        }
    }
}
