using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Creatures;

internal sealed class CreatureGroupCatalog
{
    sealed record Rule(int Cost, ImmutableArray<int> Sources, ImmutableArray<CreatureChangeCandidate> Targets);
    readonly Lazy<IReadOnlyDictionary<(int Group, int Type, int Item), ImmutableArray<Rule>>> rules;

    public CreatureGroupCatalog(PvfArchive archive) : this(() => archive.Contains("creature/common/creaturegroup.cre")
        ? CreatureGroupFile.Parse(archive.ReadTokens("creature/common/creaturegroup.cre"), archive.ResolveString)
        : new([])) { }

    internal CreatureGroupCatalog(CreatureGroupFile file) : this(() => file) { }
    CreatureGroupCatalog(Func<CreatureGroupFile> load) => rules = new(() => Read(load()));

    public CreatureChangeDefinition? Resolve(CreatureGrowthItemDefinition item, int sourceItemId, uint choice)
    {
        foreach (int type in item.Types)
        {
            if (!rules.Value.TryGetValue((item.GroupId, type, item.ItemId), out var rows)) continue;
            foreach (var row in rows)
            {
                if (!row.Sources.Contains(sourceItemId)) continue;
                var candidates = row.Targets;
                if (type is 2 or 4)
                {
                    if (choice < 1 || choice > candidates.Length) return null;
                    candidates = [candidates[checked((int)choice - 1)]];
                    if (candidates[0].ItemId == sourceItemId) return null;
                }
                else if (choice != 0) return null;
                return new(item.ItemId, sourceItemId, item.MinimumLevel, item.MaximumLevel, item.CreatureMinimumLevel,
                    [sourceItemId], candidates, item.Materials) { ConsumeCount = row.Cost, TownOnly = item.TownOnly };
            }
        }
        return null;
    }

    static IReadOnlyDictionary<(int, int, int), ImmutableArray<Rule>> Read(CreatureGroupFile file)
    {
        var result = new Dictionary<(int, int, int), ImmutableArray<Rule>>();
        var ids = new HashSet<int>();
        foreach (var group in file.Groups)
        {
            if (group.Id <= 0 || !ids.Add(group.Id)) throw new InvalidDataException("宠物转换组编号无效或重复");
            foreach (var conversion in group.Conversions)
            {
                if (conversion.Type is < 1 or > 4 || conversion.ItemId <= 0 || conversion.Rows.IsEmpty)
                    throw new InvalidDataException("宠物转换类型、道具或规则为空");
                var rows = ImmutableArray.CreateBuilder<Rule>();
                foreach (var data in conversion.Rows)
                {
                    if (data.Length < 3 || data[0] <= 0) throw new InvalidDataException("宠物转换消耗与候选不完整");
                    int cost = data[0];
                    var values = data.RemoveAt(0);
                    ImmutableArray<int> sources;
                    ImmutableArray<CreatureChangeCandidate> targets;
                    if (conversion.Type == 3)
                    {
                        if (values.Length < 5 || values.Length % 2 != 1)
                            throw new InvalidDataException("随机宠物转换必须声明源、结果及等长权重");
                        int count = (values.Length - 1) / 2;
                        sources = values.Take(count + 1).ToImmutableArray();
                        targets = Enumerable.Range(0, count).Select(index => new CreatureChangeCandidate(values[index + 1], values[count + index + 1])).ToImmutableArray();
                        if (targets.Any(row => row.Weight < 0) || targets.Sum(row => (long)row.Weight) != 10000)
                            throw new InvalidDataException("随机宠物转换权重必须合计10000");
                    }
                    else
                    {
                        if (conversion.Type == 1 && values.Length != 2)
                            throw new InvalidDataException("固定宠物转换必须是一个源和一个结果");
                        sources = conversion.Type == 4 ? values : [values[0]];
                        targets = (conversion.Type == 4 ? values : values.RemoveAt(0)).Select(id => new CreatureChangeCandidate(id, 1)).ToImmutableArray();
                    }
                    if (sources.Any(id => id <= 0) || targets.Any(row => row.ItemId <= 0))
                        throw new InvalidDataException("宠物转换模板编号必须为正数");
                    rows.Add(new(cost, sources, targets));
                }
                if (!result.TryAdd((group.Id, conversion.Type, conversion.ItemId), rows.ToImmutable()))
                    throw new InvalidDataException("重复的宠物转换组、类型和道具");
            }
        }
        return result;
    }
}
