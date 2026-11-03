using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunAnchorCore()
    {
        RunMetadataRectangleCore();
        foreach (var (kind,tag,source) in new[]
        {
            ("dungeon_animation5","[animation]","[[6,\"a.ani\"],[6,\"[normal]\"],[0,672],[0,44],[0,0]]"),
            ("dungeon_passive4","[passive object]","[[0,9000000],[0,672],[0,44],[0,0]]"),
            ("dungeon_monster10_fixed_normal","[monster]","[[0,1],[0,2],[0,3],[0,672],[0,44],[0,10000],[0,6],[0,7],[6,\"[fixed]\"],[6,\"[normal]\"]]"),
            ("dungeon_ai8","[ai character]","[[0,1],[0,672],[0,44],[0,0],[6,\"[enemy]\"],[6,\"[normal]\"],[0,6],[0,7]]"),
            ("dungeon_event_xyz3","[event monster position]","[[0,672],[0,44],[0,0]]"),
            ("dungeon_special11_single_child","[special passive object]","[[0,8815],[0,672],[0,44],[0,0],[0,1],[6,\"[trap]\"],[0,2211],[0,1],[0,3],[0,4],[0,-1]]"),
            ("dungeon_special11_single_child","[special passive object]","[[0,8815],[0,672],[0,44],[0,0],[0,1],[6,\"[item]\"],[0,2211],[0,1],[0,3],[0,4],[0,-1]]"),
            ("dungeon_ai10_npc_monster_boss","[ai character]","[[0,1514],[0,672],[0,44],[0,0],[6,\"[monster]\"],[6,\"[NPC]\"],[0,1008],[6,\"[boss]\"],[0,2],[0,0]]")
        })
        {
            const string path = "map/anchor.map";
            string from = source[..^1] + "," + source[1..].Replace("[0,672]","[0,671]",StringComparison.Ordinal);
            string to = from.Replace("[0,672]","[0,896]",StringComparison.Ordinal);
            string Document(string target) => GeometryDocument([GeometryRule("anchor",path,kind,tag,
                (JsonArray)JsonNode.Parse(from)!, (JsonArray)JsonNode.Parse(target)!)]);
            var config = MapGeometryConfig.Parse(Document(to));
            var pool = new List<string>();
            PvfToken Text(byte type, string value)
            {
                int index = pool.IndexOf(value);
                if (index < 0) { index = pool.Count; pool.Add(value); }
                return new(type,index);
            }
            PvfToken[] Tokens(string values) => [Text(3,tag),..((JsonArray)JsonNode.Parse(values)!).Select(atom =>
                atom![0]!.GetValue<int>() == 6 ? Text(6,atom[1]!.GetValue<string>()) : new PvfToken(0,atom[1]!.GetValue<int>())),Text(3,"[/" + tag[1..])];
            string Resolve(int id) => pool[id];
            var raw = Tokens(from); var expected = Tokens(to);
            var output = config.Apply(path,raw,Resolve);
            That(output.SequenceEqual(expected) && config.Apply(path,output,Resolve).SequenceEqual(expected), "末段插列仅移动X且多记录完整保持、目标幂等：" + kind);
            That(config.SignatureKind(path) == "DCG3" && !MapGeometrySignature.ComputeAnchors(path,raw,Resolve).SequenceEqual(MapGeometrySignature.ComputeAnchors(path,output,Resolve)), "插列锚点完整进入独立DCG3：" + kind);
            That(MapGeometrySignature.ComputeDungeon(path,raw,Resolve).SequenceEqual(MapGeometrySignature.ComputeDungeon(path,output,Resolve)), "普通图DCG2不因插列锚点协议改变：" + kind);
            Reject<System.IO.InvalidDataException>(() => config.Apply(path,raw[..^1],Resolve), "锚点段必须真实闭合");
            Reject<System.IO.InvalidDataException>(() => config.Apply(path,[..raw,..raw],Resolve), "重复锚点段拒绝");
            Reject<System.IO.InvalidDataException>(() => MapGeometryConfig.Parse(Document(to.Replace("[0,44]","[0,45]",StringComparison.Ordinal))), "锚点Y和身份不得顺便移动");
            Reject<System.IO.InvalidDataException>(() => MapGeometryConfig.Parse(Document(to.Replace("[0,671]","[0,895]",StringComparison.Ordinal))), "插列左侧X必须保持");
            Reject<System.IO.InvalidDataException>(() => MapGeometryConfig.Parse(Document(to.Replace("[0,896]","[0,897]",StringComparison.Ordinal))), "插列映射必须精确224");
            if (kind == "dungeon_special11_single_child")
            {
                Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(to.Replace("[0,2211]","[0,2212]",StringComparison.Ordinal))), "特殊子记录的全部原参数保持");
                if (source.Contains("[item]",StringComparison.Ordinal))
                    Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(to).Replace("[item]","[unknown]",StringComparison.Ordinal)), "未知特殊子类型不得猜同宽度");
            }
            if (kind == "dungeon_ai10_npc_monster_boss")
            {
                Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(to.Replace("[0,1008]","[0,1009]",StringComparison.Ordinal))), "NPC关联整数必须保持");
                Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(to).Replace("[boss]","[normal]",StringComparison.Ordinal)), "NPC专用十字段不猜其它rank布局");
                Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(to).Replace("[NPC]","[normal]",StringComparison.Ordinal)), "NPC专用十字段不猜普通AI布局");
            }
        }
    }
    private static void RunMetadataRectangleCore()
    {
        const string path = "map/rect.map", tag = "[dungeon movable area]";
        int[] before = [0,10,672,50,700,20,30,60,377,123,300,200];
        int[] after = [0,10,672,50,924,20,30,60,377,123,524,200];
        string Document(int[] target) => GeometryDocument([GeometryRule("metadata",path,"dungeon_movable_xywh3",tag,GeometryAtoms(before),GeometryAtoms(target))]);
        var config = MapGeometryConfig.Parse(Document(after));
        string Resolve(int id) => id == 1 ? tag : "[/dungeon movable area]";
        PvfToken[] raw = [new(3,1),..before.Select(value => new PvfToken(0,value)),new(3,2)];
        var expected = raw.ToArray(); for (int index = 0; index < after.Length; ++index) expected[index + 1] = new(0,after[index]);
        var output = config.Apply(path,raw,Resolve);
        That(output.SequenceEqual(expected) && config.Apply(path,output,Resolve).SequenceEqual(expected), "三XYWH完整映射跨缝、右侧及右界恰触缝，Y/H保持且幂等");
        That(config.SignatureKind(path) == "DCG2" && !MapGeometrySignature.ComputeDungeon(path,raw,Resolve).SequenceEqual(MapGeometrySignature.ComputeDungeon(path,output,Resolve)), "metadata只用既有DCG2字段，不冒充virtual行走矩形");
        foreach (int index in new[] { 0,1,2,3,4,10,11 })
        {
            int[] bad = after.ToArray(); bad[index]++;
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(Document(bad)), "metadata不得改变保持字段或偏离精确插列映射");
        }
        Reject<InvalidDataException>(() => config.Apply(path,raw[..^1],Resolve), "metadata段必须完整闭合");
        Reject<InvalidDataException>(() => config.Apply(path,[..raw,..raw],Resolve), "metadata段必须唯一");
    }
}
