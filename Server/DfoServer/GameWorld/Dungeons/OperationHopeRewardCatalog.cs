using System.Collections.Immutable;
using DfoServer.GameWorld.Items;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 仅保存本版资源；数值的兼容解释由玩法策略明确承担，不宣称还原原服概率。
internal sealed class OperationHopeRewardCatalog
{
    public const string PoolPath = "etc/operationhopedroplist.etc";
    public const string ExcludePath = "etc/operationhopedroplist_exclude.etc";
    public ImmutableDictionary<(int Job,int Grow),ImmutableArray<int>> Professions { get; }
    public ImmutableArray<int> Equipment { get; }
    public ImmutableArray<int> UnbiasedWeapons { get; }
    public int ProfessionWeight { get; }
    public int HiddenNumerator { get; }
    public int HiddenDenominator { get; }
    public int SealingPermyriad { get; }
    public int SealingItemId { get; }
    public int SealingItemCount { get; }

    public OperationHopeRewardCatalog(PvfArchive pvf,ItemCatalog items)
    {
        var info = new PvfTokenDocument(pvf.ReadTokens(OperationHopeCatalog.ResourcePath),pvf.ResolveString);
        var bonus = Values(info,"current job bonus drop");
        var hidden = Values(info,"hidden boss appear rate");
        var sealing = Values(info,"result box sealing item prob");
        if (bonus is not [>0 and <=10000] || hidden is not [>=0,>0] || hidden[0]>hidden[1] ||
            sealing is not [>=0 and <=10000])
            throw new InvalidDataException("代号希望兼容概率资源不完整或越界");
        ProfessionWeight=bonus[0];HiddenNumerator=hidden[0];HiddenDenominator=hidden[1];SealingPermyriad=sealing[0];
        var document = new PvfTokenDocument(pvf.ReadTokens(PoolPath),pvf.ResolveString);
        if (!document.Diagnostics.IsEmpty || document.Sections.Any(s=>s.Name is not ("operation hope grow type" or "operation hope sealing item")))
            throw new InvalidDataException("代号希望职业装备名册结构无效");
        var professions=ImmutableDictionary.CreateBuilder<(int,int),ImmutableArray<int>>();
        var equipment=ImmutableArray.CreateBuilder<int>();
        foreach(var section in document.Sections.Where(s=>s.Name=="operation hope grow type"))
        {
            if (!section.IsBlock || !section.Children.IsEmpty || section.Values.Length<2 || section.Values.Any(v=>v.Type!=0))
                throw new InvalidDataException("代号希望职业装备行无效");
            int job=section.Values[0].Value,grow=section.Values[1].Value;
            var ids=section.Values.Skip(2).Select(v=>v.Value).ToImmutableArray();
            if (job is <0 or >15 || grow is <0 or >15 || ids.Any(id=>id<=0) ||
                ids.Distinct().Count()!=ids.Length || !professions.TryAdd((job,grow),ids))
                throw new InvalidDataException("代号希望职业装备行重复或越界");
            equipment.AddRange(ids);
        }
        // 部分职业共用同一套装备；全局池去重，个人名册仍完整保留归属。
        Professions=professions.ToImmutable();Equipment=equipment.Distinct().ToImmutableArray();
        var sealedItem=Values(document,"operation hope sealing item");
        if (sealedItem is not [>0,>0]) throw new InvalidDataException("希望封装物品必须为完整物品数量对");
        SealingItemId=sealedItem[0];SealingItemCount=sealedItem[1];
        var sealingTemplate=items.Get(SealingItemId);
        if (sealingTemplate.Kind!=ItemKind.Material || SealingItemCount>sealingTemplate.StackLimit ||
            !sealingTemplate.Expiration.SupportsCreation)
            throw new InvalidDataException("希望封装物品不是可完整创建的材料");
        UnbiasedWeapons=Values(new(pvf.ReadTokens(ExcludePath),pvf.ResolveString),"exclude list").ToImmutableArray();
        if (Equipment.IsEmpty || UnbiasedWeapons.IsEmpty || Equipment.Distinct().Count()!=Equipment.Length ||
            UnbiasedWeapons.Distinct().Count()!=UnbiasedWeapons.Length || UnbiasedWeapons.Any(id=>id<=0) ||
            Equipment.Intersect(UnbiasedWeapons).Any())
            throw new InvalidDataException("代号希望普通和不加权武器池必须完整且无重复");
        foreach(int id in Equipment.Concat(UnbiasedWeapons))
        {
            var item=items.Get(id);
            if (item.Kind!=ItemKind.Equipment || !item.IsOperationHope || item.Rarity!=6 || item.MinimumLevel!=95 ||
                !item.Expiration.SupportsCreation || UnbiasedWeapons.Contains(id) &&
                (item.EquipmentSlot!=12 || !item.AttachType.Contains("[trade]",StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("代号希望奖池含未支持的装备模板："+id);
        }
    }

    static int[] Values(PvfTokenDocument document,string name)
    {
        var rows=document.Sections.Where(s=>s.Name==name).ToArray();
        if (!document.Diagnostics.IsEmpty || rows is not [var row] || !row.Children.IsEmpty ||
            row.Values.IsEmpty || row.Values.Any(v=>v.Type!=0))
            throw new InvalidDataException("代号希望奖励字段缺失、重复或不是整数："+name);
        return row.Values.Select(v=>v.Value).ToArray();
    }
}
