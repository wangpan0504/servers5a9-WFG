using System.Buffers.Binary;
using System.Net;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers.Inventory;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class BattlePassCertificatePublicationRace
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-certificate-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string change in new[] { "unchanged", "reselect", "takeover", "recharge", "recharge-twice" })
                await Scenario(change);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        async Task Scenario(string change)
        {
            const string season = "certificate-publication-race";
            var items = resources.ItemResources;
            var product = items.CashShop.Product(105659);
            That(product is { ItemId: 490705043, Count: 1, CeraPrice: 4900, Section: "package" } &&
                items.CashShop.Mileage.CeraOnly.Contains(490705043) && items.CashShop.Bonus(105659).Count == 0,
                "原商城资格证是单件4900真实点券且无附赠");
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            var clock = new Clock(now);
            string path = Path.Combine(root, change + ".db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("certificate-race", () => { });
            repository.Create(owner, new NewCharacter("CertOne", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            repository.Create(owner, new NewCharacter("CertTwo", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            That(DevelopmentGm.Answer("recharge cera 10000", owner.Account, repository, owners, _ => session)
                .StartsWith("OK ", StringComparison.Ordinal), "隔离账号准备10000真实点券");
            var initial = session.Selected!;
            long epoch = session.SelectionEpoch;
            var computer = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(computer,
                new TownProjection(resources.Towns, computer, null, items.EpicPieces), items.EpicPieces);
            var cash = new InventoryCashShopService(items.Items, items.CashShop, items.GoldLimits, items.EpicPieces, items.Cargo,
                contracts: items.Contracts, avatars: items.AvatarPackages, clock: clock);
            var battlePass = new BattlePassProjection(season, policy.Catalog, clock);
            using var log = new ServerLog(Path.Combine(root, "log-" + change));
            var commands = new GameCommandRegistry();
            new ShopHandler(new(items.Items, items.Shops, items.GoldLimits, items.EpicPieces), cash, inventory, log,
                battlePassCertificateChanged: battlePass.MissionStateAsync).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new HeldOutput(change == "recharge-twice");
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Add((kind, opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            Task held = sender.Send(1, 0, [], "held");
            await output.Started.WaitAsync(TimeSpan.FromSeconds(5));
            long heldBytes = sender.PendingBytes;
            byte[] body = new byte[19]; body[2] = 1; body[5] = 0xFF;
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(7), 105659);
            Task<CommandResult> purchase = commands.DispatchAsync(context, new(1, (ushort)CmdPacketType.BUY_CERASHOP_ITEM, body));
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (sender.PendingBytes <= heldBytes && !purchase.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Yield();
            var purchased = session.Selected!;
            var state = repository.ReadBattlePassMissions(owner, now)!;
            That(sender.PendingBytes > heldBytes && !purchase.IsCompleted &&
                !ReferenceEquals(initial, purchased) && purchased.Id == initial.Id && session.SelectionEpoch == epoch &&
                purchased.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 5100 && state.Season.PassType == 1 &&
                Number("SELECT count(*) FROM account_battle_pass_certificate_orders") == 1,
                "真实证书事务已提交4900扣款和唯一资格订单，成功ACK排队待发送");
            string order = Text("SELECT order_id FROM account_battle_pass_certificate_orders");
            AccountOwners.Lease? replacement = null;
            try
            {
                if (change is "recharge" or "recharge-twice")
                {
                    string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                    That(answer.StartsWith("OK ", StringComparison.Ordinal) && !ReferenceEquals(session.Selected, purchased) &&
                        session.Selected!.Id == purchased.Id && session.SelectionEpoch == epoch &&
                        session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 5200,
                        "在线充值提交后保持相同角色和选角纪元");
                }
                else if (change == "reselect")
                {
                    session.ReturnToRoster(); session.Select(1); session.MarkReady();
                    That(session.SelectionEpoch != epoch && session.Selected!.Id != initial.Id, "换角阻断旧购买结果投影");
                }
                else if (change == "takeover")
                {
                    replacement = owners.Acquire(owner.Account, () => { });
                    That(!owner.IsCurrent && replacement.IsCurrent, "旧购买owner已被接管");
                }
            }
            finally { output.Release(); }
            if (change == "recharge-twice")
            {
                try
                {
                    await output.CeraStarted.WaitAsync(TimeSpan.FromSeconds(5));
                    That(sent.Count(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM &&
                            row.Body.Length == 24 && row.Body[0] == 1) == 1 &&
                        session.Selected!.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 5200 && !purchase.IsCompleted,
                        "原成功ACK已写出一次，恢复组的首次真实点券帧正在发送");
                    foreach (int expected in new[] { 5300, 5400 })
                    {
                        var previous = session.Selected!;
                        string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                        That(answer.StartsWith("OK ", StringComparison.Ordinal) && session.Selected is { } next &&
                            !ReferenceEquals(next, previous) && next.Id == initial.Id && session.SelectionEpoch == epoch &&
                            next.Inventory.AccountWallet.Get(CurrencyKind.Cera) == expected &&
                            repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == expected,
                            "恢复组发送期间连续真实充值并更新同角色快照：" + expected);
                    }
                }
                finally { output.ReleaseCera(); }
            }
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

            That(Number("SELECT count(*) FROM account_battle_pass_certificate_orders") == 1 &&
                Text("SELECT order_id FROM account_battle_pass_certificate_orders") == order &&
                Number("SELECT count(*) FROM currency_audit_log WHERE action_name='cash_shop_buy' AND currency_kind=15") == 1 &&
                Number("SELECT pass_type FROM account_battle_pass_seasons") == 1,
                "发送交错后保留原唯一订单和一次实际扣款，不重建订单");
            var acks = sent.Where(row => row.Kind == 1 && row.Opcode == (ushort)CmdPacketType.BUY_CERASHOP_ITEM).ToArray();
            if (change is "unchanged" or "recharge" or "recharge-twice")
            {
                int expectedBalance = change == "recharge-twice" ? 5400 : change == "recharge" ? 5200 : 5100;
                That(acks.Length == 1 && acks[0].Body.Length == 24 && acks[0].Body[0] == 1 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(acks[0].Body.AsSpan(6)) == 105659,
                    "同角色同纪元收到原一次证书购买成功ACK：" + change);
                var cera = sent.Where(row => row.Kind == 0 && row.Opcode == (ushort)NotiPacketType.CERA).ToArray();
                That(cera.Length > 0 && cera[^1].Body.Length == 13 &&
                    BinaryPrimitives.ReadUInt32LittleEndian(cera[^1].Body.AsSpan(1)) == expectedBalance,
                    "最后的点券投影反映同角色当前实际余额：" + change);
                if (change == "recharge-twice")
                    That(cera.Length >= 2 && BinaryPrimitives.ReadUInt32LittleEndian(cera[0].Body.AsSpan(1)) == 5200 &&
                        Number("SELECT count(*) FROM currency_audit_log WHERE action_name='gm_recharge' AND currency_kind=15") == 4 &&
                        sent.All(row => row.Action != "inventory_committed_delta" && row.Action != "mileage_committed_delta"),
                        "在途旧余额后重读最新余额；四次真实充值均持久化且没有重放购买获得型增量");
                var pass = sent.Where(row => row.Kind == 0 && row.Opcode is
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS or (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION or
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
                That(pass.Length >= 3 && pass.TakeLast(3).Select(row => row.Opcode).SequenceEqual(new ushort[]
                    { (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS, (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION,
                      (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD }) &&
                    pass[^3].Body.SequenceEqual(BattlePassProjection.PassInfo(policy.Catalog, state.Season)) &&
                    pass[^2].Body.SequenceEqual(state.ToPayload()) && pass[^1].Body.SequenceEqual(state.Season.ToClaimBytes()),
                    "购买成功后送达完整且一致的资格、任务和领奖状态：" + change);
                var reopened = new CharacterRepository(path);
                reopened.ConfigureBattlePassMissions(policy);
                That(reopened.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == expectedBalance &&
                    reopened.ReadBattlePassMissions(owner, now)!.Season.PassType == 1,
                    "重开仓储恢复真实余额和购买资格");
            }
            else
                That(acks.Length == 0 && sent.All(row => row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS &&
                    row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION && row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD),
                    "换角或接管后不向新身份发送旧购买结果");

            long Number(string sql) => Convert.ToInt64(Scalar(sql));
            string Text(string sql) => (string)Scalar(sql)!;
            object? Scalar(string sql)
            {
                using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
            }
        }
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    sealed class HeldOutput(bool holdCera) : MemoryStream
    {
        readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource ceraStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource ceraRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int ceraHeld;
        public Task Started => started.Task;
        public Task CeraStarted => ceraStarted.Task;
        public void Release() => release.TrySetResult();
        public void ReleaseCera() => ceraRelease.TrySetResult();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            if (holdCera && IsCera(buffer) && Interlocked.Exchange(ref ceraHeld, 1) == 0)
            {
                ceraStarted.TrySetResult();
                await ceraRelease.Task.WaitAsync(cancellationToken);
            }
            await base.WriteAsync(buffer, cancellationToken);
        }

        static bool IsCera(ReadOnlyMemory<byte> frame) => frame.Length >= 16 && frame.Span[0] == 0 &&
            BinaryPrimitives.ReadUInt16LittleEndian(frame.Span[1..]) == (ushort)NotiPacketType.CERA;
    }
}
