using System.Buffers.Binary;
using System.Net;
using DfoServer.Development;
using DfoServer.Game.Accounts;
using DfoServer.Game.BattlePass;
using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Session;
using DfoServer.GameWorld.Characters;
using DfoServer.Infrastructure;
using DfoServer.Network;
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
    internal static async Task RunRewardSuccessPublicationAsync(TcpTestResources resources)
    {
        foreach (byte track in new byte[] { 0, 1 })
            await SuccessPublicationCoreAsync(resources, mission: false, track);
    }

    internal static Task RunMissionSuccessPublicationAsync(TcpTestResources resources) =>
        SuccessPublicationCoreAsync(resources, mission: true, track: 0);

    internal static Task RunMissionPublicationBusyAsync(TcpTestResources resources) =>
        SuccessPublicationCoreAsync(resources, mission: true, track: 0, publicationBusy: true);

    static async Task SuccessPublicationCoreAsync(TcpTestResources resources, bool mission, byte track, bool publicationBusy = false)
    {
        string root = Path.Combine(Path.GetTempPath(), "S5A9-pass-success-publish-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string change in publicationBusy ? new[] { "publish-busy-5", "publish-busy-6" } : new[] { "unchanged", "reselect", "takeover", "recharge" })
                await Scenario(change);
        }
        finally { TestFiles.DeleteDirectory(root, recursive: true); }

        async Task Scenario(string change)
        {
            const string season = "success-publication-race";
            var fixture = new Fixture();
            var (policy, now) = BattlePassMissionTests.AckFixturePolicy(season);
            var clock = new AckClock(now);
            string path = Path.Combine(root, change + ".db");
            var repository = new CharacterRepository(path);
            repository.ConfigureBattlePassMissions(policy);
            var owners = new AccountOwners();
            using var owner = owners.Acquire("pass-success", () => { });
            repository.Create(owner, new NewCharacter("PassOne", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            repository.Create(owner, new NewCharacter("PassTwo", resources.Towns.Room, resources.Towns.InitialReturn) { Level = 95 });
            var session = new GameSession(repository, resources.Towns, owner, timeProvider: clock);
            session.Select(0); session.MarkReady();
            var initial = session.Selected!;
            long epoch = session.SelectionEpoch;
            _ = repository.ReadBattlePassMissions(owner, now)!;
            // 仅在隔离库种入领取资格；领取、提交、发送和在线充值均走真实路径。
            if (mission)
            {
                var key = policy.Active(now).Single(row => row.Key.Category == 0 && row.Key.Id == 12).Key;
                Sql("INSERT INTO account_battle_pass_mission_progress VALUES($p0,$p1,$p2,$p3,$p4,$p5)",
                    owner.Account, season, key.Category, key.Period, key.Id, 1);
            }
            else
                Sql("UPDATE account_battle_pass_seasons SET total_experience=$p0,pass_type=$p1 WHERE account=$p2 AND season_key=$p3",
                    checked((long)fixture.Catalog.TotalExperienceToReach(4)), track, owner.Account, season);

            var items = resources.ItemResources;
            var stats = new CharacterStatComputer(resources.Characters);
            var inventory = new InventoryProjection(stats,
                new TownProjection(resources.Towns, stats, null, items.EpicPieces), items.EpicPieces);
            var projection = new BattlePassProjection(season, policy.Catalog, clock);
            using var log = new ServerLog(Path.Combine(root, "log-" + change));
            var commands = new GameCommandRegistry();
            new BattlePassHandler(fixture.Service, season, inventory, log).Register(commands);
            int publishCalls = 0, dispatchAttempts = 0;
            new BattlePassMissionHandler(clock, publicationBusy
                ? (_, _, _) =>
                {
                    publishCalls++;
                    return Task.FromException(new SqliteException("injected post-commit busy", change == "publish-busy-5" ? 5 : 6));
                }
                : projection.MissionStateAsync, log).Register(commands);
            commands.Seal();
            using var stop = new CancellationTokenSource();
            using var output = new AckHeldOutput();
            var sent = new List<(byte Kind, ushort Opcode, byte[] Body, string Action)>();
            await using var sender = new GameConnectionSender(owner, output, stop, 0,
                (kind, opcode, _, body, action) => sent.Add((kind, opcode, body.ToArray(), action)));
            var context = new GameConnectionContext(new(1, IPAddress.Loopback, output, stop), owner, session, sender);
            Task held = sender.Send(1, 0, [], "held");
            await output.Started.WaitAsync(TimeSpan.FromSeconds(5));
            long heldBytes = sender.PendingBytes;
            byte[] request = new byte[mission ? 24 : 19];
            if (mission)
            {
                request[13] = 1;
                BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(14), 12);
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), 1);
            }
            else
            {
                request[14] = track;
                BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(15), 4);
            }
            var opcode = mission ? CmdPacketType.BATTLE_PASS_REQUEST_MISSION : CmdPacketType.BATTLE_PASS_REQUEST_REWARD;
            Task<CommandResult> Dispatch() => commands.DispatchAsync(context, new(1, (ushort)opcode, request));
            async Task<CommandResult> DispatchWithRetry()
            {
                var (_, result) = await SqliteBusyRetry.TryExecuteAsync(() =>
                {
                    dispatchAttempts++;
                    return Dispatch();
                }, stop.Token);
                return result;
            }
            Task<CommandResult> dispatch = publicationBusy ? DispatchWithRetry() : Dispatch();
            var wait = System.Diagnostics.Stopwatch.StartNew();
            while (sender.PendingBytes <= heldBytes && !dispatch.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Yield();
            var committed = session.Selected!;
            var state = repository.ReadBattlePassMissions(owner, now)!;
            var expectedItems = track == 0 ? new[] { (15, 1) } : new[] { (490705101, 3), (490705122, 1) };
            That(sender.PendingBytes > heldBytes && !dispatch.IsCompleted &&
                (mission
                    ? ReferenceEquals(committed, initial) && state.Season.TotalExperience == 100 &&
                      Number("SELECT count(*) FROM account_battle_pass_mission_claims WHERE mission_id=12") == 1 &&
                      state.Slots.Single(row => row.Instance.Key.Category == 0 && row.Instance.Key.Id == 12).Completed
                    : !ReferenceEquals(committed, initial) && Totals(committed.Inventory).SequenceEqual(expectedItems) &&
                      Totals(repository.LoadCharacter(owner, 0).Inventory).SequenceEqual(expectedItems) &&
                      state.Season.Claims.SetEquals([new BattlePassRewardKey(track, 4)]) &&
                      Number("SELECT count(*) FROM account_battle_pass_claims") == 1),
                "真实领取已完整提交且成功ACK排队：" + (mission ? "mission" : "reward-" + track));
            AccountOwners.Lease? replacement = null;
            try
            {
                if (change == "recharge")
                {
                    string answer = DevelopmentGm.Answer("recharge cera 100", owner.Account, repository, owners, _ => session);
                    That(answer.StartsWith("OK ", StringComparison.Ordinal) && !ReferenceEquals(session.Selected, committed) &&
                        session.Selected!.Id == initial.Id && session.SelectionEpoch == epoch &&
                        session.Selected.Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100 &&
                        repository.LoadCharacter(owner, 0).Inventory.AccountWallet.Get(CurrencyKind.Cera) == 100,
                        "真实在线充值提交并替换同角色快照，选角纪元不变");
                }
                else if (change == "reselect")
                {
                    session.ReturnToRoster(); session.Select(1); session.MarkReady();
                    That(session.SelectionEpoch != epoch && session.Selected!.Id != initial.Id, "换角改变角色及纪元");
                }
                else if (change == "takeover")
                {
                    replacement = owners.Acquire(owner.Account, () => { });
                    That(!owner.IsCurrent && replacement.IsCurrent, "接管使旧owner失效");
                }
            }
            finally { output.Release(); }
            if (publicationBusy)
            {
                await held.WaitAsync(TimeSpan.FromSeconds(5));
                try
                {
                    await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
                    throw new InvalidOperationException("提交后发布忙锁必须停止本次连接");
                }
                catch (IOException error) when (error.InnerException is SqliteException sqlite &&
                    sqlite.SqliteErrorCode == (change == "publish-busy-5" ? 5 : 6))
                {
                    That(dispatchAttempts == 1 && publishCalls == 1 &&
                        sent.Count(row => row.Kind == 1 && row.Opcode == (ushort)opcode && row.Body[0] == 1) == 1 &&
                        Number("SELECT count(*) FROM account_battle_pass_mission_claims WHERE mission_id=12") == 1 &&
                        repository.ReadBattlePassMissions(owner, now)!.Season.TotalExperience == 100,
                        "任务已提交并发送唯一成功ACK，发布忙锁不能触发整次领取重试");
                }
                return;
            }
            try { await Observe(held); await Observe(dispatch); }
            finally { replacement?.Dispose(); }
            async Task Observe(Task task)
            {
                try { await task.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception error) when (change == "takeover" &&
                    error is InvalidOperationException or IOException or OperationCanceledException) { }
            }

            var acks = sent.Where(row => row.Kind == 1 && row.Opcode == (ushort)opcode).ToArray();
            if (change is "unchanged" or "recharge")
            {
                byte[] ack = mission ? new byte[] { 1 }.Concat(request.Skip(13)).ToArray() : [1];
                That(acks.Length == 1 && acks[0].Body.SequenceEqual(ack), "同角色同纪元收到一次原领取成功ACK：" + change);
                var actual = repository.ReadBattlePassMissions(owner, now)!;
                var restored = repository.LoadCharacter(owner, 0);
                That(actual.Season.TotalExperience == state.Season.TotalExperience &&
                    actual.Season.Claims.SetEquals(state.Season.Claims) &&
                    (mission || Totals(restored.Inventory).SequenceEqual(expectedItems)), "重读恢复全部领取结果和奖励");
                var pass = sent.Where(row => row.Kind == 0 && row.Opcode is
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS or (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION or
                    (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD).ToArray();
                if (mission)
                    That(pass.Length >= 3 && pass.TakeLast(3).Select(row => row.Opcode).SequenceEqual(new ushort[] {
                        (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS, (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION,
                        (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD }) &&
                        pass[^3].Body.SequenceEqual(BattlePassProjection.PassInfo(policy.Catalog, actual.Season)) &&
                        pass[^2].Body.SequenceEqual(actual.ToPayload()) && pass[^1].Body.SequenceEqual(actual.Season.ToClaimBytes()),
                        "任务领取后投影完整的当前1800、1801、1802");
                else
                {
                    That(pass.Length >= 1 && pass[^1].Opcode == (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD &&
                        pass[^1].Body.SequenceEqual(actual.Season.ToClaimBytes()), "奖励领取后投影当前领奖位");
                    var slots = restored.Inventory.Items.Select(item => item.Position).ToArray();
                    byte[] delta = InventoryPackets.Delta(restored.Inventory, InventoryListType.Main, slots);
                    byte[] full = InventoryPackets.Full(restored.Inventory);
                    That(sent.Any(row => row.Kind == 0 &&
                        (row.Opcode == (ushort)NotiPacketType.UPDATE_ITEM_LIST && row.Body.SequenceEqual(delta) ||
                         row.Opcode == (ushort)NotiPacketType.ITEM_LIST && row.Body.SequenceEqual(full))),
                        "奖励领取后发送当前完整奖励的库存投影");
                }
                await commands.DispatchAsync(context, new(1, (ushort)opcode, request)).WaitAsync(TimeSpan.FromSeconds(5));
                var repeated = repository.ReadBattlePassMissions(owner, now)!;
                That(repeated.Season.TotalExperience == state.Season.TotalExperience &&
                    repeated.Season.Claims.SetEquals(state.Season.Claims) &&
                    Number(mission ? "SELECT count(*) FROM account_battle_pass_mission_claims WHERE mission_id=12" :
                        "SELECT count(*) FROM account_battle_pass_claims") == 1 &&
                    (mission || Totals(repository.LoadCharacter(owner, 0).Inventory).SequenceEqual(expectedItems)),
                    "同请求重放不重复授予经验或完整奖励");
            }
            else
                That(acks.Length == 0 && sent.All(row => row.Kind != 0 ||
                    row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_PASS &&
                    row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_MISSION &&
                    row.Opcode != (ushort)NotiPacketType.BATTLE_PASS_INFO_REWARD &&
                    row.Opcode != (ushort)NotiPacketType.UPDATE_ITEM_LIST &&
                    row.Opcode != (ushort)NotiPacketType.ITEM_LIST), "换角或接管后不向新身份发送旧领取结果");

            long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
            void Sql(string sql, params object[] values)
            {
                using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql;
                for (int i = 0; i < values.Length; i++) command.Parameters.AddWithValue("$p" + i, values[i]);
                command.ExecuteNonQuery();
            }
            SqliteConnection Open()
            {
                var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
                db.Open(); return db;
            }
        }
    }
}
