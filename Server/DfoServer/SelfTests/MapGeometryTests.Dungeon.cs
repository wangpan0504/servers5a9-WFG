using System.Text;
using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static JsonArray GeometryAtoms(IEnumerable<int> values) => new(values.Select(value => (JsonNode)new JsonArray(0,value)).ToArray());
    private static JsonArray GeometryStrings(params string[] values) => new(values.Select(value => (JsonNode)new JsonArray(6,value)).ToArray());
    private static JsonObject GeometryRule(string id, string path, string kind, string tag, JsonArray from, JsonArray to) => new()
    {
        ["id"] = id, ["enabled"] = true, ["map"] = path, ["kind"] = kind, ["tag"] = tag, ["occurrence"] = 0,
        ["first"] = true, ["last"] = true, ["left"] = new JsonArray(), ["right"] = new JsonArray(),
        ["from"] = new JsonObject { ["800"] = from.DeepClone(), ["1067"] = from.DeepClone(), ["960"] = from.DeepClone() },
        ["to"] = new JsonObject { ["800"] = to.DeepClone(), ["1067"] = to.DeepClone(), ["960"] = to.DeepClone() }
    };
    private static string GeometryDocument(IEnumerable<JsonObject> rules, JsonObject? aliases = null) => new JsonObject
    {
        ["schema"] = 1, ["layer"] = "world", ["rules"] = new JsonArray(rules.Select(rule => rule.DeepClone()).ToArray()),
        ["aliases"] = aliases?.DeepClone() ?? new JsonObject()
    }.ToJsonString();

    private static void RunDungeonCore()
    {
        RunAnchorCore();
        RunEmptyExtensionCore();
        const string map = "map/test.map", tile = "map/test/displaycompat/grid.til";
        foreach (var (kind,tag) in new[] { ("gate_points8","[pathgate pos]"), ("gate_recognize8","[pathgate recognize range]") })
        {
            int[] original = [-1,0,100,0,20,-5,0,200], target = [-1,0,324,0,20,-5,0,200];
            var rule = GeometryRule(kind,map,kind,tag,GeometryAtoms(original),GeometryAtoms(target));
            var config = MapGeometryConfig.Parse(GeometryDocument([rule]));
            string Resolve(int id) => id switch { 1 => tag, 2 => "[map name]", 3 => "display only", _ => throw new InvalidDataException("fixture string") };
            PvfToken[] input = [new(3,1),..original.Select(value => new PvfToken(0,value)),new(3,2),new(6,3)];
            var applied = config.Apply(map,input,Resolve);
            var commonRule = (JsonObject)rule.DeepClone();
            foreach (string endpoint in new[] { "from", "to" })
                commonRule[endpoint] = new JsonObject { ["all"] = rule[endpoint]!["800"]!.DeepClone() };
            var commonConfig = MapGeometryConfig.Parse(GeometryDocument([commonRule]));
            That(commonConfig.Apply(map,input,Resolve).SequenceEqual(applied) && commonConfig.SignatureKind(map) == config.SignatureKind(map), "紧凑all端点与旧三比例端点的实际几何等效");
            foreach (string endpoint in new[] { "from", "to" })
            {
                foreach (var invalidEndpoint in new JsonObject[]
                {
                    new() { ["all"] = rule[endpoint]!["800"]!.DeepClone(), ["800"] = rule[endpoint]!["800"]!.DeepClone() },
                    new() { ["all"] = rule[endpoint]!["800"]!.DeepClone(), ["unknown"] = new JsonArray() },
                    new() { ["800"] = rule[endpoint]!["800"]!.DeepClone(), ["1067"] = rule[endpoint]!["1067"]!.DeepClone() },
                    new() { ["ALL"] = rule[endpoint]!["800"]!.DeepClone() }
                })
                {
                    var invalidRule = (JsonObject)rule.DeepClone(); invalidRule[endpoint] = invalidEndpoint;
                    Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([invalidRule])), "all端点不得混键、未知键、缺少旧比例或大小写变体");
                }
            }
            That(applied.Skip(1).Take(8).Select(atom => atom.Value).SequenceEqual(target) && input[3].Value == 100 && applied[^1] == input[^1], "副本门完整八整数允许零/负值且只改守卫坐标");
            That(config.SignatureKind(map) == "DCG2" && config.Apply(map,applied,Resolve).SequenceEqual(applied), "副本门使用独立签名且目标幂等");
            var bad = input.ToArray(); bad[8] = new(0,201);
            Reject<InvalidDataException>(() => config.Apply(map,bad,Resolve), "副本门任一完整端点冲突拒绝");
            foreach (int count in new[] { 4,9 })
                Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([GeometryRule(kind,map,kind,tag,GeometryAtoms(original.Take(count).Concat(count == 9 ? [0] : [])),GeometryAtoms(target.Take(count).Concat(count == 9 ? [0] : [])))])), "副本门未知短/长记录不得猜补");
            var overflow = (JsonObject)rule.DeepClone(); overflow["to"]!["800"]![0]![1] = 1048577;
            foreach (string profile in new[] { "1067", "960" }) overflow["to"]![profile] = overflow["to"]!["800"]!.DeepClone();
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([overflow])), "副本门整数越界拒绝");
        }

        int[] pass = [0,1,2,5,6,9,0,0,0,0,0,0,0,0];
        var aliases = new JsonObject { [tile] = "map/test/original.til" };
        var passRule = GeometryRule("pass",tile,"tile_pass14","[pass type]",GeometryAtoms(pass),GeometryAtoms(pass.Select((value,index) => index == 13 ? 2 : value)));
        var passConfig = MapGeometryConfig.Parse(GeometryDocument([passRule],aliases));
        PvfToken[] passTokens = [new(3,1),..pass.Select(value => new PvfToken(0,value))];
        string PassResolve(int id) => id == 1 ? "[pass type]" : throw new InvalidDataException("fixture string");
        var passOutput = passConfig.Apply(tile,passTokens,PassResolve);
        That(passOutput[^1].Value == 2 && passTokens[^1].Value == 0 && passConfig.SignatureKind(tile) == "DCT1", "TIL通行格保持十四列且保留非0/2合法值");
        That(passConfig.Apply(tile,passOutput,PassResolve).SequenceEqual(passOutput), "TIL通行格目标幂等");
        foreach (var badValues in new[] { pass[..13], pass.Append(0).ToArray(), pass.Select((value,index) => index == 0 ? -1 : value).ToArray(), pass.Select((value,index) => index == 0 ? 256 : value).ToArray() })
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([GeometryRule("bad",tile,"tile_pass14","[pass type]",GeometryAtoms(badValues),GeometryAtoms(badValues))],aliases)), "TIL半行或非byte格拒绝");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([GeometryRule("rows",tile,"tile_pass14","[pass type]",GeometryAtoms(pass),GeometryAtoms(pass.Concat(pass)))],aliases)), "本机制不允许顺便增加碰撞行数");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([passRule])), "通行改写只能落到声明的私有TIL别名");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([],aliases)), "每个别名必须具备首段完整pass守卫");
        var extraPass = (JsonObject)passRule.DeepClone(); extraPass["id"] = "duplicate";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([passRule,extraPass],aliases)), "重复通行段守卫拒绝");
        foreach (var badAliases in new[]
        {
            new JsonObject { [tile] = "map/test/displaycompat/chain.til" },
            new JsonObject { ["map/test/nonprivate.til"] = "map/test/original.til" },
            new JsonObject { [tile] = "../outside.til" },
            new JsonObject { [tile] = "map/test/original.map" }
        }) Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([passRule],badAliases)), "别名不允许链/外部/非TIL或越界命名空间");

        const string tileMap = "map/test/room.map", aliasReference = "displaycompat/grid.til";
        var tileRule = GeometryRule("tiles",tileMap,"dungeon_tiles","[tile]",
            GeometryStrings("a.til","b.til","c.til","native.til"),GeometryStrings("a.til","b.til","c.til",aliasReference,aliasReference));
        var tileConfig = MapGeometryConfig.Parse(GeometryDocument([tileRule,passRule],aliases));
        string TileResolve(int id) => id switch { 1 => "[tile]", 2 => "[/tile]", 3 => "[map name]", 4 => "display only", 10 => "a.til", 11 => "b.til", 12 => "c.til", 13 => "native.til", _ => throw new InvalidDataException("fixture string") };
        PvfToken[] tileInput = [new(3,1),new(6,10),new(6,11),new(6,12),new(6,13),new(3,2),new(3,3),new(6,4)];
        var tileOutput = tileConfig.Apply(tileMap,tileInput,TileResolve);
        That(tileOutput.Length == tileInput.Length + 1 && tileOutput.Take(4).SequenceEqual(tileInput.Take(4)) && tileOutput.Skip(6).SequenceEqual(tileInput.Skip(5)), "追加TIL只改变完整tile段且保留前三原始池ID及后续标签");
        That(tileOutput[4].Value < 0 && tileConfig.ResolveString(tileOutput[4].Value,TileResolve) == aliasReference && tileConfig.ResolveString(tileOutput[5].Value,TileResolve) == aliasReference, "新TIL引用使用配置冻结虚拟字符串，不污染PVF池");
        That(tileConfig.Apply(tileMap,tileOutput,TileResolve).SequenceEqual(tileOutput), "4到5追加方案使用完整目标守卫保持幂等");
        That(tileInput.Length == 8 && tileInput[4].Value == 13, "源TIL列表未被改写");
        foreach (string bad in new[] { "unknown.til", "../../../../outside.til", "map/test/original.til", tile })
        {
            var invalid = (JsonObject)tileRule.DeepClone();
            foreach (string profile in new[] { "800", "1067", "960" }) invalid["to"]![profile]![4]![1] = bad;
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([invalid,passRule],aliases)), "新增TIL只能绑定本配置已守卫别名");
        }
        var firstChanged = (JsonObject)tileRule.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) firstChanged["to"]![profile]![0]![1] = "changed.til";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([firstChanged,passRule],aliases)), "追加方案不得改前三块原始地板");

        var fileRule = GeometryRule("ext-files",tileMap,"dungeon_ext_tilefiles","[tile files]",
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"extended_native.til")),
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"extended_native.til"),new JsonArray(0,1),new JsonArray(6,aliasReference),new JsonArray(0,2),new JsonArray(6,aliasReference)));
        int[] originalGrid = [0,-1,0,0,0,0,-1,-1], targetGrid = [0,-1,0,1,2,0,0,-1,-1,-1];
        var matrixRule = GeometryRule("ext-matrix",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid),GeometryAtoms(targetGrid));
        var extConfig = MapGeometryConfig.Parse(GeometryDocument([tileRule,passRule,fileRule,matrixRule],aliases));
        string ExtResolve(int id) => id switch { 20 => "[extended tile]", 21 => "[tile files]", 22 => "[tile map]", 23 => "[/extended tile]", 30 => "extended_native.til", _ => TileResolve(id) };
        PvfToken[] extInput = [..tileInput.Take(6),new(3,20),new(3,21),new(0,0),new(6,30),new(3,22),..originalGrid.Select(value => new PvfToken(0,value)),new(3,23),..tileInput.Skip(6)];
        var extOutput = extConfig.Apply(tileMap,extInput,ExtResolve);
        PvfToken[] Body(PvfToken[] tokens, int tagId) => tokens.SkipWhile(token => token.Type != 3 || token.Value != tagId).Skip(1).TakeWhile(token => token.Type != 3).ToArray();
        var fileBody = Body(extOutput,21);
        That(fileBody.Length == 6 && fileBody.Take(2).SequenceEqual(Body(extInput,21)) && fileBody[2] == new PvfToken(0,1) && extConfig.ResolveString(fileBody[3].Value,ExtResolve) == aliasReference, "扩展TIL保留原ID/路径前缀，只追加受守卫别名");
        That(Body(extOutput,22).Select(token => token.Value).SequenceEqual(targetGrid) && extOutput.Length == extInput.Length + 7, "扩展矩阵每行保留前三格、末格拆为新原生/扩展别名，空格哨兵保持");
        That(extConfig.Apply(tileMap,extOutput,ExtResolve).SequenceEqual(extOutput), "主TIL与两扩展段共同应用后幂等");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tileRule,passRule,matrixRule],aliases)), "孤立扩展矩阵缺少文件索引拒绝");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([passRule,fileRule,matrixRule],aliases)), "扩展矩阵缺少主地板共同方案拒绝");
        foreach (var invalidRules in new[]
        {
            new[] { fileRule,GeometryRule("bad",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid),GeometryAtoms(targetGrid.Select((value,index) => index == 4 ? 3 : value))) },
            new[] { fileRule,GeometryRule("bad",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid.Select((value,index) => index == 0 ? 3 : value)),GeometryAtoms(targetGrid.Select((value,index) => index == 0 ? 3 : value))) },
            new[] { fileRule,GeometryRule("bad",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid),GeometryAtoms(targetGrid.Select((value,index) => index == 0 ? -1 : value))) },
            new[] { fileRule,GeometryRule("bad",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid),GeometryAtoms(targetGrid.Select((value,index) => index == 3 ? 0 : value))) },
            new[] { fileRule,GeometryRule("bad",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid),GeometryAtoms(targetGrid.Select((value,index) => index == 9 ? 2 : value))) },
            new[] { fileRule,GeometryRule("bad",tileMap,"dungeon_ext_tilemap","[tile map]",GeometryAtoms(originalGrid),GeometryAtoms(targetGrid.Append(0))) }
        }) Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument(new[] {tileRule,passRule}.Concat(invalidRules),aliases)), "扩展矩阵未知ID/原格变化/行列形状错误拒绝");
        var duplicateId = (JsonObject)fileRule.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) duplicateId["to"]![profile]![2]![1] = 0;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tileRule,passRule,duplicateId,matrixRule],aliases)), "扩展文件新增ID不得覆盖原身份");
        var absoluteFile = (JsonObject)fileRule.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) absoluteFile["to"]![profile]![3]![1] = tile;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tileRule,passRule,absoluteFile,matrixRule],aliases)), "扩展TIL路径沿原生目录拼接，只允许安全相对引用");
        Reject<InvalidDataException>(() => extConfig.Apply(tileMap,extInput.Where(token => token != new PvfToken(3,20)).ToArray(),ExtResolve), "扩展规则必须处于原有完整extended tile容器内");
        Reject<InvalidDataException>(() => extConfig.Apply(tileMap,extInput.Where(token => token != new PvfToken(3,23)).ToArray(),ExtResolve), "扩展容器缺少闭合拒绝");
        var duplicatePassTag = passTokens.Concat(passTokens).ToArray();
        Reject<InvalidDataException>(() => passConfig.Apply(tile,duplicatePassTag,PassResolve), "私有通行格不得选择多个重复段之一");

        const string townHex = "444347310C0000006D61702F746573742E6D617001000000130000005B746F776E206D6F7661626C6520617265615D0000000006000000000000006E0000000000000014000000000000001E000000000000002800000000000000270000000000000000000000";
        PvfToken[] townTokens = [new(3,1),..new[] {110,20,30,40,39,0}.Select(value => new PvfToken(0,value)),new(3,2)];
        That(Convert.ToHexString(MapGeometrySignature.Canonical(map,townTokens,id => id == 1 ? "[town movable area]" : "[/town movable area]")) == townHex, "旧城镇DCG1逐字节黄金保持");
        const string dungeonHex = "444347320C0000006D61702F746573742E6D6170010000000E0000005B706174686761746520706F735D000000000800000000000000FFFFFFFF000000000000000000000000640000000000000000000000000000001400000000000000FBFFFFFF000000000000000000000000C8000000";
        PvfToken[] dungeonTokens = [new(3,1),..new[] {-1,0,100,0,20,-5,0,200}.Select(value => new PvfToken(0,value))];
        That(Convert.ToHexString(MapGeometrySignature.CanonicalDungeon(map,dungeonTokens,_ => "[pathgate pos]")) == dungeonHex, "DCG2无闭合标量段逐字节黄金");
        const string tileHex = "444354310C0000006D61702F746573742E74696C010000000B0000005B7061737320747970655D000000000E00000000000000000000000000000001000000000000000200000000000000050000000000000006000000000000000900000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000";
        That(Convert.ToHexString(MapGeometrySignature.CanonicalTile("map/test.til",passTokens,PassResolve)) == tileHex, "DCT1无闭合通行段逐字节黄金");
        var afterText = dungeonTokens.Concat(new[] { new PvfToken(3,2), new PvfToken(6,3) }).ToArray();
        That(MapGeometrySignature.ComputeDungeon(map,afterText,id => id == 1 ? "[pathgate pos]" : id == 2 ? "[map name]" : "ignored").SequenceEqual(MapGeometrySignature.ComputeDungeon(map,dungeonTokens,_ => "[pathgate pos]")), "副本标量签名在下一个标签终止，不签显示文字");
        foreach (string tag in new[] { "[tile]", "[tile files]", "[tile map]", "[import script]", "[force map width]", "[ignore movable rect]", "[camera edge exception]" })
        {
            PvfToken[] binding = [new(3,1),new(0,1)];
            var signature = MapGeometrySignature.ComputeDungeon(map,binding,_ => tag);
            binding[1] = new(0,2);
            That(!signature.SequenceEqual(MapGeometrySignature.ComputeDungeon(map,binding,_ => tag)), "副本TIL/扩展索引/导入/强制宽度改变均进入DCG2：" + tag);
        }
        PvfToken[] conditional = [new(3,1),new(0,10),new(0,20),new(0,30),new(0,40),new(3,2),new(0,77),new(3,3)];
        string ConditionResolve(int id) => id switch { 1 => "[virtual movable area]", 2 => "[if quest]", 3 => "[/virtual movable area]", _ => throw new InvalidDataException("fixture string") };
        var conditionalSignature = MapGeometrySignature.ComputeDungeon(map,conditional,ConditionResolve);
        conditional[6] = new(0,78);
        That(!conditionalSignature.SequenceEqual(MapGeometrySignature.ComputeDungeon(map,conditional,ConditionResolve)), "DCG2也保留旧虚拟活动区的完整条件子段");
        Reject<InvalidDataException>(() => MapGeometrySignature.ComputeDungeon(map,conditional[..^1],ConditionResolve), "DCG2旧几何块仍要求原完整闭合");
        foreach (var invalid in new[] { Array.Empty<PvfToken>(), passTokens[..^1], passTokens.Concat(passTokens).ToArray() })
            Reject<InvalidDataException>(() => MapGeometrySignature.CanonicalTile("map/test.til",invalid,PassResolve), "DCT1缺失/半行/重复通行段拒绝");
        RunCapabilityCore();
    }

    private static void RunCapabilityCore()
    {
        const string root = "map/cap/room.map", alias = "map/cap/displaycompat/grid.til", town = "map/town.map";
        int[] gate = [10,20,100,20,30,40,50,60];
        int[] pass = Enumerable.Repeat(0,14).ToArray();
        var rules = new[]
        {
            GeometryRule("root",root,"gate_points8","[pathgate pos]",GeometryAtoms(gate),GeometryAtoms(gate)),
            GeometryRule("alias",alias,"tile_pass14","[pass type]",GeometryAtoms(pass),GeometryAtoms(pass))
        };
        var config = MapGeometryConfig.Parse(GeometryDocument(rules,new JsonObject { [alias] = "map/cap/original.til" }));
        var pool = new List<string>();
        PvfToken Text(byte kind, string value)
        {
            int id = pool.IndexOf(value);
            if (id < 0) { id = pool.Count; pool.Add(value); }
            return new(kind,id);
        }
        PvfToken Tag(string value) => Text(3,value);
        PvfToken Name(string value) => Text(6,value);
        PvfToken[] Tile() => [Tag("[pass type]"),..pass.Select(value => new PvfToken(0,value))];
        var resources = new Dictionary<string, PvfToken[]>(StringComparer.Ordinal)
        {
            [root] = [Tag("[tile]"),Name("a.til"),Name("../common/shared.til"),Name("c.til"),Name("displaycompat/grid.til"),
                Tag("[extended tile]"),Tag("[tile files]"),new(0,9),Name("ext.til"),Tag("[tile map]"),new(0,9),Tag("[/extended tile]"),
                Tag("[pathgate pos]"),..gate.Select(value => new PvfToken(0,value)),Tag("[import script]"),Name("local.map"),Name("common/root.map"),Name("common/marked.map")],
            ["map/cap/a.til"] = Tile(), ["map/common/shared.til"] = Tile(), ["map/cap/c.til"] = Tile(),
            ["map/cap/ext.til"] = Tile(), ["map/cap/original.til"] = Tile(),
            ["map/cap/local.map"] = [Tag("[tile]"),Name("a.til")],
            ["map/common/root.map"] = [Tag("[map name]"),Name("root fallback")],
            ["map/common/(r)marked.map"] = [Tag("[map name]"),Name("regional fallback")],
            [town] = [Tag("[town movable area]"),..new[] {10,20,30,40,39,0}.Select(value => new PvfToken(0,value)),Tag("[/town movable area]")]
        };
        string Resolve(int id) => config.ResolveString(id,index => pool[index]);
        bool Contains(string path) => resources.ContainsKey(path) || config.Aliases.ContainsKey(path);
        PvfToken[] Read(string path) => config.Apply(path, resources[config.Aliases.GetValueOrDefault(path,path)], index => pool[index]);
        IReadOnlyDictionary<string, byte[]> Build() => MapGeometryCapabilities.BuildCore(config,[town],Contains,Read,Resolve);
        var signatures = Build();
        That(signatures.Count == 10 && new[] {"map/cap/a.til","map/common/shared.til","map/cap/c.til","map/cap/ext.til",alias}.All(signatures.ContainsKey), "副本闭包包含前三原生TIL、扩展TIL及私有别名，不仅限world规则");
        That(signatures.ContainsKey("map/cap/local.map") && signatures.ContainsKey("map/common/root.map") && signatures.ContainsKey("map/common/(r)marked.map"), "导入按相对、map根及区域标记回退解析");
        That(signatures[town].SequenceEqual(MapGeometrySignature.Compute(town,resources[town],Resolve)), "既有城镇继续保留DCG1签名");
        That(signatures["map/cap/local.map"].SequenceEqual(MapGeometrySignature.ComputeDungeon("map/cap/local.map",resources["map/cap/local.map"],Resolve)), "无world规则的递归MAP仍签DCG2");
        var original = resources["map/cap/a.til"];
        resources["map/cap/a.til"] = [..original[..^1],new(0,2)];
        That(!Build()["map/cap/a.til"].SequenceEqual(signatures["map/cap/a.til"]), "原生前三块碰撞改变进入完整许可签名");
        resources.Remove("map/cap/a.til");
        Reject<InvalidDataException>(() => Build(), "原生TIL缺失不得跳过");
        resources["map/cap/a.til"] = original;
        var local = resources["map/cap/local.map"];
        foreach (string bad in new[] { "missing.map", "../../../escape.map", "room.map" })
        {
            resources["map/cap/local.map"] = [Tag("[import script]"),Name(bad)];
            Reject<InvalidDataException>(() => Build(), "未知、越根或循环导入拒绝");
        }
        resources["map/cap/local.map"] = local;
        resources["map/cap/local.map"] = [Tag("[tile]"),Name("../../../escape.til")];
        Reject<InvalidDataException>(() => Build(), "TIL相对路径不得逃出map根");
        resources["map/cap/local.map"] = local;
        resources["map/common/root.map"] = [Tag("[import script]"),Name("../town.map")];
        Reject<InvalidDataException>(() => Build(), "单一路径不得混用旧城镇DCG1及副本DCG2");
        That(MapGeometryCapabilities.BuildCore(MapGeometryConfig.Empty,
            Enumerable.Range(0,MapGeometryCapabilities.MaxNodes + 1).Select(index => $"map/budget/{index}.map"), _ => true, _ => [], _ => "").Count == MapGeometryCapabilities.MaxNodes + 1,
            "独立地图组成的累计快照不得误用单图节点预算");
        resources["map/common/root.map"] = [Tag("[map name]"),Name("root fallback")];
        resources["map/cap/local.map"] = [Tag("[import script]"),Name("chain0.map")];
        for (int index = 0; index < 514; ++index)
            resources[$"map/cap/chain{index}.map"] = index == 513 ? [] : [Tag("[import script]"),Name($"chain{index + 1}.map")];
        Reject<InvalidDataException>(() => Build(), "单图导入超过512层须与客户端同样拒绝");
        resources["map/cap/local.map"] = [Tag("[tile]"),..Enumerable.Range(0,MapGeometryCapabilities.MaxNodes).Select(index => Name($"budget{index}.til"))];
        for (int index = 0; index < MapGeometryCapabilities.MaxNodes; ++index) resources[$"map/cap/budget{index}.til"] = Tile();
        Reject<InvalidDataException>(() => Build(), "单图完整依赖超过32768节点仍拒绝");
        var command = Application.ServerCommand.Parse(["--selftest-map-geometry-golden","world.json","input.pvf","vectors.bin"]);
        That(command.Kind == Application.ServerCommandKind.MapGeometryGolden && command.Arguments.Length == 3 && command.Options.Count == 0, "黄金CLI使用离线早期命令，不装配监听或数据库选项");
        foreach (string[] args in new[] { new[] {"--selftest-map-geometry-golden"}, new[] {"--selftest-map-geometry-golden","", "input.pvf","out"}, new[] {"--selftest-map-geometry-golden","world","input","out","extra"} })
            Reject<InvalidDataException>(() => Application.ServerCommand.Parse(args), "黄金CLI参数缺失、空白或超额拒绝");
    }

    // 新副本向量包含全部真实碰撞/导入闭包；不改城镇30图入口或创建运行服。
    public static void RunDungeonGolden(string configuration, string pvfPath, string vectors)
    {
        int checks = Count;
        var config = MapGeometryConfig.Parse(File.ReadAllText(configuration));
        using var pvf = PvfArchive.Open(pvfPath);
        string Resolve(int id) => config.ResolveString(id,pvf.ResolveString);
        var capabilities = MapGeometryCapabilities.Build(pvf,config,[]);
        var oldPaths = config.Paths.Where(path => config.SignatureKind(path) == "DCG1").ToHashSet(StringComparer.Ordinal);
        string[] paths = capabilities.Keys.Where(path => !oldPaths.Contains(path)).Order(StringComparer.Ordinal).ToArray();
        using var writer = new BinaryWriter(File.Create(vectors),Encoding.UTF8);
        void WriteString(string value) { byte[] utf8 = Encoding.UTF8.GetBytes(value); writer.Write(utf8.Length); writer.Write(utf8); }
        writer.Write("DGG2"u8); writer.Write(paths.Length);
        foreach (string path in paths)
        {
            var tokens = config.ReadTokens(pvf,path);
            WriteString(path);
            writer.Write(path.EndsWith(".til",StringComparison.Ordinal) ? 4 : config.SignatureKind(path) == "DCG3" ? 3 : 2);
            writer.Write(tokens.Length);
            foreach (var token in tokens)
            {
                writer.Write((uint)token.Type);
                if (token.Type is 3 or 6) WriteString(Resolve(token.Value)); else writer.Write(token.Value);
            }
            byte[] signature = capabilities[path];
            writer.Write(signature);
            That(signature.Length == 32 && config.Apply(path,tokens,pvf.ResolveString).SequenceEqual(tokens), "实际副本/别名TIL黄金签名及幂等：" + path);
        }
        That(paths.Length > 0 && config.Aliases.Keys.All(path => paths.Contains(path,StringComparer.Ordinal)), "全部私有TIL别名具有独立pass黄金向量");
        Console.WriteLine($"PASS dungeon geometry golden {Count - checks}; input={pvfPath}; resources={paths.Length}");
    }
}
