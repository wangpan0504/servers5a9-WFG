using DfoServer.Game.Characters;
using DfoServer.Game.Session;
using DfoServer.Network.Builders;
using DfoServer.Network.Protocol;

namespace DfoServer.Network.Projection;

internal sealed class CharacterLevelProjection(CharacterStatComputer stats, QuestProjection? quests = null,
    DfoServer.GameWorld.Characters.SkillLearningCatalog? learning = null)
{
    public Task PlayerAsync(GameConnectionContext context, Character committed, string action, Func<bool> canSend) =>
        context.Notify(NotiPacketType.USERINFO,
            TownPackets.Player(committed, stats.Compute(committed), context.Session.ReadAdventureLevel(), context.Channel), action, canSend);

    public async Task RefreshAsync(GameConnectionContext context, Character committed, byte previousLevel,
        string action, Func<bool> canSend, byte? previousGrowType = null, bool restoreCompletedQuests = true,
        CharacterLearningState? previousLearning = null)
    {
        if (previousLevel is < 1 or > 95 || committed.Level < previousLevel)
            throw new InvalidDataException("等级刷新必须使用真实提交前后等级");
        if (committed.Level == previousLevel && (previousGrowType is null || previousGrowType == committed.GrowType) || !canSend())
            return;
        // 普通副本升级只追加Player；职业变化必须同步Actor，Player不携带job/grow字段。
        // 沿两端的职业重建链刷新真实分支；89B仅携带CHR基础，不重复累计装备和技能。
        var phase = context.Owner.Execute(() => context.Session.Phase);
        bool professionChanged = previousGrowType is { } grow && grow != committed.GrowType;
        if (professionChanged || phase is SessionPhase.Town or SessionPhase.MapSelect)
            await context.Notify(NotiPacketType.USERINFO,
                TownPackets.Actor(committed, context.Session.ReadPvpGrade(committed), context.Channel, adventureName: context.Session.ReadAdventureName(),
                    premiumPcRoom: context.Session.PremiumPcRoom), action + "_actor", canSend);
        await PlayerAsync(context,committed,action + "_player",canSend);
        // 图内完整19会清除技能31的临时武器，但其原生效果对象仍在，无法重新挂载。
        // 普通成长只更新实际改变的绑定；职业重建及城镇初始化仍走完整树。
        if (committed.Learning.Initialized)
        {
            if (!professionChanged && phase is SessionPhase.Dungeon or SessionPhase.Settlement)
                await SkillProjection.PublishGrowthAsync(context,
                    previousLearning ?? throw new InvalidOperationException("图内成长缺少真实提交前技能树"),
                    committed, action + "_skills", action + "_combo", canSend);
            else
                await SkillProjection.PublishAsync(context, committed, action + "_skills", action + "_combo", canSend);
        }
        if (learning is not null && previousLevel < DfoServer.Game.Skills.SkillGuidePolicy.EndLevel &&
            committed.Level >= DfoServer.Game.Skills.SkillGuidePolicy.EndLevel)
            await SkillGuideProjection.PublishAsync(context, committed, learning, canSend);
        if (quests is not null)
            await quests.RefreshAsync(context, committed, canSend, restoreCompletedQuests);
        if (committed.Level != previousLevel && canSend() && context.ChatEmoticons is { } chat)
            await chat.RefreshAsync();
    }
}
