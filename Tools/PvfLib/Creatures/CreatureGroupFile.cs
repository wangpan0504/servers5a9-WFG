using System.Collections.Immutable;

namespace PvfLib;

public sealed record CreatureGroupConversion(int Type, int ItemId, ImmutableArray<ImmutableArray<int>> Rows);
public sealed record CreatureGrowthGroup(int Id, ImmutableArray<CreatureGroupConversion> Conversions);

// 保留分组、转换块及行顺序；消耗、候选和概率的解释归玩法资源层。
public sealed record CreatureGroupFile(ImmutableArray<CreatureGrowthGroup> Groups)
{
    public static CreatureGroupFile Parse(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var groups = ImmutableArray.CreateBuilder<CreatureGrowthGroup>();
        foreach (var group in document.Sections)
        {
            if (group.Name != "creature growth group" || group.Values is not [{ Type: 0 } id])
                throw new InvalidDataException("宠物转换组必须声明单个整数编号");
            var conversions = ImmutableArray.CreateBuilder<CreatureGroupConversion>();
            foreach (var conversion in group.Children)
            {
                if (conversion.Name != "convert type" || conversion.Values is not [{ Type: 0 } type, { Type: 0 } item])
                    throw new InvalidDataException("宠物转换类型必须声明类型和道具编号");
                var rows = ImmutableArray.CreateBuilder<ImmutableArray<int>>();
                foreach (var row in conversion.Children)
                {
                    if (row.Name != "convert info" || !row.Children.IsEmpty || row.Values.Any(value => value.Type != 0))
                        throw new InvalidDataException("宠物转换行必须为完整整数数组");
                    rows.Add(row.Values.Select(value => value.Value).ToImmutableArray());
                }
                conversions.Add(new(type.Value, item.Value, rows.ToImmutable()));
            }
            groups.Add(new(id.Value, conversions.ToImmutable()));
        }
        return new(groups.ToImmutable());
    }
}
