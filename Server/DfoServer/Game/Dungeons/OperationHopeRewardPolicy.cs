using System.Collections.Concurrent;
using DfoServer.Game.Inventory.Models;
using DfoServer.GameWorld.Dungeons;
using DfoServer.GameWorld.Items;

namespace DfoServer.Game.Dungeons;

// 用户确认的本服规则：职业权重12、隐藏26/100、黄金碎片240/10000。
// 职业名册与exclude武器共同构成装备池，后者不吃职业加权；封装物品来自独立资源对。
internal sealed class OperationHopeRewardPolicy(OperationHopeRewardCatalog catalog,ItemCatalog items)
{
    public const string Rule="operation-hope-server-compat-v1";
    readonly ConcurrentDictionary<(int,int),DungeonItemDropPool> pools=new();
    readonly DungeonItemDropPool sealing=new([new DungeonWeightedDropItem(items.Get(catalog.SealingItemId),1)]);

    internal DungeonItemDropPool Pool(byte job,byte grow)
    {
        var key=(Job:(int)job,Grow:grow&15);
        if (key.Grow==0 || !catalog.Professions.ContainsKey(key)) key=(-1,0);
        return pools.GetOrAdd(key,pair=>
        {
            var preferred=pair.Item1<0 ? [] : catalog.Professions[pair].ToHashSet();
            return new(catalog.Equipment.Concat(catalog.UnbiasedWeapons).Select(id=>new DungeonWeightedDropItem(items.Get(id),
                preferred.Contains(id)?catalog.ProfessionWeight:1)));
        });
    }

    public ItemCore Create(byte job,byte grow,Func<int,int> draw,DateTimeOffset now)
    {
        int Sample(int bound)
        {
            int result=draw(bound);
            return result>=0 && result<bound ? result : throw new InvalidDataException("希望装备抽样越界");
        }
        var pool=Pool(job,grow);
        return DungeonDropCreation.Create(pool.Select(Sample(pool.TotalWeight)),1,Sample,now);
    }

    public ItemCore? CreateSealingItem(Func<int,int> draw,DateTimeOffset now)
    {
        int roll=draw(10000);
        if (roll is <0 or >=10000) throw new InvalidDataException("希望封装物品抽样越界");
        return roll<catalog.SealingPermyriad
            ? DungeonDropCreation.Create(items.Get(catalog.SealingItemId),catalog.SealingItemCount,draw,now) : null;
    }

    public DungeonDropGenerationPlan Extend(DungeonDropGenerationPlan plan,DungeonEntryPlan entry,DungeonPlannedMonster monster)
    {
        if (entry.Hope is not { } hope || !hope.Catalog.Mazes[hope.MazeOrdinal].HiddenBoss ||
            monster.MonsterId!=107001276 || !monster.CanReportDeath || monster.IsFriendly || monster.IsScriptedBoss || monster.AiCharacter is not null)
            return plan;
        var profession=entry.HopeRewardProfession ?? throw new InvalidDataException("希望掉落缺本人冻结职业");
        return new(plan.ResourceSource+";"+Rule+";"+OperationHopeRewardCatalog.PoolPath,
            plan.Trials.Add(new(1,Pool(profession.Job,profession.Grow),1,1,ProbabilityDenominator:1,Attempts:2))
                .Add(new(catalog.SealingPermyriad,sealing,catalog.SealingItemCount,catalog.SealingItemCount,ProbabilityDenominator:10000)),
            plan.ReferenceQuality,plan.Amplification);
    }
}
