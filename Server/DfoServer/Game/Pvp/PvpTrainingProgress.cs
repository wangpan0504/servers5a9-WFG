using DfoServer.GameWorld.Quests;

namespace DfoServer.Game.Pvp;

internal enum PvpTrainingFact { SkillsCommitted, ComboCompleted, OpponentsDefeated }

// 只解释匹配客户端已有的训练任务/通道；并非客户端33自报进度的授权入口。
// 正式调用方还须绑定训练实例、角色、选角及接取UUID，取得已提交的权威事实后才能保存结果。
internal static class PvpTrainingProgress
{
    public static bool Matches(StoryQuest quest)
    {
        int mode = quest.Id switch { 2672 or 2676 or 2678 => 0, 2674 => 1, 2673 or 2677 or 2679 => 2, _ => -1 };
        return mode >= 0 && quest.Condition == "[custom multi condition]" && quest.SubType == -1 &&
            quest.Npc == 323 && quest.CompleteNpc == -1 && quest.Definition is { Diagnostics.Length: 0 } &&
            quest.IntData.Count == 4 && quest.IntData[3] == mode && quest.IntData[0] == 1 &&
            quest.IntData[1] == (mode == 2 ? 0 : 1) && quest.IntData[2] == (mode == 2 ? 0 : 1);
    }

    internal const int ManualItemId = 10158452;
    // 015C1D60先请求1269取得秘籍；015C23A0确认持有后推进通道2，015C2886完成时移除一个。
    // 仅承认这份原始三目标及TP声明，不把其它sub reward泛化为奖励或纯展示。
    public static bool Manual(StoryQuest quest) => quest.Id==2674 && Matches(quest) &&
        DfoServer.Game.Quests.QuestCharacterReward.TryTp(quest,out var amount) && amount==2 &&
        quest.Definition!.Fields.Where(field=>field.Name=="sub reward").ToArray() is
            [{Children.Length:0,Values.Length:1} reward] && reward.Values[0] is {Type:0,Text:null,Value:ManualItemId} &&
        quest.Definition.Fields.Where(field=>field.Name=="substitutive names").ToArray() is
            [{Children.Length:0,Values.Length:3} names] &&
        names.Values.Select(value=>value.Text?.Trim()).SequenceEqual(new[]{
            "在道场中打开‘技能栏(K)’后， 点击‘自动加点’。","成功施展高级连续技。","风拳流秘笈。"});

    public static uint Initial(StoryQuest quest)
    {
        if (!Matches(quest)) throw new NotSupportedException("训练任务资源不符合已确认的三通道合同");
        return (uint)quest.IntData[0] | (uint)quest.IntData[1] << 9 | (uint)quest.IntData[2] << 18;
    }

    public static bool Valid(StoryQuest quest, uint scalar) => Matches(quest) && scalar >> 27 == 0 &&
        Enumerable.Range(0, 3).All(channel => (scalar >> (channel * 9) & 511) <= quest.IntData[channel]);

    public static bool TryApply(StoryQuest quest, uint scalar, PvpTrainingFact fact, int difficulty, out uint after)
    {
        after = scalar;
        if (!Valid(quest, scalar)) return false;
        // 015C2140 对2679额外核对训练难度2；其它模式不猜测难度门槛。
        int channel = fact switch
        {
            PvpTrainingFact.SkillsCommitted when quest.IntData[3] != 2 => 0,
            PvpTrainingFact.ComboCompleted when quest.IntData[3] != 2 => 1,
            PvpTrainingFact.OpponentsDefeated when quest.Id != 2679 || difficulty == 2 => quest.IntData[3] == 2 ? 0 : 2,
            _ => -1
        };
        if (channel < 0 || (scalar >> (channel * 9) & 511) == 0) return false;
        after = scalar & ~(511u << (channel * 9));
        return true;
    }
}
