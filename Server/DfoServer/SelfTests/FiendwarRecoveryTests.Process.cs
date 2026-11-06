using System.Diagnostics;
using DfoServer.Game.Accounts;
using DfoServer.Persistence;
using DfoServer.Testing;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer;

internal static partial class FiendwarRecoveryTests
{
    static async Task RunProcessAsync(ServerOptions options, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(options with { FileLogsEnabled = true }, root, resources: resources);
        string checkpoint = "before-crash";
        await fixture.StartAsync(isolatedProcess: true);
        var repository = new CharacterRepository(fixture.Database, resources.Quests);
        foreach (bool guide in new[] { false, true })
        {
            using var lease = new AccountOwners().Acquire(guide ? "fwcrashguide" : "fwcrashteam", () => { });
            Seed(repository, lease, resources, guide ? "FwCrashGuide" : "FwCrashTeam", guide ? 10309054 : 10306760, DateTimeOffset.UtcNow);
        }
        ushort[] roles = new ushort[2];
        using (var team = Open(fixture, "fwcrashteam", gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39))
        using (var guide = Open(fixture, "fwcrashguide", gamePort: ChannelTcpTests.Endpoints(fixture)[39], channelNumber: 39))
        {
            foreach (var (peer, single) in new[] { (team, false), (guide, true) })
            {
                peer.Select(); Read(peer);
                peer.Send(1426); Read(peer);
                peer.Send(664, Build(writer => { writer.Write((byte)2); writer.Write(Name(single ? "crash-guide" : "crash-team"));
                    writer.Write(new byte[] { single ? (byte)1 : (byte)0, 0, 0, 7 }); }));
                That(Read(peer).Any(row => row is { Kind: 1, Opcode: 664, Body: [1, ..] }), "进程中断回归真实团队/引导建团");
                byte area = single ? (byte)33 : (byte)2;
                var arrival = resources.Towns.Entrance(new(57, 1, 0, 0), new(57, area, 0, 0), new HashSet<int> { 8672 })!;
                peer.Send(36, Area(57, area, checked((ushort)arrival.X), checked((ushort)arrival.Y), 57, 1)); Read(peer);
                peer.Send(666);
                That(Read(peer).Any(row => row is { Kind: 1, Opcode: 666, Body: [1] }), "进程中断前真实666提交原票");
                roles[single ? 1 : 0] = peer.Id;
            }
            That(Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=2 AND state=0") is [[2L]] &&
                Query("SELECT count(*) FROM character_fiendwar_recovery WHERE state=0") is [[2L]], "kill前独立SQL可见两种真实扣票和活跃身份");
            // 不先断开peer或请求stop，精确终止本夹具进程，禁止会话收尾把这次崩溃伪装成主动退出。
            await fixture.CrashAsync();
            checkpoint = "after-crash-before-restart";
            That(await QueryAfterCrashAsync() is [[2L]], "突然终止没有执行断线/离团收尾");
        }
        await fixture.StartAsync("after-crash", isolatedProcess: true);
        checkpoint = "after-first-restart";
        Verify();
        await fixture.StopAsync();
        await fixture.StartAsync("second-restart", isolatedProcess: true);
        checkpoint = "after-second-restart";
        Verify();
        await fixture.StopAsync();
        string log = File.ReadAllText(Path.Combine(fixture.Options.LogDirectory, "server.jsonl"));
        int recovery = log.IndexOf("Fiendwar interruption recovery", StringComparison.Ordinal);
        int ready = log.IndexOf("server_ready", StringComparison.Ordinal);
        That(recovery >= 0 && ready > recovery,
            "真实宿主在ready之前执行并记录中断恢复阶段");

        void Verify()
        {
            That(Query("SELECT count(*) FROM character_raid_runs WHERE raid_type=2 AND state=1 AND clear_week IS NULL") is [[2L]],
                "宿主重启只终结原未完成run，不产生通关次数");
            That(Query("SELECT count(*) FROM mailbox_messages WHERE delivery_key LIKE 'fiendwar-refund:%'") is [[2L]],
                "原宿主恢复每种原票一次，重复重启不增发");
            foreach (int index in new[] { 0, 1 })
                That(Query("SELECT json_extract(i.item_core,'$.ItemId'),json_extract(i.item_core,'$.Value') FROM mailbox_messages m JOIN mailbox_attachments a USING(mail_id) JOIN character_inventory_items i ON i.item_uid=a.item_uid WHERE m.recipient_id=$p0", roles[index])
                    is [[long item, 1L]] && item == (index == 0 ? 10306760 : 10309054), "团队/引导分别返还实际已扣票种和数量");
        }
        object[][] Query(string sql, params object[] parameters)
        {
            try { return fixture.Query(sql, parameters); }
            catch (SqliteException error)
            {
                throw new IOException($"Fiendwar recovery SQL checkpoint={checkpoint}, code={error.SqliteErrorCode}, extended={error.SqliteExtendedErrorCode}, sql={sql}", error);
            }
        }
        async Task<object[][]> QueryAfterCrashAsync()
        {
            var elapsed = Stopwatch.StartNew();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    var result = Query("SELECT count(*) FROM character_fiendwar_recovery WHERE state=0");
                    if (attempt > 0) Console.WriteLine($"Fiendwar restart WAL truncation wait: attempts={attempt}, elapsed_ms={elapsed.ElapsedMilliseconds}");
                    return result;
                }
                catch (IOException error) when (OperatingSystem.IsWindows() && elapsed.Elapsed < TimeSpan.FromSeconds(5) &&
                    error.InnerException is SqliteException { SqliteExtendedErrorCode: 1546 })
                {
                    // 只在已精确终止原服务后的首次取证等候Windows释放WAL截断资源；每轮仍创建新只读连接。
                    await Task.Delay(25);
                }
            }
        }
        static List<WirePacket> Read(TcpPeer peer) => AntonTcpProbe.Barrier(peer, "fiendwar process interruption");
    }
}
