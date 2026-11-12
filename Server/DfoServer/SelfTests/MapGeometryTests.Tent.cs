using DfoServer.GameWorld.Maps;
using PvfLib;
using System.Text.Json;
using System.Text.Json.Nodes;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunTentCore()
    {
        const string path = "map/camp/room.map";
        const string before = "[[0,80303],[0,475],[0,131],[0,0],[0,19259],[0,616],[0,125],[0,0],[0,19259],[0,668],[0,169],[0,0],[0,19259],[0,777],[0,149],[0,0],[0,19259],[0,777],[0,231],[0,0],[0,19259],[0,879],[0,177],[0,0],[0,19259],[0,890],[0,258],[0,0],[0,19259],[0,704],[0,117],[0,0]]";
        string after = before.Replace("[0,616]","[0,840]",StringComparison.Ordinal).Replace("[0,668]","[0,892]",StringComparison.Ordinal)
            .Replace("[0,777]","[0,1001]",StringComparison.Ordinal).Replace("[0,879]","[0,1103]",StringComparison.Ordinal)
            .Replace("[0,890]","[0,1114]",StringComparison.Ordinal).Replace("[0,704]","[0,928]",StringComparison.Ordinal);
        string Row(string id, string map, string kind, string tag, string a, string b) => GeometryRule(id,map,kind,tag,(JsonArray)JsonNode.Parse(a)!,(JsonArray)JsonNode.Parse(b)!).ToJsonString();
        string pass = GeometryAtoms(Enumerable.Repeat(0,14)).ToJsonString();
        string objects = Row("objects",path,"dungeon_tent_passive4","[passive object]",before,after);
        string animation = Row("tent",path,"dungeon_tent_animation5","[animation]",
            "[[6,\"Animation_10424/tentopen_1.ani\"],[6,\"[bottom]\"],[0,633],[0,115],[0,0]]",
            "[[6,\"Animation_10424/tentopen_1.ani\"],[6,\"[bottom]\"],[0,857],[0,115],[0,0]]");
        string gate = Row("gate",path,"gate_points8","[pathgate pos]",GeometryAtoms([-166,293,688,206,1512,711,-227,0]).ToJsonString(),GeometryAtoms([-166,293,912,206,1736,711,-227,0]).ToJsonString());
        string tile = Row("floor",path,"dungeon_tiles","[tile]","[[6,\"a.til\"],[6,\"b.til\"],[6,\"c.til\"],[6,\"d.til\"]]",
            "[[6,\"a.til\"],[6,\"b.til\"],[6,\"c.til\"],[6,\"displaycompat/a.til\"],[6,\"displaycompat/b.til\"]]");
        string guards = Row("pass-a","map/camp/displaycompat/a.til","tile_pass14","[pass type]",pass,pass) + "," +
            Row("pass-b","map/camp/displaycompat/b.til","tile_pass14","[pass type]",pass,pass);
        string Document(string o, string a, string g, string t) => "{\"schema\":1,\"layer\":\"world\",\"aliases\":{\"map/camp/displaycompat/a.til\":\"map/camp/d.til\",\"map/camp/displaycompat/b.til\":\"map/camp/d.til\"},\"rules\":[" + string.Join(',',new[] { o,a,g,t,guards }.Where(value => value.Length != 0)) + "]}";
        string document = Document(objects,animation,gate,tile);
        var config = MapGeometryConfig.Parse(document);
        string movedGate = Row("gate",path,"gate_points8","[pathgate pos]",GeometryAtoms([-166,293,912,206,1736,711,-227,0]).ToJsonString(),GeometryAtoms([-166,293,912,206,1736,711,-227,0]).ToJsonString());
        _ = MapGeometryConfig.Parse(Document(objects,animation,movedGate,tile));
        That(config.SignatureKind(path) == "DCG3","帐篷与阻挡物完整纳入DCG3审批");
        Dictionary<string,int> pool = new(StringComparer.Ordinal);
        int Id(string value) { if (!pool.TryGetValue(value,out int id)) pool.Add(value,id = pool.Count + 1); return id; }
        string Resolve(int id) => pool.Single(row => row.Value == id).Key;
        PvfToken[] Section(string tag, JsonElement values) => [new(3,Id(tag)),..values.EnumerateArray().Select(atom => new PvfToken(atom[0].GetByte(),atom[0].GetByte() == 6 ? Id(atom[1].GetString()!) : atom[1].GetInt32())),new(3,Id("[/" + tag[1..]))];
        using var parsed = JsonDocument.Parse(document);
        PvfToken[] raw = parsed.RootElement.GetProperty("rules").EnumerateArray().Where(rule => rule.GetProperty("map").GetString() == path)
            .SelectMany(rule => Section(rule.GetProperty("tag").GetString()!,rule.GetProperty("from").GetProperty("800"))).ToArray();
        using var target = JsonDocument.Parse(after);
        PvfToken[] expected = Section("[passive object]",target.RootElement);
        var result = config.Apply(path,raw,Resolve);
        That(result.Take(expected.Length).SequenceEqual(expected) && config.Apply(path,result,Resolve).SequenceEqual(result),"插列线左侧616/668阻挡物同帐篷移动224，旁物保持且幂等");
        var changed = raw.ToArray(); expected.CopyTo(changed,0);
        That(!MapGeometrySignature.ComputeAnchors(path,raw,Resolve).SequenceEqual(MapGeometrySignature.ComputeAnchors(path,changed,Resolve)),"阻挡物真实迁移改变审批签名");
        var unclosed = raw.Where(token => token.Type != 3 || Resolve(token.Value) != "[/passive object]").ToArray();
        Reject<InvalidDataException>(() => config.Apply(path,unclosed,Resolve),"帐篷阻挡物缺闭合拒绝");
        var unrelated = JsonNode.Parse(document)!;
        foreach (string profile in new[] { "800","1067","960" }) unrelated["rules"]![0]!["to"]![profile]![1]![1] = 699;
        foreach (string bad in new[] {
            document.Replace("[0,840]","[0,841]",StringComparison.Ordinal),
            unrelated.ToJsonString(),
            document.Replace("[0,125]","[0,126]",StringComparison.Ordinal),
            document.Replace("[0,19259]","[0,19260]",StringComparison.Ordinal),
            document.Replace("[0,912]","[0,913]",StringComparison.Ordinal),
            Document(objects,"",gate,tile),Document("",animation,gate,tile),Document(objects,animation,"",tile),Document(objects,animation,gate,"") })
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(bad),"帐篷不完整配套、错位或改动旁物必须拒绝");
    }
}
