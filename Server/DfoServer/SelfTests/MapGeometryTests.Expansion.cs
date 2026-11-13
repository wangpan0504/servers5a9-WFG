using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunAdditionalExpansionCore()
    {
        RunSingleColumnCore();
        RunThreeColumnCore();
        RunAnimationAppendCore();
        RunVirtualInsertionCore();
        RunFourVirtualAppendCore();
    }

    private static void RunSingleColumnCore()
    {
        const string map = "map/single/room.map", source = "map/single/floor.til";
        string[] references = Enumerable.Range(0,5).Select(index => $"displaycompat/column{index}.til").ToArray();
        var aliases = new JsonObject();
        var passes = new List<JsonObject>();
        foreach (string reference in references)
        {
            string alias = "map/single/" + reference;
            aliases[alias] = source;
            passes.Add(GeometryRule("pass-" + passes.Count,alias,"tile_pass14","[pass type]",
                GeometryAtoms(Enumerable.Repeat(2,14)),GeometryAtoms(Enumerable.Repeat(2,14))));
        }
        var tiles = GeometryRule("tiles",map,"dungeon_tiles1","[tile]",GeometryStrings("floor.til"),GeometryStrings(references));
        string Document(JsonObject? tileRule = null, JsonObject? aliasRows = null, IEnumerable<JsonObject>? extra = null) =>
            GeometryDocument(new[] { tileRule ?? tiles }.Concat(passes).Concat(extra ?? []),aliasRows ?? aliases);
        var config = MapGeometryConfig.Parse(Document());
        string[] pool = ["[tile]","floor.til","[/tile]","[map name]","unchanged"];
        PvfToken[] raw = [new(3,0),new(6,1),new(3,2),new(3,3),new(6,4)];
        string Resolve(int id) => config.ResolveString(id,index => pool[index]);
        var output = config.Apply(map,raw,index => pool[index]);
        That(output.Length == raw.Length + 4 && output.Skip(1).Take(5).All(token => token.Type == 6) &&
            output.Skip(1).Take(5).Select(token => Resolve(token.Value)).SequenceEqual(references) && output.Skip(6).SequenceEqual(raw.Skip(2)),
            "单列精确派生五个私有别名并保持其他原始段");
        That(config.Apply(map,output,index => pool[index]).SequenceEqual(output) && raw[1] == new PvfToken(6,1),
            "单列共同目标幂等且不写源token");
        That(config.SignatureKind(map) == "DCG2" && !MapGeometrySignature.ComputeDungeon(map,raw,index => pool[index]).SequenceEqual(
            MapGeometrySignature.ComputeDungeon(map,output,Resolve)), "单列五块进入既有DCG2完整几何签名");
        var common = (JsonObject)tiles.DeepClone();
        foreach (string endpoint in new[] { "from", "to" }) common[endpoint] = new JsonObject { ["all"] = tiles[endpoint]!["800"]!.DeepClone() };
        var commonConfig = MapGeometryConfig.Parse(Document(tileRule:common));
        That(commonConfig.Apply(map,raw,index => pool[index]).Select(token => (token.Type,Value:token.Type == 6 ? commonConfig.ResolveString(token.Value,index => pool[index]) : token.Value.ToString())).SequenceEqual(
            output.Select(token => (token.Type,Value:token.Type == 6 ? Resolve(token.Value) : token.Value.ToString()))), "单列all端点与三个完整比例端点等效");
        foreach (string profile in new[] { "800", "1067", "960" })
        {
            var wrongSource = (JsonObject)tiles.DeepClone(); wrongSource["from"]![profile]![0]![1] = "other.til";
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:wrongSource)), "每个比例的单列源必须等于全部五个别名源");
            var wrongWidth = (JsonObject)tiles.DeepClone(); ((JsonArray)wrongWidth["from"]![profile]!).Add(new JsonArray(6,"floor.til"));
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:wrongWidth)), "单列源不能伪装成多列");
            var wrongType = (JsonObject)tiles.DeepClone(); wrongType["from"]![profile]![0] = new JsonArray(0,1);
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:wrongType)), "单列源必须是字符串token");
        }
        for (int slot = 0; slot < 5; ++slot)
        {
            var wrongAliases = (JsonObject)aliases.DeepClone(); wrongAliases["map/single/" + references[slot]] = "map/single/other.til";
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(aliasRows:wrongAliases)), "全部五个别名均须保留唯一原始源");
            var unguarded = (JsonObject)tiles.DeepClone();
            foreach (string profile in new[] { "800", "1067", "960" }) unguarded["to"]![profile]![slot]![1] = "floor.til";
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:unguarded)), "目标五列不能夹带未经私有别名守卫的原始引用");
            var mixed = output.ToArray(); mixed[slot + 1] = raw[1];
            Reject<InvalidDataException>(() => config.Apply(map,mixed,index => pool[index]), "运行时拒绝五列中混入原始引用的部分应用状态");
        }
        var shortTarget = GeometryRule("tiles",map,"dungeon_tiles1","[tile]",GeometryStrings("floor.til"),GeometryStrings(references[..4]));
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:shortTarget)), "单列目标必须精确五列");
        var wrongTargetType = (JsonObject)tiles.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) wrongTargetType["to"]![profile]![0] = new JsonArray(0,1);
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:wrongTargetType)), "单列目标必须全部字符串token");
        Reject<InvalidDataException>(() => config.Apply(map,[..raw,..raw],index => pool[index]), "单列必须唯一主段");
        Reject<InvalidDataException>(() => config.Apply(map,[raw[0],new(0,1),..raw.Skip(2)],index => pool[index]), "运行时单列源类型不匹配拒绝");
        foreach (int columns in new[] { 3,4 })
        {
            var files = GeometryRule("files",map,"dungeon_ext_tilefiles","[tile files]",
                new JsonArray(new JsonArray(0,0),new JsonArray(6,"floor.til")),
                new JsonArray(new JsonArray(0,0),new JsonArray(6,"floor.til"),new JsonArray(0,1),new JsonArray(6,references[0])));
            int[] before = Enumerable.Repeat(0,columns).ToArray();
            int[] after = Enumerable.Repeat(0,columns - 1).Concat(Enumerable.Repeat(1,6 - columns)).ToArray();
            var matrix = GeometryRule("matrix",map,columns == 3 ? "dungeon_ext_tilemap3" : "dungeon_ext_tilemap","[tile map]",GeometryAtoms(before),GeometryAtoms(after));
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(extra:[files,matrix])), "单列根不得借用三列或四列扩展矩阵");
        }
    }

    private static void RunFourVirtualAppendCore()
    {
        const string map = "map/waiting/room.map", alias = "map/waiting/displaycompat/last.til";
        var aliases = new JsonObject { [alias] = "map/waiting/c.til" };
        var pass = GeometryRule("pass",alias,"tile_pass14","[pass type]",GeometryAtoms(Enumerable.Repeat(0,14)),GeometryAtoms(Enumerable.Repeat(0,14)));
        var tiles = GeometryRule("base",map,"dungeon_tiles3","[tile]",GeometryStrings("a.til","b.til","c.til"),GeometryStrings("a.til","b.til","displaycompat/last.til","displaycompat/last.til","displaycompat/last.til"));
        int[] before = [11,152,670,70,12,209,670,70,10,253,670,70,11,300,670,70];
        int[] after = before.Select((value,index) => index % 4 == 2 ? value + 448 : value).ToArray();
        var rule = GeometryRule("virtual4",map,"dungeon_virtual_xywh4_append","[virtual movable area]",GeometryAtoms(before),GeometryAtoms(after));
        var config = MapGeometryConfig.Parse(GeometryDocument([tiles,pass,rule],aliases));
        string[] pool = ["[tile]","a.til","b.til","c.til","[virtual movable area]","[/virtual movable area]"];
        PvfToken[] raw = [new(3,0),new(6,1),new(6,2),new(6,3),new(3,4),..before.Select(value => new PvfToken(0,value)),new(3,5)];
        var output = config.Apply(map,raw,index => pool[index]);
        That(output.Skip(7).Take(16).Select(token => token.Value).SequenceEqual(after), "三到五列的四个实际通行矩形逐条宽增加448，X/Y/H保持");
        That(config.Apply(map,output,index => pool[index]).SequenceEqual(output) && config.SignatureKind(map) == "DCG2", "四矩形共同派生幂等且沿用DCG2");
        Reject<InvalidDataException>(() => config.Apply(map,raw[..^1],index => pool[index]), "四矩形段缺闭合拒绝");
        Reject<InvalidDataException>(() => config.Apply(map,[..raw,..raw],index => pool[index]), "四矩形段重复拒绝");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([pass,rule],aliases)), "四矩形必须绑定同图三到五列根");
        foreach (int index in new[] {0,1,2,3,6,10,14})
        {
            int[] bad = after.ToArray(); bad[index]++;
            var invalid = GeometryRule("virtual4",map,"dungeon_virtual_xywh4_append","[virtual movable area]",GeometryAtoms(before),GeometryAtoms(bad));
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tiles,pass,invalid],aliases)), "四矩形只能按精确448宽度追加");
        }
    }

    private static void RunVirtualInsertionCore()
    {
        const string map = "map/virtual/room.map", alias = "map/virtual/displaycompat/last.til";
        var aliases = new JsonObject { [alias] = "map/virtual/d.til" };
        var pass = GeometryRule("pass",alias,"tile_pass14","[pass type]",GeometryAtoms(Enumerable.Repeat(0,14)),GeometryAtoms(Enumerable.Repeat(0,14)));
        var tiles = GeometryRule("base",map,"dungeon_tiles","[tile]",GeometryStrings("a.til","b.til","c.til","d.til"),GeometryStrings("a.til","b.til","c.til","displaycompat/last.til","displaycompat/last.til"));
        foreach (var (before,after) in new[] { (new[] {8,192,900,128},new[] {8,192,1124,128}), (new[] {191,194,520,155},new[] {191,194,744,155}) })
        {
            var rule = GeometryRule("virtual",map,"dungeon_virtual_xywh1","[virtual movable area]",GeometryAtoms(before),GeometryAtoms(after));
            var config = MapGeometryConfig.Parse(GeometryDocument([tiles,pass,rule],aliases));
            string[] pool = ["[tile]","a.til","b.til","c.til","d.til","[virtual movable area]","[/virtual movable area]"];
            PvfToken[] raw = [new(3,0),new(6,1),new(6,2),new(6,3),new(6,4),new(3,5),..before.Select(value => new PvfToken(0,value)),new(3,6)];
            var output = config.Apply(map,raw,index => pool[index]);
            That(output.Skip(7).Take(4).Select(token => token.Value).SequenceEqual(after), "虚拟通行域按真实XYWH跨缝扩展，X/Y/H保持");
            That(config.Apply(map,output,index => pool[index]).SequenceEqual(output) && config.SignatureKind(map) == "DCG2", "虚拟通行域与主拓扑共同幂等，纳入既有DCG2");
            Reject<InvalidDataException>(() => config.Apply(map,raw[..^1],index => pool[index]), "虚拟通行域必须真实闭合");
            Reject<InvalidDataException>(() => config.Apply(map,[..raw,..raw],index => pool[index]), "虚拟通行域不选择多个同名段之一");
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([pass,rule],aliases)), "虚拟域适配必须绑定四到五列主拓扑");
            int[] bad = after.ToArray(); bad[1]++;
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tiles,pass,GeometryRule("virtual",map,"dungeon_virtual_xywh1","[virtual movable area]",GeometryAtoms(before),GeometryAtoms(bad))],aliases)), "虚拟域不得偏移原Y");
        }
    }

    private static void RunThreeColumnCore()
    {
        const string map = "map/three/room.map", alias = "map/three/displaycompat/terminal.til";
        var aliases = new JsonObject { [alias] = "map/three/c.til" };
        var pass = GeometryRule("pass",alias,"tile_pass14","[pass type]",GeometryAtoms(Enumerable.Repeat(2,14)),GeometryAtoms(Enumerable.Repeat(2,14)));
        var tiles = GeometryRule("tiles",map,"dungeon_tiles3","[tile]",GeometryStrings("a.til","b.til","c.til"),
            GeometryStrings("a.til","b.til","displaycompat/terminal.til","displaycompat/terminal.til","displaycompat/terminal.til"));
        var files = GeometryRule("files",map,"dungeon_ext_tilefiles","[tile files]",
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"c.til")),
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"c.til"),new JsonArray(0,1),new JsonArray(6,"displaycompat/terminal.til"),
                new JsonArray(0,2),new JsonArray(6,"displaycompat/terminal.til"),new JsonArray(0,3),new JsonArray(6,"displaycompat/terminal.til")));
        int[] source = [0,-1,0,0,0,-1], target = [0,-1,1,2,3,0,0,-1,-1,-1];
        var matrix = GeometryRule("matrix",map,"dungeon_ext_tilemap3","[tile map]",GeometryAtoms(source),GeometryAtoms(target));
        string Document(JsonObject? tileRule = null, JsonObject? fileRule = null, JsonObject? matrixRule = null, JsonObject? aliasRows = null) =>
            GeometryDocument([tileRule ?? tiles,pass,fileRule ?? files,matrixRule ?? matrix],aliasRows ?? aliases);
        var config = MapGeometryConfig.Parse(Document());
        var pool = new List<string>();
        PvfToken Text(byte type, string value)
        {
            int id = pool.IndexOf(value); if (id < 0) { id = pool.Count; pool.Add(value); }
            return new(type,id);
        }
        string Resolve(int id) => config.ResolveString(id,index => pool[index]);
        PvfToken[] raw = [Text(3,"[tile]"),Text(6,"a.til"),Text(6,"b.til"),Text(6,"c.til"),Text(3,"[/tile]"),
            Text(3,"[extended tile]"),Text(3,"[tile files]"),new(0,0),Text(6,"c.til"),Text(3,"[tile map]"),
            ..source.Select(value => new PvfToken(0,value)),Text(3,"[/extended tile]")];
        var output = config.Apply(map,raw,index => pool[index]);
        int tileEnd = Array.FindIndex(output,token => token.Type == 3 && Resolve(token.Value) == "[/tile]");
        int matrixStart = Array.FindIndex(output,token => token.Type == 3 && Resolve(token.Value) == "[tile map]");
        That(tileEnd == 6 && output.Take(3).SequenceEqual(raw.Take(3)) &&
            output.Skip(3).Take(3).All(token => token.Type == 6 && Resolve(token.Value) == "displaycompat/terminal.til"), "三到五列只保前二，末三使用原终端源的受管别名");
        That(output.Skip(matrixStart + 1).Take(target.Length).Select(token => token.Value).SequenceEqual(target), "三列扩展矩阵末格拆三格，空末格保持三格全负一");
        That(config.Apply(map,output,index => pool[index]).SequenceEqual(output) && raw[3] == Text(6,"c.til"), "三列主地板与扩展文件/矩阵共同应用幂等且源不变");
        That(config.SignatureKind(map) == "DCG2", "三列地板沿用完整共同几何签名");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(aliasRows:new JsonObject { [alias] = "map/three/other.til" })), "主地板末三别名不得来自不同终端源");
        var changedPrefix = (JsonObject)tiles.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) changedPrefix["to"]![profile]![1]![1] = "changed.til";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:changedPrefix)), "三列方案不得改前二原块");
        var wrongBase = GeometryRule("tiles",map,"dungeon_tiles","[tile]",GeometryStrings("a.til","b.til","c.til","c.til"),
            GeometryStrings("a.til","b.til","c.til","displaycompat/terminal.til","displaycompat/terminal.til"));
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(tileRule:wrongBase)), "三列扩展矩阵不得绑定四列根");
        foreach (int index in new[] { 0,2,3,4,7,8,9 })
        {
            int[] bad = target.ToArray(); bad[index] = index == 0 ? -1 : index >= 7 ? 1 : 0;
            var invalid = GeometryRule("matrix",map,"dungeon_ext_tilemap3","[tile map]",GeometryAtoms(source),GeometryAtoms(bad));
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(matrixRule:invalid)), "三列矩阵原前二、空格哨兵及新增身份必须全部保持");
        }
        var secondAlias = "map/three/displaycompat/other.til";
        var otherAliases = (JsonObject)aliases.DeepClone(); otherAliases[secondAlias] = "map/three/other.til";
        var otherPass = GeometryRule("other-pass",secondAlias,"tile_pass14","[pass type]",GeometryAtoms(Enumerable.Repeat(2,14)),GeometryAtoms(Enumerable.Repeat(2,14)));
        var wrongFiles = (JsonObject)files.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) wrongFiles["to"]![profile]![5]![1] = "displaycompat/other.til";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tiles,pass,wrongFiles,matrix,otherPass],otherAliases)), "三列扩展新增ID的别名源必须等于该行原末ID的实际TIL");
        var missingId = GeometryRule("matrix",map,"dungeon_ext_tilemap3","[tile map]",GeometryAtoms(source),GeometryAtoms(target.Select((value,index) => index == 4 ? 4 : value)));
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(matrixRule:missingId)), "三列扩展矩阵不得引用不存在的新增ID");
        Reject<InvalidDataException>(() => config.Apply(map,[..raw,..raw],index => pool[index]), "三列方案不选择多个主段或扩展段之一");
        Reject<InvalidDataException>(() => config.Apply(map,raw[..^1],index => pool[index]), "三列扩展容器缺闭合拒绝");
    }

    private static void RunAnimationAppendCore()
    {
        const string map = "map/append/room.map", alias = "map/append/displaycompat/terminal.til";
        var aliases = new JsonObject { [alias] = "map/append/dummy.til" };
        var pass = GeometryRule("pass",alias,"tile_pass14","[pass type]",GeometryAtoms(Enumerable.Repeat(2,14)),GeometryAtoms(Enumerable.Repeat(2,14)));
        var tiles = GeometryRule("tiles",map,"dungeon_tiles","[tile]",GeometryStrings("dummy.til","dummy.til","dummy.til","dummy.til"),
            GeometryStrings("dummy.til","dummy.til","dummy.til","displaycompat/terminal.til","displaycompat/terminal.til"));
        var before = (JsonArray)JsonNode.Parse("[[6,\"floor.ani\"],[6,\"[bottom]\"],[0,560],[0,263],[0,0],[6,\"floor.ani\"],[6,\"[bottom]\"],[0,784],[0,264],[0,0]]")!;
        var after = (JsonArray)before.DeepClone();
        foreach (var atom in (JsonArray)JsonNode.Parse("[[6,\"floor.ani\"],[6,\"[bottom]\"],[0,1008],[0,264],[0,0]]")!) after.Add(atom!.DeepClone());
        JsonObject Animation(JsonArray from, JsonArray to) => GeometryRule("animation",map,"dungeon_animation5_append","[animation]",from,to);
        string Document(JsonArray from, JsonArray to) => GeometryDocument([tiles,pass,Animation(from,to)],aliases);
        var config = MapGeometryConfig.Parse(Document(before,after));
        var pool = new List<string>();
        PvfToken Text(byte kind,string value)
        {
            int id = pool.IndexOf(value); if (id < 0) { id = pool.Count; pool.Add(value); }
            return new(kind,id);
        }
        PvfToken[] Atoms(JsonArray values) => values.Select(atom => atom![0]!.GetValue<int>() == 6 ? Text(6,atom[1]!.GetValue<string>()) : new PvfToken(0,atom[1]!.GetValue<int>())).ToArray();
        PvfToken[] raw = [Text(3,"[tile]"),Text(6,"dummy.til"),Text(6,"dummy.til"),Text(6,"dummy.til"),Text(6,"dummy.til"),Text(3,"[/tile]"),
            Text(3,"[animation]"),..Atoms(before),Text(3,"[/animation]")];
        string Resolve(int id) => config.ResolveString(id,index => pool[index]);
        var output = config.Apply(map,raw,index => pool[index]);
        int start = Array.FindIndex(output,token => token.Type == 3 && Resolve(token.Value) == "[animation]") + 1;
        That(output.Skip(start).Take(before.Count).SequenceEqual(Atoms(before)) &&
            output.Skip(start).Take(after.Count).Select(token => (token.Type,Value:token.Type == 6 ? Resolve(token.Value) : token.Value.ToString())).SequenceEqual(
                Atoms(after).Select(token => (token.Type,Value:token.Type == 6 ? Resolve(token.Value) : token.Value.ToString()))), "动画完整原列表保持后仅追加源784到1008的第五块");
        That(config.Apply(map,output,index => pool[index]).SequenceEqual(output) && raw.Length + 6 == output.Length, "动画与四到五列根共同生效，三比例共同目标且重入幂等");
        That(config.SignatureKind(map) == "DCG3" && !MapGeometrySignature.ComputeAnchors(map,raw,index => pool[index]).SequenceEqual(MapGeometrySignature.ComputeAnchors(map,output,Resolve)), "追加动画进入现有DCG3完整锚点保护");
        const string townMap = "map/town/metrocenter/metro_time_waiting.map";
        var townGate = GeometryRule("gate",townMap,"town_gate","[town movable area]",GeometryAtoms([860,235,50,230,-1,-1]),GeometryAtoms([1084,235,50,230,-1,-1]));
        var townWalk = GeometryRule("walk",townMap,"walk_area","[virtual movable area]",GeometryAtoms([10,240,900,220]),GeometryAtoms([10,240,1124,220]));
        var beforePair = (JsonArray)before.DeepClone(); beforePair[2]![1] = 664; beforePair[7]![1] = 771;
        var afterPair = (JsonArray)after.DeepClone(); afterPair[2]![1] = 664; afterPair[7]![1] = 771; afterPair[12]![1] = 995;
        foreach (var atom in (JsonArray)JsonNode.Parse("[[6,\"floor.ani\"],[6,\"[bottom]\"],[0,888],[0,263],[0,0]]")!) afterPair.Add(atom!.DeepClone());
        string TownDocument(JsonArray to) => GeometryDocument([townGate,townWalk,GeometryRule("both",townMap,"town_animation5_append","[animation]",beforePair,to)]);
        var pairConfig = MapGeometryConfig.Parse(TownDocument(afterPair));
        PvfToken[] pairRaw = [Text(3,"[town movable area]"), ..new[]{860,235,50,230,-1,-1}.Select(n=>new PvfToken(0,n)), Text(3,"[virtual movable area]"), ..new[]{10,240,900,220}.Select(n=>new PvfToken(0,n)), Text(3,"[animation]"), ..Atoms(beforePair), Text(3,"[/animation]")];
        var pairOutput = pairConfig.Apply(townMap,pairRaw,index => pool[index]);
        That(pairOutput.Length == pairRaw.Length + 10 && pairConfig.Apply(townMap,pairOutput,index => pool[index]).SequenceEqual(pairOutput), "城镇白线与石块两条追加共同生效且幂等");
        var wrongPair = (JsonArray)afterPair.DeepClone(); wrongPair[17]![1] = 889;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(TownDocument(wrongPair)), "石块追加必须保持精确664到888合同");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(TownDocument(afterPair).Replace("metro_time_waiting.map","other.map",StringComparison.Ordinal)), "城镇追加只准入已核实地图");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(TownDocument(afterPair).Replace("town_gate","walk_area",StringComparison.Ordinal)), "城镇追加必须绑定出入口与可行走区域");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([pass,Animation(before,after)],aliases)), "动画追加缺少同图四到五列根拒绝");
        var threeTiles = GeometryRule("tiles",map,"dungeon_tiles3","[tile]",GeometryStrings("dummy.til","dummy.til","dummy.til"),
            GeometryStrings("dummy.til","dummy.til","displaycompat/terminal.til","displaycompat/terminal.til","displaycompat/terminal.til"));
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([threeTiles,pass,Animation(before,after)],aliases)), "动画第五块不得绑定三列根");
        foreach (int index in new[] { 0,1,2,3,4,10,11,12,13,14 })
        {
            var bad = (JsonArray)after.DeepClone();
            if (bad[index]![0]!.GetValue<int>() == 6) bad[index]![1] = "changed";
            else bad[index]![1] = bad[index]![1]!.GetValue<int>() + 1;
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(before,bad)), "动画原前缀、源身份、Y/Z或精确X变换不得改变");
        }
        var noSource = (JsonArray)before.DeepClone(); noSource[7]![1] = 783;
        var noSourceAfter = (JsonArray)noSource.DeepClone(); foreach (var atom in after.Skip(before.Count)) noSourceAfter.Add(atom!.DeepClone());
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(noSource,noSourceAfter)), "追加不能从783或任意其他X猜补第五块");
        var twice = (JsonArray)after.DeepClone(); foreach (var atom in after.Skip(before.Count)) twice.Add(atom!.DeepClone());
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(before,twice)), "动画不得重复追加同一记录");
        Reject<InvalidDataException>(() => config.Apply(map,raw[..^1],index => pool[index]), "动画追加必须真实闭合");
        Reject<InvalidDataException>(() => config.Apply(map,[..raw,..raw],index => pool[index]), "动画追加必须唯一完整段");
        var changed = raw.ToArray(); changed[^3] = new(0,265);
        Reject<InvalidDataException>(() => config.Apply(map,changed,index => pool[index]), "真实源动画发生变化时拒绝旧追加规则");
    }
}
