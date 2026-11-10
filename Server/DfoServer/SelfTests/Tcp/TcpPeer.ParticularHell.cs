using System.Buffers.Binary;
using System.Globalization;

namespace DfoServer.Testing;

internal sealed partial class TcpPeer
{
    public uint? ParticularHellUsed { get; private set; }
    // 独立从夹具时钟和当前角色SQL取预期，不用收到的通知反推已用次数。
    void CheckParticularHellUsage(byte[] body)
    {
        int used = 0;
        if (fixture is not null)
        {
            int day = int.Parse(fixture.UtcNow.UtcDateTime.AddHours(2).ToString("yyyyMMdd", CultureInfo.InvariantCulture));
            var values = fixture.Query("""
                SELECT COALESCE(CASE WHEN u.day=$p1 THEN u.daily_used ELSE 0 END,0)
                FROM characters c LEFT JOIN character_dungeon_incount u
                  ON u.character_id=c.id AND u.account=c.account AND u.dungeon=316
                WHERE c.id=$p0
                """, Id, day).Single();
            used = Convert.ToInt32(values[0]);
        }
        TestCheck.That(body.Length == 8 && U32(body, 0) == 316 && U32(body, 4) == used,
            "魔界833严格两个DWORD，已用来自当前角色当前业务日账本，不沿用其它角色");
        ParticularHellUsed = U32(body, 4);
    }

    public void ReadParticularHellCounts(WirePacket? information = null)
    {
        var total = information ?? Expect(0, 832);
        TestCheck.That(total is { Kind: 0, Opcode: 832, Body.Length: 264 } &&
            BinaryPrimitives.ReadUInt32LittleEndian(total.Body) == 3 && total.Body.AsSpan(4).IndexOfAnyExcept((byte)0) == -1,
            "魔界832严格按匹配PVF总额度3和260字节零填充清旧表，不能伪造已用或改变包长");
        CheckParticularHellUsage(Expect(0, 833).Body);
    }
}
