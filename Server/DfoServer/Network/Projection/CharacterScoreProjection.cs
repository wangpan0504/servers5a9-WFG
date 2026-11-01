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
        Func<ushort, byte[], string, Func<bool>, bool> send, string? machineNonce = null,
        Func<CharacterInvestmentSnapshot, CombatPowerResult>? combat = null)
    {
        var snapshot = capture.Snapshot;
        var score = calculate(snapshot);
        if (score.CharacterId != snapshot.Equipment.CharacterId || score.CharacterRevision != snapshot.Equipment.CharacterRevision ||
            score.ObservedAt != snapshot.Equipment.ObservedAt || score.Profile is not (CharacterInvestmentScore.Profile or CharacterInvestmentScore.ChronicleProfile or CharacterInvestmentScore.DimensionEnergyProfile or CharacterInvestmentScore.QualityProfile or CharacterInvestmentScore.CompleteWornProfile) || score.Status == "Invalid" || score.Score is null)
            throw new InvalidDataException("评分提示计算身份或数据无效");
        long deadline = checked(snapshot.Equipment.ObservedAt + GameSession.ScoreNoticeLifetimeSeconds);
        foreach (var item in snapshot.Worn)
            if ((score.Profile == CharacterInvestmentScore.CompleteWornProfile || item.Position.Index is not (11 or 33)) && item.ExpireTime > snapshot.Equipment.ObservedAt)
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
        bool truncated = false;
        byte[] body;
        if (machineNonce is null) body = CharacterScorePackets.Body(capture.Character.Name, score, out truncated);
        else
        {
            if (score.Status != (score.Partial ? "Partial" : "Complete"))
                throw new InvalidDataException("机器评分状态不一致");
            int ttl = session.CharacterScoreRemainingMilliseconds(capture, deadline);
            if (ttl == 0) return new("expired", false, score.CharacterId, score.CharacterRevision, score.Profile,
                score.Score, score.Status, score.ScoreCapped, deadline, false, "机器缓存已到期，未入队");
            string version = score.Profile switch
            {
                CharacterInvestmentScore.DimensionEnergyProfile => "v2.2",
                CharacterInvestmentScore.QualityProfile => "v2.3",
                CharacterInvestmentScore.CompleteWornProfile => "v2.4",
                CharacterInvestmentScore.ChronicleProfile => "v2.1",
                _ => "v2"
            };
            if (combat is null)
                body = MachineCharacterScorePackets.Build(machineNonce, score.CharacterId, score.CharacterRevision,
                    "character-investment", version, score.Partial ? "partial" : "complete", score.Score, score.ScoreCapped, ttl);
            else
            {
                var power = combat(snapshot);
                if (power.CharacterId != snapshot.Equipment.CharacterId || power.CharacterRevision != snapshot.Equipment.CharacterRevision ||
                    power.ObservedAt != snapshot.Equipment.ObservedAt)
                    throw new InvalidDataException("战斗力计算身份无效");
                body = MachineCharacterScorePackets.BuildCombat(machineNonce, power.CharacterId, power.CharacterRevision,
                    power.Partial ? "partial" : "complete", power.Total, power.BaseScore, power.WhiteBp, power.YellowBp,
                    power.CritDamageBp, power.EquipmentBp, power.Grade, ttl);
            }
        }
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
