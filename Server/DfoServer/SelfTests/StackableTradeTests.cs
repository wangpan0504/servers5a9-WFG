using DfoServer.Game.Accounts;
using DfoServer.Game.Auction;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Trade;
using DfoServer.GameWorld.Items;
using DfoServer.Network.Builders;
using DfoServer.Persistence;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class StackableTradeTests
{
    static readonly ItemTemplate Card = new(490007851, ItemKind.Profession, 0, 1, 0, 999, 1, 0, 1, "", 65535, 0, "测试卡片")
        { AccountBoundAfterTrade = true, AccountBindingTradeLimit = 1 };

    internal static void RunCore()
    {
        int before = Count;
        Rules();
        Mail();
        Trade();
        Auction();
        Console.WriteLine($"PASS {Count - before} stackable trade limit assertions");
    }

    static void Rules()
    {
        var core = InventoryRewardGrantService.Create(Card, 5) with { Extension121 = 0x7123, Attr = 3 };
        var item = new InventoryItem(1, new(InventoryListType.Main, 121), core);
        That(core.Extension120 == 0 && MailboxService.TransferError(item, Card, "a", "b", 1) == MailboxError.None &&
            AuctionService.TransferBlocked(core, Card) is null, "原奖励创建的零状态卡片可首次邮寄、交易及上架");
        var bound = StackableTradePolicy.AfterTransfer(core, Card);
        That(!InventoryStackRuleService.CanShareStack(core, bound), "已绑定和未交易卡片不能合堆洗掉状态");
        That(bound == core with { Extension120 = 0x51 } && AuctionPackets.ItemEntrySnapshot(bound)[120] == 0x51 &&
            AuctionPackets.ItemCoreFromEntry(AuctionPackets.ItemEntrySnapshot(bound), Card.Kind) == bound,
            "一次交易后仅offset120推进到0x51，完整核心与123B记录往返保持");
        That(MailboxService.TransferError(item with { Core = bound }, Card, "a", "b", 1) == MailboxError.AccountBound &&
            MailboxService.TransferError(item with { Core = bound }, Card, "a", "a", 1) == MailboxError.None &&
            AuctionService.TransferBlocked(bound, Card) is not null && StackableTradePolicy.AfterTransfer(bound, Card) == bound,
            "绑定后跨账号拒绝、同账号保留状态且不再累加溢出");
        That(InventoryCargoService.AllowsInsert(bound, InventoryListType.AccountCargo, Card),
            "交易后的卡片仍可存账号仓库");
        foreach (var denied in new[] { Card with { AttachType = "[account]" }, Card with { AttachType = "[untradable]" },
            Card with { AccountBindingTradeLimit = 0 }, Card with { AccountBindingTradeLimit = 16 } })
            That(MailboxService.TransferError(item, denied, "a", "b", 1) != MailboxError.None,
                "显式绑定或未证次数声明不能被新规则放行");
        That(!StackableTradePolicy.CanTransfer(core with { Extension120 = 0x10 }, Card, true), "未知实例绑定类型不放行");
        var multi = Card with { AccountBindingTradeLimit = 3 };
        var once = StackableTradePolicy.AfterTransfer(core, multi);
        var twice = StackableTradePolicy.AfterTransfer(once, multi);
        That(once.Extension120 == 1 && twice.Extension120 == 2 &&
            StackableTradePolicy.AfterTransfer(twice, multi).Extension120 == 0x53, "多次交易按已用次数推进至模板上限");
    }

    static void Mail()
    {
        using var f = new Fixture();
        var sender = f.First;
        var source = sender.Inventory.Items.Single();
        var request = new MailboxSendRequest(f.Second.Name, 0, [new(0, source.Position.Index, Card.Id, 2)], "测试");
        That(f.Mail.PlanSend(sender, f.FirstOwner.Account, request, f.Now, out var plan) == MailboxError.None &&
            plan!.Attachments.Single().Core == source.Core with { Value = 2, Extension120 = 0x51 } &&
            plan.Inventory.After.Items.Single().Core == source.Core with { Value = 3 }, "邮寄只推进转出部分，余堆保持未交易");
        f.Sql("CREATE TRIGGER fail_card_mail BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'card mail'); END");
        Reject<SqliteException>(() => f.Mail.Send(f.FirstOwner, sender, plan!), "邮件附件晚SQL失败整体回滚");
        That(f.ReloadFirst().Inventory.Items.Single() == source && f.ReloadFirst().Inventory.Gold == sender.Inventory.Gold,
            "失败不吞次数、数量、UID或邮费");
        f.Sql("DROP TRIGGER fail_card_mail");
        var sent = f.Mail.Send(f.FirstOwner, sender, plan!);
        That(sent.Accepted && sent.Character!.Inventory.Items.Single().Core.Extension120 == 0, "真实寄送提交源余堆未绑定");
        var message = f.Mail.Open(f.SecondOwner, f.Second.Id, f.Now).Entries.Single();
        That(message.Attachments.Single().Item.Core.Extension120 == 0x51, "托管附件持久保存已交易状态");
        That(f.Mail.PlanClaim(f.SecondOwner, f.Second, [message.Id], f.Now, out var claim) == MailboxError.None,
            "实际托管卡片可领取");
        var claimed = f.Mail.Claim(f.SecondOwner, f.Second, claim!);
        That(claimed.Accepted && f.Repository.LoadCharacter(f.SecondOwner, f.Second.Slot).Inventory.Items.Single().Core.Extension120 == 0x51,
            "领取重读仍绑定，不恢复首次交易资格");
    }

    static void Trade()
    {
        using var f = new Fixture();
        var source = f.First.Inventory.Items.Single();
        var ticket = new TradeTicket(new(f.First.Id, f.FirstOwner.Generation, 1), new(f.Second.Id, f.SecondOwner.Generation, 1),
            new(0, [new(0, source, 2)]), new(0, []));
        var plan = TradeSettlement.Plan(f.First, f.Second, ticket, f.Items)!;
        That(plan.First.After.Items.Single().Core.Extension120 == 0 && plan.Second.After.Items.Single().Core.Extension120 == 0x51,
            "直接交易拆堆使用同一原生状态推进");
        var forged = plan with { Second = new InventoryPlan(plan.Second.Before, plan.Second.After with
            { Items = [plan.Second.After.Items.Single() with { Core = source.Core with { Value = 2 } }] }, "player_trade") };
        Reject<InvalidDataException>(() => f.Repository.CommitPlayerTrade(f.FirstOwner, f.SecondOwner, f.First, f.Second, ticket, forged, f.Items),
            "仓储复核拒绝去掉绑定状态的伪造成交");
        f.Sql($"CREATE TRIGGER fail_card_trade BEFORE UPDATE ON characters WHEN NEW.id={f.Second.Id} BEGIN SELECT RAISE(ABORT,'card trade'); END");
        Reject<SqliteException>(() => f.Repository.CommitPlayerTrade(f.FirstOwner, f.SecondOwner, f.First, f.Second, ticket, plan, f.Items),
            "第二方写入失败不消耗源次数或资产");
        That(f.ReloadFirst().Inventory.Items.Single() == source && f.Repository.LoadCharacter(f.SecondOwner, f.Second.Slot).Inventory.Items.IsEmpty,
            "直接交易失败后两侧精确恢复");
        f.Sql("DROP TRIGGER fail_card_trade");
        var (first, second) = f.Repository.CommitPlayerTrade(f.FirstOwner, f.SecondOwner, f.First, f.Second, ticket, plan, f.Items);
        var received = second.Inventory.Items.Single();
        That(first.Inventory.Items.Single().Core == source.Core with { Value = 3 } && received.Core == source.Core with { Value = 2, Extension120 = 0x51 },
            "直接交易持久化数量及完整状态");
        var back = new TradeTicket(new(first.Id, f.FirstOwner.Generation, 1), new(second.Id, f.SecondOwner.Generation, 1),
            new(0, []), new(0, [new(0, received, 1)]));
        That(TradeSettlement.Plan(first, second, back, f.Items) is null, "绑定后的卡片不能再向另一账号交易");
        Reject<InvalidOperationException>(() => f.Repository.CommitPlayerTrade(f.FirstOwner, f.SecondOwner, f.First, f.Second, ticket, plan, f.Items),
            "已成交旧报价重放不重复推进或复制物品");
    }

    static void Auction()
    {
        using var f = new Fixture();
        long now = f.Now.ToUnixTimeSeconds();
        var core = f.First.Inventory.Items.Single().Core with { Value = 2 };
        f.Repository.InsertListings([new(f.First.Id, f.First.Name, Card.Id, (byte)Card.Kind, 2,
            AuctionPackets.ItemEntrySnapshot(core), 90, 100, now, now + 86400)]);
        var listing = f.Repository.LoadSellerListings(f.First.Id, now, false).Single();
        var registration = f.Auction.PlanRegister(f.First, new(0, 0, 233, Card.Id, 2, 90, 100, 100, 24), now);
        That(registration.Options is { } registered && registered.ItemEntry[120] == 0 &&
            registered.Inventory.After.Items.Single().Core.Extension120 == 0, "上架本身不消耗源或挂牌的交易次数");
        var cancel = f.Auction.PlanCancel(f.First, listing, new(0, listing.ListingId, listing.ExpiresAtUnix), now);
        That(cancel.Options is { } cancelled && cancelled.Inventory.After.Items.All(item => item.Core.Extension120 == 0),
            "撤单返还保持完整未交易状态");
        var expiry = f.Auction.PlanExpire(f.First, now + 86401);
        That(expiry.Rows.Single().Mail.Items.Single().Core == core, "到期退回邮件不推进交易状态");
        var request = new AuctionBuyRequest(0, 200, listing.ListingId, (uint)listing.ExpiresAtUnix, Card.Id);
        var plan = f.Auction.PlanBuy(f.Second, listing, request, now).Options!;
        That(plan.Custody.First().Items.Single().Core == core with { Extension120 = 0x51 } &&
            f.Repository.LoadListing(listing.ListingId)!.ItemEntry[120] == 0, "仅成交规划推进买方核心，未售挂牌保持零状态");
        f.Sql("CREATE TRIGGER fail_card_auction BEFORE INSERT ON mailbox_attachments BEGIN SELECT RAISE(ABORT,'card auction'); END");
        Reject<SqliteException>(() => f.Auction.Buy(f.SecondOwner, f.Second, plan), "成交托管失败整体回滚");
        That(f.Repository.LoadListing(listing.ListingId) is { State: AuctionListingState.Active } original && original.ItemEntry[120] == 0 &&
            f.Repository.LoadCharacter(f.SecondOwner, f.Second.Slot).Inventory.Gold == f.Second.Inventory.Gold,
            "成交失败保留挂牌、交易状态与买方余额");
        f.Sql("DROP TRIGGER fail_card_auction");
        var bought = f.Auction.Buy(f.SecondOwner, f.Second, plan);
        var message = f.Mail.Open(f.SecondOwner, f.Second.Id, f.Now).Entries.Single();
        That(bought.Error == AuctionBuyError.None && message.Attachments.Single().Item.Core == core with { Extension120 = 0x51 },
            "实际成交托管保存账号绑定状态");
        That(f.Auction.Buy(f.SecondOwner, bought.Character!, plan).Error == AuctionBuyError.RaceLost,
            "重复成交不重复扣款或增加次数");
    }

    sealed class Fixture : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "S5A9-stackable-trade-" + Guid.NewGuid().ToString("N"));
        readonly string path;
        readonly AccountOwners owners = new();
        public AccountOwners.Lease FirstOwner { get; }
        public AccountOwners.Lease SecondOwner { get; }
        public CharacterRepository Repository { get; }
        public ItemCatalog Items { get; } = new(null!, null!, null!, 0);
        public MailboxService Mail { get; }
        public AuctionService Auction { get; }
        public Character First { get; }
        public Character Second { get; }
        public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

        public Fixture()
        {
            Directory.CreateDirectory(root);
            path = Path.Combine(root, "game.db");
            Repository = new(path);
            FirstOwner = owners.Acquire("card-a", () => { });
            SecondOwner = owners.Acquire("card-b", () => { });
            Items.PrefillTemplates(new Dictionary<int, ItemTemplate> { [Card.Id] = Card });
            var pieces = new EpicPieceCatalog("[piece list]\n60000 70001\n[/piece list]");
            Mail = new(Repository, Items, new GoldLimitCatalog("[gold limit from level]\n0 2000000000\n[/gold limit from level]"), pieces);
            Auction = new(Repository, new AuctionCategoryResolver(Items), Items, pieces,
                AuctionPackets.ItemEntrySnapshot, AuctionPackets.ItemCoreFromEntry);
            First = Repository.Create(FirstOwner, new("CardA", new(38, 1, 450, 234, 0), null) { Level = 95,
                Inventory = new([new(0, new(InventoryListType.Main, 233), InventoryRewardGrantService.Create(Card, 5) with { Extension121 = 0x7123 })], Gold: 100000) });
            Second = Repository.Create(SecondOwner, new("CardB", new(38, 1, 450, 234, 0), null) { Level = 95, Inventory = new([], Gold: 100000) });
        }

        public Character ReloadFirst() => Repository.LoadCharacter(FirstOwner, First.Slot);
        public void Sql(string text)
        {
            using var connection = new SqliteGameDatabase(path).Open();
            using var command = connection.CreateCommand();
            command.CommandText = text;
            command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            FirstOwner.Dispose();
            SecondOwner.Dispose();
            SqliteConnection.ClearAllPools();
            TestFiles.DeleteDirectory(root, true);
        }
    }
}
