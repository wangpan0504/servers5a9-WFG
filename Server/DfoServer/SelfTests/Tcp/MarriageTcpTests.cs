using System.Text.Json;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class MarriageTcpTests
{
    public static async Task RunAsync(ServerOptions source, string root, TcpTestResources resources)
    {
        int checks = Count;
        await using var fixture = new TcpTestFixture(source, Path.Combine(root, "marriage"), resources: resources);
        await fixture.StartAsync();
        ushort firstId, secondId;
        uint context;
        using (var first = Open(fixture, "marriagea"))
        using (var second = Open(fixture, "marriageb"))
        {
            first.Create("MarryA", 0); second.Create("MarryB", 1);
            firstId = first.Id; secondId = second.Id;
            fixture.Sql("UPDATE characters SET level=30,gold=1000000 WHERE id IN ($p0,$p1)", firstId, secondId);
            first.Select(); second.Select();
            first.Barrier(); second.Barrier();
            first.Send(1034, Request(secondId, 1));
            first.Expect(1, 1034, Ack(secondId, 1));
            second.Expect(0, 1000, Notice(firstId, 1));
            second.Send(1035, Response(firstId, 1, 1));
            second.Expect(1, 1035, AnswerAck(firstId, 1, 1));
            first.Expect(0, 1001, AnswerNotice(secondId, 1, 1));
            That(fixture.Query("SELECT count(*) FROM marriage_records") is [[0L]], "拒绝求婚不建立关系或扣费");

            first.Send(1034, Request(secondId, 1)); first.Expect(1, 1034, Ack(secondId, 1));
            second.Expect(0, 1000, Notice(firstId, 1));
            second.Send(1035, Response(firstId, 1, 0));
            second.Expect(1, 1035, AnswerAck(firstId, 1, 0));
            first.Expect(0, 1001, AnswerNotice(secondId, 1, 0));
            using var record = JsonDocument.Parse((string)fixture.Query("SELECT aggregate_json FROM marriage_records").Single()[0]);
            context = record.RootElement.GetProperty("ContextId").GetUInt32();
            byte[] information = Info(context, 1, 0);
            first.Expect(0, 1003, information); second.Expect(0, 1003, information);
            That(context != 0 && fixture.Query("SELECT count(*) FROM marriage_active_members") is [[2L]], "双方确认生成同一非零上下文与唯一关系");
            second.Send(1035, Response(firstId, 1, 0));
            Rejected(second, 1035);
            That(fixture.Query("SELECT revision FROM marriage_records") is [[1L]], "确认重放不能重复建立婚约");
            first.Send(1034, Request(secondId, 2, 0, 255)); Rejected(first, 1034);
            first.Send(1034, new byte[7]); Rejected(first, 1034);
            first.Barrier(); second.Barrier();
        }
        // 重建服务清空在线状态，复用只读资源并验证关系及每日积分由数据库恢复。
        await fixture.StopAsync(); await fixture.StartAsync("marriage-reopen");
        using (var first = Open(fixture, "marriagea"))
        using (var second = Open(fixture, "marriageb"))
        {
            first.Select(); second.Select();
            Bytes(first.MarriageInformation, Info(context, 1, 1), "订婚登录发放本游戏日的一点心意");
            Bytes(second.MarriageInformation, Info(context, 1, 1), "配偶每日点独立持久化");
            first.Send(1037, Structure(Build(w => w.Write(context))));
            var card = first.Expect(1, 1037).Body;
            That(card.Length == 98 && card[0] == 1 && U32(card, 5) == context && card[43] == 30 && card[44] == 1,
                "角色卡查询97字节真实配偶数据，保留上下文及职业字段");
            first.Send(1034, Request(secondId, 5));
            first.Expect(0, 14);
            first.Expect(1, 1034, Ack(secondId, 5));
            first.Expect(0, 1005, [5]); second.Expect(0, 1005, [5]);
            first.Expect(0, 1003, new byte[15]); second.Expect(0, 1003, new byte[15]);
            byte[] completed = new byte[30004];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(completed, 30000);
            completed[4 + 13700] = 1; // 本版城镇悬浮兼容位，不代表真实婚姻准备任务已完成。
            first.Expect(0, 574, new byte[4]); first.Expect(0, 356, completed);
            first.Expect(0, 1011, new byte[17]);
            second.Expect(0, 574, new byte[4]); second.Expect(0, 356, completed);
            second.Expect(0, 1011, new byte[17]);
            That(fixture.Query("SELECT count(*) FROM marriage_active_members") is [[0L]] &&
                fixture.Query("SELECT gold FROM characters WHERE id=$p0", firstId) is [[500000L]], "退婚扣真实费用并释放双方唯一关系");
            first.Send(1034, Request(secondId, 1)); Rejected(first, 1034);
            That(fixture.Query("SELECT count(*) FROM marriage_records") is [[1L]], "当日退婚后不能通过新求婚绕过冷却");
            first.Barrier(); second.Barrier();
        }
        That(fixture.Query("PRAGMA foreign_key_check").Length == 0 && fixture.Query("PRAGMA integrity_check") is [["ok"]],
            "婚约创建、重开和解除保持数据库完整");
        Console.WriteLine($"PASS {Count - checks} marriage TCP assertions");
    }

    static byte[] Structure(byte[] data) => [.. new byte[13], .. data];
    static byte[] Request(ushort target, byte operation, byte type = 0, byte selection = 0) =>
        Structure(Build(w => { w.Write((uint)target); w.Write(operation); w.Write(type); w.Write(selection); }));
    static byte[] Response(ushort target, byte operation, byte answer) =>
        Structure(Build(w => { w.Write((uint)target); w.Write(operation); w.Write((ushort)0); w.Write(answer); }));
    static byte[] Notice(ushort target, byte operation) => Build(w => { w.Write(0); w.Write((uint)target); w.Write(operation); w.Write((ushort)0); });
    static byte[] Ack(ushort target, byte operation) => [1, .. Notice(target, operation)];
    static byte[] AnswerNotice(ushort target, byte operation, byte answer) => [.. Notice(target, operation), answer];
    static byte[] AnswerAck(ushort target, byte operation, byte answer) => [1, .. AnswerNotice(target, operation, answer)];
    static byte[] Info(uint context, byte state, uint love) => Build(w => { w.Write(context); w.Write(state); w.Write((byte)0); w.Write(0u); w.Write(love); w.Write((byte)0); });
    static void Rejected(TcpPeer peer, ushort opcode)
    {
        byte[] body = peer.Expect(1, opcode).Body;
        That(body.Length >= 5 && body[0] == 1 && unchecked((int)U32(body, 1)) < 0, "业务拒绝保持连接并使用真实i32错误码");
    }
}
