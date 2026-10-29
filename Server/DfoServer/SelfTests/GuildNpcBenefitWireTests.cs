using System.Buffers.Binary;
using DfoServer.Game.Guild;
using DfoServer.Network.Builders;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class GuildNpcBenefitWireTests
{
    public static void RunCore()
    {
        int before = Count;
        byte[] Request(uint action, uint npc, params uint[] ids)
        {
            byte[] body = new byte[69]; body.AsSpan(0, 13).Fill(0xB7);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(13), action);
            BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(17), npc);
            for (int at = 0; at < ids.Length; at++) BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(21 + at * 4), ids[at]);
            return body;
        }
        That(GuildNpcRequests.TryParse(Request(3, 400001062, 3002), out var purchase) && purchase is { Argument: 3002 },
            "购买福利只使用首DWORD参数，旧前缀不作为身份");
        var malformed = Request(3, 400001062, 3002); malformed[68] = 1;
        That(!GuildNpcRequests.TryParse(malformed, out _) && !GuildNpcRequests.TryParse(Request(3, 400001062), out _),
            "购买拒绝非零尾部与空福利索引");
        That(BinaryPrimitives.ReadInt32LittleEndian(GuildNpcPackets.Result(new(17, 3)).AsSpan(5)) == 17,
            "福利费用不足保留业务错误17，不改成通用非法操作");
        That(GuildNpcRequests.TryParse(Request(6, 0, 5001, 5005, 5009, 5013), out var selected) &&
            selected!.Training.SequenceEqual(new uint[] { 5001, 5005, 5009, 5013 }) &&
            GuildNpcRequests.TryParse(Request(6, 0), out var empty) && empty!.Training.Length == 0,
            "训练选择恢复最多四类，空选择用于清除选择");
        foreach (var body in new[] { Request(6, 400001064, 5001), Request(6, 0, 5001, 0, 5005),
            Request(6, 0, 5001, 5001), Request(6, 0, 5001, 5005, 5009, 5013, 5014) })
            That(!GuildNpcRequests.TryParse(body, out _), "训练拒绝错误NPC、空洞、重复和超过四项");
        var state = new GuildNpcBenefitState(null!, [new(3001, 0, 1700000300)],
            [new(6002, 5, 1700000400), new(5001, 0, 0, true)]);
        var snapshot = GuildNpcPackets.BenefitsAll([new(1004, 1700000500)], state);
        That(snapshot.Length == 1080 && BinaryPrimitives.ReadUInt32LittleEndian(snapshot) == 1004 &&
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(12)) == 3001 &&
            snapshot.AsSpan(24, 456).IndexOfAnyExcept((byte)0) < 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(480)) == 6002 &&
            BinaryPrimitives.ReadInt16LittleEndian(snapshot.AsSpan(484)) == 5 &&
            BinaryPrimitives.ReadUInt16LittleEndian(snapshot.AsSpan(486)) == 0 &&
            BinaryPrimitives.ReadInt32LittleEndian(snapshot.AsSpan(488)) == 1700000400 &&
            BinaryPrimitives.ReadUInt32LittleEndian(snapshot.AsSpan(492)) == 5001 &&
            snapshot.AsSpan(504).IndexOfAnyExcept((byte)0) < 0,
            "全量福利保留生产、公有40格和个人50格，不覆盖生产及次数对齐字段");
        Console.WriteLine($"PASS {Count - before} guild NPC benefit wire assertions");
    }
}
