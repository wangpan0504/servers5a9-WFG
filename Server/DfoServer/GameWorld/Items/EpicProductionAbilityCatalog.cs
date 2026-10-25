using System.Collections.Frozen;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 制作史诗的属性/攻击特效是独立资源，不把其它装备的自定义属性类型混入。
internal sealed class EpicProductionAbilityCatalog
{
    public const string Path="etc/customability/epicproductionability.etc";
    public FrozenDictionary<(byte Group,byte Option),ImmutableArray<ItemMaterialCost>> Options { get; }
    public EpicProductionAbilityCatalog(PvfArchive archive):this(archive.ReadText(Path)) { }
    internal EpicProductionAbilityCatalog(string text)
    {
        var resource=CustomAbilityFile.Parse(text);
        if (resource.Type!="epic production") throw Invalid();
        var groups=new Dictionary<byte,byte>();
        foreach (var declaration in resource.Groups)
        {
            byte slot=declaration.Key switch {"1"=>0,"2"=>1,_=>throw Invalid()};
            if (declaration.Indexes.IsEmpty) throw Invalid();
            foreach(int id in declaration.Indexes)
                if(id is <1 or >255 || !groups.TryAdd((byte)id,slot)) throw Invalid();
        }
        var options=new Dictionary<(byte,byte),ImmutableArray<ItemMaterialCost>>();
        foreach(var declaration in resource.Options)
        {
            int id=declaration.Index;
            if(id is <1 or >255 || !groups.TryGetValue((byte)id,out byte group)) throw Invalid();
            // 本版只有D档；客户端403ACBB在未命中实例品级时回落到唯一材料档。
            // 若资源增加分档，不能继续统一套用D档而悄悄少扣材料。
            if(declaration.Materials is not [{Grade:"D"} material]) throw Invalid();
            if(material.Items.IsEmpty || material.Items.Any(row=>row.ItemId<=0 || row.Count<=0)) throw Invalid();
            var rows=material.Items.Select(row=>new ItemMaterialCost(row.ItemId,row.Count)).ToImmutableArray();
            if(rows.Select(x=>x.ItemId).Distinct().Count()!=rows.Length || !options.TryAdd((group,(byte)id),rows)) throw Invalid();
        }
        if(options.Count==0 || options.Count!=groups.Count) throw Invalid();
        Options=options.ToFrozenDictionary();
    }
    static InvalidDataException Invalid()=>new("史诗制作属性资源的组别、选项或材料声明无效");
}
