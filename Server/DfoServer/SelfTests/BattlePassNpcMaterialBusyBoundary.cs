using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
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

// 候选：只在 root 隔离资源测试中调用。DELETE journal 用真实独占锁制造投影读库 SQLITE_BUSY。
internal static class BattlePassNpcMaterialBusyBoundary
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-npc-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "pass-npc-busy", season = "npc-material-busy";
            const int material = 10158964, product = 10160299, npc = 476;
            var items = resources.ItemResources;
            var start = new DateTimeOffset(2026, 10, 29, 6, 0, 0, TimeSpan.FromHours(8));
            var clock = new Clock(start.AddHours(1));
            var policy = new BattlePassMissionPolicy(season, start, BattlePassCatalog.Load(resources.Archive));
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            var source = InventoryRewardGrantService.Create(items.Items.Get(material), 18);
            repository.Create(owner, new NewCharacter("NpcBusy", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 95,
                Inventory = new([new(0, new(InventoryListType.Main, 121), source)], Gold: 0)
            });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            _ = repository.ReadBattlePassMissions(owner, clock.GetUtcNow());
            That(items.Shops.Allows(npc, -1, product), "fixture uses actual Jonathan offer");

            // 只改这个隔离测试库。WAL允许读者绕开写锁，DELETE模式下EXCLUSIVE确实阻塞后续读取。
            SqliteDatabaseBootstrap.ReleaseRuntimePool();
            using (var setup = Open())
            using (var command = setup.CreateCommand())
            {
                command.CommandText = "PRAGMA journal_mode=DELETE";
                That(string.Equals(Convert.ToString(command.ExecuteScalar()), "delete", StringComparison.OrdinalIgnoreCase),
                    "isolated fixture accepts DELETE journal");
            }
            var stats = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(stats, new TownProjection(resources.Towns, stats, null, items.EpicPieces), items.EpicPieces);
            var shop = new InventoryShopService(items.Items, items.Shops, items.GoldLimits, items.EpicPieces, clock);
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            using var log = new ServerLog(Path.Combine(root, "log"));
            SqliteConnection? locker = null;
            int attempts = 0, acks = 0;
            bool observedCommit = false;
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, _, _, _, action) =>
            {
                if (action != "npc_shop_bought") return;
                if (Interlocked.Increment(ref acks) != 1) return;
                var selected = session.Selected!;
                observedCommit = selected.Inventory.Items.Where(row => row.Core.ItemId == material).Sum(row => row.Core.Count) == 9 &&
                    selected.Inventory.Items.Where(row => row.Core.ItemId == product).Sum(row => row.Core.Count) == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='npc_material_consume'") == 1;
                locker = Open();
                using var hold = locker.CreateCommand();
                hold.CommandText = "BEGIN EXCLUSIVE";
                hold.ExecuteNonQuery();
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var commands = new GameCommandRegistry();
            new ShopHandler(shop, null!, inventory, log).Register(commands);
            commands.Seal();
            byte[] body = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(body, product);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(8), -1);
            BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12), npc);
            var request = new Packet(1, (ushort)CmdPacketType.BUY_ITEM, body);
            bool protectedBoundary = false;
            try
            {
                await SqliteBusyRetry.TryExecuteAsync(() =>
                {
                    if (Interlocked.Increment(ref attempts) == 2) ReleaseLock();
                    return commands.DispatchAsync(context, request);
                }, CancellationToken.None);
            }
            catch (IOException error) when (error.InnerException is SqliteException { SqliteErrorCode: 5 })
            {
                protectedBoundary = true;
            }
            finally { ReleaseLock(); }

            var final = repository.LoadCharacter(owner, 0)!;
            That(observedCommit && protectedBoundary && attempts == 1 && acks == 1 &&
                final.Inventory.Items.Where(row => row.Core.ItemId == material).Sum(row => row.Core.Count) == 9 &&
                final.Inventory.Items.Where(row => row.Core.ItemId == product).Sum(row => row.Core.Count) == 1 &&
                Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='npc_material_consume'") == 1,
                "post-commit projection SQLITE_BUSY cannot redispatch or duplicate the NPC material purchase");

            void ReleaseLock()
            {
                var held = Interlocked.Exchange(ref locker, null);
                if (held is null) return;
                using (var rollback = held.CreateCommand()) { rollback.CommandText = "ROLLBACK"; rollback.ExecuteNonQuery(); }
                held.Dispose();
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
            long Number(string sql)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }

    sealed class Clock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
