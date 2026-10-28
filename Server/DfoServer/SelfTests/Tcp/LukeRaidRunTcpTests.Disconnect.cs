using System.Diagnostics;
using System.Text.Json;
using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static partial class LukeRaidRunTcpTests
{
    internal static async Task DisconnectCandidatesAsync(TcpTestFixture fixture, List<TcpPeer> peers,
        List<WirePacket>[] started, Func<string, TcpPeer> reconnect, int dungeon)
    {
        var ids = peers.Select(peer => peer.Id).ToArray();
        string Assets() => fixture.Snapshot("SELECT * FROM character_inventory_items ORDER BY character_id,item_uid") +
            fixture.Snapshot("SELECT * FROM inventory_audit_log ORDER BY audit_id") +
            fixture.Snapshot("SELECT * FROM character_raid_reward_receipts ORDER BY character_id,instance_id,phase,kind");
        string original = Assets();
        await CloseAsync(7);
        var candidate = fixture.LukeRejoinCandidate(ids[7]);
        That(candidate is { RaidSlot: 7, Squad: 2, PartySlot: 3, Phase: 0, OldOwnerReleased: true } &&
            candidate.PartyId == U16(started[7].Single(frame => frame.Opcode == 9).Body, 4),
            "真实socket关闭在原队伍拆除前捕获双层槽位，旧owner释放后才记录同实例候选");
        foreach (var peer in peers.Take(7)) Drain(peer);
        using var fresh = reconnect("lukestart11");
        var selected = fresh.Select();
        That(selected.All(frame => frame.Opcode is not (835 or 594 or 587)), "新登录不收到Luke断线提示或假恢复状态");
        var observed = Drain(fresh);
        That(observed.All(frame => frame.Opcode is not (835 or 594 or 587)), "进入Town后的延后通知仍不冒充恢复");
        fresh.Send(668, Build(writer => writer.Write(candidate!.Key)));
        var rejected = ReadRejoinRejection(fresh);
        Bytes(rejected.Single(frame => frame is { Kind: 1, Opcode: 668 }).Body, [0, 19, 0], "内部候选不开放668成功");
        That(rejected.All(frame => frame.Opcode is not (835 or 594 or 587)) && fixture.LukeRejoinCandidate(ids[7]) == candidate,
            "拒绝不消费原候选或重装Battle/分队");
        That(Assets() == original, "捕获和重登拒绝零门票、库存、审计、领奖写入");

        // 同一实际Runtime沿原15/16进入Loading后断线，不在测试中建立Run。
        var first = peers.Take(4).ToArray();
        var captain = first.Single(peer => peer.Id == Leader(started[0].Single(frame => frame.Opcode == 9).Body));
        Enter(first, captain, dungeon);
        string afterEntry = Assets();
        // 另一分队仍在Town，但原Battle已经有在途Run。
        await CloseAsync(6);
        That(fixture.LukeRejoinCandidate(ids[6]) is null && Assets() == afterEntry,
            "另一分队Run仍在途时Town断线也拒绝候选");
        await CloseAsync(3);
        That(fixture.LukeRejoinCandidate(ids[3]) is null && Assets() == afterEntry,
            "真实Loading断线不保留Town重返候选，也不再次扣费或发奖");
        foreach (var peer in peers.Take(3)) Drain(peer);
        foreach (int index in new[] { 0, 1, 2, 4, 5 }) await CloseAsync(index);
        That(ids.All(id => fixture.LukeRejoinCandidate(id) is null), "末位团成员清理同步清空全部原候选，不留死Battle引用");
        That(Assets() == afterEntry, "全团断线清理无重复资产或奖励事务");

        List<WirePacket> ReadRejoinRejection(TcpPeer peer)
        {
            var rows = new List<WirePacket>(); peer.SendBarrier();
            for (int index = 0; index < 256; index++)
            {
                var packet = peer.Read();
                if (packet is { Kind: 1, Opcode: 1347 })
                { Bytes(packet.Body, [1], "668拒绝后的真实连接屏障"); return rows; }
                if (packet is { Kind: 1, Opcode: 668 }) Bytes(packet.Body, [0, 19, 0], "仅本请求消费确切668拒绝，不能接成功01");
                else Validate(packet);
                rows.Add(packet);
            }
            throw new InvalidOperationException("668拒绝通知超过有界预算");
        }

        async Task CloseAsync(int index)
        {
            var wait = Stopwatch.StartNew(); int connection = -1;
            while (wait.Elapsed < TimeSpan.FromSeconds(10) && connection < 0)
            {
                var rows = Events().Where(row => row.GetProperty("Kind").GetString() == "selected" &&
                    row.GetProperty("Detail").GetProperty("Character").GetUInt16() == ids[index]).ToArray();
                if (rows.Length > 0) connection = rows[^1].GetProperty("Detail").GetProperty("Connection").GetInt32();
                else await Task.Delay(10);
            }
            That(connection >= 0, "选角日志确定要关闭的原连接，不能按角色猜当前连接");
            peers[index].Dispose(); wait.Restart();
            while (wait.Elapsed < TimeSpan.FromSeconds(10))
            {
                if (Events().Any(row => row.GetProperty("Kind").GetString() == "disconnected" &&
                    row.GetProperty("Detail").GetProperty("Connection").GetInt32() == connection)) return;
                await Task.Delay(10);
            }
            throw new InvalidOperationException("等待指定原连接完整清理超时");
        }
        JsonElement[] Events()
        {
            try { return fixture.Events().ToArray(); }
            catch (JsonException) { return []; } // 正在刷最后一行时重读，不以睡眠认定清理已发生。
        }
    }
}
