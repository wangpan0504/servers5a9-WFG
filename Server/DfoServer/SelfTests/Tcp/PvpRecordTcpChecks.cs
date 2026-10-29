using static DfoServer.Testing.TestCheck;

namespace DfoServer.Testing;

internal static class PvpRecordTcpChecks
{
    internal static byte[] Request(ushort target, byte self = 1, byte view = 6, int member = -1) => TcpPeer.Build(w =>
    {
        w.Write(target); w.Write(self); w.Write(view); w.Write(member);
    });

    internal static void Score(TcpTestFixture fixture, TcpPeer requester, ushort target, bool self,
        int wins = 0, int losses = 0, int draws = 0, byte recentWins = 0, byte recentLosses = 0,
        byte recentDraws = 0, int streak = 0, int highest = 0, byte view = 6, int member = -1)
    {
        string before = Durable(fixture);
        requester.Send(375, Request(target, self ? (byte)1 : (byte)0, view, member));
        byte[] body = requester.Expect(1, 375).Body;
        using var reader = new BinaryReader(new MemoryStream(body));
        That(reader.ReadByte() == 1 && reader.ReadUInt16() == target && reader.ReadByte() == view &&
            reader.ReadByte() == (self ? 1 : 0), "375回包绑定原目标、实际本人身份及原生赛季页");
        That(reader.ReadByte() == 0 && reader.ReadByte() == 0, "未确认段位映射保留原生中性帧");
        That(reader.ReadInt32() == wins && reader.ReadInt32() == 0, "375个人胜数取真实匹配来源，不混普通或总力");
        That(reader.ReadByte() == recentWins && reader.ReadByte() == recentLosses && reader.ReadByte() == recentDraws &&
            reader.ReadInt32() == streak, "375近期胜负平及连胜按已结算来源计算");
        That(reader.ReadInt32() == 0 && reader.ReadInt32() == 0 && reader.ReadInt32() == highest,
            "375最高连胜来自真实评分回执，未确认历史次数不造值");
        if (self)
        {
            That(reader.ReadInt32() == losses && reader.ReadInt32() == draws, "本人额外负平明细真实且完整");
            for (int at = 0; at < 5; at++) That(reader.ReadInt32() == 0, "未开放模式没有借用个人或总力战绩");
            That(reader.ReadByte() == 0, "没有已确认分类键时不伪造分类行");
        }
        That(reader.BaseStream.Position == body.Length && body.Length == (self ? 63 : 34),
            "375按原生消费者完整读到末字节，他人响应不泄漏本人详细字段");
        That(before == Durable(fixture),
            "战绩查询不更改角色、物品、任务、个人评分来源及队伍评分");
        requester.Barrier();
    }

    static string Durable(TcpTestFixture fixture) => string.Join('|', new[]
    {
        "characters", "character_inventory_items", "character_inventory_slots", "inventory_audit_log",
        "mailbox_messages", "mailbox_attachments", "character_pvp_mission_facts", "character_pvp_mission_rewards",
        "character_pvp_daily_missions", "character_pvp_ratings", "character_pvp_rating_claims",
        "account_pvp_total_ratings", "account_pvp_total_rating_claims"
    }.Select(table => fixture.Snapshot($"SELECT * FROM {table} ORDER BY 1,2")));

    internal static void Reject(TcpPeer requester, byte[] body)
    {
        requester.Send(375, body);
        requester.Expect(1, 375, [0]);
        requester.Barrier();
    }
}
