using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunNativeRestoreCore()
    {
        RunNativeGeometryRestoreCore();
        RunVisibleTerminalCloneCore();
        const string path = "map/native/room.map";
        var tiles = GeometryRule("tiles",path,"native_tiles","[tile]",
            GeometryStrings("a.til","a.til","a.til","native.til","extension.til"),GeometryStrings("a.til","a.til","a.til","a.til"));
        var files = GeometryRule("files",path,"native_ext_tilefiles","[tile files]",
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"a.til"),new JsonArray(0,1),new JsonArray(6,"native.til"),new JsonArray(0,2),new JsonArray(6,"extension.til")),
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"a.til")));
        var matrix = GeometryRule("matrix",path,"native_ext_tilemap","[tile map]",GeometryAtoms([0,0,0,1,2]),GeometryAtoms([0,0,0,0]));
        string Document() => GeometryDocument([tiles,files,matrix]);
        var config = MapGeometryConfig.Parse(Document());
        var pool = new List<string>();
        PvfToken Text(byte kind, string value)
        {
            int id = pool.IndexOf(value);
            if (id < 0) { id = pool.Count; pool.Add(value); }
            return new(kind,id);
        }
        string Resolve(int id) => config.ResolveString(id,index => pool[index]);
        PvfToken[] Record(string tag, params PvfToken[] values) => [Text(3,tag),..values,Text(3,"[/" + tag[1..])];
        PvfToken[] input = [..Record("[tile]",Text(6,"a.til"),Text(6,"a.til"),Text(6,"a.til"),Text(6,"native.til"),Text(6,"extension.til")),
            Text(3,"[extended tile]"),..Record("[tile files]",new(0,0),Text(6,"a.til"),new(0,1),Text(6,"native.til"),new(0,2),Text(6,"extension.til")),
            ..Record("[tile map]",new(0,0),new(0,0),new(0,0),new(0,1),new(0,2)),Text(3,"[/extended tile]"),
            ..Record("[pathgate pos]",new(0,11),new(0,12),new(0,13),new(0,14),new(0,15),new(0,16),new(0,17),new(0,18))];
        var frozen = input.ToArray();
        var output = config.Apply(path,input,index => pool[index]);
        That(input.SequenceEqual(frozen) && output.Length == input.Length - 6,"原生地板与扩展层同步收回一列，输入保持");
        That(config.Apply(path,output,index => pool[index]).SequenceEqual(output),"原生还原目标幂等");
        That(output[^9..].SequenceEqual(input[^9..]),"未受管门坐标及其闭合标签保持");
        That(!MapGeometrySignature.ComputeDungeon(path,input,Resolve).SequenceEqual(MapGeometrySignature.ComputeDungeon(path,output,Resolve)),"原生几何变化进入DCG2");
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tiles,matrix])),"原生矩阵缺少定义拒绝");
        var badMatrix = (JsonObject)matrix.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) badMatrix["to"]![profile]![0]![1] = 9;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([tiles,files,badMatrix])),"原生扩展矩阵不能引用未知ID");
        var badTiles = (JsonObject)tiles.DeepClone();
        foreach (string profile in new[] { "800", "1067", "960" }) badTiles["to"]![profile]![0]![1] = "../../outside.til";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([badTiles,files,matrix])),"原生TIL引用不能越出map根");
        var anchor = GeometryRule("anchor",path,"native_anchor_restore","[animation]",
            new JsonArray(new JsonArray(6,"a.ani"),new JsonArray(6,"[normal]"),new JsonArray(0,896),new JsonArray(0,44),new JsonArray(0,0)),
            new JsonArray(new JsonArray(6,"a.ani"),new JsonArray(6,"[normal]"),new JsonArray(0,672),new JsonArray(0,44),new JsonArray(0,0)));
        var anchorConfig = MapGeometryConfig.Parse(GeometryDocument([anchor]));
        var anchorInput = Record("[animation]",Text(6,"a.ani"),Text(6,"[normal]"),new(0,896),new(0,44),new(0,0));
        var restored = anchorConfig.Apply(path,anchorInput,index => pool[index]);
        That(restored[3].Value == 672 && restored[4].Value == 44 && anchorConfig.SignatureKind(path) == "DCG3","原生锚点只逆向224且选择DCG3");
        Reject<InvalidDataException>(() => anchorConfig.Apply(path,anchorInput[..^1],index => pool[index]),"原生锚点缺闭合拒绝");
        foreach (string profile in new[] { "800", "1067", "960" }) anchor["to"]![profile]![3]![1] = 45;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument([anchor])),"原生锚点不能顺便修改Y");
        var town = Record("[town movable area]",new(0,10),new(0,20),new(0,30),new(0,40),new(0,39),new(0,0));
        var changed = town.ToArray(); changed[1] = new(0,11);
        That(!MapGeometrySignature.ComputeDungeon(path,town,Resolve).SequenceEqual(MapGeometrySignature.ComputeDungeon(path,changed,Resolve)),"DCG2包含城镇坐标");
        That(!MapGeometrySignature.ComputeAnchors(path,town,Resolve).SequenceEqual(MapGeometrySignature.ComputeAnchors(path,changed,Resolve)),"DCG3包含城镇坐标");
    }
}
