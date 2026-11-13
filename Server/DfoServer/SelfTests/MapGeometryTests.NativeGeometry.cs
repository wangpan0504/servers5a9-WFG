using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunNativeGeometryRestoreCore()
    {
        const string path = "map/native/room.map";
        JsonObject Rule(string id, string tag, int[] before, int[] after)
        {
            var row = GeometryRule(id,path,"native_geometry_restore",tag,GeometryAtoms(before),GeometryAtoms(after));
            row["from"] = new JsonObject { ["all"] = GeometryAtoms(before) };
            row["to"] = new JsonObject { ["all"] = GeometryAtoms(after) };
            row["original_section_present"] = after.Length != 0;
            row["section_closed"] = tag == "[virtual movable area]";
            return row;
        }
        var width = Rule("width","[force map width]",[1120],[]);
        var area = Rule("area","[virtual movable area]",[0,156,1120,204],[]);
        var config = MapGeometryConfig.Parse(GeometryDocument([width,area]));
        string Resolve(int id) => id switch
        {
            1 => "[force map width]", 2 => "[virtual movable area]", 3 => "[/virtual movable area]",
            4 => "[map name]", 5 => "kept", 6 => "[/force map width]", _ => throw new InvalidDataException("fixture string")
        };
        PvfToken[] suffix = [new(3,4),new(6,5)];
        PvfToken[] input = [new(3,1),new(0,1120),new(3,2),new(0,0),new(0,156),new(0,1120),new(0,204),new(3,3),..suffix];
        var frozen = input.ToArray();
        var output = config.Apply(path,input,Resolve);
        That(output.SequenceEqual(suffix) && input.SequenceEqual(frozen),"新增标量段及闭合矩形段整体删除，旁段与输入保持");
        That(config.Apply(path,output,Resolve).SequenceEqual(output),"缺原段且目标为空的原生恢复幂等");
        That(config.SignatureKind(path) == "DCG2","只有新增原生几何恢复规则的MAP也选择DCG2");
        That(!MapGeometrySignature.ComputeDungeon(path,input,Resolve).SequenceEqual(MapGeometrySignature.ComputeDungeon(path,output,Resolve)),"段删除进入共同几何签名");
        const string text = "[force map width]\r\n1120\r\n[virtual movable area]\r\n0 156 1120 204\r\n[/virtual movable area]\r\n[map name]\r\n`kept`\r\n";
        string rendered = config.ApplyText(path,text,input,Resolve);
        That(rendered == "[map name]\r\n`kept`\r\n","文本读取也整体删除开闭标签，不遗留空段");
        That(config.ApplyText(path,rendered,output,Resolve) == rendered,"文本原生恢复幂等");
        Reject<InvalidDataException>(() => config.Apply(path,[..input[..7],..input[8..]],Resolve),"矩形缺闭合拒绝");
        Reject<InvalidDataException>(() => config.Apply(path,[..input,..input[2..8]],Resolve),"重复矩形段拒绝");
        Reject<InvalidDataException>(() => config.Apply(path,[..suffix,new(3,3)],Resolve),"空目标不接受孤立闭标签");
        Reject<InvalidDataException>(() => config.Apply(path,[..input[..2],new(3,6),..input[2..]],Resolve),"标量强制宽度不接受闭标签");
        var corrupt = input.ToArray(); corrupt[3] = new(0,1);
        Reject<InvalidDataException>(() => config.Apply(path,corrupt,Resolve),"第二段守卫失败时第一段不能提前发布");
        That(input.SequenceEqual(frozen) && corrupt[1].Value == 1120,"整批失败保留原始输入");
        var restore = MapGeometryConfig.Parse(GeometryDocument([Rule("restore","[virtual movable area]",[8,192,1124,128],[8,192,900,128])]));
        PvfToken[] expanded = [new(3,2),new(0,8),new(0,192),new(0,1124),new(0,128),new(3,3)];
        var native = restore.Apply(path,expanded,Resolve);
        That(native[3].Value == 900 && native.Length == expanded.Length && restore.Apply(path,native,Resolve).SequenceEqual(native),"既有完整矩形恢复并保留原段且幂等");
        That(restore.ApplyText(path,"[virtual movable area]\n8 192 1124 128\n[/virtual movable area]\n",expanded,Resolve).Contains("8 192 900 128",StringComparison.Ordinal),"非空文本端点按完整原矩形恢复");
        Reject<InvalidDataException>(() => restore.Apply(path,[],Resolve),"非空原生目标不能接受缺失段");
        int[] many = Enumerable.Range(0,58).SelectMany(i => new[] { i,156,1120,204 }).ToArray();
        var large = MapGeometryConfig.Parse(GeometryDocument([Rule("large","[virtual movable area]",many,[])]));
        That(many.Length == 232 && large.Apply(path,[new(3,2),..many.Select(value => new PvfToken(0,value)),new(3,3)],Resolve).Length == 0,"完整232整数矩形正文可还原删除");
        foreach (var invalid in new[] { "map", "tag", "occurrence", "first", "original_section_present", "section_closed" })
        {
            var row = (JsonObject)width.DeepClone();
            row[invalid] = invalid switch
            {
                "map" => JsonValue.Create("map/../outside.map"), "tag" => JsonValue.Create("[ignore movable rect]"),
                "occurrence" => JsonValue.Create(1), "first" => JsonValue.Create(false), _ => JsonValue.Create(true)
            };
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([row])),"原生几何路径/机制/选择器/段元数据严格拒绝");
        }
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([Rule("width","[force map width]",[-1],[])])),"强制宽度必须为单个正整数");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([Rule("area","[virtual movable area]",[0,0,0,100],[])])),"矩形宽高必须为正");
        var legacy = (JsonObject)area.DeepClone(); legacy["kind"] = "walk_area";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([legacy])),"新元字段不扩大旧机制白名单");
        var duplicate = (JsonObject)width.DeepClone(); duplicate["id"] = "second";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([width,duplicate])),"重复原生几何选择器拒绝");
    }
}
