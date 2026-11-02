using System.Buffers.Binary;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Mailbox;
using DfoServer.Game.Session;
using DfoServer.GameWorld.BattlePass;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

// 未执行候选：对真实不渝之咏邮件提取提交后的战令读库施加SQLite独占锁。
internal static class BattlePassMailBusyBoundary
{
    internal static async Task RunAsync(TcpTestResources resources)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-mail-busy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string account = "pass-mail-busy", season = "mail-material-busy";
            const int itemId = 10308807, quantity = 25;
            var items = resources.ItemResources;
            That(items.Items.Get(itemId) is { Kind: ItemKind.Material, AccountMaterial: not null },
                "matching resource identifies song as an account material");
            var catalog = BattlePassCatalog.Load(resources.Archive);
            var now = DateTimeOffset.UtcNow;
            var local = now.ToOffset(TimeSpan.FromHours(8));
            var thursday = new DateTimeOffset(local.Year, local.Month, local.Day, 6, 0, 0, TimeSpan.FromHours(8))
                .AddDays(-(((int)local.DayOfWeek - (int)DayOfWeek.Thursday + 7) % 7));
            if (thursday > local) thursday = thursday.AddDays(-7);
            BattlePassMissionPolicy? policy = null;
            for (int week = 0; week < catalog.WeeklyMissionIds.Length / 4; week++)
            {
                var candidate = new BattlePassMissionPolicy(season, thursday.AddDays(-7 * week), catalog);
                if (candidate.Active(now).Any(row => BattlePassUndefeatedSongSource.Matches(row.Key.Category, row.Definition)))
                { policy = candidate; break; }
            }
            if (policy is null) throw new InvalidDataException("fixture has no active original weekly mission 48");
            string path = Path.Combine(root, "game.db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire(account, () => { });
            repository.Create(owner, new NewCharacter("MailBusy", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner);
            session.Select(0); session.MarkReady();
            long originalRevision = session.Selected!.Revision;
            var service = new MailboxService(repository, items.Items, items.GoldLimits, items.EpicPieces);
            var recipient = service.FindRecipient("MailBusy") ?? throw new InvalidDataException("fixture recipient missing");
            var delivery = service.Deliver(new MailboxDelivery("mail-busy-once", recipient, "系统", "测试附件", 0,
                [new(0, new(InventoryListType.Main, 0), InventoryRewardGrantService.Create(items.Items.Get(itemId), quantity))],
                now.ToUnixTimeSeconds()));
            That(delivery.Accepted, "one actual material attachment delivered to isolated mailbox");
            var mail = service.Open(owner, recipient.Id, now).Entries.Single(row => row.Id == delivery.MailId);
            uint claim = mail.Attachments.Single().ClaimId;
            _ = repository.ReadBattlePassMissions(owner, now);

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
            using var stop = new CancellationTokenSource();
            using var stream = new MemoryStream();
            SqliteConnection? locker = null;
            int attempts = 0, acks = 0, successes = 0;
            bool observedCommit = false;
            await using var sender = new GameConnectionSender(owner, stream, stop, 0, (_, opcode, _, body, action) =>
            {
                if (action != "mail_claim_result" || opcode != (ushort)CmdPacketType.MAILBOX_EXTRACT_ITEM) return;
                if (!body.IsEmpty && body.Span[0] == 1) Interlocked.Increment(ref successes);
                if (Interlocked.Increment(ref acks) != 1) return;
                observedCommit = session.Selected!.Revision == originalRevision + 1 &&
                    session.Selected.Inventory.AccountWallet.Material(itemId).Balance == quantity &&
                    session.Selected.Inventory.Items.All(row => row.Core.ItemId != itemId) &&
                    Number("SELECT count(*) FROM mailbox_attachments WHERE claimed=1") == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='mail_undefeated_song_acquisition'") == 1 &&
                    Number("SELECT count(*) FROM account_battle_pass_mission_progress WHERE category=1 AND mission_id=48 AND current_count=25") == 1;
                locker = Open();
                using var hold = locker.CreateCommand(); hold.CommandText = "BEGIN EXCLUSIVE"; hold.ExecuteNonQuery();
            });
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, stream, stop), owner, session, sender);
            var commands = new GameCommandRegistry();
            new MailboxHandler(service, new MailboxProjection(service, new OnlineSessionDirectory()), inventory).Register(commands);
            commands.Seal();
            byte[] body = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(body, 1);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(4), claim);
            var request = new Packet(1, (ushort)CmdPacketType.MAILBOX_EXTRACT_ITEM, body);
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
            { protectedBoundary = true; }
            finally { ReleaseLock(); }
            var final = repository.LoadCharacter(owner, 0)!;
            That(observedCommit && protectedBoundary && attempts == 1 && acks == 1 && successes == 1 && final.Revision == originalRevision + 1 &&
                final.Inventory.AccountWallet.Material(itemId).Balance == quantity &&
                final.Inventory.Items.All(row => row.Core.ItemId != itemId) &&
                Number("SELECT count(*) FROM mailbox_attachments WHERE claimed=1") == 1 &&
                Number("SELECT count(*) FROM account_battle_pass_source_events WHERE source_kind='mail_undefeated_song_acquisition'") == 1 &&
                Number("SELECT count(*) FROM account_battle_pass_mission_progress WHERE category=1 AND mission_id=48 AND current_count=25") == 1,
                "committed mail acquisition and success ACK are not replayed after projection SQLITE_BUSY");

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
}
