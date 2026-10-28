using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildApplicationTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-applications"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort masterId, targetId;
        using (var master = owners.Acquire("10000", static () => { }))
        using (var target = owners.Acquire("20001", static () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            masterId = repository.Create(master, new("CWireMaster", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            targetId = repository.Create(target, new("CWireTarget", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(92,'申请测试','c-wire',$p0,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
            VALUES(92,$p0,0,1600000000)
            """, masterId);
        await fixture.StartAsync();
        using (var master = Open(fixture))
        using (var target = Open(fixture, "20001"))
        {
            Enter(master); Enter(target);
            string assets = fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id");
            target.Send(365, []);
            target.Expect(0, 308, [0]);
            target.Send(1453, [0, .. Text("申请")]);
            using (var reader = new BinaryReader(new MemoryStream(target.Expect(1, 1453).Body)))
            {
                That(reader.ReadByte() == 1 && reader.ReadUInt32() == 1 && reader.ReadByte() == 0 && reader.ReadByte() == 1 &&
                    reader.ReadUInt32() == 92 && reader.ReadByte() == 0 && reader.ReadUInt32() == 0 &&
                    reader.ReadUInt16() == 1 && reader.ReadUInt32() == 0 && ReadText(reader) == "申请测试" &&
                    ReadText(reader) == "CWireMaster" && ReadText(reader) == "" && reader.ReadUInt32() == 0 &&
                    reader.ReadByte() == 0 && reader.ReadByte() == 1 && reader.BaseStream.Position == reader.BaseStream.Length,
                    "真实1453搜索路由按总数、页、富行完整回复，不误走1507排名");
            }
            // 真实客户端「申请」先发745，正文比347多一个u32尾字段（实测0）；两者都必须回详情，不能落错误23。
            target.Send(745, [.. Text("申请测试"), 0, 0, 0, 0]);
            byte[] detail = target.Expect(1, 745).Body;
            using (var reader = new BinaryReader(new MemoryStream(detail)))
                That(reader.ReadByte() == 1 && reader.ReadUInt32() == 92 && ReadText(reader) == "申请测试",
                    "真实745带尾字段的名称搜索回详情，不再因宽度判错落成错误23");
            target.Send(745, [.. Text("申请测试")]);
            That(target.Expect(1, 745).Body[0] == 1, "745不带尾字段时同样按名称回详情");
            target.Send(347, Text("申请测试"));
            That(target.Expect(1, 347).Body[0] == 1, "347旧宽度校验请求保持可用");
            target.Send(745, [.. Text("不存在的公会"), 0, 0, 0, 0]);
            target.Expect(1, 745, [0, 23, 0]);
            target.Send(348, [.. Text("申请测试"), .. Text("请批准")]);
            That(target.Expect(1, 348).Body[0] == 1, "真实348接收申请");
            Drain(target);
            string pendingBefore = fixture.Snapshot("SELECT * FROM guild_applications ORDER BY guild_id,character_id");
            target.Send(365, []);
            using (var reader = new BinaryReader(new MemoryStream(target.Expect(0, 308).Body)))
                That(reader.ReadByte() == 1 && reader.ReadUInt32() == 92 && ReadText(reader) == "申请测试" &&
                    ReadText(reader) == "CWireMaster" && reader.ReadUInt32() == 0 && reader.ReadUInt32() == 1 &&
                    reader.ReadByte() == 0 && ReadText(reader) == "" && reader.BaseStream.Position == reader.BaseStream.Length,
                    "真实365通过NOTI308恢复当前角色待审批行，成功正文没有同号CMD假确认");
            That(fixture.Snapshot("SELECT * FROM guild_applications ORDER BY guild_id,character_id") == pendingBefore,
                "恢复申请状态只读，不续期或改写申请记录");
            byte region = checked((byte)Convert.ToInt64(fixture.Query("SELECT region FROM guild_applications WHERE character_id=$p0", targetId)[0][0]));
            target.Send(349, Build(writer => writer.Write(92u)));
            target.Expect(1, 349, [1]);
            Drain(target);
            That(fixture.Query("SELECT count(*) FROM guild_applications") is [[0L]], "真实349取消仅删除当前角色申请");
            target.Send(365, []);
            target.Expect(0, 308, [0]);
            target.Send(348, [.. Text("申请测试"), .. Text("请批准")]);
            target.Expect(1, 348);
            Drain(target);
            master.Send(352, []);
            That(GuildApplicationTests.DecodeApplications(master.Expect(1, 352).Body) is [{ Name: "CWireTarget", Message: "请批准" }],
                "真实352读取当前申请及权威角色资料");

            master.Send(350, [1, 2, 3, 4]);
            master.Expect(1, 350, [0, 23, 0, 0, 0, 0, 0, 0]);
            That(GuildApplicationTests.DecodeApplications(master.Expect(1, 352).Body).Length == 1,
                "畸形批准也回完整零身份，并恢复仍存在的乐观移除行");
            byte[] wrongRegion = Build(writer => { writer.Write((uint)targetId); writer.Write((byte)((region + 1) % 250)); });
            master.Send(350, wrongRegion);
            master.Expect(1, 350, [0, 23, 0, .. wrongRegion]);
            That(GuildApplicationTests.DecodeApplications(master.Expect(1, 352).Body).Length == 1,
                "错误区服批准失败并保留原申请");

            byte[] denial = Build(writer => { writer.Write((uint)targetId); writer.Write((uint)region); });
            master.Send(351, denial);
            master.Expect(1, 351, [1, .. denial]);
            That(GuildApplicationTests.DecodeApplications(master.Expect(1, 352).Body).Length == 0 &&
                fixture.Query("SELECT count(*) FROM guild_applications") is [[0L]], "真实351按8B身份拒绝申请并重发空的权威列表");
            Drain(target);
            target.Send(348, [.. Text("申请测试"), .. Text("请批准")]);
            target.Expect(1, 348);
            Drain(target);
            byte[] approval = Build(writer => { writer.Write((uint)targetId); writer.Write(region); });
            fixture.Sql("CREATE TRIGGER c_application_abort AFTER INSERT ON guild_members WHEN NEW.character_id=" + targetId +
                " BEGIN SELECT RAISE(ABORT,'application TCP rollback'); END");
            master.Send(350, approval);
            master.Expect(1, 350, [0, 23, 0, .. approval]);
            That(GuildApplicationTests.DecodeApplications(master.Expect(1, 352).Body).Length == 1 &&
                fixture.Query("SELECT count(*) FROM guild_members WHERE character_id=$p0", targetId) is [[0L]],
                "真实SQL失败后回错误及恢复列表，成员插入回滚且连接继续可用");
            fixture.Sql("DROP TRIGGER c_application_abort");
            master.Send(350, approval);
            master.Expect(1, 350, [1, .. approval]);
            var actorUpdates = Drain(master);
            var targetUpdates = Drain(target);
            That(actorUpdates.Contains((1, 352)) && targetUpdates.Contains((0, 58)) && targetUpdates.Contains((0, 127)) &&
                targetUpdates.Contains((1, 1454)) && fixture.Query("SELECT grade FROM guild_members WHERE character_id=$p0", targetId) is [[4L]] &&
                fixture.Query("SELECT count(*) FROM guild_applications") is [[0L]],
                "批准成功才安装目标会籍并刷新其账号公会视图，提交普通职位4及清空申请");
            target.Send(352, []);
            target.Expect(1, 352, [0, 36, 0]);
            target.Send(1453, [2, 128, 0, 0, 0, 0]);
            target.Expect(1, 1453, [0, 23, 0]);
            Drain(target);
            That(fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id") == assets,
                "搜索及完整申请生命周期不改写角色资产或资产revision");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "申请TCP结束后隔离库及外键完整");
        Console.WriteLine($"PASS {Count - before} 公会搜索、申请取消、审批恢复及在线入会 TCP 断言");
    }

    static HashSet<(byte Kind, ushort Opcode)> Drain(TcpPeer peer)
    {
        peer.Send(156, Text("CAppBarrier"));
        var packets = new HashSet<(byte Kind, ushort Opcode)>();
        for (int guard = 0; guard < 128; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == 156)
            {
                Bytes(packet.Body, [1], "申请请求后的查重哨兵成功");
                return packets;
            }
            packets.Add((packet.Kind, packet.Opcode));
        }
        throw new InvalidOperationException("Guild application reply did not reach request barrier");
    }

    static void Enter(TcpPeer peer)
    {
        peer.Send(8, [255, 255, 2]);
        peer.Send(4, Build(writer => writer.Write((ushort)0)));
        for (int guard = 0; guard < 256; guard++)
            if (peer.Read() is { Kind: 0, Opcode: 124 }) { peer.ReadWeeklyDungeon(); return; }
        throw new InvalidOperationException("Guild application TCP did not enter town");
    }

    static byte[] Text(string text) => GuildApplicationTests.Text(text);
    static string ReadText(BinaryReader reader) => GuildApplicationTests.ReadText(reader);
}
