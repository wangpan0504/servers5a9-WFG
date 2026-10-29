using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class GuildManagementTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-management"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        ushort masterId, targetId;
        using (var master = owners.Acquire("10000", static () => { }))
        using (var target = owners.Acquire("20001", static () => { }))
        {
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            masterId = repository.Create(master, new("BWireMaster", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
            targetId = repository.Create(target, new("BWireTarget", resources.Towns.Room, resources.Towns.InitialReturn)
                { Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter }).Id;
        }
        fixture.Sql("""
            INSERT INTO guilds(guild_id,name,address,master_character_id,created_at_unix)
            VALUES(91,'管理测试','b-wire',$p0,1600000000);
            INSERT INTO guild_members(guild_id,character_id,grade,joined_at_unix)
            VALUES(91,$p0,0,1600000000)
            """, masterId);
        await fixture.StartAsync();
        using (var master = Open(fixture))
        using (var target = Open(fixture, "20001"))
        {
            Enter(master); Enter(target);
            string assets = fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id");
            master.Send(151, Target(2, "BWireTarget"));
            master.Expect(1, 151, [1]);
            var invitation = target.Expect(0, 147).Body;
            using (var reader = new BinaryReader(new MemoryStream(invitation)))
                That(Text(reader) == "管理测试" && Text(reader) == "BWireMaster" && Text(reader) == "" &&
                    reader.ReadUInt32() == 1 && reader.ReadUInt16() == 1 && reader.ReadByte() == 0 &&
                    reader.ReadUInt32() == 91 && Text(reader) == "BWireMaster" && reader.ReadUInt32() == 300 &&
                    reader.BaseStream.Position == reader.BaseStream.Length,
                    "真实151→147邀请窗口字段独立解码完整，末尾容量300");
            target.Send(152, [0]);
            target.Send(156, Dstr("BRejectBar"));
            target.Expect(1, 156, [1]);
            master.Expect(0, 148, [0, .. Dstr("BWireTarget")]);
            That(fixture.Query("SELECT count(*) FROM guild_members") is [[1L]],
                "拒绝不回会触发已加入提示的成功152，后继查重确认连接继续可用");

            master.Send(151, Target(2, "BWireTarget"));
            master.Expect(1, 151, [1]);
            target.Expect(0, 147);
            target.Send(152, [1]);
            target.Expect(1, 152, [1]);
            master.Expect(0, 148, [1, .. Dstr("BWireTarget")]);
            Barrier(master);
            var joined = Barrier(target);
            That(joined.Contains((0, 58)) && joined.Contains((0, 127)) && joined.Contains((0, 70)) &&
                fixture.Query("SELECT grade FROM guild_members WHERE character_id=$p0", targetId) is [[4L]],
                "接受入会事务落普通职位4，原连接接收归属、职级和管理页刷新");
            master.Send(154, Dstr("公会新公告"));
            ExpectAfterGuildRefresh(master, 154, [1]);
            var masterRefresh = Barrier(master); var targetRefresh = Barrier(target);
            That(!masterRefresh.Contains((0, 127)) && !targetRefresh.Contains((0, 127)) &&
                !masterRefresh.Contains((0, 2)) && !targetRefresh.Contains((0, 2)),
                "普通公告刷新只更新公会模型，不能重放本人职位或Actor重置已打开的界面");
            That(fixture.Query("SELECT notice FROM guilds WHERE guild_id=91") is [["公会新公告"]],
                "普通公告原生请求实际持久保存");
            master.Send(153, Target(0, ""));
            master.Expect(1, 153, [0, 87, 0]);
            master.Send(153, [2]);
            master.Expect(1, 153, [0, 3, 0]);
            fixture.Sql("""
                INSERT INTO character_guild_npc_benefits(character_id,guild_id,joined_at,effect,benefit_id,remaining,deadline,selected)
                SELECT character_id,guild_id,joined_at_unix,'guild trainer attack raid buff',5001,0,0,1
                FROM guild_members WHERE character_id=$p0
                """, targetId);
            master.Send(153, Target(2, "BWireTarget"));
            master.Expect(1, 153, [1, 2, .. Dstr("管理测试"), 0, .. Dstr("BWireTarget")]);
            Barrier(master);
            var cleared = Barrier(target);
            That(cleared.Contains((0, 151)) && fixture.Query("SELECT count(*) FROM guild_membership_cooldowns") is [[0L]],
                "踢人确认完整且目标即时收到清会籍通知，被踢不增加主动退会限制");
            That(fixture.Query("SELECT count(*) FROM character_guild_npc_benefits WHERE character_id=$p0", targetId) is [[0L]],
                "真实踢人同时删除个人福利，立即重新受邀也不继承解锁或选择");

            master.Send(151, Target(2, "BWireTarget"));
            master.Expect(1, 151, [1]);
            target.Expect(0, 147);
            target.Send(152, [1]);
            target.Expect(1, 152, [1]);
            master.Expect(0, 148, [1, .. Dstr("BWireTarget")]);
            Barrier(master); Barrier(target);
            target.Send(153, Target(0, ""));
            target.Expect(1, 153, [1, 1, .. Dstr("管理测试"), 0, .. Dstr("BWireTarget")]);
            Barrier(target); Barrier(master);
            That(fixture.Query("SELECT count(*) FROM guild_membership_cooldowns WHERE character_id=$p0", targetId) is [[1L]],
                "主动退出写本角色次日06点限制并清除成员行");
            master.Send(303, []);
            master.Expect(1, 303, [1]);
            That(Barrier(master).Contains((0, 151)) && fixture.Query("SELECT count(*) FROM guilds WHERE guild_id=91") is [[0L]],
                "唯一会长可以真实解散，客户端收到清理通知");
            That(fixture.Snapshot("SELECT id,gold,revision FROM characters ORDER BY id") == assets,
                "全部管理请求均不修改角色资产及资产revision");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] &&
            fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]], "成员管理结束后库与外键完整");
        Console.WriteLine($"PASS {Count - before} 公会普通邀请、退出、踢人及解散 TCP 断言");
    }

    static HashSet<(byte Kind, ushort Opcode)> Barrier(TcpPeer peer)
    {
        peer.Send(156, Dstr("BManageBar"));
        var packets = new HashSet<(byte Kind, ushort Opcode)>();
        for (int guard = 0; guard < 80; guard++)
        {
            var packet = peer.Read();
            if (packet is { Kind: 0, Opcode: 487 }) peer.CheckGuildPassiveBuffs(packet);
            if (packet.Kind == 1 && packet.Opcode == 156)
            {
                Bytes(packet.Body, [1], "公会管理后继查重哨兵成功");
                return packets;
            }
            packets.Add((packet.Kind, packet.Opcode));
        }
        throw new InvalidOperationException("公会管理刷新未到达后继请求哨兵");
    }

    static void ExpectAfterGuildRefresh(TcpPeer peer, ushort opcode, byte[] body)
    {
        for (int guard = 0; guard < 80; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 1 && packet.Opcode == opcode)
            {
                Bytes(packet.Body, body, "公会管理确认正文");
                return;
            }
            if (packet.Kind == 0 && packet.Opcode == 744)
            {
                peer.CheckGuildDungeon(packet.Body);
                continue;
            }
            if (packet is { Kind: 0, Opcode: 487 }) { peer.CheckGuildPassiveBuffs(packet); continue; }
            if (packet is { Kind: 0, Opcode: 488 })
            {
                Bytes(packet.Body, Convert.FromHexString("01FC040000"), "普通成员刷新仅重置已投影的守护石1276");
                continue;
            }
            That(packet.Kind == 0 && packet.Opcode is 2 or 58 or 70 or 127 or 151 ||
                packet.Kind == 1 && packet.Opcode is 140 or 141 or 744 or 1454,
                "只允许跨连接成员刷新晚于前一连接哨兵到达");
        }
        throw new InvalidOperationException("公会管理确认未到达");
    }

    static void Enter(TcpPeer peer)
    {
        peer.Send(8, [255, 255, 2]);
        peer.Send(4, Build(writer => writer.Write((ushort)0)));
        for (int guard = 0; guard < 256; guard++)
            if (peer.Read() is { Kind: 0, Opcode: 124 }) { peer.ReadWeeklyDungeon(); Barrier(peer); return; }
        throw new InvalidOperationException("公会管理TCP未完成入城序列");
    }
    static string Text(BinaryReader reader) => CharacterName.Encoding.GetString(reader.ReadBytes(reader.ReadInt32()));
    static byte[] Dstr(string text) => Build(writer => { byte[] bytes = CharacterName.Encoding.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes); });
    static byte[] Target(byte region, string name) => [region, .. Dstr(name)];
}
