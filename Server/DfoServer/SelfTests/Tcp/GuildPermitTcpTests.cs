using System.Buffers.Binary;
using DfoServer.Game.Accounts;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.Persistence;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

/// <summary>
/// 真实网络覆盖完整邀请、双方入会、显示文本与公会ID顺序。
/// 拒绝不建会不扣费，提交后用独立读库核对资产与成员行。
///
/// 本组是**双方同时在线**（被邀请方当场在城镇）的投递路径；创建者先下线、被邀请方之后入城的**顺序模式**
/// 由 `guild-permit-sequential` 组覆盖。
/// </summary>
internal static class GuildPermitTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-permit"), resources: resources);
        var repository = new CharacterRepository(fixture.Database);
        var owners = new AccountOwners();
        using (var creatorOwner = owners.Acquire("10000", () => { }))
        using (var inviteeOwner = owners.Acquire("20001", () => { }))
        {
            // 建会花费来自 PVF（[guild create cost] = 300000），创建者必须带够金币上线。
            var starter = InventoryRewardGrantService.CreateStarter(resources.Characters.Get(0));
            repository.Create(creatorOwner, new("GuildMaster", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills,
                Inventory = starter with { Gold = 1_000_000 }
            });
            repository.Create(inviteeOwner, new("GuildInvitee", resources.Towns.Room, resources.Towns.InitialReturn)
            {
                Level = 60, AcceptedStoryLevel = 1, Skills = resources.Characters.Get(0).Skills, Inventory = starter
            });
        }
        await fixture.StartAsync();
        using (var creator = Open(fixture))
        using (var invitee = Open(fixture, "20001"))
        {
            Enter(creator);
            Enter(invitee);
            long creatorId = Convert.ToInt64(fixture.Query("SELECT id FROM characters WHERE name='GuildMaster'")[0][0]);
            long inviteeId = Convert.ToInt64(fixture.Query("SELECT id FROM characters WHERE name='GuildInvitee'")[0][0]);

            // 真实流程：客户端先做名称查重与介绍校验（0x2E5 本身不带公会名/地址/介绍），再自动发认证邀请。
            creator.Send(0x009C, Text("认证公会"));
            creator.Expect(1, 0x009C, [0x01]);
            creator.Send(0x02E3, Text("介绍文本"));
            creator.Expect(1, 0x02E3, [0x01]);

            // ① 拒绝分支：被邀请方关窗/点拒绝（含客户端 60 秒超时自动发的 0）。
            creator.Send(0x02E5, Permit(2, "GuildInvitee"));
            invitee.Expect(0, 0x02AD, [.. Text("认证公会"), 2, .. Text("GuildMaster"), .. Text("介绍文本")]);
            creator.Expect(1, 0x02E5, [0x01]);
            invitee.Send(0x02E6, [0x00]);
            invitee.Expect(1, 0x02E6, [0x00, 0x22, 0x00]);
            creator.Expect(0, 0x02AE, [0x00]);
            That(fixture.Query("SELECT count(*) FROM guilds") is [[0L]] &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", creatorId) is [[1_000_000L]],
                "★ 拒绝分支不建会也不扣钱（没有公会行、创建者金币未变）");

            // ② 接受分支：建会提交在创建者会话里完成，金币差额与三个通知都发到创建者连接。
            creator.Send(0x02E5, Permit(2, "GuildInvitee"));
            invitee.Expect(0, 0x02AD, [.. Text("认证公会"), 2, .. Text("GuildMaster"), .. Text("介绍文本")]);
            creator.Expect(1, 0x02E5, [0x01]);
            invitee.Send(0x02E6, [0x01]);
            invitee.Expect(1, 0x02E6, [0x01]);
            uint inviteeGuild = GuildPermitTests.Entered(invitee.Expect(0, 58).Body, checked((ushort)inviteeId), "认证公会", "GuildInvitee");
            var inviteeActor = invitee.Expect(0, 2).Body;
            invitee.Expect(0, 127, [4]);
            GuildPermitTests.Information(invitee.Expect(0, 70).Body, "认证公会", "GuildMaster", 2, inviteeActor[3], online: 2);
            That(Gold(creator.Expect(0, 14).Body) == 700_000,
                "★ 创建者连接收到建会后的金币差额（1,000,000 − 300,000），不被邀请方连接代发");
            creator.Expect(0, 0x02AE, [0x01]);
            var created = creator.Expect(0, 0x47).Body;
            uint guildId = GuildPermitTests.Created(created, "认证公会");
            var entered = creator.Expect(0, 0x3A).Body;
            That(GuildPermitTests.Entered(entered, checked((ushort)creatorId), "认证公会", "GuildMaster") == guildId &&
                inviteeGuild == guildId, "创建与双方入会投影使用同一实际公会ID");
            var creatorActor = creator.Expect(0, 2).Body;
            creator.Expect(0, 127, [1]);
            GuildPermitTests.Information(creator.Expect(0, 70).Body, "认证公会", "GuildMaster", 2, creatorActor[3], online: 2, canInvite: 1);
            That(fixture.Query("SELECT name||':'||address||':'||master_character_id FROM guilds") is [[string guild]] &&
                guild == "认证公会:认证公会:" + creatorId,
                "★ 独立读库：公会行用查重过的名称建（客户端没有域名输入框，地址回退为公会名），会长是创建者");
            That(fixture.Query("SELECT guild_id||':'||character_id||':'||grade FROM guild_members ORDER BY character_id") is
                [[string master], [string joiner]] && master == guildId + ":" + creatorId + ":0" &&
                joiner == guildId + ":" + inviteeId + ":0",
                "★ 会长与认可认证的被邀请角色同事务写入成员行（两行、职位都是 0）");
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", creatorId) is [[700_000L]],
                "★ 独立读库确认建会花费只扣了一次");
            That(fixture.Query("SELECT gold FROM characters WHERE id=$p0", inviteeId) is [[0L]],
                "被邀请方不承担建会花费");
        }
        await fixture.StopAsync();
        That(fixture.Query("PRAGMA integrity_check") is [["ok"]] && fixture.Query("SELECT count(*) FROM pragma_foreign_key_check") is [[0L]],
            "TCP 建会认证全过程数据库与外键完整");
        Console.WriteLine($"PASS {Count - before} 公会建会认证 TCP 断言");
    }

    /// <summary>
    /// 选角并消费整段入城序列，读到入城最后一条 `NOTI 124 ENTER_GAMEWORLD_COMPLETE` 为止。
    ///
    /// 这里**不**调用共享夹具的 <c>TcpPeer.Select()</c>：本分支的入城序列在 `USER_STATE` 之前多发了两个
    /// `NOTI 183` 拍卖行服务通知（`TownProjection.EnterAsync` 的探测包），共享期望表尚未跟上——
    /// 现有 `mailbox` 组在同一次 `--selftest-tcp` 运行里以完全相同的断言
    /// 「mailbox initializes after account assets and before town state」失败。建会认证只依赖服务端侧的
    /// Ready + Town，因此本组按哨兵消费入城序列，不复制也不修改共享期望表。
    /// </summary>
    static void Enter(TcpPeer peer, ushort slot = 0)
    {
        peer.Send(8, [255, 255, 2]);                                 // 客户端选角前请求名册
        peer.Send(4, Build(writer => writer.Write(slot)));           // (1,4) SELECT_CHARACTER
        for (int guard = 0; guard < 256; guard++)
        {
            var packet = peer.Read();
            if (packet.Kind == 0 && packet.Opcode == 124)            // (0,124)：EnterAsync 的最后一条
            {
                // 入城完成后固定跟720空正文与721五槽；本组不校验内容，只消费以免污染后续应答。
                peer.ReadWeeklyDungeon();
                return;
            }
        }
        throw new InvalidOperationException("入城序列没有在预期条数内结束");
    }

    /// <summary>UPDATE_ITEM_LIST 差量正文里的主背包槽 0（金币）。</summary>
    static uint Gold(byte[] body)
    {
        int count = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(1));
        for (int index = 0; index < count; index++)
        {
            int offset = 3 + 123 * index;
            if (BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(offset)) == 0)
                return BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(offset + 6));
        }
        throw new InvalidOperationException("金币差额里没有主背包槽 0");
    }

    /// <summary>请求正文 = u32 字节长度 + CP936 文本（查重 / 介绍校验）。</summary>
    static byte[] Text(string text)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        byte[] body = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, bytes.Length);
        bytes.CopyTo(body, 4);
        return body;
    }

    /// <summary>请求正文 = u8 大区序号 + u32 名称长度 + CP936 角色名（实机样本 `02 05 00 00 00 "admin"`）。</summary>
    static byte[] Permit(byte region, string name)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(name);
        byte[] body = new byte[5 + bytes.Length];
        body[0] = region;
        BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(1), bytes.Length);
        bytes.CopyTo(body, 5);
        return body;
    }
}
