using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;
using System.Text.Json;

namespace DfoServer.Testing;

internal static partial class TownProtocolTcpTests
{
    static void DeleteCharacters(TcpTestFixture fixture)
    {
        const string account = "delete-flow";
        ushort first, middle, highest, next;
        using (var peer = Open(fixture, account))
        {
            peer.Create("删除首"); first = peer.Id;
            peer.Create("删除中"); middle = peer.Id;
            peer.Create("删除末"); highest = peer.Id;
            Roster(peer.Roster, (0, "删除首"), (1, "删除中"), (2, "删除末"));
            string before = Persisted();
            foreach (var invalid in new[] { ((ushort)1, "Wrong"), ((ushort)31, "Missing") })
            {
                SendDelete(peer, invalid.Item1, invalid.Item2);
                peer.Expect(1, 6, [0, 123, 0]);
                peer.Send(692, Name("Available"));
                peer.Expect(1, 692, [1]);
                That(Persisted() == before, "删除确认名或行索引拒绝保持连接和全部存档");
            }
            SendDelete(peer, 1, "删除中");
            peer.Expect(1, 6, [1, 0, 1, 0]);
            Roster(peer.Expect(0, 2).Body, (0, "删除首"), (2, "删除末"));
            That(fixture.Query("SELECT id,slot FROM characters WHERE account=$p0 ORDER BY slot", account) is var rows &&
                rows.Length == 2 && Convert.ToUInt16(rows[0][0]) == first && Convert.ToUInt16(rows[1][0]) == highest &&
                Convert.ToUInt16(rows[1][1]) == 2, "中间行删除不改剩余角色真实身份和高槽");
            // ACK6消费者随后确认选角上下文，冒险团回放使用未选角色的owner0。
            peer.Send(645);
            peer.Expect(1, 645, [1, 0]);
            var adventure = peer.Expect(0, 1343).Body;
            That(U16(adventure, 0) == 0 && U32(adventure, 14) == 7440 &&
                adventure.Length == 22 + 7440 + U32(adventure, 18 + 7440),
                "删除后仍按原账号初始化owner0、7440B主体和原生名称的选角冒险团信息");
            peer.Send(692, Name("删除中"));
            peer.Expect(1, 692, [1]);
            peer.Select(1);
            That(peer.Id == highest, "C2S4行1在有缺口名册中选择持久slot2角色，完整入城结果正确");
            before = Persisted();
            SendDelete(peer, 1, "删除末");
            peer.Expect(1, 6, [0, 123, 0]);
            peer.Barrier();
            That(Persisted() == before, "已选角删除静默拒绝后连接可继续，当前角色和资产不变");
            Return(peer);
            SendDelete(peer, 0, "删除首");
            peer.Expect(1, 6, [1, 0, 0, 0]);
            Roster(peer.Expect(0, 2).Body, (2, "删除末"));
            peer.Create("删除中");
            That(peer.Id > highest && peer.Id != middle, "删除后同连接立即重建原姓名，角色身份不复用");
            Roster(peer.Roster, (0, "删除中"), (2, "删除末"));
            peer.Select(1);
            That(peer.Id == highest, "立即建角填空槽后名册行1仍映射原高槽角色");
            Return(peer);
            SendDelete(peer, 1, "删除末");
            peer.Expect(1, 6, [1, 0, 1, 0]);
            Roster(peer.Expect(0, 2).Body, (0, "删除中"));
            peer.Create("连续建角"); next = peer.Id;
            Roster(peer.Roster, (0, "删除中"), (1, "连续建角"));
            SendDelete(peer, 1, "删除末");
            peer.Expect(1, 6, [0, 123, 0]);
            peer.Send(8, [255, 255, 2]);
            Roster(peer.Expect(0, 2).Body, (0, "删除中"), (1, "连续建角"));
            peer.Send(3, [0]); peer.Expect(1, 3, [1]); peer.Closed();
        }
        using (var peer = Open(fixture, account))
        {
            Roster(peer.Roster, (0, "删除中"), (1, "连续建角"));
            peer.Select(1);
            That(peer.Id == next && next > highest && fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
                "重新登录从存档恢复连续建角名册、高槽映射及完整外键");
        }
        foreach (byte[] malformed in new[] { new byte[] { 0 }, new byte[] { 0, 0, 1, 0, 0, 0, 0x81 },
            Build(w => { w.Write((ushort)0); w.Write(Name("删除中")); w.Write((byte)0); }) })
        {
            using var peer = Open(fixture, account);
            string before = Persisted();
            peer.Send(6, malformed); peer.Closed();
            That(Persisted() == before, "畸形删除正文关闭原连接，不发成功或写半份存档");
        }
        using (var peer = Open(fixture, account))
        {
            foreach (string name in new[] { "删除中", "连续建角" })
            {
                SendDelete(peer, 0, name);
                peer.Expect(1, 6, [1, 0, 0, 0]);
                byte[] roster = peer.Expect(0, 2).Body;
                if (name == "删除中") Roster(roster, (1, "连续建角"));
                else Roster(roster);
            }
        }

        string Persisted() => JsonSerializer.Serialize(fixture.Query("SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name")
            .Select(row => fixture.Query("SELECT rowid,* FROM \"" + (string)row[0] + "\" ORDER BY rowid")));

        static void SendDelete(TcpPeer peer, ushort index, string name) => peer.Send(6, Build(w =>
        {
            w.Write(index); w.Write(Name(name));
        }));
        static void Return(TcpPeer peer)
        {
            peer.Send(7); peer.Expect(1, 7, [1]); peer.Expect(0, 2);
        }
        static void Roster(byte[] body, params (ushort Slot, string Name)[] expected)
        {
            That(body[0] == 2 && body[1] == expected.Length && U16(body, 7) == expected.Length &&
                U16(body, 13) == expected.Length, "删除/建角名册头三处数量一致");
            int at = 15;
            foreach (var row in expected)
            {
                int width = checked((int)U32(body, at + 2));
                That(U16(body, at) == row.Slot && DecodeName(body, at + 6, width) == row.Name,
                    "主动名册保留真实slot与CP936姓名，按提交名册顺序排列");
                int equipment = at + 21 + width;
                var summary = AssetWireChecks.ActorEntries(body, equipment);
                That(summary.Entries.Length == 5, "删除刷新及连续建角保留完整初始穿戴外观");
                at = equipment + summary.ConsumedBytes + 48;
            }
            That(body.Length == at + 10 && body[at] == 1 && body.AsSpan(at + 1).ToArray().All(value => value == 0),
                "角色名册完整尾部保持原合同，不截断投影");
        }
    }
}
