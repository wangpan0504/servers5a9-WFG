using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Infrastructure;
using DfoServer.Network;
using DfoServer.Network.Projection;

namespace DfoServer.Development;

internal static partial class DevelopmentGm
{
    internal static CharacterInvestmentService CharacterScoreService(ItemResources resources) =>
        new(resources.Items, resources.CharacterInvestmentMetadata, resources.CollectBoxes, resources.RandomOptions);

    internal static CharacterInvestmentResult CalculateCharacterScore(CharacterInvestmentSnapshot snapshot, ItemResources resources) =>
        CharacterScoreService(resources).Calculate(snapshot);

    static string SendCharacterScoreNotice(DevelopmentRequest request, GmNoticeSender? sender, ItemResources? resources, ServerLog? log)
    {
        if (resources is null || sender is null) throw new InvalidOperationException("评分提示只支持已装配资源的在线服务");
        var target = sender.FindScoreTarget(request.Name) ?? throw new InvalidDataException("评分提示目标不在线或未选角");
        var service = CharacterScoreService(resources);
        var result = PrepareCharacterScoreNotice(request, target, service.Calculate, service.BoxExpiration);
        // 同已有GM入口只记投递尝试；不声称客户端已接收或显示，不在发送泵owner守卫里写日志。
        log?.Write("gm_character_score_notice", new { target.ConnectionId, result.Status, result.CharacterId,
            result.CharacterRevision, result.ExpiresAt, result.Truncated });
        return Ok(result);
    }

    internal static CharacterScoreNoticeResult PrepareCharacterScoreNotice(DevelopmentRequest request, ChatProbeConnection target,
        Func<CharacterInvestmentSnapshot, CharacterInvestmentResult> calculate, Func<CharacterInvestmentSnapshot, long?> boxExpiration)
    {
        request.Validate();
        if (request.Action != DevelopmentAction.CharacterScoreNotice || target.GuardedProbe is null)
            throw new InvalidDataException("评分提示缺少受控投递入口");
        var capture = target.Session.CaptureCharacterScoreNotice(request.Account, request.Name);
        return CharacterScoreProjection.Send(target.Session, capture, calculate, boxExpiration, target.GuardedProbe);
    }
}
