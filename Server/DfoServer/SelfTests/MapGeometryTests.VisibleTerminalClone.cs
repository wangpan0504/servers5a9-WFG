using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;
namespace DfoServer;
internal static partial class MapGeometryTests
{
    private static void RunVisibleTerminalCloneCore()
    {
        const string json = """{"schema":1,"layer":"world","aliases":{"map/clone/displaycompat/base.til":"map/clone/b.til","map/clone/displaycompat/ext.til":"map/clone/b.til"},"rules":[{"id":"base","enabled":true,"map":"map/clone/room.map","kind":"native_tiles","tag":"[tile]","occurrence":0,"first":true,"last":true,"left":[],"right":[],"from":{"all":[[6,"a.til"],[6,"a.til"],[6,"b.til"]]},"to":{"all":[[6,"a.til"],[6,"a.til"],[6,"displaycompat/base.til"]]}},{"id":"files","enabled":true,"map":"map/clone/room.map","kind":"native_ext_tilefiles_clone","tag":"[tile files]","occurrence":0,"first":true,"last":true,"left":[],"right":[],"from":{"all":[[0,0],[6,"a.til"],[0,1],[6,"b.til"]]},"to":{"all":[[0,0],[6,"a.til"],[0,1],[6,"b.til"],[0,2],[6,"displaycompat/ext.til"]]}},{"id":"matrix","enabled":true,"map":"map/clone/room.map","kind":"native_ext_tilemap","tag":"[tile map]","occurrence":0,"first":true,"last":true,"left":[],"right":[],"from":{"all":[[0,0],[0,0],[0,1],[0,0],[0,0],[0,1]]},"to":{"all":[[0,0],[0,0],[0,2],[0,0],[0,0],[0,2]]}},{"id":"base-pass","enabled":true,"map":"map/clone/displaycompat/base.til","kind":"tile_pass14","tag":"[pass type]","occurrence":0,"first":true,"last":true,"left":[],"right":[],"from":{"all":[[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0]]},"to":{"all":[[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0]]}},{"id":"ext-pass","enabled":true,"map":"map/clone/displaycompat/ext.til","kind":"tile_pass14","tag":"[pass type]","occurrence":0,"first":true,"last":true,"left":[],"right":[],"from":{"all":[[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0]]},"to":{"all":[[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0],[0,0]]}}]}""";
        var config=MapGeometryConfig.Parse(json);
        string[] strings=["[tile]","a.til","b.til","[/tile]","[extended tile]","[tile files]","[/tile files]","[tile map]","[/tile map]","[/extended tile]","[pass type]","[image]","original.img","[pathgate pos]"];
        PvfToken[] input=[new(3,0),new(6,1),new(6,1),new(6,2),new(3,3),new(3,4),new(3,5),new(0,0),new(6,1),new(0,1),new(6,2),new(3,6),new(3,7),new(0,0),new(0,0),new(0,1),new(0,0),new(0,0),new(0,1),new(3,8),new(3,9),new(3,13),new(0,77)];
        var frozen=input.ToArray();var output=config.Apply("map/clone/room.map",input,id=>strings[id]);
        That(input.SequenceEqual(frozen)&&output.Length==input.Length+2&&output[^2..].SequenceEqual(input[^2..]),"可见末列只追加同源索引并保持门与输入");
        That(config.Apply("map/clone/room.map",output,id=>strings[id]).SequenceEqual(output),"可见末列克隆幂等");
        PvfToken[] tile=[new(3,11),new(6,12),new(0,0),new(3,10),..Enumerable.Repeat(new PvfToken(0,0),14)];
        That(config.Apply("map/clone/displaycompat/ext.til",tile,id=>strings[id]).SequenceEqual(tile),"图像与真实pass守卫保持");
        foreach(string name in new[]{"source","slot","pass","nonterminal","columns","file-prefix","base-prefix","base-source","empty-tail","old-repair"})
        {
            var bad=JsonNode.Parse(json)!;var rows=bad["rules"]!;
            switch(name){
                case "source":bad["aliases"]!["map/clone/displaycompat/ext.til"]="map/clone/a.til";break;
                case "slot":rows[1]!["to"]!["all"]![4]![1]=1;break;
                case "pass":rows[4]!["to"]!["all"]![0]![1]=1;break;
                case "nonterminal":rows[2]!["to"]!["all"]![0]![1]=2;break;
                case "columns":rows[0]!["to"]!["all"]!.AsArray().RemoveAt(0);break;
                case "file-prefix":rows[1]!["to"]!["all"]![1]![1]="b.til";break;
                case "base-prefix":rows[0]!["to"]!["all"]![0]![1]="b.til";break;
                case "base-source":rows[0]!["to"]!["all"]![2]![1]="a.til";break;
                case "empty-tail":rows[2]!["from"]!["all"]![2]![1]=-1;break;
                default:rows[1]!["kind"]="native_ext_tilefiles";rows[1]!["to"]!["all"]![1]![1]="displaycompat/ext.til";break;
            }
            Reject<InvalidDataException>(()=>MapGeometryConfig.Parse(bad.ToJsonString()),"可见末列拒绝非法配方："+name);
        }
        tile[^1]=new(0,1);Reject<InvalidDataException>(()=>config.Apply("map/clone/displaycompat/ext.til",tile,id=>strings[id]),"可见末列真实源pass不符拒绝");
        RunVisibleTerminalMultipleSourcesCore(json);
    }
}
