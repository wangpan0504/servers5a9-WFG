using DfoServer.Game.Characters;
using DfoServer.GameWorld.Quests;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Quests;

internal sealed partial class QuestCompletionPlan
{
    public QuestTicketSource? Coupon { get; }
    internal ActiveMainQuest? CouponActive { get; }
    public ushort RequestedSelector => Coupon is null ? RewardSelector : ushort.MaxValue;

    // 仅真实110券授权绕过目标达成及交付材料，普通34仍保留全部权威条件。
    static ActiveMainQuest PrepareCouponActive(Character expected,StoryQuest quest,QuestCatalog quests,
        QuestTicketSource coupon,DateTimeOffset now)
    {
        if (coupon.Definition.ActionIndex!=QuestTicketDefinition.CouponAction || quest.Grade!="[epic]" ||
            quest.Definition is not {AccountQuest:false} || QuestAccountPolicy.SharedCompletion(quest) ||
            QuestCycle.Daily(quest) || QuestCycle.Repeat(quest) || QuestEligibility.Completed(expected).Contains(quest.Id) ||
            !ReferenceEquals(coupon.Inventory.Before,expected.Inventory) || expected.Inventory.Find(coupon.Item.Position)!=coupon.Item ||
            coupon.UtcSeconds!=now.ToUnixTimeSeconds())
            throw new InvalidDataException("单任务券只能完成当前角色的一项未完成主线");
        var active=expected.ActiveMainQuests.SingleOrDefault(row=>row.QuestId==quest.Id);
        uint state=0;
        if (quest.Condition=="[question]")
        {
            if (active is not null && QuestQuestionProgress.OrdinaryChoice(quest,active.ConditionState,quests))
                state=active.ConditionState;
            else
            {
                if (!QuestQuestionProgress.TryCount(quest,out int count)) throw new InvalidDataException("单任务券问题选项不完整");
                int choice=Enumerable.Range(0,count).FirstOrDefault(value=>QuestQuestionProgress.OrdinaryChoice(quest,(uint)value,quests),-1);
                if (choice<0) throw new NotSupportedException("单任务券问题缺少普通后继选项，不能代选旅程跳过");
                state=(uint)choice;
            }
        }
        if (active is not null)
        {
            if (active.AcceptanceId==Guid.Empty || !QuestEligibility.CharacterRequirements(expected,quest,quests).Eligible)
                throw new InvalidDataException("单任务券的原接取身份或角色适用条件不符");
            return active with {ConditionState=state};
        }
        var eligibility=QuestEligibility.Evaluate(expected,quest,quests);
        if (!eligibility.Eligible || !eligibility.Visible)
            throw new InvalidDataException("单任务券目标当前不可领取: "+eligibility.Reason);
        // 未接任务无需单独写一次接取；临时身份仅用于本次原子领奖，不占用或覆盖真实活动槽。
        byte slot=Enumerable.Range(0,30).Select(value=>(byte)value).FirstOrDefault(value=>expected.ActiveMainQuests.All(row=>row.Slot!=value));
        return new(slot,checked((ushort)quest.Id),state) {AcceptanceId=Guid.NewGuid()};
    }
}
