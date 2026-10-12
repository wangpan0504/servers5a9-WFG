using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureRepresentativeTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure-representatives"), resources: resources);
        await fixture.StartAsync();
        ushort first, second, actor;
        using (var peer = Open(fixture, "representative"))
        {
            peer.Send(1476, [0, 33]); peer.Expect(1, 1476, [0, 3, 0]);
            peer.Create("RepActor"); actor = peer.Id;
            peer.Create("RepFirst"); first = peer.Id;
            peer.Create("RepSecond"); second = peer.Id;
            Execute("UPDATE characters SET level=95,grow_type=33 WHERE id IN(" + first + "," + second + ")");
            peer.Select();
            peer.Send(1476, [0, 33]);
            var candidates = peer.Expect(1, 1476).Body;
            That(candidates.Length == 108 && candidates[1] == 1 && candidates[2] == 2 && candidates[3] == 2 &&
                U32(candidates, 16) == first && U32(candidates, 68) == second, "真实候选包含两个独立UID和52B行，不用UI序号冒充角色");
            peer.Send(1477, Set(second));
            var ack = peer.Expect(1, 1477).Body;
            That(ack.Length == 64 && U32(ack, 1) == 3 && U32(ack, 8) == 52 && DecodeName(ack, 24, 9) == "RepSecond",
                "真实1477成功先返回职业和收藏记录，再同步完整账号信息");
            peer.Expect(0, 1345); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(fixture.Query("SELECT character_id FROM account_adventure_representatives WHERE account='representative'") is [[long chosen]] && chosen == second,
                "账号绑定真实第二名角色，不仍使用第一候选");
            string state = fixture.Snapshot("SELECT revision,name,level,experience FROM account_adventures");
            peer.Send(1477, Set(second)); peer.Expect(1, 1477); peer.Expect(0, 1345); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(fixture.Snapshot("SELECT revision,name,level,experience FROM account_adventures") == state, "合法重复设置应答但不重复writer或revision");
            peer.Send(1477, Set(actor)); peer.Expect(1, 1477, [0, 3, 0]); peer.Barrier();
            That(fixture.Snapshot("SELECT revision,name,level,experience FROM account_adventures") == state, "未合格当前角色不能冒充满级二觉代表");
            peer.Send(1478, [0, 33]); peer.Expect(1, 1478, [1]);
            peer.Expect(0, 1345);
            var removed = peer.Expect(0, 1343).Body;
            peer.Expect(0, 1349); peer.Barrier();
            That(removed[18 + 72] == 0 && removed[18 + 73] == 1 && U32(removed, 18 + 72 + 48) == 0 &&
                fixture.Query("SELECT character_id FROM account_adventure_representatives") is [[DBNull]],
                "解除返回职业空收藏格，保留NULL绑定，不重新回到默认第一角色");
            string removedState = fixture.Snapshot("SELECT revision FROM account_adventures");
            peer.Send(1478, [0, 33]); peer.Expect(1, 1478, [1]); peer.Expect(0, 1345); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(fixture.Snapshot("SELECT revision FROM account_adventures") == removedState, "重复解除不改变账号版本");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("representatives-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "representative"))
        {
            peer.Select();
            That(U32(peer.AdventureInfo, 18 + 72 + 48) == 0, "进程重启保留显式解除状态");
            peer.Send(1477, Set(first)); peer.Expect(1, 1477); peer.Expect(0, 1345); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "TCP设置、解除与重启保持所有权外键");
        Console.WriteLine($"PASS {Count - checks} 代表查询、设置、解除及进程重启TCP断言");
        static byte[] Set(ushort id) => Build(writer => { writer.Write((byte)2); writer.Write((uint)id); writer.Write((byte)0); writer.Write((byte)33); });
        void Execute(string sql) { using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
}
