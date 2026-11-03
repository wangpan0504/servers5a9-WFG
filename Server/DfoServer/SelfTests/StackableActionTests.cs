using DfoServer.GameWorld.Items;
using DfoServer.Game.Inventory.Services;
using DfoServer.Game.Session;
using DfoServer.Game.Characters;
using DfoServer.Game.Inventory.Models;
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

    static void RunUsePlaces()
    {
        foreach (var (places, current, expected) in new (string[], StackableUseContext, bool)[]
        {
            ([], new(StackableUsePlace.Dungeon), true),
            (["[village]", "[seria room]", "[village]"], new(StackableUsePlace.Village), true),
            (["[village]"], new(StackableUsePlace.SeriaRoom), true),
            (["[seria room]"], new(StackableUsePlace.Village), false),
            (["[village]"], new(StackableUsePlace.Dungeon), false),
            (["[unknown]", "[dungeon]"], new(StackableUsePlace.Village), true),
            (["[tournament]", "[dungeon]"], new(StackableUsePlace.Village), false),
            (["[tournament]"], new(StackableUsePlace.Dungeon), false),
            (["[tournament]"], new(StackableUsePlace.Tournament), true),
            (["[pvp]"], new(StackableUsePlace.Tournament), false),
            (["[tournament]"], new(StackableUsePlace.Pvp), false),
            (["[pvp]"], new(StackableUsePlace.Pvp), true),
            (["[village]"], new(StackableUsePlace.Pvp), false),
            (["[dont open]", "[village]", "[dungeon]", "[pvp]", "[all]"], new(StackableUsePlace.Village), false),
            (["[dont open]"], new(StackableUsePlace.Dungeon), false),
            (["[dont use exercise]"], new(StackableUsePlace.Village), false),
            (["[dungeon]", "[dont use exercise]"], new(StackableUsePlace.Dungeon), true),
            (["[dungeon]", "[dont use exercise]"], new(StackableUsePlace.Dungeon, IsPractice: true), false),
            (["[dungeon]"], new(StackableUsePlace.Dungeon, IsPractice: true), true),
            (["[dont use assult]"], new(StackableUsePlace.Village), false),
            (["[village]", "[dont use assult]"], new(StackableUsePlace.Village), true),
            (["[dungeon]", "[dont use assult]"], new(StackableUsePlace.Dungeon), true),
            (["[dungeon]", "[dont use assult]"], new(StackableUsePlace.Dungeon, IsAssault: true), false),
            (["[all]"], new(StackableUsePlace.Tournament), true),
            (["[all]", "[dont use exercise]"], new(StackableUsePlace.Dungeon, IsPractice: true), false),
            (["[all]"], new(StackableUsePlace.None), false)
        })
        {
            That(StackableUsePlaces.Allows(places, current) == expected,
                "原生地点许可和排除项不互相覆盖：" + string.Join(',', places) + ":" + current);
            That(BoosterCatalog.ParseUsePlaces(places).Allows(current) == expected,
                "礼包保留全部地点与禁用语义，不另造两态匹配：" + current);
        }
        foreach (var state in Enum.GetValues<UserState>())
        {
            var expected = state switch
            {
                UserState.Town => StackableUsePlace.SeriaRoom,
                UserState.PvpTown or UserState.PvpRoom or UserState.PvpBattle => StackableUsePlace.Pvp,
                UserState.Dungeon or UserState.TutorialDungeon => StackableUsePlace.Dungeon,
                _ => StackableUsePlace.None
            };
            That(StackableUsePlacePolicy.Context(state, seriaRoom: true).Place == expected,
                "场所来自UserState，选角、入城、选图与未实现状态不能冒充村庄/副本：" + state);
        }
        foreach (var state in new[] { UserState.PvpTown, UserState.PvpRoom, UserState.PvpBattle })
            That(StackableUsePlacePolicy.Context(state, tournamentChannel: true).Place == StackableUsePlace.Tournament,
                "竞技大赛频道在城镇、候场及战斗均不混成普通PvP：" + state);
        That(!StackableUsePlaces.TownOnly(["[tournament]"]) &&
            !StackableUsePlaces.TownOnly(["[dungeon]", "[dont use exercise]"]) &&
            StackableUsePlaces.SeriaRoomOnly(["[seria room]", "[dont use exercise]"]),
            "目录的城镇/房间资格不再将比赛或练习排除值映射为村庄");
    }

    static void RunUseQualifications()
    {
        var town = new StackableUseContext(StackableUsePlace.Village);
        foreach (var (conditions, context, allowed) in new (string[], StackableUseContext, bool)[]
        {
            ([],town,true), ([],town with { IsPvpPrivate=true },false),
            (["[guildmember]"],town,false), (["[guildmember]"],town with { InGuild=true },true),
            (["[guildmaster]"],town,false), (["[guildmaster]"],town with { InGuild=true },false),
            (["[guildmaster]"],town with { IsGuildMaster=true },false),
            (["[guildmaster]","[guildmember]"],town with { InGuild=true,IsGuildMaster=true },true),
            (["[privatePvpChar]"],town,true), (["[privatePvpChar]"],town with { IsPvpPrivate=true },true),
            (["[guildmember]","[privatePvpChar]"],town with { IsPvpPrivate=true },false),
            (["[guildmember]","[privatePvpChar]"],town with { IsPvpPrivate=true,InGuild=true },true),
            (["[unknown]"],town,true), (["[unknown]"],town with { IsPvpPrivate=true },false)
        })
            That(new StackableUseRules([], [..conditions], "[waste]").Check(context) ==
                (allowed ? StackableUseRejection.None : StackableUseRejection.Condition),
                "使用资格按实际公会和角色身份判断，许可与限制不互相覆盖：" + string.Join(',',conditions) + ":" + context);
        foreach (string type in new[] { "[only effect]", "[booster random]", "[couple room item]" })
            That(new StackableUseRules([], StackableType:type).Check(town with { IsPvpPrivate=true }) == StackableUseRejection.None,
                "保留本版ARAD专用角色的13/16/39原生例外：" + type);
        That(new StackableUseRules([],StackableType:"[etc]").Check(town with { IsPvpPrivate=true },directUse:false) == StackableUseRejection.None &&
            new StackableUseRules([], ["[guildmaster]"], "[etc]").Check(town with { IsPvpPrivate=true },directUse:false) == StackableUseRejection.Condition,
            "自动保护仍检查显式资格，但不把主动使用的缺省专用角色许可规则扩展成额外毁装风险");
        foreach (var (source,target,allowed) in new (ItemWorlds,ItemWorlds,bool)[]
        {
            (ItemWorlds.Common,ItemWorlds.Common,true), (ItemWorlds.Fair,ItemWorlds.Fair,true),
            (ItemWorlds.Common|ItemWorlds.Fair,ItemWorlds.Common|ItemWorlds.Fair,true),
            (ItemWorlds.Common,ItemWorlds.Fair,false), (ItemWorlds.Fair,ItemWorlds.Common,false),
            (ItemWorlds.Common|ItemWorlds.Fair,ItemWorlds.Common,false), (ItemWorlds.Common,ItemWorlds.Common|ItemWorlds.Fair,false),
            (ItemWorlds.None,ItemWorlds.Common,false), (ItemWorlds.None,ItemWorlds.None,true)
        })
            That(new StackableUseRules([],Worlds:source).Check(town,target) ==
                (allowed ? StackableUseRejection.None : StackableUseRejection.World),
                "源与目标世界按双向包含匹配，不用交集或频道名代替：" + source + ":" + target);
        That(StackableUseRules.ParseWorlds(null) == ItemWorlds.Common && StackableUseRules.ParseWorlds([]) == ItemWorlds.None &&
            StackableUseRules.ParseWorlds(["[fair]","[unknown]","[common]"]) == ItemWorlds.Fair &&
            new StackableUseRules([],Worlds:ItemWorlds.Fair).Check(town) == StackableUseRejection.None,
            "缺省为common，显式空不补默认，未知值终止世界读取；无目标的使用不伪造当前频道世界门禁");
        var costCatalog = new EnhancementCatalog(_ => "[stackable type]\n`[material]` 0\n[action usable condition]\n`[guildmaster]`\n[/action usable condition]",
            new Dictionary<int,string> { [100]="cost.stk" },new Dictionary<int,string>());
        var costService = new InventoryEnhancementService(null!,costCatalog,null!);
        var role = new Character(1,0,"Cost",0,new(1,0,1,1),null) { Inventory=new([
            new(1,new(InventoryListType.Main,65),new(100,ItemKind.Material,10)),
            new(2,new(InventoryListType.Equipment,12),new(101,ItemKind.Equipment,1))]) };
        var request = new EnhancementRequest(EnhancementMode.Reinforce,new(InventoryListType.Equipment,12),101,65,-1,default,-1,-1);
        That(!costService.IsEffectSource(role,request),
            "普通NPC材料即使声明会长使用资格也只是成本，不因扣数量进入道具效果门禁");
    }

    static void RunIgnoredServerTypes()
    {
        var item = new ItemTemplate(100, ItemKind.Consumable, 0, 1, 0, 100, 0, 0, 1, "account", ushort.MaxValue, 0, "Use");
        var raw = new StackableFile(100, "stackable/use.stk", new("[waste]", [0]), 100, 0, 0, 1, null, [])
        {
            ActionDeclaration = new("[add fatigue]", [30]), HasUseActionPacket = true, DeclaredUseActionPacket = 1,
            DeclaredActionUsableServerTypes = ["[event server]"], DeclaredNeededMaterials = [],
            DeclaredUsableFirstGrowTypes = [], DeclaredDontChangeGrowTypes = []
        };
        foreach (string field in new[] { "action usable server type", "usable game server type", "usable server type" })
        {
            var file = new RecoveryConsumableFile(100, raw.ScriptPath, raw, 0, null, null, null, [field], []);
            That(FatigueConsumableDefinition.Parse(file, item) is { Amount: 30 } &&
                ActionConsumableDefinition.Parse(file with { Stackable = raw with { ActionDeclaration = new("[add mileage]", [7]) } }, item) is
                    { Label: "[add mileage]", Parameters: [7] },
                "疲劳药和动作道具忽略服务器字段，不改变效果参数：" + field);
            var expRaw = raw with { TypeDeclaration = new("[etc]", [0]), ActionDeclaration = null, DeclaredUseActionPacket = 0 };
            var experience = new ExperienceConsumableFile(expRaw, new("[expUp]", [50]), null, [], [field], []);
            That(ExperienceConsumableDefinition.Parse(experience, item) is { Amount: 50 } &&
                LevelUpByTableDefinition.Parse(experience with { Effect = new("[levelUpByTable]", [1, 49, 50]) }, item) is not null,
                "经验与等级表不因服务器字段丢失已实现效果：" + field);
            That(InventoryExpansionDefinition.Parse(file with { Stackable = expRaw with
                { ActionDeclaration = new("[upgrade inventory]", []) } }, item) is not null,
                "严格字段白名单同样忽略服务器声明：" + field);
            That(PartnerTeleportDefinition.Parse(file with { Stackable = raw with
                { ActionDeclaration = new("[teleport to partner]", [1]), DeclaredUseActionPacket = 0 } }, item) is not null,
                "伙伴传送不按服务器字段拒绝，保留购买成本与使用边界：" + field);
            That(PermanentGrowthDefinition.Parse(experience with { Stackable = expRaw with
                { TemplateId = 1039, ScriptPath = "stackable/stone_str.stk", TypeDeclaration = new("[etc]", [1]) }, Effect = null },
                item with { Id = 1039 }) is { Amount: 50 } &&
                LegacyStatusConsumableDefinition.Parse(experience with { Stackable = expRaw with
                { TemplateId = 7180, ScriptPath = "stackable/event/newyear/coin_newyear.stk", TypeDeclaration = new("[waste]", [0]) }, Effect = null },
                item with { Id = 7180 }) is { Amount: 1 },
                "原生属性石及复活币道具忽略服务器声明，不改原效果：" + field);
            string daily = "[stackable type]\n`[booster]` 0\n[total usable count]\n1\n[impossible contents]\n`delete booster`\n[/impossible contents]\n";
            string server = "[" + field + "]\n`[event server]`\n[/" + field + "]\n";
            That(DailyRetainedBoosterDefinition.TryParse(server + daily + server, out byte limit) && limit == 1 &&
                DailyRetainedBoosterDefinition.TryParse(daily.Replace("[impossible contents]", server + "[impossible contents]"), out limit) && limit == 1,
                "每日保留礼盒忽略重复服务器声明，原每日上限仍为1：" + field);
        }
    }

    public static void RunCore()
    {
        RunUsePlaces();
        RunUseQualifications();
        RunIgnoredServerTypes();
        int checks = Count;
        That(StackableActionCatalog.ClientTypes.Count == 194 &&
            StackableActionCatalog.ClientTypes.Values.Distinct().Count() == 194,
            "保留的ARAD标签和实际动作号一一对应，退役两项不重编号其余动作");
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
            ("[epic production plus count]", 196), ("[avatar transport stone]", 197),
            ("[add skin storage]", 199), ("[level up ticket]", 39), ("[class change]", 49)
        })
        {
            var raw = new StackableFile(123, "stackable/fixture.stk", null, null, null, null, null, null, [])
                { ActionDeclaration = new(label, []) };
            That(StackableActionCatalog.TryType(label, out uint actual) && actual == type &&
                StackableActionCatalog.Matches(raw, type) && !StackableActionCatalog.Matches(raw, type + 1),
                "动作身份统一核对源STK而不是相信请求中宣称的功能 " + label);
        }
        foreach (var (label, type) in new (string, uint)[]
        {
            ("[beast dungeon reset]", 194), ("[add beast incount]", 195)
        })
        {
            var raw = new StackableFile(123, "stackable/retired-fixture.stk", null, null, null, null, null, null, [])
                { ActionDeclaration = new(label, [1]) };
            That(!StackableActionCatalog.TryType(label, out _) && !StackableActionCatalog.Parsers.ContainsKey(label) &&
                !StackableActionCatalog.ClientTypes.Values.Contains(type) && !StackableActionCatalog.Matches(raw, type),
                "退役动作标签与旧动作号均不注册或匹配 " + label);
            var retired = StackableActionCatalog.ParseFile(raw);
            That(retired is
                { Status: StackableActionParseStatus.UnknownLabel, Identity.ClientType: null, Arguments: null, Parser: null } &&
                ReferenceEquals(retired.Declaration,raw.ActionDeclaration) && StackableActionCatalog.Arguments(raw) is null,
                "退役声明保留未知身份，不生成可执行参数 " + label);
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
        var pending = StackableActionCatalog.Parse(new("[open guide book]", [123]));
        That(pending is { Status: StackableActionParseStatus.Parsed, Parser.Business: StackableActionBusinessStatus.Pending } &&
            pending.Declaration!.Parameters is [123] &&
            StackableActionCatalog.Parse(new("[add fatigue]", [10])).Parser?.Business == StackableActionBusinessStatus.ExistingVariants &&
            StackableActionCatalog.Parse(new("[unknown action]", [456])).Status == StackableActionParseStatus.UnknownLabel &&
            StackableActionCatalog.Parse((StackableActionDeclaration?)null).Status == StackableActionParseStatus.Absent,
            "业务待接入、已有受限业务、未知标签及不存在分别报告，原参数不因尚未实现被丢弃");
        That(StackableActionCatalog.Parse(new("[story book]", [3108])) is
            { Status: StackableActionParseStatus.Parsed, Identity.ClientType: 139, Parser.Business: StackableActionBusinessStatus.ExistingVariants },
            "故事簿在统一动作注册中关联139及已接入业务，不影响其它待接入动作");
        That(StackableActionCatalog.ParseFile(new StackableFile(1, "stackable/bad.stk", null, null, null, null, null, null, [])
            { HasActionType = true }).Status == StackableActionParseStatus.InvalidDeclaration,
            "存在但无唯一合法声明的动作不是无动作");
        Console.WriteLine($"PASS {Count - checks} STK原生动作映射、源身份和未知边界断言");
    }
}
