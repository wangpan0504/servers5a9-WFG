using System.Collections.Immutable;

namespace PvfLib;

// 重复表逐块保留原序与原值；合成模式、概率单位及颜色编号不由库层推断。
public sealed record RuneSystemFile(ImmutableArray<ImmutableArray<int>> CompoundInfo,
    ImmutableArray<ImmutableArray<int>> RandomChooseInfo, int? RefineMaterialId,
    ImmutableArray<int>? RefineMaterialCounts)
{
    public static RuneSystemFile Parse(PvfToken[] tokens, Func<int, string> resolve)
    {
        // 当前资源的结束标签比起始标签少了count，别名只适用于这个已确认的原始块。
        var document = new PvfTokenDocument(tokens, resolve,
            new Dictionary<string, string> { ["talisman refine material count"] = "talisman refine material" });
        if (!document.Diagnostics.IsEmpty)
            throw new InvalidDataException("符文系统声明结构无效: " + document.Diagnostics[0]);
        return new(Rows("compound info"), Rows("random choose info"),
            TalismanItemFields.Integer(document, "talisman refine material index"),
            TalismanItemFields.Integers(document, "talisman refine material count"));

        ImmutableArray<ImmutableArray<int>> Rows(string name)
        {
            if (document.Sections.Any(section => TalismanItemFields.ContainsNested(section, name)))
                throw new InvalidDataException("符文系统表不能嵌套: " + name);
            var rows = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
            foreach (var section in document.Sections.Where(section => section.Name == name))
            {
                if (section.Values.IsEmpty || !section.Children.IsEmpty || section.Values.Any(token => token.Type != 0))
                    throw new InvalidDataException("符文系统表必须为非空整数序列: " + name);
                rows.Add(section.Values.Select(token => token.Value).ToImmutableArray());
            }
            return rows.ToImmutable();
        }
    }
}
