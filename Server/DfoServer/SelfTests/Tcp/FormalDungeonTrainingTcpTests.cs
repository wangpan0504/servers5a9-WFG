using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FormalDungeonTcpTests
{
    public static async Task RunTrainingAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        await using var fixture = new TcpTestFixture(source with { DevelopmentDungeonEntryProbe = DfoServer.Development.Dungeons.DungeonEntryProbeMode.Disabled },
            Path.Combine(root,"dungeon-training"),gm: true,resources: resources);
        await fixture.StartAsync();
        That(DfoServer.Network.Management.DevelopmentGmClient.TryExecute(fixture.Options.GmPort,"infinite-fatigue off",out _),
            "修炼场回归关闭GM无限疲劳，独立证明玩法免疲劳");
        VerifyTrainingRoom(fixture);
        VerifyTrainingItems(fixture,resources);
        PartyDungeonTcpTests.VerifyTrainingPartyEntry(fixture, resources);
        await DungeonSelectionReturnTests.VerifyTrainingPartyEntryAsync(resources.Towns, resources.Characters, resources.ItemResources, root);
    }

    static void VerifyTrainingRoom(TcpTestFixture fixture)
    {
        foreach (int remaining in new[] { 0, 17 })
        {
            ushort slot = checked((ushort)Convert.ToInt32(fixture.Query("SELECT count(*) FROM characters")[0][0]));
            using var peer = Open(fixture);
            peer.Create("Training" + remaining);
            fixture.Sql("UPDATE character_fatigue SET used=maximum-$p1,remaining=$p1 WHERE character_id=$p0", peer.Id, remaining);
            peer.Select(slot);
            string fatigue = fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0", peer.Id);
            string assets = fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid", peer.Id);
            var body = Convert.FromHexString("881300000000000001FFFF0000000000000000000000000000");
            peer.Send(15, Convert.FromHexString("88130000"));
            peer.Expect(1,15,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),1]);
            foreach (int offset in new[] { 4, 9, 16 })
            {
                byte[] invalid = (byte[])body.Clone(); invalid[offset] ^= 1;
                peer.Send(16,invalid); peer.Expect(1,16,[0,9,0,0]); peer.Barrier();
                That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[0L]],
                    "修炼场错误难度、token及任务字段不得安装普通或回放实例");
            }
            peer.Send(16,body);
            peer.Expect(0,27,new byte[42]);
            byte[] info = peer.Expect(0,28).Body;
            byte[] map = peer.Expect(0,29).Body;
            That(info.Length == 36 && U32(info) == 5000 && info[7] == 0 && info[8] == 0 && info[9] == 0 &&
                map.Length == 23 && U32(map,14) == 36250 && map[18] == 0,
                "修炼场使用真实5000迷宫和36250起点，空静态演员不伪造Boss");
            Load(peer); peer.Barrier();
            That(fixture.Query("SELECT last_attempt,next_object_key FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[1L,256L]],
                "空静态修炼场持久预留新尝试号，保持未使用的对象key高水位");
            peer.Send(37,new byte[8]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM character_dungeon_fatigue_receipts WHERE character_id=$p0",peer.Id) is [[0L]] &&
                fixture.Query("SELECT count(*) FROM character_dungeon_settlement_receipts WHERE character_id=$p0",peer.Id) is [[0L]],
                "修炼场加载及重复加载不写疲劳扣点回执，也不自动产生普通通关结算");
            peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            That(fixture.Snapshot("SELECT * FROM character_fatigue WHERE character_id=$p0",peer.Id) == fatigue &&
                fixture.Snapshot("SELECT item_uid,item_core FROM character_inventory_items WHERE character_id=$p0 ORDER BY item_uid",peer.Id) == assets,
                "零疲劳和有余额均可进入修炼场；加载、返回完整疲劳及原UID资产保持");
            // 同一角色回到普通入口后仍使用普通疲劳规则，不能被修炼场豁免泄漏。
            EntryTown(peer); SelectMap(peer);
            peer.Send(16,Convert.FromHexString("030000000000000000FFFF0000000000000000000000000000"));
            if (remaining == 0)
            {
                // 普通副本零疲劳按 dstr 49061 回业务错误22（疲劳值不足），不再是通用静默9。
                peer.Expect(1,16,[0,22,0,0]); peer.Barrier();
                That(fixture.Query("SELECT last_attempt FROM character_dungeon_state WHERE character_id=$p0",peer.Id) is [[1L]],
                    "零疲劳普通副本仍拒绝，不安装第二份尝试");
            }
            else
            {
                peer.Expect(0,28); peer.Expect(0,29); Load(peer, fatigue: false);
                peer.Expect(0,36,[140,0,156,0,0,0,0,0,0,0]); peer.Barrier();
                That(fixture.Query("SELECT used,remaining FROM character_fatigue WHERE character_id=$p0",peer.Id) is [[140L,16L]],
                    "修炼场返回后普通首房仍准确扣1点，原疲劳余额不变形");
                peer.Send(42); peer.Expect(1,42,[1]); peer.Expect(0,3,[1,(byte)peer.Id,(byte)(peer.Id >> 8),0]); peer.Expect(0,24); RefreshedQuests(peer); peer.Barrier();
            }
        }
        Console.WriteLine("PASS 修炼场原始请求、真实地图、零疲劳入场及加载返回不扣点TCP");
    }
}
