using System.Text.Json;
using DfoServer.Development.Dungeons;
using DfoServer.Game.Inventory.Models;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunDurabilityAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DungeonEntryProbeMode.Disabled },
            Path.Combine(root, "durability"), resources: resources);
        await fixture.StartAsync();
        ItemCore? retained = null;
        long retainedUid = 0;
        using (var peer = Open(fixture))
        {
            peer.Create("WearTcp");
            peer.Select();
            Unchanged(peer, "城镇");
            EntryTown(peer);
            Enter(peer);
            Unchanged(peer, "加载中");
            Load(peer);
            That(ReadWorn(peer.Id).Core.Durability >= 2, "使用真实CHR的初始满耐久武器，不改写资源或模拟服务规则");
            for (int report = 0; report < 2; report++)
            {
                var before = ReadWorn(peer.Id);
                peer.Send(48, [12]);
                peer.SendBarrier();
                byte[] delta = peer.Expect(0, 14).Body;
                peer.Expect(1, 1347, [1]);
                var after = ReadWorn(peer.Id);
                That(delta.Length == 126 && delta[0] == 3 && U16(delta, 1) == 1 && U16(delta, 3) == 12 &&
                    U32(delta, 5) == before.Core.ItemId && U16(delta, 14) == before.Core.Durability - 1,
                    "48只返回一行完整装备差量，之后直接到屏障，不造ACK48、NOTI179或Actor/Player重建");
                That(after.Uid == before.Uid && after.Core == before.Core with { Durability = (ushort)(before.Core.Durability - 1) } &&
                    after.Revision == before.Revision + 1 && after.Gold == before.Gold && after.Audits == before.Audits + 1,
                    "连续相同的有效报告各提交一次损耗，UID、非耐久核心和金币不变，revision与审计同步增长");
            }
            string assets = Assets();
            foreach (byte slot in new byte[] { 0, 255 }) peer.Send(48, [slot]);
            peer.Barrier();
            That(Assets() == assets, "空槽及非普通装备不写库或额外发包");
            var worn = ReadWorn(peer.Id);
            retained = worn.Core;
            retainedUid = worn.Uid;
        }
        using (var peer = Open(fixture))
        {
            var packets = peer.Select();
            var worn = ReadWorn(peer.Id);
            var player = packets.Single(packet => packet.Kind == 0 && packet.Opcode == 2 && packet.Body[0] == 1).Body;
            var weapon = AssetWireChecks.PlayerEntries(player, 126).Entries.Single(item => item.Slot == 12);
            That(worn.Uid == retainedUid && worn.Core == retained && weapon.Durability == retained!.Durability,
                "断线重选从已提交存档恢复同一实例和耐久，本人Player穿戴列表不恢复为模板最大值");
            string assets = Assets();
            peer.Send(48, []);
            peer.Closed();
            That(Assets() == assets, "畸形报告关闭连接而不是返回伪成功，不改变资产");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0,
            "耐久报告与重选后的文件库完整性通过");

        string Assets() => fixture.Snapshot("SELECT id,revision,gold FROM characters ORDER BY id") +
            fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items ORDER BY item_uid") +
            fixture.Snapshot("SELECT count(*) FROM inventory_audit_log");

        void Unchanged(TcpPeer peer, string phase)
        {
            string before = Assets();
            peer.Send(48, [12]);
            peer.Barrier();
            That(Assets() == before, $"{phase}耐久报告不扣减、不回ACK或库存通知");
        }

        (long Uid, ItemCore Core, long Revision, long Gold, long Audits) ReadWorn(ushort character)
        {
            var row = fixture.Query("""
                SELECT i.item_uid,i.item_core,c.revision,c.gold FROM characters c
                JOIN character_inventory_slots s ON s.character_id=c.id
                JOIN character_inventory_items i ON i.item_uid=s.item_uid
                WHERE c.id=$p0 AND s.list_type=3 AND s.slot_index=12
                """, character).Single();
            return ((long)row[0], JsonSerializer.Deserialize<ItemCore>((string)row[1])!, (long)row[2], (long)row[3],
                (long)fixture.Query("SELECT count(*) FROM inventory_audit_log")[0][0]);
        }
    }
}
