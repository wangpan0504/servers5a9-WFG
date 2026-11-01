using System.Buffers.Binary;
using DfoServer.Network.Parsers;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class FiendwarRaidTcpTests
{
    static byte[] VerifyCounterReports(TcpTestFixture fixture, TcpPeer[] team, byte[] map,
        Dictionary<ushort, byte[]> rooms, Dictionary<ushort, byte[]> previous,
        Func<TcpPeer, byte[]>? afterSqlRollback = null)
    {
        foreach (var peer in team)
            if (previous.TryGetValue(peer.Id, out var old) && U32(old, 4) != U32(rooms[peer.Id], 4))
            {
                long before = Total(peer);
                peer.Send(FiendwarCounterRequests.Opcode, old); AntonTcpProbe.Barrier(peer, "counter stale room");
                That(Total(peer) == before, "离房、重连及房间重建后旧serial不能切回旧流或增加事实");
            }
        foreach (int index in Enumerable.Range(0, map[18]))
        {
            int at = 19 + index * 22;
            uint source = U32(map, at + 8);
            if (source is not (107013148 or 107013178)) continue;
            foreach (var peer in team)
            {
                byte[] room = rooms[peer.Id];
                long before = Total(peer);
                byte[] report = Build(writer =>
                {
                    writer.Write(FiendwarCounterRequests.Magic); writer.Write(U32(room, 4));
                    writer.Write(U32(room, 8)); writer.Write(U32(room, 12)); writer.Write(U32(room, 16));
                    writer.Write((uint)(map[0] | map[1] << 8)); writer.Write(10u);
                    writer.Write(1u); writer.Write(source); writer.Write((uint)U16(map, at + 6));
                });
                foreach (int offset in new[] { 4, 8, 12, 16, 20, 28, 32, 36 })
                {
                    var invalid = (byte[])report.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(invalid.AsSpan(offset), uint.MaxValue);
                    peer.Send(FiendwarCounterRequests.Opcode, invalid); AntonTcpProbe.Barrier(peer, "counter rejected");
                    That(Total(peer) == before, "破招拒绝错误serial/副本/地图/种子/坐标/来源身份，不创造事实");
                }
                peer.Send(FiendwarCounterRequests.Opcode, report); AntonTcpProbe.Barrier(peer, "counter first");
                That(Total(peer) == before + 1 && fixture.Query(
                    "SELECT applied FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7006", peer.Id) is [[long applied]] && applied == before + 1,
                    "正式一人或组队成员的原生报告每包只推进一次事实及称号水位");
                peer.Send(FiendwarCounterRequests.Opcode, report); AntonTcpProbe.Barrier(peer, "counter replay");
                That(Total(peer) == before + 1, "同流同序号重发不会重复增加事实");
                BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(24), 100);
                peer.Send(FiendwarCounterRequests.Opcode, report); AntonTcpProbe.Barrier(peer, "counter gap");
                That(Total(peer) == before + 2, "序号跳跃只记本包一件，不把序号差当成计数");
                if (afterSqlRollback is not null)
                {
                    That(team.Length == 1, "破招晚SQL故障仅作用于一名单人团队成员");
                    string stream = fixture.Snapshot("SELECT * FROM character_fiendwar_counter_stream WHERE character_id=$p0", peer.Id);
                    string facts = fixture.Snapshot("SELECT * FROM character_fiendwar_title_facts WHERE character_id=$p0 ORDER BY quest_id", peer.Id);
                    string progress = fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", peer.Id) +
                        fixture.Snapshot("SELECT * FROM character_title_progress WHERE character_id=$p0 ORDER BY quest_id", peer.Id);
                    var failed = (byte[])report.Clone();
                    BinaryPrimitives.WriteUInt32LittleEndian(failed.AsSpan(24), 1000);
                    fixture.Sql($"""
                        CREATE TRIGGER fiendwar_counter_late_failure AFTER UPDATE ON character_fiendwar_title_facts
                        WHEN NEW.character_id={peer.Id} AND NEW.quest_id=7006 AND
                          (SELECT sequence FROM character_fiendwar_counter_stream WHERE character_id=NEW.character_id)=1000
                        BEGIN SELECT RAISE(ABORT,'fiendwar counter late rollback'); END;
                        """);
                    try
                    {
                        peer.Send(FiendwarCounterRequests.Opcode, failed); peer.Closed();
                        That(stream == fixture.Snapshot("SELECT * FROM character_fiendwar_counter_stream WHERE character_id=$p0", peer.Id) &&
                            facts == fixture.Snapshot("SELECT * FROM character_fiendwar_title_facts WHERE character_id=$p0 ORDER BY quest_id", peer.Id) &&
                            progress == fixture.Snapshot("SELECT revision FROM characters WHERE id=$p0", peer.Id) +
                                fixture.Snapshot("SELECT * FROM character_title_progress WHERE character_id=$p0 ORDER BY quest_id", peer.Id),
                            "真实破招在流水位及事实更新后的SQL失败一起回滚原流、全部事实、称号进度和角色revision");
                    }
                    finally { fixture.Sql("DROP TRIGGER fiendwar_counter_late_failure"); }
                    previous[peer.Id] = failed;
                    byte[] freshMap = afterSqlRollback(peer);
                    return VerifyCounterReports(fixture, team, freshMap, rooms, previous);
                }
                if (source == 107013178)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(24), 101);
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(28), 2);
                    BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(32), 109035530);
                    peer.Send(FiendwarCounterRequests.Opcode, report); AntonTcpProbe.Barrier(peer, "counter light passive");
                    That(Total(peer) == before + 3, "真实光幻影资源只在仍有光本体的本房间允许上报");
                }
                previous[peer.Id] = report;
            }
        }
        return map;
        long Total(TcpPeer peer) => Convert.ToInt64(fixture.Query(
            "SELECT coalesce(sum(total),0) FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7006", peer.Id)[0][0]);
    }

    static void VerifyDeadCounterBody(TcpTestFixture fixture, TcpPeer[] team, uint source, ushort key,
        Dictionary<ushort, byte[]> previous)
    {
        if (source is not (107013148 or 107013178)) return;
        foreach (var peer in team)
        {
            That(previous.TryGetValue(peer.Id, out var prior), "本体死亡负例来自本房间已经成功的自然破招报告");
            var report = (byte[])prior!.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(24), 2000);
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(28), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(32), source);
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(36), key);
            string before = fixture.Snapshot("SELECT * FROM character_fiendwar_counter_stream WHERE character_id=$p0", peer.Id) +
                fixture.Snapshot("SELECT * FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7006", peer.Id);
            peer.Send(FiendwarCounterRequests.Opcode, report); AntonTcpProbe.Barrier(peer, "counter dead body");
            That(before == fixture.Snapshot("SELECT * FROM character_fiendwar_counter_stream WHERE character_id=$p0", peer.Id) +
                fixture.Snapshot("SELECT * FROM character_fiendwar_title_facts WHERE character_id=$p0 AND quest_id=7006", peer.Id),
                "本体真实死亡后即使房间和资源身份未变也不得再增加破招事实或消费流水位");
        }
    }
}
