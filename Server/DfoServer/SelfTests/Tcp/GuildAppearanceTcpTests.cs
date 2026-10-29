using DfoServer.Game.Accounts;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class GuildAppearanceTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-appearance"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort masterId, memberId;
        using (var master = owners.Acquire("10000", static () => { }))
        using (var member = owners.Acquire("20001", static () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            masterId = repository.Create(master, new("LookMaster", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            memberId = repository.Create(member, new("LookMember", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix,public_funds)
            VALUES(96,'外观测试','g-wire',$p0,1600000000,4000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
            VALUES(96,$p0,0,1600000000),(96,$p1,4,1600000000)
            """, masterId, memberId);
        await fixture.StartAsync();
        using (var master = Open(fixture))
        using (var member = Open(fixture, "20001"))
        {
            Enter(master); Enter(member);
            Drain(master); Drain(member);
            string assets = fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id");
            Crafts(master, member, fixture);
            Npcs(master, member, fixture);
            That(fixture.Query("SELECT count(*) FROM guild_attendance WHERE guild_id=96") is [[2L]],
                "成功入城对两名真实成员各记录一条出勤");
            master.Send(746, []);
            using (var reader = new BinaryReader(new MemoryStream(master.Expect(1, 746).Body)))
            {
                That(reader.ReadByte() == 1 && reader.ReadUInt16() == 2 &&
                    new[] { GuildApplicationTests.ReadText(reader), GuildApplicationTests.ReadText(reader) }
                        .Order().SequenceEqual(new[] { "LookMaster", "LookMember" }.Order()) &&
                    reader.BaseStream.Position == reader.BaseStream.Length,
                    "真实746空请求返回两名自然出勤成员，仅含名称且完整消费");
            }
            master.Send(746, [0]);
            master.Expect(1, 746, [0, 34, 0]);
            That(fixture.Query("SELECT count(*) FROM guild_attendance WHERE guild_id=96") is [[2L]],
                "出勤查询及畸形查询均不新增签到事实");
            member.Send(787, [7]);
            member.Expect(1, 787, [0, 36, 0]);
            master.Send(787, [7, 0]);
            master.Expect(1, 787, [0, 34, 0]);
            master.Send(787, Mark(4));
            master.Expect(1, 787, [0, 34, 0]);
            master.Send(787, Mark(3u | 3u << 16));
            master.Expect(1, 787, [1]);
            Drain(master); Drain(member);
            That(fixture.Query("SELECT mark FROM guild_appearance WHERE guild_id=96") is [[196611L]] &&
                fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=96") is [[4000L]],
                "现代4B选择器按成长门槛开放首四图案及四背景，不套旧会标费用");
            master.Send(787, [7]);
            master.Expect(1, 787, [1]);
            That(Drain(master).Contains((0, 70)) && Drain(member).Contains((0, 70)) &&
                fixture.Query("SELECT mark FROM guild_appearance WHERE guild_id=96") is [[7L]] &&
                fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=96") is [[2000L]],
                "真实787提交会标与扣款后向同会双方投影权威公会信息");
            master.Send(787, [7]);
            master.Expect(1, 787, [1]);
            Drain(master); Drain(member);
            That(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=96") is [[2000L]],
                "同值网络重试不重复收费");
            master.Send(834, Aura(1, 0));
            var masterPackets = Drain(master);
            var memberPackets = Drain(member);
            That(!masterPackets.Contains((1, 834)) && masterPackets.Contains((0, 70)) && memberPackets.Contains((0, 70)) &&
                fixture.Query("SELECT mark,aura_mark,aura_frame FROM guild_appearance WHERE guild_id=96") is [[7L, 1L, 0L]],
                "真实834保存后发送权威信息，不伪造同号成功ACK");
            master.Send(834, Aura(uint.MaxValue, 0));
            That(!Drain(master).Contains((1, 834)) &&
                fixture.Query("SELECT aura_mark,aura_frame FROM guild_appearance WHERE guild_id=96") is [[1L, 0L]],
                "半组清除被拒绝且连接可继续处理");
            fixture.Sql("CREATE TRIGGER g_tcp_abort AFTER UPDATE ON guild_appearance BEGIN SELECT RAISE(ABORT,'appearance TCP rollback'); END");
            master.Send(787, [8]);
            // 存储异常遵循统一连接边界，不伪造可被原生解释为成功的应答。
            master.Closed();
            That(fixture.Query("SELECT public_funds FROM guilds WHERE guild_id=96") is [[2000L]] &&
                fixture.Query("SELECT mark FROM guild_appearance WHERE guild_id=96") is [[7L]],
                "真实SQL写失败时会标与此前扣款一起回滚");
            fixture.Sql("DROP TRIGGER g_tcp_abort");
            That(fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id") == assets,
                "外观、出勤均不改写真实角色资产或资产revision");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "外观TCP隔离库完整");
        Console.WriteLine($"PASS {Count - before} 公会外观及真实出勤 TCP 断言");
    }

    static byte[] Mark(uint value) => Build(writer => writer.Write(value));
    static void Npcs(TcpPeer master, TcpPeer member, TcpTestFixture fixture)
    {
        master.Send(1737, []);
        master.Expect(1, 1737, [1]);
        byte[] state = master.Expect(0, 1771).Body;
        byte[] benefits = master.Expect(0, 1773).Body;
        That(master.Expect(0, 1774).Body.Length == 16, "NPC查询包含完整训练状态");
        That(benefits.Length == 1080 && benefits.All(value => value == 0), "NPC查询发送完整空福利状态以清除旧生产倒计时");
        That(state.Length == 128 && BitConverter.ToUInt32(state, 0) == 400001060 &&
            BitConverter.ToUInt32(state, 4) == 1 && BitConverter.ToUInt32(state, 8) == 0,
            "1737真实查询补齐八个NPC，守护石初始等级与空倒计时精确消费");
        byte[] request = Build(writer =>
        {
            writer.Write(new byte[13]); writer.Write(1u); writer.Write(400001060u); writer.Write(new byte[48]);
        });
        member.Send(1738, request);
        member.Expect(1, 1738, [1, 1, 0, 0, 0, 14, 0, 0, 0]);
        That(fixture.Query("SELECT count(*) FROM guild_npc_receipts") is [[0L]],
            "普通会员不能用原生NPC请求启动公会晋阶");
        fixture.Sql("UPDATE guilds SET level=10 WHERE guild_id=96");
        master.Send(1738, request);
        master.Expect(1, 1738, [1, 1, 0, 0, 0, 22, 0, 0, 0]);
        fixture.Sql("UPDATE guilds SET public_funds=100000,guild_crystal=1 WHERE guild_id=96");
        master.Send(1738, request);
        master.Expect(1, 1738, [1, 1, 0, 0, 0, 0, 0, 0, 0]);
        var upgraded = Drain(master);
        var sharedUpgrade = Drain(member);
        That(upgraded.Contains((0, 1771)) && sharedUpgrade.Contains((0, 70)) && sharedUpgrade.Contains((0, 1771)) &&
            fixture.Query("SELECT public_funds,guild_crystal FROM guilds WHERE guild_id=96") is [[0L, 0L]] &&
            fixture.Query("SELECT level,upgrade_target,upgrade_until>0 FROM guild_npc_progress WHERE guild_id=96 AND npc_id=400001060")
                is [[1L, 2L, 1L]],
            "1738真实升级同事务扣公款与结晶，持久五天目标并通知操作者NPC及同会状态");
        fixture.Sql("UPDATE guilds SET public_funds=4000 WHERE guild_id=96");
    }
    static byte[] Aura(uint mark, uint frame) => Build(writer => { writer.Write(mark); writer.Write(frame); });
    static byte[] Text(string value) => GuildApplicationTests.Text(value);
    static HashSet<(byte Kind, ushort Opcode)> Drain(TcpPeer peer)
    {
        peer.Send(156, Text("GLookBarrier"));
        var packets = new HashSet<(byte Kind, ushort Opcode)>();
        for (int guard = 0; guard < 128; guard++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 487 }) peer.CheckGuildPassiveBuffs(packet);
            if (packet.Kind == 1 && packet.Opcode == 156)
            {
                Bytes(packet.Body, [1], "外观请求后的查重哨兵成功");
                return packets;
            }
            packets.Add((packet.Kind, packet.Opcode));
        }
        throw new InvalidOperationException("Guild appearance reply did not reach request barrier");
    }
    static void Enter(TcpPeer peer)
    {
        peer.Send(8, [255, 255, 2]);
        peer.Send(4, Build(writer => writer.Write((ushort)0)));
        for (int guard = 0; guard < 256; guard++)
            if (peer.Read() is { Kind: 0, Opcode: 124 }) { peer.ReadWeeklyDungeon(); Drain(peer); return; }
        throw new InvalidOperationException("Guild appearance TCP did not enter town");
    }
}
