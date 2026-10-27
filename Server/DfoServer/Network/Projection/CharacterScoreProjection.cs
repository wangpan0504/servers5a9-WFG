using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;

namespace DfoServer.Network.Projection;

internal sealed record CharacterScoreNoticeResult(string Status, bool DeliveryConfirmed, ushort CharacterId,
    long CharacterRevision, string Profile, int? Score, string ScoreStatus, bool ScoreCapped, long ExpiresAt,
    bool Truncated, string DeliveryPolicy);

// 管理和玩家入口共享捕获后的展示流程；只负责计算/摘要及当前连接投递。
internal static class CharacterScoreProjection
{
    internal static CharacterScoreNoticeResult Send(GameSession session, CharacterScoreNoticeCapture capture,
        Func<CharacterInvestmentSnapshot, CharacterInvestmentResult> calculate,
        Func<CharacterInvestmentSnapshot, long?> boxExpiration,
        Func<ushort, byte[], string, Func<bool>, bool> send)
    {
        var snapshot = capture.Snapshot;
        var score = calculate(snapshot);
        if (score.CharacterId != snapshot.Equipment.CharacterId || score.CharacterRevision != snapshot.Equipment.CharacterRevision ||
            score.ObservedAt != snapshot.Equipment.ObservedAt || score.Profile is not (CharacterInvestmentScore.Profile or CharacterInvestmentScore.ChronicleProfile) || score.Status == "Invalid" || score.Score is null)
            throw new InvalidDataException("评分提示计算身份或数据无效");
        long deadline = checked(snapshot.Equipment.ObservedAt + GameSession.ScoreNoticeLifetimeSeconds);
        foreach (var item in snapshot.Worn)
            if (item.Position.Index is not (11 or 33) && item.ExpireTime > snapshot.Equipment.ObservedAt)
                deadline = Math.Min(deadline, item.ExpireTime);
        try
        {
            if (boxExpiration(snapshot) is { } expiry && expiry > snapshot.Equipment.ObservedAt)
                deadline = Math.Min(deadline, expiry);
        }
        catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or FileNotFoundException or FormatException or OverflowException)
        {
            // 缺失收集箱资源已由真实计算标Partial，摘要允许显示已知分；不将未知期限伪作永久有效。
            if (!score.Partial) throw new InvalidDataException("完整评分缺少收集箱期限", error);
        }
        var body = CharacterScorePackets.Body(capture.Character.Name, score, out bool truncated);
        string status = session.CharacterScoreNoticeStatus(capture, deadline);
        if (status == "ready")
        {
            bool queued = send((ushort)NotiPacketType.AAS_TIPS, body, "character_score_notice",
                () => session.CharacterScoreNoticeStatus(capture, deadline) == "ready");
            status = queued ? "queued" : "queue_rejected";
        }
        return new(status, false, score.CharacterId, score.CharacterRevision, score.Profile, score.Score,
            score.Status, score.ScoreCapped, deadline, truncated,
            "建设分提示；入队不代表已显示。出队写前复核身份、版本及有效期；已开始写出的帧不能撤回。失效请重新手动查询。");
    }

}
