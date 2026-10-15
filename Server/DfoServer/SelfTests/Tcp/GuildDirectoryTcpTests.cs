using System.Buffers.Binary;
using DfoServer.Game.Characters;
using static DfoServer.Testing.TcpPeer;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

/// <summary>
/// 创建公会窗口两次查重的线上契约：应答走 CMD 通道且与请求同号（可用 `01`，不可用 `00 + u16LE 错误码`）。
/// 名称码 0x6A 长度 / 0x6B 占用 / 0x6C 非法字符；地址码 0x6E / 0x6F / 0x70。
/// </summary>
internal static class GuildDirectoryTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int before = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "guild-directory"), resources: resources);
        await fixture.StartAsync();
        // 查重不需要已选角：Handler 只要求连接属于当前账号（Owner），因此登录后直接验证线上契约。
        using var peer = Open(fixture);

        // ① 未占用名称：客户端据此弹「可以使用的公会名」并解锁创建按钮。
        peer.Send(0x009C, Text("NewGuild"));
        peer.Expect(1, 0x009C, [0x01]);

        // ② 已占用名称：库里已有同名公会，唯一索引命中。
        fixture.Sql("INSERT INTO guilds(name,address,master_character_id,created_at_unix) VALUES('BUG公会','bug-guild',1,1700000000)");
        peer.Send(0x009C, Text("BUG公会"));
        peer.Expect(1, 0x009C, [0x00, 0x6B, 0x00]);

        // ③ 地址查重（0x9D）：占用与可用各一。
        peer.Send(0x009D, Text("bug-guild"));
        peer.Expect(1, 0x009D, [0x00, 0x6F, 0x00]);
        peer.Send(0x009D, Text("new-guild"));
        peer.Expect(1, 0x009D, [0x01]);

        // ④ 长度违规与非法字符（只按控制字符拒绝）。
        peer.Send(0x009C, Text(new string('a', 15)));
        peer.Expect(1, 0x009C, [0x00, 0x6A, 0x00]);
        peer.Send(0x009C, Text("坏\u0001名"));
        peer.Expect(1, 0x009C, [0x00, 0x6C, 0x00]);

        // ⑤ 创建窗口「介绍」校验（0x2E3）：成功才让客户端继续自动发认证邀请（0x2E5）。
        peer.Send(0x02E3, Text("2233"));
        peer.Expect(1, 0x02E3, [0x01]);
        peer.Send(0x02E3, Text(""));
        peer.Expect(1, 0x02E3, [0x01]);
        peer.Send(0x02E3, Text("坏\u0001介绍"));
        peer.Expect(1, 0x02E3, [0x00, 0x6C, 0x00]);

        That(fixture.Query("SELECT count(*) FROM guilds").Single()[0] is 1L,
            "★ 两次查重不写库：公会表里只剩夹具自己插入的那一行");
        Console.WriteLine($"PASS {Count - before} 公会创建窗口查重 TCP 断言");
    }

    /// <summary>请求正文 = u32 字节长度 + CP936 文本。</summary>
    static byte[] Text(string text)
    {
        byte[] bytes = CharacterName.Encoding.GetBytes(text);
        byte[] body = new byte[4 + bytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(body, bytes.Length);
        bytes.CopyTo(body, 4);
        return body;
    }
}
