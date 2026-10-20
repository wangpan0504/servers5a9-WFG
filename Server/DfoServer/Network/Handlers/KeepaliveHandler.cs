using System.Runtime.CompilerServices;
using DfoServer.Infrastructure;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Handlers;

// 客户端保活：CHECK_USER_CONNECTION(0x04FE，空 body) 与 PING(0x06BB) 都由客户端每 10 秒发送一次。
// 86 版只回 CHECK_USER_CONNECTION 的同 opcode 成功 ack；本服两者都回，PING 原样回显 body，
// 消除“客户端发保活、服务端不回应”的 off-by-design 隐患。
// 包体不参与解析：任何异常只记 keepalive_rejected 并跳过，绝不因为保活包体异常终止会话。
internal sealed class KeepaliveHandler(ServerLog log) : IGameCommandModule
{
    // 保活包每 10 秒一次：逐包 recv/send 日志会刷屏，由本模块每连接每 60 秒汇总一条 keepalive。
    internal static bool IsKeepalive(ushort opcode) =>
        opcode is (ushort)CmdPacketType.CHECK_USER_CONNECTION or (ushort)CmdPacketType.PING;

    // 客户端 10 秒一次；正常回包不逐条写日志，每连接每 60 秒最多一条 keepalive 汇总。
    const long SummaryIntervalMilliseconds = 60_000;
    // 连接级计数器挂在连接上下文上，随连接回收，不按 Connection id 长期持有。
    readonly ConditionalWeakTable<GameConnectionContext, Tracker> trackers = new();

    sealed class Tracker
    {
        public long? LastSummaryTick;
        public int CheckConnections;
        public int Pings;
        public int Rejected;
    }

    public void Register(GameCommandRegistry commands)
    {
        commands.Register(CmdPacketType.CHECK_USER_CONNECTION, CheckConnectionAsync, nameof(KeepaliveHandler));
        commands.Register(CmdPacketType.PING, PingAsync, nameof(KeepaliveHandler));
    }

    // 参考 86 版：同 opcode 成功 ack（单字节 0x01），不读 body，也不要求角色已就绪。
    async Task<CommandResult> CheckConnectionAsync(GameConnectionContext context, Packet request)
    {
        var tracker = trackers.GetValue(context, static _ => new Tracker());
        try
        {
            await context.Reply(CmdPacketType.CHECK_USER_CONNECTION, KeepalivePackets.SuccessAck(), "keepalive_ack");
            tracker.CheckConnections++;
        }
        catch (Exception error)
        {
            // 这里吞掉的是保活自身的发送/编码失败；连接若真的坏了，下一轮 ReadAsync 会按原语义收尾。
            Reject(context, tracker, request, "send_failed", error);
            return CommandResult.Handled;
        }
        Summarize(context, tracker);
        return CommandResult.Handled;
    }

    async Task<CommandResult> PingAsync(GameConnectionContext context, Packet request)
    {
        var tracker = trackers.GetValue(context, static _ => new Tracker());
        try
        {
            // 只回显：不解析客户端时钟/序号，也不改写任何字节。
            await context.Reply(CmdPacketType.PING, KeepalivePackets.Echo(request.Body), "keepalive_echo");
            tracker.Pings++;
        }
        catch (InvalidDataException error)
        {
            Reject(context, tracker, request, "body_too_large", error);
            return CommandResult.Handled;
        }
        catch (Exception error)
        {
            Reject(context, tracker, request, "send_failed", error);
            return CommandResult.Handled;
        }
        Summarize(context, tracker);
        return CommandResult.Handled;
    }

    void Reject(GameConnectionContext context, Tracker tracker, Packet request, string reason, Exception error)
    {
        tracker.Rejected++;
        // kind 以 _rejected 结尾，控制台按 WARN 显示；Accepted=false 供日志侧过滤。
        log.Write("keepalive_rejected", new
        {
            Connection = context.Id, Opcode = request.Opcode, Name = ((CmdPacketType)request.Opcode).ToString(),
            Length = request.Body.Length, Reason = reason, Accepted = false, Message = error.Message
        });
    }

    void Summarize(GameConnectionContext context, Tracker tracker)
    {
        long now = Environment.TickCount64;
        if (tracker.LastSummaryTick is { } last && now - last < SummaryIntervalMilliseconds) return;
        tracker.LastSummaryTick = now;
        log.Write("keepalive", new
        {
            Connection = context.Id, CheckConnections = tracker.CheckConnections, Pings = tracker.Pings,
            Rejected = tracker.Rejected
        });
    }
}
