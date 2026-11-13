using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Net;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;
using DfoServer.Network.Builders.Inventory;
using DfoServer.Network.Handlers;
using DfoServer.Network.Projection;
using DfoServer.Network.Projection.Inventory;
using DfoServer.Network.Protocol;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class BattlePassRewardTests
{
    // 与同组 1744 成功发布同一条命令分发：19B 正文的 action 字节为 1，进入 BattlePassHandler，
    // 不在领域层直接调用 ClaimBattlePassReward。邮箱回调与 GameProtocolHandler 绑定的 MailboxProjection.Alarm 相同。
    internal static async Task RunClaimAllOverflowPublicationAsync(TcpTestResources resources)
    {
        await PublishAsync(resources, 55, "one-slot",
            [(999001, 55 * 999), (490705120, 3)],
            [(15, 1), (490705101, 3), (490705122, 1)]);
        await PublishAsync(resources, 56, "full",
            [(999001, 56 * 999)],
            [(15, 1), (490705101, 3), (490705120, 3), (490705122, 1)]);
    }

    static async Task PublishAsync(TcpTestResources resources, int filledSlots, string name,
        (int ItemId, int Count)[] inBag, (int ItemId, int Count)[] mailed)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-claim-all-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string season = "claim-all-overflow";
            var fixture = new Fixture();
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            var clock = new AckClock(now);
            string path = Path.Combine(root, name + ".db");
            long Number(string sql)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                return Convert.ToInt64(command.ExecuteScalar());
            }
            (int ItemId, int Count)[] MailTotals() => Query("""
                SELECT json_extract(i.item_core,'$.ItemId'), SUM(json_extract(i.item_core,'$.Value'))
                FROM mailbox_attachments a
                JOIN character_inventory_items i ON i.item_uid=a.item_uid
                GROUP BY json_extract(i.item_core,'$.ItemId')
                ORDER BY json_extract(i.item_core,'$.ItemId')
                """).Select(row => (Convert.ToInt32(row[0]), Convert.ToInt32(row[1]))).ToArray();
            void Sql(string sql, params object[] values)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                command.ExecuteNonQuery();
            }
            object?[][] Query(string sql)
            {
                using var db = Open();
                using var command = db.CreateCommand();
                command.CommandText = sql;
                using var reader = command.ExecuteReader();
                var rows = new List<object?[]>();
                while (reader.Read())
                {
                    var row = new object?[reader.FieldCount];
                    for (int i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                    rows.Add(row);
                }
                return rows.ToArray();
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open();
                return db;
            }
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy, clock);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("pass-claim-all-" + name, () => { });
            repository.Create(owner, new NewCharacter("PassOverflow", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0);
            session.MarkReady();
            _ = repository.ReadBattlePassMissions(owner, now)!;
            long experience = checked((long)fixture.Catalog.TotalExperienceToReach(4));
            Sql("UPDATE account_battle_pass_seasons SET total_experience=$p0,pass_type=1 WHERE account=$p1 AND season_key=$p2",
                experience, owner.Account, season);
            var fill = session.PlanAndCommitInventory(character =>
            {
                var after = new InventorySnapshot(Enumerable.Range(65, filledSlots).Select(slot =>
                    new InventoryItem(0, new(InventoryListType.Main, (short)slot), new(999001, ItemKind.Consumable, 999)))
                    .ToImmutableArray());
                return new InventoryResult(true, 0, new InventoryPlan(character.Inventory, after, "reward_grant", new(character)));
            }, 1, requireTown: false);
            That(fill.Accepted && session.Selected is { } filled && filled.Inventory.Items.Length == filledSlots &&
                Totals(filled.Inventory).SequenceEqual([(999001, filledSlots * 999)]),
                "1744 action1 夹具先提交 " + filledSlots + " 格消耗品，再发送全部领取");

            var items = resources.ItemResources;
            var stats = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(stats,
                new TownProjection(resources.Towns, stats, null, items.EpicPieces), items.EpicPieces);
            using var log = new ServerLog(Path.Combine(root, "log-" + name));
            var commands = new GameCommandRegistry();
            new BattlePassHandler(fixture.Service, season, inventory, log).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new AckHeldOutput();
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Add((kind, opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            session.MailboxArrived = receipt => sender.Post(0, (ushort)NotiPacketType.MAILBOX_ALARM, MailboxPackets.Alarm(),
                "mailbox_arrived", () => session.CanPublishMailboxAlarm(receipt));
            Task held = sender.Send(1, 0, [], "held");
            await output.Started.WaitAsync(TimeSpan.FromSeconds(5));
            long heldBytes = sender.PendingBytes;
            byte[] request = new byte[19];
            request[13] = 1;
            request[14] = 1;
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(15), 4);
            ushort rewardOpcode = (ushort)CmdPacketType.BATTLE_PASS_REQUEST_REWARD;
            That(rewardOpcode == 1744 && request.Length == 19 && request[13] == 1,
                "全部领取沿已解析的 19B 1744 正文发送 action1");
            Task<CommandResult> dispatch = commands.DispatchAsync(context, new(1, rewardOpcode, request));
            var wait = Stopwatch.StartNew();
            while (sender.PendingBytes <= heldBytes && !dispatch.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Yield();
            var granted = session.Selected ?? throw new InvalidOperationException("claim-all selection missing");
            var committed = repository.ReadBattlePass(owner, season);
            try
            {
                That(sender.PendingBytes > heldBytes && !dispatch.IsCompleted &&
                    sent.All(row => row.Opcode != rewardOpcode) &&
                    committed.Claims.SetEquals([new BattlePassRewardKey(1, 1), new(1, 2), new(1, 3), new(0, 4), new(1, 4)]) &&
                    Totals(granted.Inventory).SequenceEqual(inBag) &&
                    Number("SELECT count(*) FROM mailbox_messages") == 1 &&
                    MailTotals().SequenceEqual(mailed),
                    "1744 action1 在成功ACK写出前已提交入包物品、溢出附件和全部领取键：" + name);
            }
            finally { output.Release(); }
            await held.WaitAsync(TimeSpan.FromSeconds(5));
            await dispatch.WaitAsync(TimeSpan.FromSeconds(5));

            var frames = sent.Where(row => row.Kind == 1 && row.Opcode == rewardOpcode ||
                row.Kind == 0 && row.Opcode is (ushort)NotiPacketType.UPDATE_ITEM_LIST or (ushort)NotiPacketType.ITEM_LIST
                    or (ushort)NotiPacketType.MAILBOX_ALARM or (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
            string described = string.Join(",", frames.Select(row => row.Opcode + ":" + Convert.ToHexString(row.Body)));
            byte[] claimBytes = committed.ToClaimBytes();
            if (filledSlots == 55)
            {
                var rewardSlots = granted.Inventory.Items.Where(item => item.Core.ItemId == 490705120)
                    .Select(item => item.Position).ToArray();
                byte[] delta = InventoryPackets.Delta(granted.Inventory, InventoryListType.Main, rewardSlots);
                That(frames.Length == 4 && frames[0].Body.SequenceEqual(new byte[] { 1 }) &&
                    frames[1].Opcode == (ushort)NotiPacketType.UPDATE_ITEM_LIST && frames[1].Body.SequenceEqual(delta) &&
                    frames[2].Opcode == (ushort)NotiPacketType.MAILBOX_ALARM && frames[2].Body.SequenceEqual(MailboxPackets.Alarm()) &&
                    frames[3].Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD && frames[3].Body.SequenceEqual(claimBytes) &&
                    claimBytes is [8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 15, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                    "一格剩余的 1744 响应链为成功ACK、入包差量、邮箱提醒和20B 1802；实际 " + described);
            }
            else
            {
                That(frames.Length == 3 && frames[0].Body.SequenceEqual(new byte[] { 1 }) &&
                    frames[1].Opcode == (ushort)NotiPacketType.MAILBOX_ALARM && frames[1].Body.SequenceEqual(MailboxPackets.Alarm()) &&
                    frames[2].Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD && frames[2].Body.SequenceEqual(claimBytes) &&
                    sent.All(row => row.Opcode != (ushort)NotiPacketType.UPDATE_ITEM_LIST &&
                        row.Opcode != (ushort)NotiPacketType.ITEM_LIST) &&
                    claimBytes is [8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 15, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                    "背包全满不把奖励写入库存投影，响应链为成功ACK、邮箱提醒和20B 1802；实际 " + described);
            }
            That(Query("SELECT sender_name,body FROM mailbox_messages").Single() is { Length: 2 } mail &&
                Equals(mail[0], BattlePassRewardService.OverflowMailSender) &&
                Equals(mail[1], BattlePassRewardService.OverflowMailText) &&
                Number("SELECT count(*) FROM mailbox_attachments WHERE claimed=0") == Number("SELECT count(*) FROM mailbox_attachments") &&
                MailTotals().SequenceEqual(mailed),
                "溢出邮件附件与探险记发件人、正文一起提交，且都尚未领取：" + name);

            int framesBeforeReplay = sent.Count;
            await commands.DispatchAsync(context, new(1, rewardOpcode, request)).WaitAsync(TimeSpan.FromSeconds(5));
            var replay = sent.Skip(framesBeforeReplay).Where(row => row.Kind == 1 && row.Opcode == rewardOpcode ||
                row.Kind == 0 && row.Opcode is (ushort)NotiPacketType.UPDATE_ITEM_LIST or (ushort)NotiPacketType.ITEM_LIST
                    or (ushort)NotiPacketType.MAILBOX_ALARM or (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
            var repeated = repository.ReadBattlePass(owner, season);
            That(replay.Length == 1 && replay[0].Body.SequenceEqual(BattlePassHandler.Acknowledge(false, 19)) &&
                repeated.Claims.SetEquals(committed.Claims) && repeated.Revision == committed.Revision &&
                Totals(repository.LoadCharacter(owner, 0).Inventory).SequenceEqual(inBag) &&
                Number("SELECT count(*) FROM mailbox_messages") == 1 && MailTotals().SequenceEqual(mailed),
                "重复 1744 action1 只返回失败ACK，不重复入包、不重复发邮件、不重复记领取键：" + name);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }
    }
}
