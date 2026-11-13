using static DfoServer.Testing.TestCheck;
using System.Collections.Immutable;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Towns;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;

namespace DfoServer;

/// <summary>
/// 上架（CMD 183）**提交之后的发布路径**：会话安装提交后的快照、产出提交回执、
/// 由背包投影发布整列刷新（`0x000D`）。
///
/// 这一组存在的直接原因是线上事故：上架路径不走常规背包写用例，Handler 自己拼了一个
/// `WholeListRefresh = true` 但**没有回执**的库存结果，投影在
/// <c>InventoryProjection.RefreshAsync</c> 的第一行就抛「背包刷新缺少提交回执」，
/// 服务端主动断开连接（用户界面显示「网络连接中断」，并以为道具被吞）。
///
/// 因此这里跑的是**真实链路**：真实文件库 + 真实角色会话 + 真实投影 + 真实发送队列，断言：
///   ① 提交后的快照确实安装回会话（否则下一笔背包操作会以陈旧 revision 被写事务拒绝）；
///   ② 整列刷新请求带真实提交回执，投影发布**不抛异常**且动作是整列重建；
///   ③ 客户端将看到的快照与服务端一致（源槽已空），并且**可以连续上架第二件**、背包仍可继续操作；
///   ④ 陈旧计划（提交前捕获的旧快照）被拒绝，且不产生第二次扣减。
/// 不需要 PVF：模板显式预置，数据库是真实文件库。
/// </summary>
internal static class AuctionCommitTests
{
    /// <summary>实机样本（10400 残缺的牛皮背心、数量 1、起拍价空、一口价 17000、24 小时）。</summary>
    const string Sample =
        "00001B00A028000001000000FFFFFFFF684200006842000018000000000000000000000000";
    const int SampleTemplate = 10400;
    const int StackTemplate = 10014751;
    static readonly Location Room = new(38, 1, 450, 234, 0);

    public static void Run()
    {
        int checks = Count;
        // 同步桥：拍卖行组由 SelfTestRunner（公共入口，由负责人维护）以同步方式调用，
        // 而本组要跑真实的异步发布链路（投影 → 发送队列）。控制台没有 SynchronizationContext，
        // 发送泵在线程池上，因此这里等待不会自锁。
        Publication().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {Count - checks} auction commit/publication assertions");
    }

