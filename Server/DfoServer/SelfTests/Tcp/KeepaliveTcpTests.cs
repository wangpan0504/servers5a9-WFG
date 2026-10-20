using System.Text.Json;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

/// <summary>
/// 保活包线上回归（真 socket，无需真实客户端）：客户端每 10 秒发 <c>CMD 0x04FE CHECK_USER_CONNECTION</c>（空 body）
/// 与 <c>CMD 0x06BB PING</c>（17 字节客户端时钟/序号），服务端必须回同 opcode 的 <c>Kind=1</c> 帧：
/// CHECK 回单字节成功 ack（86 版 BuildSuccessAck 的字节形状），PING 原样回显 body。
/// 同时锁住日志边界：两个 opcode 不再记 <c>unimplemented</c>，也不再逐包记 <c>recv</c>/<c>send</c>，
/// 正常保活只按连接每 60 秒汇总一条 <c>keepalive</c>；超单帧上限的 PING 只记 <c>keepalive_rejected</c> 且不断线。
/// 期望帧形状来自 86 版 Chat 实现与本仓库已有 ack 形状，不调用服务端 Parser/Builder 反算。
/// </summary>
internal static class KeepaliveTcpTests
{
    const ushort CommandCheckUserConnection = 0x04FE;
    const ushort CommandPing = 0x06BB;
    // 17 字节，同远端日志采样（客户端时钟/序号，服务端不解析）。
    static readonly byte[] PingBody = Convert.FromHexString("BA7C70890AD7233C010203040506070809");

    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        // 保活与角色进度无关：登录后停在选角阶段即可，不需要建立角色或入城。
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "keepalive"), resources: resources);
        await fixture.StartAsync();
        // 超过 GameFrame.Build 单帧 body 上限（65520）的保活体：服务端不回显、只记拒绝，连接仍可继续保活。
        byte[] oversize = new byte[65521];
        using (var peer = Open(fixture, "keepalive"))
        {
            peer.Send(CommandCheckUserConnection);
            var checkAck = peer.Expect(1, CommandCheckUserConnection);
            Bytes(checkAck.Body, [0x01], "CHECK_USER_CONNECTION 空 body 回同 opcode 单字节成功 ack");

            That(PingBody.Length == 17, "PING 采样体固定 17 字节");
            peer.Send(CommandPing, PingBody);
            var pong = peer.Expect(1, CommandPing);
            That(pong.Body.Length == PingBody.Length && pong.Body.AsSpan().SequenceEqual(PingBody),
                "PING 17 字节回显与服务端收到字节完全一致");
            Bytes(pong.Body, PingBody, "PING 回显逐字节一致");

            peer.Send(CommandPing);
            var emptyPong = peer.Expect(1, CommandPing);
            That(emptyPong.Body.Length == 0, "空 body PING 回显空 body，不断线");

            peer.Send(CommandPing, oversize);
            peer.NoFrame(800, "超单帧上限的 PING 不回显", (1, CommandPing));
            peer.Send(CommandCheckUserConnection);
            Bytes(peer.Expect(1, CommandCheckUserConnection).Body, [0x01], "异常 body 之后同一连接仍能正常收到保活 ack");
        }

        await fixture.StopAsync();
        var events = fixture.Events();
        bool IsKeepaliveOpcode(int opcode) => opcode is CommandCheckUserConnection or CommandPing;
        var details = events.Select(row => (Kind: row.GetProperty("Kind").GetString(), Detail: row.GetProperty("Detail"))).ToArray();

        That(!details.Any(row => row.Kind == "unimplemented" && IsKeepaliveOpcode(row.Detail.GetProperty("Opcode").GetInt32())),
            "CHECK_USER_CONNECTION/PING 不再记 unimplemented");
        That(!details.Any(row => row.Kind is "recv" or "send" && IsKeepaliveOpcode(row.Detail.GetProperty("Opcode").GetInt32())),
            "保活包不逐条记 recv/send，避免日志每 10 秒刷屏");
        var summaries = details.Where(row => row.Kind == "keepalive").ToArray();
        That(summaries.Length == 1, "3 次 CHECK + 3 次 PING 只汇总一条 keepalive（每连接 60 秒节流）");
        // 首包立即登记一条，随后 60 秒窗口内其余 5 包不再写日志；窗口计数为写入时的快照。
        That(summaries[0].Detail.GetProperty("Connection").GetInt32() == 1 &&
             summaries[0].Detail.GetProperty("CheckConnections").GetInt32() == 1 &&
             summaries[0].Detail.GetProperty("Pings").GetInt32() == 0,
            "keepalive 在首包登记，窗口内其余包不重复刷屏");
        That(details.Any(row => row.Kind == "keepalive_rejected" &&
                row.Detail.GetProperty("Opcode").GetInt32() == CommandPing &&
                row.Detail.GetProperty("Length").GetInt32() == oversize.Length &&
                row.Detail.GetProperty("Reason").GetString() == "body_too_large"),
            "超限 PING 记 keepalive_rejected(body_too_large) 且不写 unimplemented");
    }
}
