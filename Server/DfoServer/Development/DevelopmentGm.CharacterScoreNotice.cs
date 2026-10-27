using System.Globalization;
using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Builders;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    internal static CharacterInvestmentResult CalculateCharacterScore(CharacterInvestmentSnapshot snapshot, ItemResources resources)
    {
        var metadata = resources.CharacterInvestmentMetadata;
        return CharacterInvestmentScore.Calculate(snapshot, resources.Items.Get,
            (card, index, kind, slot) =>
            {
                var definition = metadata.ReadEnchantment(card, index);
                return definition.ValidIndex && definition.Targets.Contains((kind, slot));
            }, metadata.ValidateRandomTitle, metadata.ReadSet, resources.Items.Talisman, resources.Items.Rune,
            resources.CollectBoxes.Get, (item, type) =>
            {
                var original = resources.RandomOptions.Resolve(item);
                if (original?.Pools.Any(pool => pool.Groups.Any(group => group.Any(option => option.Type == type && option.Weight > 0))) == true)
                    return true;
                var changed = resources.RandomOptions.ResolveChange(item);
                return changed?.Types.Any(row => row.Weight > 0 && row.Groups.Any(group => group.Weight > 0 &&
                    group.Options.Any(option => option.Type == type && option.Weight > 0))) == true;
            });
    }

    static string SendCharacterScoreNotice(DevelopmentRequest request, GmNoticeSender? sender, ItemResources? resources, ServerLog? log)
    {
        if (resources is null || sender is null) throw new InvalidOperationException("评分提示只支持已装配资源的在线服务");
        var target = sender.FindScoreTarget(request.Name) ?? throw new InvalidDataException("评分提示目标不在线或未选角");
        var result = PrepareCharacterScoreNotice(request, target,
            snapshot => CalculateCharacterScore(snapshot, resources),
            snapshot => snapshot.CollectBox.SelectedBox == 0 ? null : resources.CollectBoxes.Get(snapshot.CollectBox.SelectedBox)
                .ResolveExpiration(snapshot.CollectBox.Expiration(snapshot.CollectBox.SelectedBox)));
        // 同已有GM入口只记投递尝试；不声称客户端已接收或显示，不在发送泵owner守卫里写日志。
        log?.Write("gm_character_score_notice", new { target.ConnectionId, result.Status, result.CharacterId,
            result.CharacterRevision, result.ExpiresAt, result.Truncated });
        return Ok(result);
    }

    internal sealed record CharacterScoreNoticeResult(string Status, bool DeliveryConfirmed, ushort CharacterId,
        long CharacterRevision, string Profile, int? Score, string ScoreStatus, bool ScoreCapped, long ExpiresAt,
        bool Truncated, string DeliveryPolicy);

    internal static CharacterScoreNoticeResult PrepareCharacterScoreNotice(DevelopmentRequest request, ChatProbeConnection target,
        Func<CharacterInvestmentSnapshot, CharacterInvestmentResult> calculate, Func<CharacterInvestmentSnapshot, long?> boxExpiration)
    {
        request.Validate();
        if (request.Action != DevelopmentAction.CharacterScoreNotice || target.GuardedProbe is null)
            throw new InvalidDataException("评分提示缺少受控投递入口");
        var capture = target.Session.CaptureCharacterScoreNotice(request.Account, request.Name);
        var snapshot = capture.Snapshot;
        var score = calculate(snapshot);
        if (score.CharacterId != snapshot.Equipment.CharacterId || score.CharacterRevision != snapshot.Equipment.CharacterRevision ||
            score.ObservedAt != snapshot.Equipment.ObservedAt || score.Profile != CharacterInvestmentScore.Profile || score.Status == "Invalid" || score.Score is null)
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
        var body = CharacterScoreNoticeBody(capture.Character.Name, score, out bool truncated);
        string status = target.Session.CharacterScoreNoticeStatus(capture, deadline);
        if (status == "ready")
        {
            bool queued = target.GuardedProbe((ushort)NotiPacketType.AAS_TIPS, body, "gm_character_score_notice",
                () => target.Session.CharacterScoreNoticeStatus(capture, deadline) == "ready");
            status = queued ? "queued" : "queue_rejected";
        }
        return new(status, false, score.CharacterId, score.CharacterRevision, score.Profile, score.Score,
            score.Status, score.ScoreCapped, deadline, truncated,
            "管理触发提示；入队不代表已显示。出队写前复核身份、版本及有效期；已开始写出的帧不能撤回。失效请重新手动查询。");
    }

    internal static byte[] CharacterScoreNoticeBody(string name, CharacterInvestmentResult score, out bool truncated)
    {
        string heading = score.Partial ? "不完整：仅已知建设分" : "本服角色建设分";
        string cap = score.ScoreCapped ? "（含封顶项）" : "";
        string text = $"{heading}{cap}\n分数：{score.Score?.ToString(CultureInfo.InvariantCulture) ?? "无法计算"}\n" +
            $"版本：{score.Profile}\n角色：{name}\n采样秒：{score.ObservedAt.ToString(CultureInfo.InvariantCulture)}，修订：{score.CharacterRevision.ToString(CultureInfo.InvariantCulture)}\n";
        var modules = score.Parts.GroupBy(part => part.Module).Select(group =>
            $"{group.Key}={group.Sum(part => part.Score).ToString(CultureInfo.InvariantCulture)}{(group.Any(part => part.Partial) ? "?" : "")}");
        text += string.Join("；", modules) + "\n建设投入参考，非伤害预测。完整明细请查询GM JSON。";
        return ServerNoticePackets.Build("角色建设分", text, out truncated);
    }
}
