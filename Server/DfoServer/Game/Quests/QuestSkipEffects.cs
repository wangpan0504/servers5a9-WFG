using DfoServer.Game.Characters;
using DfoServer.Game.Currency;
using DfoServer.Game.Inventory.Models;
using DfoServer.Game.Inventory.Services;
using DfoServer.GameWorld.Characters;
using DfoServer.GameWorld.Items;
using DfoServer.GameWorld.Quests;
using DfoServer.Game.Titles;

namespace DfoServer.Game.Quests;

internal sealed record QuestEffectPlan(Character Expected,InventoryPlan Inventory,byte EquipmentSlots,byte GrowType,
    CharacterLearningState Learning)
{
    public TitleAchievementPlan? Titles { get; init; }
    public bool Changed => Inventory.Changed || EquipmentSlots!=Expected.EquipmentSlotFlags || GrowType!=Expected.GrowType ||
        !ReferenceEquals(Learning,Expected.Learning) || Titles?.Changed == true;
}

// 跳过只兑现任务道具和永久角色状态，不授予普通装备、经验、金币或选择奖励。
internal sealed class QuestSkipEffects(ItemCatalog items,SkillLearningCatalog skills,
    GoldLimitCatalog goldLimits,EpicPieceCatalog pieces,TitleAchievementService? titles=null)
{
    public TitleCatalog? TitleCatalog => titles?.Catalog;
    public byte ProfessionTarget(Character character,StoryQuest quest) => QuestProfessionReward.Target(character,quest,skills);

    public QuestEffectPlan PrepareAchievementTitles(Character expected,IEnumerable<StoryQuest> scope,InventoryPlan inventory,DateTimeOffset now,
        IReadOnlyCollection<int> generalQuestIds)
    {
        var rewards=new List<InventoryReward>();
        foreach (var quest in scope)
        {
            if (!QuestQuickClearPlan.AchievementTitle(quest)) throw new InvalidDataException("成就券不能兑现范围外任务");
            foreach (var reward in QuestFixedRewardDefinition.Parse(quest,allowGold:true).ForJob(expected.Job,expected.GrowType))
            {
                var template=items.Get(reward.ItemId);
                if (template.Kind!=ItemKind.Equipment || template.EquipmentSlot!=13)
                    throw new NotSupportedException("[title]奖励必须引用称号装备: "+quest.Id);
                rewards.Add(new(reward.ItemId,reward.Count));
            }
        }
        var after=inventory.After;
        if (rewards.Count!=0)
        {
            var grant=InventoryRewardGrantService.TryGrantBatch(expected with { Inventory=after },rewards,items,goldLimits,pieces,now);
            if (!grant.Accepted) throw new InvalidDataException("称号奖励不能整批放入背包，未消费成就券");
            after=grant.Plan!.After;
        }
        TitleAchievementPlan? titlePlan=null;
        if (generalQuestIds.Count!=0)
        {
            var result=(titles ?? throw new NotSupportedException("general称号发放资源未装配"))
                .CompleteGeneral(expected with { Inventory=after },generalQuestIds,now);
            if (!result.Accepted || result.Plan is null) throw new InvalidDataException("general称号不能整组发放，未消费成就券");
            titlePlan=result.Plan;
            after=titlePlan.Inventory.After;
        }
        long? deadline=after.Items.Where(item=>item.Uid==0 && item.ExpireTime>0).Select(item=>(long?)item.ExpireTime).Min();
        var combined=new InventoryPlan(expected.Inventory,after,"achievement_quest_ticket",new(expected,deadline));
        return new(expected,combined,expected.EquipmentSlotFlags,expected.GrowType,expected.Learning)
            { Titles=titlePlan is null ? null : titlePlan with { Before=expected,Inventory=combined } };
    }

    public QuestEffectPlan Prepare(Character expected,IEnumerable<StoryQuest> scope,InventoryPlan inventory,
        byte slots,byte grow,CharacterLearningState learning,
        bool characterState=true)
    {
        var definitions = scope.DistinctBy(quest => quest.Id).ToArray();
        var current = expected with { GrowType=grow,Learning=learning,EquipmentSlotFlags=slots,Inventory=inventory.After };
        foreach (var quest in definitions.Where(quest => characterState && quest.RewardType=="[slot expansion]"))
            current = current with { EquipmentSlotFlags=QuestCharacterReward.EquipmentSlots(current,quest) };
        bool profession = false;
        foreach (var quest in definitions.Where(quest => characterState && quest.RewardType=="[awakening type]").OrderBy(quest => quest.Rewards[0].Value))
        {
            int stage = quest.Rewards[0].Value;
            if (stage>(current.GrowType>>4))
            {
                current = current with { GrowType=ProfessionTarget(current,quest) };
                profession = true;
            }
        }
        if (profession)
            current = current with { Learning=QuestProfessionReward.Prepare(current,current.GrowType,current.Level,current.Learning,skills,DateTimeOffset.UtcNow) };
        foreach (var quest in definitions.Where(quest => characterState && quest.RewardType=="[tp]"))
        {
            if (!QuestCharacterReward.TryTp(quest,out ushort amount)) throw new InvalidDataException("指定券TP奖励形状无效: "+quest.Id);
            current=current with { Learning=QuestCharacterReward.AddTp(current.Learning,amount) };
        }
        long now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var held=current.Inventory.Items.Where(item=>item.Position.List==InventoryListType.Main && item.Core.Kind==ItemKind.Quest && !item.IsExpired(now))
            .GroupBy(item=>item.Core.ItemId).ToDictionary(group=>group.Key,group=>group.Sum(item=>(long)item.Core.Count));
        var balances=new Dictionary<int,long>();
        foreach (var quest in InTaskOrder(definitions))
        {
            var gifts=SpecialRewards(expected,quest,quest.Definition?.AcceptanceRewards ?? []).ToArray();
            var rewards=quest.RewardType is (null or "[item]") ? SpecialRewards(expected,quest,quest.Rewards).ToArray() : [];
            var costs=SpecialCosts(quest).ToArray();
            // 新跳过按接取赠品→交付消耗→完成奖励处理；已接取的赠品已在原背包里。
            if(!expected.ActiveMainQuests.Any(row=>row.QuestId==quest.Id))
                foreach(var row in gifts) Add(row.ItemId,row.Count);
            foreach(var cost in costs) balances[cost.ItemId]=Math.Max(0,Balance(cost.ItemId)-cost.Count);
            foreach(var row in rewards) Add(row.ItemId,row.Count);
        }
        var grants=new List<InventoryReward>();
        foreach(var (id,target) in balances)
        {
            long quantity=held.GetValueOrDefault(id);
            long remove=Math.Max(0,quantity-target);
            foreach(var item in current.Inventory.Items.Where(item=>item.Position.List==InventoryListType.Main && item.Core.ItemId==id && !item.IsExpired(now))
                .OrderBy(item=>item.Position.Index).ToArray())
            {
                if(remove==0) break;
                int count=(int)Math.Min(remove,item.Core.Count);
                var deletion=InventoryDeleteService.Plan(current.Inventory,item.Position,item.Core.Value,count);
                if(!deletion.Accepted) throw new InvalidDataException("特殊任务物品不能扣除");
                current=current with {Inventory=deletion.Plan!.After}; remove-=count;
            }
            if(target>quantity) grants.Add(new(id,checked((int)(target-quantity))));
        }
        if (grants.Count>0)
        {
            var grant = InventoryRewardGrantService.TryGrantBatch(current,grants,items,goldLimits,pieces);
            if (!grant.Accepted) throw new InvalidDataException("任务特殊物品不能整批发放，需保留背包空间");
            current = current with { Inventory=grant.Plan!.After };
        }
        return new(expected,new(expected.Inventory,current.Inventory,"quest_skip_effects"),current.EquipmentSlotFlags,current.GrowType,
            current.Learning);

        long Balance(int id)
        {
            if(!balances.TryGetValue(id,out long value)) balances[id]=value=held.GetValueOrDefault(id);
            return value;
        }
        void Add(int id,int count)=>balances[id]=checked(Balance(id)+count);
    }

    IEnumerable<QuestFixedItemReward> SpecialRewards(Character expected,StoryQuest quest,IReadOnlyList<QuestRewardToken> tokens)
        => QuestFixedRewardDefinition.Parse(quest with {Rewards=tokens},allowGold:true).ForJob(expected.Job,expected.GrowType)
            .Where(row=>items.Get(row.ItemId).Kind==ItemKind.Quest);

    IEnumerable<QuestFixedItemReward> SpecialCosts(StoryQuest quest)
    {
        if(quest.Condition is not ("[seeking]" or "[seek n meet npc]") || !QuestLifecyclePlan.ValidSeeking(quest)) yield break;
        for(int at=0;at<QuestLifecyclePlan.MaterialLength(quest);at+=2)
            if(quest.IntData[at]>0 && CurrencyWallet.RewardKind(quest.IntData[at]) is null && items.Get(quest.IntData[at]).Kind==ItemKind.Quest)
                yield return new(quest.IntData[at],quest.IntData[at+1]);
    }

    // 前置优先；其余保持规划原序，不能用任务编号代表道具的获得/消耗顺序。
    static IEnumerable<StoryQuest> InTaskOrder(StoryQuest[] scope)
    {
        var remaining=scope.ToList(); var ids=scope.Select(quest=>quest.Id).ToHashSet(); var done=new HashSet<int>();
        while(remaining.Count>0)
        {
            var quest=remaining.FirstOrDefault(row=>(row.Definition is not {PrerequisiteGroups.Length:>0} definition ||
                definition.PrerequisiteGroups.Any(group=>group.Where(ids.Contains).All(done.Contains))) &&
                (row.Definition?.RequiredAnswers.Select(answer=>answer.QuestId) ?? []).Where(ids.Contains).All(done.Contains))
                ?? throw new InvalidDataException("特殊任务物品的任务前置存在循环");
            remaining.Remove(quest); done.Add(quest.Id); yield return quest;
        }
    }

}
