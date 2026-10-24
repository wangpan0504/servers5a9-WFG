using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// freepassitem的四元组保留券数量及额外灵石；第三列只支持本版明确的-1。
internal sealed record CosmoPassCost(int ItemId, int Count, int AdditionalStones)
{
    public const string Path = "etc/freepassitem.etc";
    public static ImmutableArray<CosmoPassCost> Load(PvfArchive archive) => archive.Contains(Path)
        ? Parse(archive.ReadTokens(Path),archive.ResolveString) : [];

    internal static ImmutableArray<CosmoPassCost> Parse(IReadOnlyList<PvfToken> tokens,Func<int,string> resolve)
    {
        var document = new PvfTokenDocument(tokens,resolve);
        var sections = document.Sections.Where(s=>s.Name=="hell cosmo freepass item").ToArray();
        if (sections.Length==0) return [];
        if (!document.Diagnostics.IsEmpty || sections is not [{ Children.Length:0, IsBlock:true } section] ||
            section.Values.Length%4!=0 || section.Values.Any(v=>v.Type!=0))
            throw new InvalidDataException("超星空通行证声明须为唯一完整整数四元组表");
        var rows = ImmutableArray.CreateBuilder<CosmoPassCost>();
        var ids = new HashSet<int>();
        for(int at=0;at<section.Values.Length;at+=4)
        {
            var row = new CosmoPassCost(section.Values[at].Value,section.Values[at+1].Value,section.Values[at+3].Value);
            if(row.ItemId<=0 || row.Count<=0 || section.Values[at+2].Value!=-1 || row.AdditionalStones<0 || !ids.Add(row.ItemId))
                throw new InvalidDataException("超星空通行证编号、费用或附加条件未确认");
            rows.Add(row);
        }
        return rows.ToImmutable();
    }
}
