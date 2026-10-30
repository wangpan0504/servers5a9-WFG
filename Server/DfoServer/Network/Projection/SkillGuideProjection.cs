using DfoServer.Game.Characters;
using DfoServer.Game.Skills;
using DfoServer.GameWorld.Characters;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal static class SkillGuideProjection
{
    public static async Task PublishAsync(GameConnectionContext context, Character character,
        SkillLearningCatalog catalog, Func<bool>? canSend = null)
    {
        byte count = SkillGuidePolicy.DisplayCount(character.Level);
        // 使用职业完整名册，包含稍后学习、另一方案及装备赋予的技能，避免只覆盖当前快捷栏。
        foreach (var ids in catalog.SkillIds(character.Job).Chunk(byte.MaxValue))
            await context.Notify(NotiPacketType.OFFER_SKILL_GUIDE, SkillPackets.Guide(ids, count),
                "skill_guide_initialize", canSend);
    }
}
