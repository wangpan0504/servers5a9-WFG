using static DfoServer.Testing.TestCheck;
using static DfoServer.Testing.TcpPeer;

namespace DfoServer.Testing;

internal static class PvpMissionTcpChecks
{
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
