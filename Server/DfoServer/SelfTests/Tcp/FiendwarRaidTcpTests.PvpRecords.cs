using System.Buffers.Binary;
using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static void VerifyCrossPvp(TcpTestFixture fixture, TcpPeer requester, TcpPeer target, byte server)
    {
        string before = fixture.Snapshot("SELECT * FROM character_pvp_rating_claims ORDER BY 1,2");
        for (byte raw = 1; raw <= 5; raw++)
        {
            requester.Send(375, PvpRecordTcpChecks.Request(target.Id, 0, checked((byte)(raw + 1))));
            var native = requester.Expect(1, 375).Body;
            requester.Barrier();
            uint requestId = (uint)(0x12340000 + raw);
            requester.Send(821, CrossPvpRequest(server, target.Id, 10, raw, requestId));
            var frames = AntonTcpProbe.Barrier(requester, "fiendwar cross-channel pvp score");
            var body = frames.Single(packet => packet is { Kind: 0, Opcode: 763 }).Body;
            That(body.Length == 47 && BinaryPrimitives.ReadUInt32LittleEndian(body) == 0x31505652 &&
                body[4] == server && BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(5)) == target.Id &&
                body[7] == 10 && body[8] == raw && BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(9)) == requestId &&
                body[13] == 1, "真实跨频道763携带准确目标、页面及关联号");
            Bytes(body.AsSpan(14), native.AsSpan(1), "跨频道战绩来自与375相同的持久查询，不猜造统计");
            That(!frames.Any(packet => packet is { Kind: 1, Opcode: 821 or 375 }), "跨频道查询不混发普通ACK");
        }
        foreach (var body in new[]
        {
            CrossPvpRequest(unchecked((byte)(server + 1)), target.Id, 10, 5, 11),
            CrossPvpRequest(server, target.Id, 39, 5, 12),
            CrossPvpRequest(server, ushort.MaxValue, 10, 5, 13)
        })
        {
            requester.Send(821, body);
            var result = AntonTcpProbe.Barrier(requester, "fiendwar unavailable cross-channel pvp score")
                .Single(packet => packet is { Kind: 0, Opcode: 763 }).Body;
            That(result.Length == 14 && result[13] == 0 && result.AsSpan(4, 3).SequenceEqual(body.AsSpan(0, 3)) &&
                result[7] == body[4] && result[8] == body[5] && result.AsSpan(9, 4).SequenceEqual(body.AsSpan(6, 4)),
                "错服、错频道和离线目标返回准确关联的明确失败，不伪造成功数据");
        }
        foreach (var body in new[]
        {
            CrossPvpRequest(server, target.Id, 10, 0, 20), CrossPvpRequest(server, target.Id, 10, 6, 21),
            CrossPvpRequest(server, target.Id, 10, 5, 0), CrossPvpRequest(server, target.Id, 10, 5, 22)[..9],
            CrossPvpRequest(server, target.Id, 10, 5, 23).Concat(new byte[] { 0 }).ToArray()
        })
        {
            requester.Send(821, body);
            That(!AntonTcpProbe.Barrier(requester, "fiendwar malformed cross-channel pvp score")
                .Any(packet => packet.Opcode is 763 or 821), "畸形决斗请求不回通用成功");
        }
        That(before == fixture.Snapshot("SELECT * FROM character_pvp_rating_claims ORDER BY 1,2"),
            "重复成功及拒绝的跨频道查询均不生成评分结算");
    }

    static byte[] CrossPvpRequest(byte server, ushort target, byte channel, byte rawView, uint requestId) =>
        TcpPeer.Build(writer =>
        {
            writer.Write(server); writer.Write(target); writer.Write((byte)3); writer.Write(channel);
            writer.Write(rawView); writer.Write(requestId);
        });
}
