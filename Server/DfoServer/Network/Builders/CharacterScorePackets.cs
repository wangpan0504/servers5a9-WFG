using System.Globalization;
using DfoServer.Game.Characters;

namespace DfoServer.Network.Builders;

internal static class CharacterScorePackets
{
    public static byte[] Body(string name, CharacterInvestmentResult score, out bool truncated)
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
