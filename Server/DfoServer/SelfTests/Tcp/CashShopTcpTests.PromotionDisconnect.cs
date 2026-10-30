using System.Net;
using System.Text.Json;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class CashShopTcpTests
{
    static async Task PromotionDisconnectAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        var items = resources.ItemResources;
        var rules = Inventory.CashShopPromotionSessionTests.Rules with { Products = [new(100004, 1, 1)] };
        var catalog = CashShopPromotionCatalog.Parse(JsonSerializer.Serialize(new[] { new PromotionConfiguration(rules, true) }),
            items.CashShop, items.Items.Get, _ => null);
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "promotion-before-ack"), resources: resources,
            promotions: () => catalog);
        var repository = new CharacterRepository(fixture.Database, resources.Quests, promotions: () => catalog);
        var owners = new AccountOwners();
        byte[] cart = Cart([100004, 100004]);
        uint giftId;
        using (var owner = owners.Acquire("10000", () => { }))
        {
            var character = repository.Create(owner, CharacterCreation.Prepare("PromoLostAck", resources.Characters.Get(0),
                resources.Towns.Room, resources.Towns.InitialReturn, resources.Learning.Get));
            repository.CommitInventory(owner, character, new(character.Inventory,
                CurrencyWallet.Set(character.Inventory, CurrencyKind.Cera, 2000), "seed"));
            var session = new GameSession(repository, resources.Towns, owner, promotionStore: repository);
            session.Select(0); session.MarkReady();
            var computer = new CharacterStatComputer(resources.Characters);
            var projection = new InventoryProjection(computer, new TownProjection(resources.Towns, computer, null, items.EpicPieces), items.EpicPieces);
            using var stop = new CancellationTokenSource();
            using var stream = new BeforeAckOutput();
            int sent = 0;
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, _) => sent++);
            var context = new GameConnectionContext(new(901, IPAddress.Loopback, stream, stop), owner, session, sender);
            using var log = new ServerLog(Path.Combine(fixture.Root, "fault-log"));
            var registry = new GameCommandRegistry();
            new ShopHandler(new(items.Items, items.Shops, items.GoldLimits, items.EpicPieces),
                new(items.Items, items.CashShop, items.GoldLimits, items.EpicPieces, items.Cargo), projection, log,
                promotions: new(() => catalog, items.Items.Get)).Register(registry);
            registry.Seal();
            Task purchase = registry.DispatchAsync(context, new(1, 64, cart));
            await stream.Started.WaitAsync(TimeSpan.FromSeconds(10));
            That(sent == 0 && stream.Length == 0 && fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[1L]],
                "真实Handler/Sender首ACK写出前，主货扣款及赠礼事务已提交，尚无任何成功字节");
            giftId = checked((uint)(long)fixture.Query("SELECT mail_id FROM mailbox_messages")[0][0]!);
            stop.Cancel();
            bool disconnected = false;
            try { await purchase.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (OperationCanceledException) { disconnected = true; }
            catch (IOException) { disconnected = true; }
            That(disconnected && sent == 0 && stream.Length == 0, "首ACK前确定性断流，不把排队或提交称为已发送");
        }
        await fixture.StartAsync();
        using (var peer = Open(fixture))
        {
            peer.Select();
            That(MailboxTcpTests.ReadList(peer.Mailbox.Single()).Summaries is [var gift] && gift.MailId == giftId &&
                fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[2L]],
                "ACK前断流后真实TCP登录恢复同一赠礼和累计，不补寄第二次");
            peer.Send(64, cart);
            peer.Expect(1, 64); peer.Expect(1, 64); peer.Expect(0, 14); peer.Expect(0, 53); peer.Expect(0, 99, [1, 0]); peer.Barrier();
            That(fixture.Query("SELECT count FROM cash_shop_promotion_progress") is [[4L]] &&
                fixture.Query("SELECT count(*) FROM cash_shop_promotion_orders") is [[2L]] &&
                fixture.Query("SELECT count(*) FROM mailbox_messages") is [[2L]],
                "丢ACK后的相同包体新请求购买第二次，不按包hash吞单");
        }
        await fixture.StopAsync();
    }

    sealed class BeforeAckOutput : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => started.Task;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
