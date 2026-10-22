using DfoServer.GameWorld.Items;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class StackableActionTests
{
    public static void RunResources(PvfArchive pvf, ItemCatalog items)
    {
        var catalog = StackableCatalog.Load(pvf);
        int total = Survey(catalog.TemplateIds.Select(catalog.Get), items);
        That(total == catalog.TemplateCount, "动作专项覆盖实际STK名册，不把原版固定数量用于修改版PVF");
    }

    internal static int Survey(IEnumerable<StackableFile> definitions, ItemCatalog? items = null)
    {
        var actions = new Dictionary<string, (int Count, int Parsed, int Invalid, List<int> Samples)>(StringComparer.Ordinal);
        var fatigueGaps = new Dictionary<string, (int Count, List<int> Samples)>(StringComparer.Ordinal);
        var shapes = new Dictionary<(string Label, string Types), (int Count, int Sample, string Values)>();
        int total = 0, invalid = 0;
        foreach (var definition in definitions)
        {
            total++;
            if (definition.ActionDeclaration is { } action)
            {
                var row = actions.GetValueOrDefault(action.Label, (Count: 0, Parsed: 0, Invalid: 0, Samples: new List<int>()));
                if (row.Samples.Count < 3) row.Samples.Add(definition.TemplateId);
                var parsed = StackableActionCatalog.ParseFile(definition);
                string types = string.Join(',', action.Arguments.Select(value => value.Type));
                var shape = shapes.GetValueOrDefault((action.Label, types));
                shapes[(action.Label, types)] = (shape.Count + 1, shape.Count == 0 ? definition.TemplateId : shape.Sample,
                    shape.Count == 0 ? string.Join(',', action.Arguments.Take(12).Select(value =>
                        value.Type == 6 ? "`" + value.Text + "`" : value.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))) : shape.Values);
                actions[action.Label] = (row.Count + 1, row.Parsed + (parsed.Status == StackableActionParseStatus.Parsed ? 1 : 0),
                    row.Invalid + (parsed.Status == StackableActionParseStatus.InvalidArguments ? 1 : 0), row.Samples);
                if (action.Label == "[add fatigue]" && items is not null && items.FatigueConsumable(definition.TemplateId) is null)
                {
                    string signature = $"type={definition.TypeDeclaration?.Label}; use-packet={definition.DeclaredUseActionPacket}; places={string.Join(',', definition.DeclaredActionUsablePlaces ?? [])}";
                    var gap = fatigueGaps.GetValueOrDefault(signature, (Count: 0, Samples: new List<int>()));
                    if (gap.Samples.Count < 3) gap.Samples.Add(definition.TemplateId);
                    fatigueGaps[signature] = (gap.Count + 1, gap.Samples);
                }
            }
            else if (definition.HasActionType)
            {
                invalid++;
                if (invalid <= 10) Console.WriteLine($"STK action invalid declaration: item={definition.TemplateId}; path={definition.ScriptPath}");
            }
        }
        Console.WriteLine($"STK action survey: templates={total}; labels={actions.Count}; items={actions.Values.Sum(row => row.Count)}; parsed={actions.Values.Sum(row => row.Parsed)}; invalid-args={actions.Values.Sum(row => row.Invalid)}; invalid-declarations={invalid}; unregistered={actions.Keys.Count(label => !StackableActionCatalog.Parsers.ContainsKey(label))}; 解析注册不代表消费业务已实现");
        foreach (var row in actions.OrderBy(row => row.Key, StringComparer.Ordinal).Take(256))
        {
            StackableActionCatalog.Parsers.TryGetValue(row.Key, out var parser);
            Console.WriteLine($"STK action {row.Key}: client={(StackableActionCatalog.TryType(row.Key, out uint type) ? type.ToString() : "unknown")}; parser={parser?.Source.ToString() ?? "missing"}; business={parser?.Business.ToString() ?? "Pending"}; parsed={row.Value.Parsed}; invalid-args={row.Value.Invalid}; items={row.Value.Count}; samples={string.Join(',', row.Value.Samples)}");
        }
        if (actions.Count > 256) Console.WriteLine($"STK action survey: remaining labels={actions.Count - 256}");
        foreach (var row in shapes.OrderBy(row => row.Key.Label, StringComparer.Ordinal).ThenBy(row => row.Key.Types, StringComparer.Ordinal))
            Console.WriteLine($"STK action shape {row.Key.Label}: types={row.Key.Types}; items={row.Value.Count}; sample={row.Value.Sample}; values={row.Value.Values}");
        foreach (var gap in fatigueGaps.OrderBy(row => row.Key, StringComparer.Ordinal).Take(32))
            Console.WriteLine($"STK fatigue pending: {gap.Key}; items={gap.Value.Count}; samples={string.Join(',', gap.Value.Samples)}");
        return total;
    }

    public static void RunCore()
    {
        int checks = Count;
        That(StackableActionCatalog.ClientTypes.Count == 196 &&
            StackableActionCatalog.ClientTypes.Values.Distinct().Count() == 196,
            "匹配ARAD标签和实际返回动作号一一对应，不拿字符串资源索引冒充动作号");
        foreach (var (label, parser) in StackableActionCatalog.Parsers)
        {
            var parsed = StackableActionCatalog.Parse(new(label, [-1, 0, 42]));
            That(parsed is { Status: StackableActionParseStatus.Parsed, Arguments.Parameters: [-1, 0, 42] } &&
                ReferenceEquals(parsed.Parser, parser) && (parser.Source == StackableActionParserSource.Arad) ==
                StackableActionCatalog.TryType(label, out _), "每个注册动作按资源读取合同解析，不依赖消费接入或opcode " + label);
        }
        That(StackableActionCatalog.ClientTypes.Keys.All(StackableActionCatalog.Parsers.ContainsKey) &&
            StackableActionCatalog.Parsers.Values.Count(parser => parser.Source == StackableActionParserSource.PvfOnly) == 17,
            "完整注册匹配客户端动作及当前PVF额外声明，不仅限于已有业务");
        foreach (var (label, type) in new (string, uint)[]
        {
            ("[add fatigue]", 58), ("[quest clear]", 77), ("[change creature]", 64), ("[creature growth]", 107),
            ("[first awaken clear]", 119), ("[second awaken clear]", 122), ("[any quest clear]", 130),
            ("[open aura skin slot]", 127), ("[open creature skin slot]", 224), ("[jumping lv]", 193),
            ("[add skin storage]", 199), ("[level up ticket]", 39), ("[class change]", 49)
        })
        {
            var raw = new StackableFile(123, "stackable/fixture.stk", null, null, null, null, null, null, [])
                { ActionDeclaration = new(label, []) };
            That(StackableActionCatalog.TryType(label, out uint actual) && actual == type &&
                StackableActionCatalog.Matches(raw, type) && !StackableActionCatalog.Matches(raw, type + 1),
                "动作身份统一核对源STK而不是相信请求中宣称的功能 " + label);
        }
        That(!StackableActionCatalog.TryType("[not implemented action]", out _) &&
            !StackableActionCatalog.TryType("[ADD FATIGUE]", out _), "未知或不同大小写的动作不猜测成已实现动作");
        foreach (var (label, values) in new (string, int[])[]
        {
            ("[add fatigue]", [10]), ("[class change]", [4]), ("[level up ticket]", []),
            ("[fixed probability kaleido]", [50, 100]), ("[new portable amplify]", [0, 12, 30000]),
            ("[portable genuine damage upgrade upgrede]", []), ("[repair coupon]", []), ("[extend item period]", [0, 1])
        })
        {
            var parsed = StackableActionCatalog.Parse(new(label, values));
            That(parsed.Status == StackableActionParseStatus.Parsed && parsed.Arguments!.Parameters.SequenceEqual(values),
                "按标签调用资源参数解析器，不限于515使用的动作 " + label);
        }
        foreach (var (label, values) in new (string, int[])[]
        {
            ("[first awaken clear]", []), ("[first awaken clear]", [1, 1]), ("[second awaken clear]", [0, 1]),
            ("[level up ticket]", [85]), ("[add skin storage]", [10000, 10001, 10002, 10003]),
            ("[add fatigue]", [10, 20]), ("[class change]", [1, 2])
        })
            That(StackableActionCatalog.Arguments(new StackableActionDeclaration(label, values))?.Parameters.SequenceEqual(values) == true,
                "资源层保留合法数值变体，由具体业务判断可否使用 " + label);
        var mixed = StackableActionDeclaration.Read(new PvfDocument("[action type]\n`[story book]` 7 -3.75 2.9"))!;
        That(StackableActionCatalog.Parse(mixed).Arguments?.Parameters is [7, -3, 2] &&
            StackableActionCatalog.Arguments(mixed) is null &&
            mixed.Arguments[1] == new StackableActionArgument(2, BitConverter.SingleToInt32Bits(-3.75f)),
            "匹配ARAD向零截断浮点参数，同时保留原始声明位模式");
        string Resolve(int id) => id == -1 ? "[action type]" : "[story book]";
        var alternate = StackableFile.Parse(1, "stackable/numeric.stk", [new(3, -1), new(6, -2), new(1, -9)], Resolve);
        That(StackableActionCatalog.ParseFile(alternate).Arguments?.Parameters is [-9] &&
            !alternate.UnsupportedFields.Contains("action type"), "匹配ARAD读取type1数值，原声明不改成type0");
        foreach (string value in new[] { "`10`", "1 `text` 2", "2147483648.0", "-2147483904.0" })
            That(StackableActionCatalog.Parse(StackableActionDeclaration.Read(new PvfDocument("[action type]\n`[add fatigue]` " + value)))
                is { Status: StackableActionParseStatus.InvalidArguments, Arguments: null, Declaration: not null },
                "不提取字符串数字、不截取前缀或吞掉越界参数 " + value);
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var raw = StackableFile.Parse(1, "stackable/numeric.stk", [new(3, -1), new(6, -2), new(2, BitConverter.SingleToInt32Bits(value))], Resolve);
            That(StackableActionCatalog.ParseFile(raw).Status == StackableActionParseStatus.InvalidArguments, "非有限浮点数保持参数非法");
        }
        var expansion = StackableActionCatalog.Parse(new("[avatar inventory expansion]", [1]));
        That(expansion is { Status: StackableActionParseStatus.Parsed, Identity.ClientType: null } && expansion.Arguments!.Parameters is [1],
            "资源解析器注册不要求当前EXE分配动作号，更不要求绑定515");
        var pending = StackableActionCatalog.Parse(new("[story book]", [123]));
        That(pending is { Status: StackableActionParseStatus.Parsed, Parser.Business: StackableActionBusinessStatus.Pending } &&
            pending.Declaration!.Parameters is [123] &&
            StackableActionCatalog.Parse(new("[add fatigue]", [10])).Parser?.Business == StackableActionBusinessStatus.ExistingVariants &&
            StackableActionCatalog.Parse(new("[unknown action]", [456])).Status == StackableActionParseStatus.UnknownLabel &&
            StackableActionCatalog.Parse((StackableActionDeclaration?)null).Status == StackableActionParseStatus.Absent,
            "业务待接入、已有受限业务、未知标签及不存在分别报告，原参数不因尚未实现被丢弃");
        That(StackableActionCatalog.ParseFile(new StackableFile(1, "stackable/bad.stk", null, null, null, null, null, null, [])
            { HasActionType = true }).Status == StackableActionParseStatus.InvalidDeclaration,
            "存在但无唯一合法声明的动作不是无动作");
        Console.WriteLine($"PASS {Count - checks} STK原生动作映射、源身份和未知边界断言");
    }
}
