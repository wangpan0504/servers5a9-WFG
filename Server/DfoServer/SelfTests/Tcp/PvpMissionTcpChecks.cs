using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PvpMissionTcpChecks
{
    internal static Dictionary<ushort, (uint Current, uint Target)> Update(TcpPeer peer, TcpTestFixture fixture)
    {
        var packet = peer.Read();
        if (packet is { Kind: 0, Opcode: 14 })
        {
            peer.PvpMissionRewardBatches++;
            var delta = packet.Body;
            That(delta.Length == 3 + 123 * U16(delta, 1) && delta[0] == 0 && U16(delta, 1) > 0,
                "日常自动奖励以正式123字节物品差量先于578发布");
            for (int at = 3; at < delta.Length; at += 123)
            {
                That(U32(delta, at + 2) == 10092640 && fixture.Query("""
                    SELECT count(*) FROM character_inventory_slots s JOIN character_inventory_items i USING(item_uid)
                    WHERE s.character_id=$p0 AND s.list_type=0 AND s.slot_index=$p1
                      AND json_extract(i.item_core,'$.ItemId')=10092640 AND json_extract(i.item_core,'$.Value')=$p2
                    """, peer.Id, U16(delta, at), U32(delta, at + 6)) is [[1L]],
                    "通知的胜利证明槽位、物品与数量必须对应已提交UID行");
            }
            packet = peer.Read();
        }
        That(packet is { Kind: 0, Opcode: 578 }, "资产差量后收到正式任务更新578");
        var body = packet.Body;
        Progress(fixture, peer.Id, body);
        Daily(peer, fixture);
        return Parse(body);
    }

    internal static void Daily(TcpPeer peer, TcpTestFixture? fixture)
    {
        DfoServer.Game.Pvp.PvpDailyMissionDay? saved = null;
        if (fixture is not null)
        {
            var rows = fixture.Query("SELECT state_json FROM character_pvp_daily_missions WHERE character_id=$p0 ORDER BY service_day DESC LIMIT 1", peer.Id);
            That(rows is [[string]], "首次选角必须已提交当天真实日常分配，不能只发临时列表");
            saved = System.Text.Json.JsonSerializer.Deserialize<DfoServer.Game.Pvp.PvpDailyMissionDay>((string)rows[0][0]);
        }
        foreach (byte grade in new byte[] { 2, 4 })
        {
            var wire = peer.Expect(0, 579).Body;
            That(wire.Length == 56 && wire[0] == grade && wire[1] == 3,
                "579按普通/总力分组，各三行18字节，所有行带真实奖励扩展");
            var ids = new HashSet<ushort>();
            for (int at = 2; at < wire.Length; at += 18)
            {
                ushort id = U16(wire, at);
                That(ids.Add(id) && U32(wire, at + 2) <= U32(wire, at + 6) && U32(wire, at + 6) > 0 &&
                    U32(wire, at + 10) == 2 && U32(wire, at + 14) > 0, "579唯一ID、进度与胜利证明扩展");
                if (saved is null) continue;
                var row = saved.Entries.Single(entry => entry.Mission.Id == id);
                That(row.Mission.Grade == grade && row.Mission.Target == U32(wire, at + 6) &&
                    row.Mission.Rewards.Single().Count == U32(wire, at + 14) &&
                    (!row.Claimed || U32(wire, at + 2) == U32(wire, at + 6)),
                    "579逐行绑定持久化任务ID、目标、奖励快照和领取完成态");
            }
        }
    }

    internal static Dictionary<ushort, (uint Current, uint Target)> Parse(byte[] body)
    {
        That(body.Length > 0 && body.Length == 1 + body[0] * 10 && body[0] is 115 or 118,
            "任务书按职业包含完整普通及总力前置链，逐行十字节且无尾部");
        var rows = new Dictionary<ushort, (uint Current, uint Target)>();
        for (int at = 1; at < body.Length; at += 10)
        {
            ushort id = U16(body, at);
            uint current = U32(body, at + 2), target = U32(body, at + 6);
            That(id is > 0 and <= 32767 && current <= int.MaxValue && target is > 0 and <= int.MaxValue &&
                rows.TryAdd(id, (current, target)), "任务书唯一正ID及有符号正目标，不将负目标判作完成");
        }
        That(rows[499].Target == 100 && rows[522].Target == 9999 && rows[523].Target == 100 &&
            rows[544].Target == 5000 && rows[800].Target == 100 && rows[823].Target == 9999 &&
            rows[824].Target == 100 && rows[845].Target == 5000 && rows[604].Target == 1 && rows[605].Target == 1 &&
            rows[606].Target == 3 && rows[607].Target == 130, "任务书目标独立核对本版MSN参数，包括道场难度与目标数的区分");
        for (ushort id = 499; id <= 563; id++) That(rows.ContainsKey(id), "普通场次、胜场和段位完整前置链");
        for (ushort id = 800; id <= 845; id++) That(rows.ContainsKey(id), "总力场次和胜场完整前置链");
        That(rows.Where(row => row.Key is 606 or 607).All(row => row.Value.Current == 0),
            "普通及总力比赛不伪造成斗兽场胜利");
        return rows;
    }

    internal static void Progress(TcpTestFixture fixture, ushort character, byte[] body)
    {
        var rows = Parse(body);
        var counts = fixture.Query($"""
            SELECT COUNT(*), COALESCE(SUM(outcome=0),0), COALESCE(SUM(rating_mode=2),0),
              COALESCE(SUM(rating_mode=2 AND outcome=0),0)
            FROM character_pvp_rating_claims WHERE character_id={character}
              AND receipt_json IS NOT NULL AND outcome<>3
            """)[0];
        That(rows[499].Current == (long)counts[0] && rows[523].Current == (long)counts[1] &&
            rows[800].Current == (long)counts[2] && rows[824].Current == (long)counts[3],
            "任务书与本角色已结算来源一致，取消不计，总力与普通范围分开");
        var facts = fixture.Query("SELECT kind,job,parameter FROM character_pvp_mission_facts WHERE character_id=$p0", character);
        byte[] jobs = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 11, 12, 13];
        foreach (var row in rows.Where(row => row.Key is >= 565 and <= 603))
        {
            int ordinal = row.Key - 565, job = jobs[ordinal % 13], index = ordinal / 13 * 4;
            bool cleared = facts.Any(fact => (long)fact[0] == 0 && (long)fact[1] == job && (long)fact[2] == index);
            That(row.Value.Current == (cleared ? 1 : 0), "连招回放与本职业独立持久事实一致");
        }
        That(rows[604].Current == (facts.Any(fact => (long)fact[0] == 1) ? 1 : 0) &&
            rows[605].Current == (facts.Any(fact => (long)fact[0] == 1 && (long)fact[2] == 2) ? 1 : 0),
            "道场回放核对实际持久难度，普通通关不冒作勇士");
    }
}
