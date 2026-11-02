using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassOrdinaryCashPublicationRace
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-cash-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string change in new[] { "recharge", "unchanged", "reselect", "takeover", "recharge-cart" })
                await Scenario(change);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        async Task Scenario(string change)
        {
            const string account = "pass-cash-race", season = "ordinary-cash-publication-race";
            var items = resources.ItemResources;
            var product = items.CashShop.Product(105681) ?? throw new InvalidDataException("缺少真实商品105681");
            That(product is { ItemId: 490705385, Count: 1, CeraPrice: > 1 and < 100000, Section: "package" } &&
                !items.CashShop.IsCouponProduct(product.CommodityId), "使用匹配资源真实非证书商城商品105681");
            var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
            var policy = new BattlePassMissionPolicy(season, start, BattlePassCatalog.Load(resources.Archive));
            var now = start.AddHours(1);
            var clock = new Clock(now);
            string path = Path.Combine(root, change + ".db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            repository.Create(owner, new NewCharacter("CashOne", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            repository.Create(owner, new NewCharacter("CashTwo", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            That(DevelopmentGm.Answer("recharge cera 200000", account, repository, owners, _ => session)
                .StartsWith("OK ", StringComparison.Ordinal), "隔离账号使用实际GM充值准备点券");
            var initial = session.Selected!;
            int quantity = change == "recharge-cart" ? 2 : 1;
            long epoch = session.SelectionEpoch;
            var stats = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(stats, new TownProjection(resources.Towns, stats, null, items.EpicPieces), items.EpicPieces);
            var cash = new InventoryCashShopService(items.Items, items.CashShop, items.GoldLimits, items.EpicPieces, items.Cargo,
                contracts: items.Contracts, avatars: items.AvatarPackages, clock: clock);
            // 105681是已证历史商品；只在此隔离测试使用现有商城绝对期限调试开关。
            cash.IgnoreAbsoluteExpiration = true;
            var pass = new BattlePassProjection(season, policy.Catalog, clock);
            using var log = new ServerLog(Path.Combine(root, "log-" + change));
            var commands = new GameCommandRegistry();
            new ShopHandler(new(items.Items, items.Shops, items.GoldLimits, items.EpicPieces), cash, inventory, log,
                battlePassCertificateChanged: pass.MissionStateAsync).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new HeldOutput();
            var sent = new ConcurrentQueue<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Enqueue((kind, opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            Task held = sender.Send(1, 0, [], "held");
            await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            long heldBytes = sender.PendingBytes;
            byte[] body = new byte[4 + 15 * quantity]; body[2] = checked((byte)quantity);
            for (int i = 0; i < quantity; i++)
            {
                body[5 + 15 * i] = 0xFF;
                BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7 + 15 * i), checked((uint)product.CommodityId));
            }
            Task<CommandResult> purchase = commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, body));
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (sender.PendingBytes <= heldBytes && !purchase.IsCompleted && timer.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Yield();
            var purchased = session.Selected!;
            var state = repository.ReadBattlePassMissions(owner, now)!;
            int purchaseBalance = purchased.Inventory.AccountWallet.Get(CurrencyKind.Cera);
            int mainItemCount = product.Count + items.CashShop.Bonus(product.CommodityId)
                .Where(bonus => bonus.ItemId == product.ItemId).Sum(bonus => bonus.Count);
            That(sender.PendingBytes > heldBytes && !purchase.IsCompleted && !ReferenceEquals(initial, purchased) &&
                purchased.Id == initial.Id && session.SelectionEpoch == epoch &&
                purchaseBalance == 200000 - quantity * product.CeraPrice &&
                purchased.Inventory.Items.Where(item => item.Core.ItemId == product.ItemId).Sum(item => item.Core.Count) ==
                    initial.Inventory.Items.Where(item => item.Core.ItemId == product.ItemId).Sum(item => item.Core.Count) + quantity * mainItemCount &&
                Number("SELECT count(*) FROM account_battle_pass_cera_orders") == 1 &&
                Number("SELECT cera FROM account_battle_pass_cera_orders") == quantity * product.CeraPrice &&
                Number("SELECT count(*) FROM account_battle_pass_certificate_orders") == 0 &&
                state.Season.Revision > 0 && state.Season.TotalExperience > 0,
                "真实普通商品、点券扣款与消费经验已提交，原ACK排队等待");
            AccountOwners.Lease? replacement = null;
            try
            {
                if (change is "recharge" or "recharge-cart")
                {
                    string answer = DevelopmentGm.Answer("recharge cera 100", account, repository, owners, _ => session);
                    That(answer.StartsWith("OK ", StringComparison.Ordinal) && !ReferenceEquals(session.Selected, purchased) &&
                        session.Selected!.Id == initial.Id && session.SelectionEpoch == epoch &&
                        session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == purchaseBalance + 100,
                        "ACK出队前真实充值更换同角色快照但不改变纪元");
                }
                else if (change == "reselect")
                {
                    session.ReturnToRoster(); session.Select(1); session.MarkReady();
                    That(session.SelectionEpoch != epoch && session.Selected!.Id != initial.Id, "切换到另一角色");
                }
                else if (change == "takeover")
                {
                    replacement = owners.Acquire(account, () => { });
                    That(!owner.IsCurrent && replacement.IsCurrent, "原账号owner被接管");
                }
            }
            finally { output.Release.TrySetResult(); }
            try
            {
                await Observe(held);
                await Observe(purchase);
            }
            finally { replacement?.Dispose(); }

            async Task Observe(Task task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (change == "takeover" &&
                    error is InvalidOperationException or IOException or OperationCanceledException) { }
            }

            That(Number("SELECT count(*) FROM account_battle_pass_cera_orders") == 1 &&
                Number("SELECT count(*) FROM currency_audit_log WHERE action_name='cash_shop_buy' AND currency_kind=15") == 1,
                "发送交错不重复扣款或写入消费订单");
            var frames = sent.ToArray();
            var acks = frames.Where(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM).ToArray();
            if (change is "recharge" or "unchanged" or "recharge-cart")
            {
                var expectedAck = CashShopPackets.Success(product, items.CashShop.Bonus(product.CommodityId));
                That(acks.Length == quantity && acks.All(ack => ack.Body.SequenceEqual(expectedAck)),
                    "同角色同纪元的每件已提交商品必须且只能收到一次成功ACK：" + change);
                var cera = frames.Where(row => row.Kind == 0 && row.Opcode == (ushort)NotiPacketType.CERA).ToArray();
                int expected = purchaseBalance + (change is "recharge" or "recharge-cart" ? 100 : 0);
                That(cera.Length > 0 && cera[^1].Body.Length == 13 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(cera[^1].Body.AsSpan(1)) == expected,
                    "最后点券投影等于当前已提交余额：" + change);
                if (change is "recharge" or "recharge-cart")
                {
                    var fullInventory = frames.LastOrDefault(row => row.Action == "inventory_sync_main");
                    That(fullInventory.Body is not null &&
                        fullInventory.Body.SequenceEqual(InventoryPackets.Full(session.Selected!.Inventory)),
                        "完整主背包投影必须匹配当前权威快照：" + change);
                }
                var pages = frames.Where(row => row.Kind == 0 && row.Opcode is
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS or (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION or
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
                That(pages.Length >= 3 && pages.TakeLast(3).Select(row => row.Opcode).SequenceEqual(new ushort[]
                    { (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS, (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION,
                      (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD }) &&
                    pages[^3].Body.SequenceEqual(BattlePassProjection.PassInfo(policy.Catalog, state.Season)) &&
                    pages[^2].Body.SequenceEqual(state.ToPayload()) && pages[^1].Body.SequenceEqual(state.Season.ToClaimBytes()),
                    "最后三页与消费经验提交后的权威账号季一致：" + change);
            }
            else
                That(acks.Length == 0 && frames.All(row => row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS &&
                    row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION && row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD),
                    "换角或账号接管不投递旧购买结果");

            long Number(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    sealed class HeldOutput : MemoryStream
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
