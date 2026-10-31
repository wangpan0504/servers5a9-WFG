using System.Collections.Immutable;

namespace DfoServer.GameWorld.Raids;

internal sealed record LukeSanctumObjectBudget(int Dungeon, string Symbol, int Cap);
internal sealed record LukeSanctumObjectTier(int Value, int Minimum, int Maximum);

// 只解释本版设施预算与连续档位，不拥有在线计数或客户端动作权限。
internal sealed class LukeSanctumObjectRules
{
    public ImmutableArray<LukeSanctumObjectBudget> Budgets { get; }
    public ImmutableArray<LukeSanctumObjectTier> Tiers { get; }
    LukeSanctumObjectRules(ImmutableArray<LukeSanctumObjectBudget> budgets, ImmutableArray<LukeSanctumObjectTier> tiers)
    { Budgets = budgets; Tiers = tiers; }

    public LukeSanctumObjectBudget? Budget(int dungeon) => Budgets.SingleOrDefault(row => row.Dungeon == dungeon);
    public int Tier(int total) => Tiers.Single(row => total >= row.Minimum && total <= row.Maximum).Value;

    public static LukeSanctumObjectRules Create(RaidScript script)
    {
        if (script.Phases.Length != 2 || script.Symbols.GetValueOrDefault("[energy control open]", -1) != 133)
            throw new InvalidDataException("卢克设施档位缺少阶段或能源控制符号");
        var budgets = ImmutableArray.CreateBuilder<LukeSanctumObjectBudget>();
        foreach (var (tag, dungeon, symbol) in new[] { ("[birth sanctum]", 5054, 117), ("[extinction sanctum]", 5055, 118), ("[spread sanctum]", 5056, 119) })
        {
            string name = tag[..^1] + " object]";
            if (script.Headers.Where(row => row.Tag == tag).ToArray() is not
                [{ Values: [{ Type: 0, Number: > 0 and <= 255 } cap, { Type: 0 } id] }] || id.Number != dungeon ||
                !script.Phases[0].Dungeons.Any(row => row.Id == dungeon) || script.Symbols.GetValueOrDefault(name, -1) != symbol)
                throw new InvalidDataException("卢克设施预算或归属不完整：" + tag);
            budgets.Add(new(dungeon, name, cap.Number));
        }
        if (script.Headers.Where(row => row.Tag == "[1phase obj destroy]").ToArray() is not [var table] ||
            table.Values.IsDefaultOrEmpty || table.Values.Length % 3 != 0 || table.Values.Any(value => value.Type != 0))
            throw new InvalidDataException("卢克设施破坏档位格式不完整");
        var tiers = ImmutableArray.CreateBuilder<LukeSanctumObjectTier>();
        int next = 0;
        for (int index = 0; index < table.Values.Length; index += 3)
        {
            int value = table.Values[index].Number, minimum = table.Values[index + 1].Number, maximum = table.Values[index + 2].Number;
            if (value < 0 || minimum != next || maximum < minimum || maximum > budgets.Sum(row => row.Cap) || tiers.Any(row => row.Value == value))
                throw new InvalidDataException("卢克设施档位重叠、缺口或超出预算");
            tiers.Add(new(value, minimum, maximum)); next = maximum + 1;
        }
        if (next != budgets.Sum(row => row.Cap) + 1) throw new InvalidDataException("卢克设施档位未覆盖完整预算");
        return new(budgets.ToImmutable(), tiers.ToImmutable());
    }
}
