using System.Text.Json;
using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class MapGeometryTests
{
    public static void RunCore()
    {
        int checks = Count;
        const string row = """
            {"id":"gate","enabled":true,"map":"Map/Test.map","kind":"town_gate","tag":"[town movable area]","occurrence":0,
            "first":true,"last":true,"left":[],"right":[],
            "from":{"800":[[0,10],[0,20],[0,30],[0,40],[0,39],[0,0]],"1067":[[0,110],[0,20],[0,30],[0,40],[0,39],[0,0]],"960":[[0,10],[0,20],[0,30],[0,40],[0,39],[0,0]]},
            "to":{"800":[[0,110],[0,20],[0,30],[0,40],[0,39],[0,0]],"1067":[[0,110],[0,20],[0,30],[0,40],[0,39],[0,0]],"960":[[0,110],[0,20],[0,30],[0,40],[0,39],[0,0]]}}
            """;
        string Doc(string body) => "{\"schema\":1,\"layer\":\"world\",\"rules\":[" + body + "]}";
        var config = MapGeometryConfig.Parse(Doc(row));
        string Resolve(int id) => id switch { 1 => "[town movable area]", 2 => "[/town movable area]", 3 => "[map name]", 4 => "中文地图", _ => throw new InvalidDataException("fixture string") };
        PvfToken[] input = [new(3,1),new(0,10),new(0,20),new(0,30),new(0,40),new(0,39),new(0,0),new(3,2),new(3,3),new(6,4)];
        var output = config.Apply("map/test.map", input, Resolve);
        That(input[1].Value == 10 && output[1].Value == 110 && output[5].Value == 39 && output[^1] == input[^1], "共同几何只改坐标，保留源、目的地与中文");
        That(config.Apply("map/test.map", output, Resolve).SequenceEqual(output), "目标端点幂等");
        var signature = MapGeometrySignature.Compute("map/test.map",output,Resolve);
        That(!signature.SequenceEqual(MapGeometrySignature.Compute("map/test.map",input,Resolve)), "有效几何变化改变签名");
        var remapped = output.Select(t => t.Type is 3 or 6 ? t with { Value = t.Value + 100 } : t).ToArray();
        That(signature.SequenceEqual(MapGeometrySignature.Compute("map/test.map",remapped,id => Resolve(id - 100))), "池ID变化不改变签名");
        That(signature.SequenceEqual(MapGeometrySignature.Compute("map/test.map",output,id => id == 4 ? "其他中文显示文字" : Resolve(id).ToUpperInvariant())), "显示文字及标签大小写不改变签名");
        That(ReferenceEquals(config.Apply("map/elsewhere.map", input, Resolve), input), "无规则资源原样借用");
        var bad = input.ToArray(); bad[1] = new(0,12);
        Reject<InvalidDataException>(() => config.Apply("map/test.map", bad, Resolve), "源守卫冲突拒绝");
        That(bad[1].Value == 12 && bad[5].Value == 39, "失败不修改输入");
        foreach (string invalid in new[] { Doc(row + "," + row), Doc(row.Replace("Map/Test.map", "../outside.map", StringComparison.Ordinal)),
            Doc(row.Replace("\"first\":true", "\"first\":false", StringComparison.Ordinal)),
            Doc(row).Replace("\"schema\":1", "\"schema\":1,\"schema\":1", StringComparison.Ordinal),
            Doc(row.Replace("[0,30]", "[0,-30]", StringComparison.Ordinal)),
            Doc(row.Replace("\"town_gate\"", "\"unverified_dungeon_gate\"", StringComparison.Ordinal)),
            Doc(row).Replace("\"world\"", "\"visual\"", StringComparison.Ordinal) })
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(invalid), "非法规则或未知机制拒绝");
        var different = JsonNode.Parse(Doc(row))!;
        different["rules"]![0]!["to"]!["960"]![0]![1] = 111;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(different.ToJsonString()), "玩法目标不能随比例变化");
        Console.WriteLine($"PASS common map geometry core {Count - checks}");
    }

    // 本地黄金文件由维护生成器提供，不把私有资源端点嵌入仓库。
    public static void RunGolden(string configuration, string pvfPath, string vectors)
    {
        int checks = Count;
        string text = File.ReadAllText(configuration);
        var config = MapGeometryConfig.Parse(text);
        using var doc = JsonDocument.Parse(text);
        int rows = 0;
        foreach (var rule in doc.RootElement.GetProperty("rules").EnumerateArray())
        {
            ++rows;
            string path = rule.GetProperty("map").GetString()!, tag = rule.GetProperty("tag").GetString()!;
            var strings = new Dictionary<int, string> { [0] = tag };
            PvfToken[] Sequence(JsonElement value) => value.EnumerateArray().Select(atom => atom[0].GetInt32() == 0 ? new PvfToken(0,atom[1].GetInt32()) : AddString(atom[1].GetString()!)).ToArray();
            PvfToken AddString(string value) { int id = strings.Count; strings.Add(id,value); return new(6,id); }
            string one = "{\"schema\":1,\"layer\":\"world\",\"rules\":[" + rule.GetRawText() + "]}";
            var isolated = MapGeometryConfig.Parse(one);
            var expected = Sequence(rule.GetProperty("to").GetProperty("800"));
            foreach (string profile in new[] { "800", "1067", "960" })
            {
                var source = new[] { new PvfToken(3,0) }.Concat(Sequence(rule.GetProperty("from").GetProperty(profile))).ToArray();
                var applied = isolated.Apply(path,source,id => strings[id]);
                That(applied.Skip(1).Select(t => t.Type == 6 ? strings[t.Value] : t.Value.ToString()).SequenceEqual(expected.Select(t => t.Type == 6 ? strings[t.Value] : t.Value.ToString())), "C#黄金端点：" + path);
                That(isolated.Apply(path,applied,id => strings[id]).SequenceEqual(applied), "C#黄金端点幂等：" + path);
            }
        }
        That(rows == 58 && config.Paths.Count == 30, "首批共同几何30图58字段");
        using var pvf = PvfArchive.Open(pvfPath);
        using var writer = new BinaryWriter(File.Create(vectors),System.Text.Encoding.UTF8);
        void WriteString(string value) { byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(value); writer.Write(utf8.Length); writer.Write(utf8); }
        writer.Write(config.Paths.Count);
        foreach (string path in config.Paths)
        {
            var original = pvf.ReadTokens(path);
            var applied = config.ReadTokens(pvf,path);
            WriteString(path); writer.Write(applied.Length);
            foreach (var token in applied)
            {
                writer.Write((uint)token.Type);
                if (token.Type is 3 or 6) WriteString(pvf.ResolveString(token.Value)); else writer.Write(token.Value);
            }
            writer.Write(MapGeometrySignature.Compute(path,applied,pvf.ResolveString));
            That(applied.Length == original.Length && applied.Select(t => t.Type).SequenceEqual(original.Select(t => t.Type)), "实际中文PVF结构保留：" + path);
            That(config.Apply(path,applied,pvf.ResolveString).SequenceEqual(applied), "实际中文PVF幂等：" + path);
            string rendered = config.ReadText(pvf,path);
            foreach (string tag in new[] { "town movable area", "virtual movable area" })
                That(TownCatalog.Rows(rendered,tag,tag.StartsWith("town",StringComparison.Ordinal)?6:4).All(row => row.Values[2] > 0 && row.Values[3] > 0), "城镇文本消费者保持合法矩形：" + path);
        }
        var towns = TownCatalog.Load(pvf,config);
        That(ReferenceEquals(towns.Geometry,config) && towns.IsPosition(towns.InitialReturn), "实际城镇目录显式装配共同配置");
        foreach (string path in towns.GeometryPaths) That(MapGeometrySignature.Compute(path,config.ReadTokens(pvf,path),pvf.ResolveString).Length == 32, "全部城镇/导入图能力快照：" + path);
        Console.WriteLine($"PASS common map geometry golden {Count - checks}; input={pvfPath}");
    }
}