    static async Task Publication()
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-auction-commit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "game.db");
            var owners = new AccountOwners();
            using var owner = owners.Acquire("auctioneer", () => { });
            var repository = new CharacterRepository(path);
            // 空目录的城镇目录足够 Select 使用（返回点取 InitialReturn，不触发传送）；
            // 本组不进入副本、不查询场景，因此不需要 PVF。
            var towns = new TownCatalog(new Dictionary<(byte, byte), TownArea>(), Room, Room, new TownDock(0, [], []),
                [], new Dictionary<uint, uint[]>());
            var catalog = new ItemCatalog(null!, null!, null!, 0);
            catalog.PrefillTemplates(Templates);
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            var service = new AuctionService(repository, new AuctionCategoryResolver(catalog), catalog, pieces,
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var inventory = new InventorySnapshot([
                new(0, new(InventoryListType.Main, 27), new ItemCore(SampleTemplate, ItemKind.Equipment, 999_999_998) { Attr = 2, Durability = 45 }),
                new(0, new(InventoryListType.Main, 121), new ItemCore(StackTemplate, ItemKind.Material, 5) { EnchantUpgrade = 2 }),
                new(0, new(InventoryListType.Main, 122), new ItemCore(StackTemplate, ItemKind.Material, 1) { SortLockFlag = 1 }),
                new(0, new(InventoryListType.Talisman, 70), new ItemCore(70001, ItemKind.Rune, 1))
            ]);
            var created = repository.Create(owner, new("Auctioneer", Room, null) { Level = 95, Inventory = inventory with { Gold = 1_000_000 } });
            var session = new GameSession(repository, towns, owner);
            session.Select(created.Slot);
            session.MarkReady();
            var planned = session.Selected!;
            That(planned.Inventory.Items.Length == 4, "会话持有挂牌源、未变化的锁定材料和其他容器符文");

            // 真实投影 + 真实发送队列：事故就发生在 RefreshAsync 的第一行。
            var stats = new CharacterStatComputer(new CharacterCatalog(new Dictionary<byte, CharacterProfile>()));
            var projection = new InventoryProjection(stats, new TownProjection(towns, stats, null, pieces), pieces);
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            var sent = new List<string>();
            var frames = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (kind, opcode, _, body, action) =>
            {
                sent.Add(action);
                frames.Add((kind, opcode, body.ToArray(), action));
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);

            // ① 第一件：实机样本（整件装备）。
            var plan = service.PlanRegister(planned, AuctionRegisterRequest.Parse(Convert.FromHexString(Sample)), now).Options!;
            var commit = session.RegisterListing(service, planned, plan);
            That(commit.Listing.Character is not null && commit.Listing.Error == AuctionRegisterError.None, "上架提交成功并返回提交后的角色快照");
            That(ReferenceEquals(session.Selected, commit.Listing.Character),
                "★ 提交后的快照安装回会话：否则下一笔背包操作会以陈旧 revision 被写事务拒绝（连续上架第二件会直接断线）");
            That(commit.Listing.Character!.Inventory.Find(new(InventoryListType.Main, 27)) is null,
                "★ 在线快照里源槽已清空（客户端与服务端看到的是同一份扣减结果）");
            That(commit.Inventory is { WholeListRefresh: true, Receipt: not null },
                "★ 上架产出的整列刷新请求带提交回执（复用常规背包写用例的 InventoryCommitReceipt，不另造一套）");
            That(session.CanPublishInventory(commit.Inventory!.Receipt!),
                "★ 回执绑定刚安装的快照与当前 owner/generation/选角 epoch，投影因此允许发布");
            That(commit.Inventory.Receipt!.Character.Inventory.Find(new(InventoryListType.Main, 27)) is null,
                "回执里的快照就是扣减后的快照，投影据此生成清空源槽的条目");
            var stored = repository.List(owner).Single();
            That(stored.Inventory.Items.Length == inventory.Items.Length - 1 && stored.Revision == planned.Revision + 1,
                "独立读库确认源槽已扣且 revision 前进一次（与挂牌插入同一个提交）");
            That(repository.Search(new(null, null), now, 0, 10).TotalCount == 1, "挂牌已落库并且在售");

            bool threw = false;
            try
            {
                await projection.RefreshAsync(context, commit.Inventory);
            }
            catch (Exception error)
            {
                threw = true;
                Console.WriteLine("整列刷新抛异常：" + error);
            }
            That(!threw, "★ 上架成功后整列刷新**不抛异常**（事故的直接原因就是这里抛「背包刷新缺少提交回执」）");
            That(sent.Contains("inventory_whole_list_refresh"), "★ 发布动作是整列重建（不是只发变化槽的差量）");
            That(sent.Count(action => action == "inventory_whole_list_refresh") == 1, "整列刷新只发一条（不重复轰炸客户端）");
            var clear = frames.Single(frame => frame.Action == "inventory_listing_source_cleared");
            var reload = frames.Single(frame => frame.Action == "inventory_whole_list_refresh");
            That(clear.Kind == 0 && clear.Opcode == (ushort)NotiPacketType.UPDATE_ITEM_LIST &&
                clear.Body.Length == 3 + 123 && clear.Body[0] == (byte)InventoryListType.Main &&
                BitConverter.ToInt16(clear.Body, 3) == 27 && BitConverter.ToInt32(clear.Body, 5) == -1,
                "耗尽源槽仅发空条目，不把未变化的物品作为新获得差量重发");
            That(reload.Kind == 0 && reload.Opcode == (ushort)NotiPacketType.ITEM_LIST &&
                reload.Body[0] == (byte)InventoryListType.Main && frames.IndexOf(clear) < frames.IndexOf(reload),
                "先清源槽再用NOTI13静默重载主背包，保留解除上架窗口持有状态的整列刷新");
            That(frames.Where(frame => frame.Kind == 0 && frame.Opcode is (ushort)NotiPacketType.ITEM_LIST or
                    (ushort)NotiPacketType.UPDATE_ITEM_LIST).All(frame => frame.Body[0] == (byte)InventoryListType.Main),
                "其他容器的既有符文不被刷新或标记成新获得");
            That(sent.Contains("restore_sort_lock"), "静默整列重建后恢复未变化材料的排序锁");

            // ② 第二件：同一次会话里再上架一堆材料，只扣本次数量。
            var secondPlan = service.PlanRegister(session.Selected!, AuctionRegisterRequest.Parse(StackBody(121, StackTemplate, 2)), now).Options!;
            var second = session.RegisterListing(service, session.Selected!, secondPlan);
            That(second.Listing.Character is not null, "★ 可连续上架第二件（在线快照已随第一次提交前进）");
            That(second.Listing.Character!.Inventory.Find(new(InventoryListType.Main, 121)) is { Core.Value: 3 },
                "拆堆上架后余量 3 留在原槽（只扣本次数量）");
            sent.Clear();
            frames.Clear();
            threw = false;
            try
            {
                await projection.RefreshAsync(context, second.Inventory!);
            }
            catch (Exception error)
            {
                threw = true;
                Console.WriteLine("第二次整列刷新抛异常：" + error);
            }
            That(!threw && sent.Contains("inventory_whole_list_refresh"), "★ 第二次上架的整列刷新同样发布成功");
            That(repository.List(owner).Single().Inventory.Items.Length == 3, "两次上架后保留材料余量、锁定材料与其他容器符文");
            That(!sent.Contains("inventory_listing_source_cleared") && frames.Where(frame => frame.Action == "inventory_whole_list_refresh")
                .All(frame => frame.Opcode == (ushort)NotiPacketType.ITEM_LIST), "拆堆余量由静默全量更新，不制造新获得差量");
            var upgradedListing = service.SellerListings(session.Selected!.Id, now, includeInactive: false)
                .Single(row => row.ItemTemplateId == StackTemplate);
            That(AuctionPackets.ItemCoreFromEntry(upgradedListing.ItemEntry, ItemKind.Material).EnchantUpgrade == 2 &&
                session.Selected.Inventory.Find(new(InventoryListType.Main, 121))!.Core.EnchantUpgrade == 2,
                "拆堆挂牌副本与原槽余量均保留升级等级");

            // ③ 上架之后背包仍可继续操作：常规背包写用例必须能接受当前会话快照。
            //    金币基准取**提交后的真实值**（两次上架各扣了 10,000 保证金）：这里断言的是「常规写入
            //    能落库」而不是某个固定数字，写死数字会被保证金口径变化误伤。
            uint goldBefore = repository.List(owner).Single().Inventory.Gold;
            var mutation = session.MutateInventory(current => new(true, 0,
                new(current.Inventory, current.Inventory with { Gold = current.Inventory.Gold + 7 }, "commit_test_gold")));
            That(mutation.Accepted && mutation.Receipt is not null, "★ 上架之后常规背包操作仍可提交（快照没有停在提交前）");
            That(repository.List(owner).Single().Inventory.Gold == goldBefore + 7u, "常规背包操作的提交结果与独立读库一致");

            // ④ 陈旧计划：用提交前捕获的旧快照再提交一次，必须被拒绝且不产生第二次扣减。
            var staleCount = repository.List(owner).Single().Inventory.Items.Length;
            var stale = session.RegisterListing(service, planned, secondPlan);
            That(stale.Listing.Error == AuctionRegisterError.StalePlan && stale.Listing.Character is null && stale.Inventory is null,
                "★ 陈旧计划（提交前的旧快照）被拒绝：不安装快照、也不产出可发布的刷新请求");
            That(repository.List(owner).Single().Inventory.Items.Length == staleCount && repository.Search(new(null, null), now, 0, 10).TotalCount == 2,
                "陈旧计划既没有第二次扣背包，也没有写入第二条挂牌");

            // ⑤ 取消上架（CMD 184）走**同一条会话级发布路径**：提交后刷新同样不能成为失败点。
            //    取第一条挂牌（实机样本那件装备）取消，断言「道具回背包 + 挂牌下架 + 刷新不抛异常」。
            var listing = service.SellerListings(session.Selected!.Id, now, includeInactive: false)
                .Single(row => row.ItemTemplateId == SampleTemplate);
            var cancelPlan = service.PlanCancel(session.Selected!, listing,
                AuctionCancelRequest.Parse(CancelBody(listing.ListingId, listing.ExpiresAtUnix)), now).Options!;
            var cancelled = session.CancelListing(service, session.Selected!, cancelPlan);
            That(cancelled.Listing.Character is not null &&
                ReferenceEquals(session.Selected, cancelled.Listing.Character),
                "★ 取消提交成功且提交后的快照安装回会话");
            That(cancelled.Inventory is { Receipt: not null, WholeListRefresh: false },
                "★ 取消的刷新请求带提交回执，且只发常规差量（道具回背包，不需要整列重建）");
            That(session.CanPublishInventory(cancelled.Inventory!.Receipt!), "取消了同样能发布：回执绑定刚安装的快照");
            sent.Clear();
            threw = false;
            try
            {
                await projection.RefreshAsync(context, cancelled.Inventory);
            }
            catch (Exception error)
            {
                threw = true;
                Console.WriteLine("取消后的背包刷新抛异常：" + error);
            }
            That(!threw && sent.Contains("inventory_committed_delta"),
                "★ 取消上架后的背包刷新不抛异常，且以常规差量发布（「提交后的刷新不是失败点」）");
            That(repository.List(owner).Single().Inventory.Items.Any(item => item.Core.ItemId == SampleTemplate),
                "道具回到了背包（独立读库确认）");
            var reconnected = new GameSession(new CharacterRepository(path), towns, owner);
            reconnected.Select(created.Slot);
            reconnected.MarkReady();
            That(reconnected.Selected!.Inventory.Find(new(InventoryListType.Main, 121)) is { Core.Value: 3, Core.EnchantUpgrade: 2 } &&
                reconnected.Selected.Inventory.Find(new(InventoryListType.Main, 122)) is { Core.SortLockFlag: 1 } &&
                reconnected.Selected.Inventory.Find(new(InventoryListType.Talisman, 70)) is { Core.ItemId: 70001 },
                "重建会话回读持久化拆堆余量、升级、锁及未变化容器；不冒充独立进程恢复");
        }
        finally
        {
            TestFiles.DeleteDirectory(root, recursive: true);
        }
    }

    /// <summary>把实机样本改成「指定槽位 + 指定模板 + 指定数量」的上架请求（其余字段保持样本取值）。</summary>
    static byte[] StackBody(short slot, int templateId, int count)
    {
        var body = Convert.FromHexString(Sample);
        BitConverter.GetBytes(templateId).CopyTo(body, 4);
        BitConverter.GetBytes(count).CopyTo(body, 8);
        body[2] = (byte)slot;
        body[3] = (byte)(slot >> 8);
        return body;
    }

    /// <summary>取消上架请求：前导 0 + 挂牌 id(i32@1) + 到期(i32@5)。</summary>
    static byte[] CancelBody(int listingId, long expiresAtUnix)
    {
        var body = new byte[AuctionCancelRequest.BodySize];
        BitConverter.GetBytes(listingId).CopyTo(body, 1);
        BitConverter.GetBytes((int)expiresAtUnix).CopyTo(body, 5);
        return body;
    }

    /// <summary>预置模板：样本装备与一堆可上架材料（字段取值即断言的依据，与 PVF 无关）。</summary>
    static readonly Dictionary<int, ItemTemplate> Templates = new()
    {
        [SampleTemplate] = new(SampleTemplate, ItemKind.Equipment, 15, 1, 45, 1, 1, 0, 1, "[free]", 65535, 0, "残缺的牛皮背心"),
        [StackTemplate] = new(StackTemplate, ItemKind.Material, 0, 1, 0, 999, 1, 0, 1, "[free]", 65535, 0, "粗布腰带随机礼盒")
    };
}
