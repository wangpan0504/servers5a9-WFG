using DfoServer.Game.Adventure;
using Microsoft.Data.Sqlite;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class AdventureExpeditionTcpTests
{
    sealed class Clock : TimeProvider
    {
        // 只验证六小时远征，固定在UTC日零点，避免偶然跨越UTC+8六点日界。
        public DateTimeOffset Now = new(DateTime.UtcNow.Date, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        // 真实资源：属性行是线上ID、名称、组名、图片四项；255只是组别名，不能下发给客户端。
        That(resources.Expeditions.Areas[1].Groups.SequenceEqual(new[] { "[GROUP C ALLOW]" }) &&
            resources.Expeditions.Attributes["[TEMP_A_00]"] is { Id: 1, Group: "" } &&
            resources.Expeditions.Attributes["[TEMP_C_03]"] is { Id: 23 } &&
            resources.Expeditions.Attributes["[GROUP C ALLOW]"] is { Id: 255, Group: "[group C]" },
            "真实资源远征属性按线上ID解析，组别名保留255");
        var clock = new Clock();
        That(new AdventureExpeditionService(resources.Adventure, resources.Expeditions).AreaAttributes(1, clock.Now) is [>= 20 and <= 23],
            "地区1的组别名展开为TEMP_C_00至TEMP_C_03的具体ID");
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "adventure-expeditions"), resources: resources, clock: clock);
        await fixture.StartAsync();
        ushort member;
        using (var peer = Open(fixture, "expedition"))
        {
            peer.Send(441, [0]);
            var selecting = peer.Expect(1, 441).Body;
            That(selecting[0] == 1 && selecting[1] == resources.Expeditions.Areas.Count + 1 && selecting[2] == 0,
                "选角阶段查询本账号远征，包含空闲area0，不要求先进入角色");
            peer.Create("ExpActor");
            peer.Create("ExpMember"); member = peer.Id;
            Execute("UPDATE characters SET level=95,grow_type=33 WHERE id=" + member);
            peer.Select();
            That(peer.PlayerBody is [1, ..] player && U32(peer.AdventureInfo, 18) >= 1 &&
                U32(player, player.Length - 11) == U32(peer.AdventureInfo, 18) &&
                U32(player, player.Length - 7) == 0,
                "入城Player尾部携带1343同一冒险团等级，胶囊计量保持0，不能写0清空常驻效果");
            peer.Send(441, [0]);
            var available = peer.Expect(1, 441).Body;
            That(U16(available, 13) == 1, "空闲候选只使用本账号合法代表");
            var unchanged = fixture.Query("SELECT revision FROM account_adventures WHERE account='expedition'")[0][0];
            peer.Send(442, [1, 0x80, 0x51, 1, 0, 0, 0]);
            peer.Expect(1, 442, [0, 22, 0]);
            peer.Send(441, [0]);
            Bytes(peer.Expect(1, 441).Body, available, "空成员拒绝后连接可继续查询，候选及远征未改变");
            That(Equals(fixture.Query("SELECT revision FROM account_adventures WHERE account='expedition'")[0][0], unchanged) &&
                fixture.Query("SELECT count(*) FROM account_adventure_expeditions") is [[0L]],
                "空成员拒绝不写账号版本或无成员远征");
            peer.Send(442, Start(1)); peer.Expect(1, 442, [1]); peer.Expect(0, 1345); peer.Barrier();
            That(fixture.Query("SELECT character_id FROM account_adventure_expedition_members") is [[long chosen]] && chosen == member,
                "出发绑定UID，不持久化临时视图selector");
            peer.Send(1478, [0, 33]); peer.Expect(1, 1478, [0, 227, 0]); peer.Barrier();
            peer.Send(1408, [1]); peer.Expect(1, 1408, [0, 3, 0]); peer.Barrier();
            peer.Send(1407, [1]); peer.Expect(1, 1407, [1]); peer.Expect(0, 1345); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_adventure_expeditions") is [[0L]], "取消释放原远征和成员，不给奖励");
            peer.Send(442, Start(1)); peer.Expect(1, 442, [1]); peer.Expect(0, 1345);
            long storedReward = Convert.ToInt64(fixture.Query("SELECT reward FROM account_adventure_expeditions")[0][0]);
            clock.Now = clock.Now.AddHours(6);
            peer.Send(1408, [1]);
            var reward = peer.Expect(1, 1408).Body;
            That(reward.Length == 5 && U32(reward, 1) == storedReward, "领取使用冻结倍率结果，不由角色等级重算");
            peer.Expect(0, 1345); peer.Expect(0, 1343); peer.Expect(0, 1349); peer.Barrier();
            peer.Send(1408, [1]); peer.Expect(1, 1408, [0, 3, 0]); peer.Barrier();
            That(fixture.Query("SELECT count(*) FROM account_adventure_receipts WHERE action='expedition_claim'") is [[1L]],
                "重复领取只有一份永久凭据，不再给结晶");
        }
        await fixture.StopAsync();
        await fixture.StartAsync("expedition-restarted", isolatedProcess: true);
        using (var peer = Open(fixture, "expedition"))
        {
            peer.Send(441, [0]); var restored = peer.Expect(1, 441).Body;
            That(U16(restored, 13) == 1 && fixture.Query("SELECT count(*) FROM account_adventure_expeditions") is [[0L]],
                "冷启动已领取远征不复活，代表重新空闲");
            peer.Send(692, Name("ExpProbe")); peer.Expect(1, 692, [1]);
        }
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("PRAGMA foreign_key_check").Length == 0, "远征生命周期数据库完整");
        Console.WriteLine($"PASS {Count - checks} 远征查询、出发、忙碌门禁、取消及唯一领取TCP断言");
        static byte[] Start(byte area) => Build(writer =>
        {
            writer.Write(area); writer.Write(21600u); writer.Write((ushort)1); writer.Write(new byte[] { 0, 0, 1 });
        });
        void Execute(string sql) { using var db = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False;Foreign Keys=True"); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    }
}
