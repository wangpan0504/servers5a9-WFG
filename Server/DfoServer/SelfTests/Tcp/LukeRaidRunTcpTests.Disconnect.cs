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
        That(selected.All(frame => frame.Opcode is not (594 or 587)), "新登录在668前不伪造已恢复状态");
        var observed = selected.Concat(Drain(fresh)).ToArray();
        That(observed.Count(frame => frame is { Kind: 0, Opcode: 835, Body.Length: 7 } && U32(frame.Body) == candidate!.Key) == 1,
            "旧owner释放后同角色收到恰一次指向原团的完整835");
        That(observed.All(frame => frame.Opcode is not (594 or 587)), "835只是候选提示，不先绑定原团");
        fresh.Send(668, Build(writer => writer.Write(candidate!.Key + 1)));
        var rejected = ReadRejoinResponse(fresh, [0, 19, 0]);
        That(rejected.All(frame => frame.Opcode is not (594 or 587)) && fixture.LukeRejoinCandidate(ids[7]) == candidate,
            "错误团key拒绝且不消费原候选、不重装Battle或分队");
        That(Assets() == original, "捕获和错误重返零门票、库存、审计、领奖写入");
        fresh.Send(668, Build(writer => writer.Write(candidate!.Key)));
        _ = ReadRejoinResponse(fresh, [1]);
        peers[7] = fresh;
        That(Assets() == original && fixture.Query("SELECT count(*) FROM character_raid_runs WHERE character_id=$p0", ids[7]) is [[1L]],
            "正确668恢复原团和原分队，不重扣票、不增加原角色入场");
        fresh.Send(668, Build(writer => writer.Write(candidate!.Key)));
        _ = ReadRejoinResponse(fresh, [1]);
        That(Assets() == original, "重复668确认同一绑定，不重复写入资产");

        // 同一实际Runtime沿原15/16进入Loading后断线，不在测试中建立Run。
        var first = peers.Take(4).ToArray();
        var captain = first.Single(peer => peer.Id == Leader(started[0].Single(frame => frame.Opcode == 9).Body));
        Enter(first, captain, dungeon);
        string afterEntry = Assets();
        // 另一分队仍在Town，但原Battle已经有在途Run。
        await CloseAsync(6);
        That(fixture.LukeRejoinCandidate(ids[6]) is { OldOwnerReleased: true } other && other.Instance == candidate!.Instance && Assets() == afterEntry,
            "另一分队Run在途不剥夺候战成员的原团重返资格");
        await CloseAsync(3);
        That(fixture.LukeRejoinCandidate(ids[3]) is { OldOwnerReleased: true } loading && loading.Instance == candidate!.Instance && Assets() == afterEntry,
            "真实Loading断线保留回候战资格，不恢复旧房、不重复扣费或发奖");
        foreach (var peer in peers.Take(3)) Drain(peer);
        foreach (int index in new[] { 0, 1, 2, 4, 5, 7 }) await CloseAsync(index);
        That(ids.All(id => fixture.LukeRejoinCandidate(id) is { OldOwnerReleased: true } saved && saved.Instance == candidate!.Instance),
            "末位团成员断线后保留同Battle五分钟候选，不因全团离线提前丢失资格");
        That(Assets() == afterEntry, "全团断线清理无重复资产或奖励事务");

        List<WirePacket> ReadRejoinResponse(TcpPeer peer, byte[] expected)
        {
            var rows = Drain(peer);
            Bytes(rows.Single(row => row is { Kind: 1, Opcode: 668 }).Body, expected,
                "本请求恰一次确切668成功或拒绝，不把任意响应当恢复");
            return rows;
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
