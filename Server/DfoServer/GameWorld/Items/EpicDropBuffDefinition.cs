using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

// 通用效果表的单项计时视图；历史维护位与恢复入口保持，不用Buff编号识别效果。
internal sealed record EpicDropBuffDefinition(int BuffId, int Percent)
{
    // 仅保留本服明确要求的道具适用范围；不通过Buff编号或计时参数猜效果类型。
    static readonly ImmutableHashSet<int> HellRewardScopeOverrides = ImmutableHashSet.Create(490005358, 490005359);
    public bool HellRewardOnly { get; init; }
    internal bool HasSupportedParameters => BuffId > 0 && Percent >= 0;
    public string SystemGroup { get; init; } = "random_box_buff";
    public ImmutableHashSet<int>? ItemIds { get; init; }
    public bool IsFamilyItem(int itemId) => ItemIds?.Contains(itemId) == true;

    internal static ImmutableDictionary<int, EpicDropBuffDefinition> Load(PvfArchive archive) => Load(VariousBuffCatalog.Load(archive));

    internal static ImmutableDictionary<int, EpicDropBuffDefinition> Load(VariousBuffCatalog catalog)
    {
        // 范围配置锚定已知来源，再由关联解析到Buff身份；新STK别名和重编号资源自动继承，不重复加白名单。
        var scoped = HellRewardScopeOverrides.Where(catalog.Items.ContainsKey).Select(item => catalog.Items[item].BuffId).ToHashSet();
        return catalog.Items.Where(pair => pair.Value.Supported && IsLegacyView(pair.Value)).ToImmutableDictionary(pair => pair.Key,
            pair => Convert(pair.Value) with { ItemIds = catalog.Families[pair.Value.SystemGroup], HellRewardOnly = scoped.Contains(pair.Value.BuffId) });
    }

    internal static bool IsLegacyView(VariousBuffDefinition buff) => buff.Timed && buff.SystemGroup == "random_box_buff" &&
        buff.DungeonScope == VariousBuffDungeonScope.All && buff.OnlyDropEffect(VariousBuffDropEffect.OrdinaryEpic);

    static EpicDropBuffDefinition Convert(VariousBuffDefinition buff) =>
        new(buff.BuffId, buff.Percent(VariousBuffDropEffect.OrdinaryEpic)) { SystemGroup = buff.SystemGroup };

    internal static ImmutableDictionary<int, EpicDropBuffDefinition> ReadDefinitions(ImmutableArray<PvfTokenSection> rows,
        Func<int, string> resolve) => VariousBuffCatalog.ReadDefinitions(rows, resolve).Values
        .Where(buff => buff.Supported && IsLegacyView(buff)).ToImmutableDictionary(buff => buff.BuffId, Convert);

    // 单独切出每个index再解析，损坏的未闭合块不能把下一条合法声明吞作自己的子字段。
    internal static ImmutableArray<PvfTokenSection> ReadIndices(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var result = ImmutableArray.CreateBuilder<PvfTokenSection>();
        for (int at = 0; at < tokens.Count;)
        {
            if (tokens[at].Type != 3 || resolve(tokens[at].Value) != "[index]") { at++; continue; }
            int start = at++;
            int values = at;
            while (at < tokens.Count && tokens[at].Type != 3) at++;
            var identity = tokens.Skip(values).Take(at - values).ToImmutableArray();
            while (at < tokens.Count && !(tokens[at].Type == 3 && resolve(tokens[at].Value) == "[index]")) at++;
            // 正式表这两项只漏最外层闭合；只接受完整且唯一的六token固定1136/100声明。
            // 不修复未知条目，也不借下一项的结束标签补齐当前项。
            if (at - start == 6 && identity is [{ Type: 0, Value: 490009005 or 490703161 }] &&
                tokens[start + 2].Type == 3 && resolve(tokens[start + 2].Value) == "[probability buff list]" &&
                tokens[start + 3] is { Type: 0, Value: 1136 } && tokens[start + 4] is { Type: 0, Value: 100 } &&
                tokens[start + 5].Type == 3 && resolve(tokens[start + 5].Value) == "[/probability buff list]")
            {
                result.Add(new("index", identity,
                    [new("probability buff list", [tokens[start + 3], tokens[start + 4]], [], true)], true));
                continue;
            }
            try
            {
                var document = new PvfTokenDocument(tokens.Skip(start).Take(at - start).ToArray(), resolve);
                if (document.Diagnostics.IsEmpty && document.Sections is [{ Name: "index", IsBlock: true } row])
                { result.Add(row); continue; }
            }
            catch (InvalidDataException) { }
            // 保留无效声明的身份，缺映射兼容不得覆盖任何原声明。
            result.Add(new("index", identity, []));
        }
        return result.ToImmutable();
    }

    internal static ImmutableDictionary<int, EpicDropBuffDefinition> ParseMappings(ImmutableArray<PvfTokenSection> sections,
        IReadOnlyDictionary<int, EpicDropBuffDefinition> definitions)
    {
        var declared = sections.Where(row => row.Name == "index").SelectMany(row => row.Values)
            .Where(value => value.Type == 0).Select(value => value.Value).ToHashSet();
        var result = ImmutableDictionary.CreateBuilder<int, EpicDropBuffDefinition>();
        foreach (var group in sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0, Value: > 0 }])
            .GroupBy(row => row.Values[0].Value))
        {
            if (group.Count() != 1 || sections.Count(row => row.Name == "index" &&
                    row.Values.Any(value => value.Type == 0 && value.Value == group.Key)) != 1 ||
                group.Single().Children is not [{ Children.Length: 0 } field]) continue;
            int? buff = field switch
            {
                { Name: "buff list", Values: [{ Type: 0 } value] } => value.Value,
                { Name: "probability buff list", Values: [{ Type: 0 } value, { Type: 0, Value: 100 }] } => value.Value,
                _ => null
            };
            if (buff is { } id && definitions.TryGetValue(id, out var definition) && definition.BuffId == id && definition.HasSupportedParameters)
                result.Add(group.Key, definition);
        }
        // 本服仅补这一个已核缺映射物品；显式但损坏/重复/随机的声明仍拒绝。
        if (!declared.Contains(490000413) && definitions.TryGetValue(1136, out var canonical) && canonical is { BuffId: 1136, Percent: 100 })
            result.Add(490000413, canonical);
        var scoped = result.Where(pair => HellRewardScopeOverrides.Contains(pair.Key)).Select(pair => pair.Value.BuffId).ToHashSet();
        foreach (var pair in result.ToArray()) result[pair.Key] = pair.Value with { HellRewardOnly = scoped.Contains(pair.Value.BuffId), ItemIds = result
            .Where(other => other.Value.SystemGroup == pair.Value.SystemGroup).Select(other => other.Key).ToImmutableHashSet() };
        return result.ToImmutable();
    }
}
