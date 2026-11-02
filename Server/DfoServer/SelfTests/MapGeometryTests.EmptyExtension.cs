using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunEmptyExtensionCore()
    {
        const string root = "map/test/repair.map";
        string[] targets = ["native", "edge", "empty"];
        int[] pass = Enumerable.Repeat(2,140).ToArray();
        var aliases = new JsonObject();
        var rules = new List<JsonObject>();
        foreach (string name in targets)
        {
            string path = $"map/test/displaycompat/{name}.til";
            aliases[path] = "map/test/source.til";
            rules.Add(GeometryRule(name,path,"tile_pass14","[pass type]",GeometryAtoms(pass),GeometryAtoms(pass)));
        }
        rules.Add(GeometryRule("base",root,"dungeon_tiles","[tile]",GeometryStrings("a.til","b.til","c.til","source.til"),GeometryStrings("a.til","b.til","c.til","displaycompat/native.til","displaycompat/edge.til")));
        var fileRule = GeometryRule("files",root,"dungeon_ext_tilefiles_repair","[tile files]",
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"missing.til")),
            new JsonArray(new JsonArray(0,0),new JsonArray(6,"displaycompat/empty.til"),new JsonArray(0,1),new JsonArray(6,"displaycompat/native.til"),new JsonArray(0,2),new JsonArray(6,"displaycompat/edge.til")));
        rules.Add(fileRule);
        rules.Add(GeometryRule("cells",root,"dungeon_ext_tilemap","[tile map]",GeometryAtoms([0,0,0,0]),GeometryAtoms([0,0,0,1,2])));
        var config = MapGeometryConfig.Parse(GeometryDocument(rules,aliases));
        var pool = new List<string>();
        PvfToken Text(byte type, string value)
        {
            int index = pool.IndexOf(value);
            if (index < 0) { index = pool.Count; pool.Add(value); }
            return new(type,index);
        }
        PvfToken Tag(string value) => Text(3,value);
        PvfToken Name(string value) => Text(6,value);
        PvfToken[] tile = [Tag("[image]"),Name(""),new(0,0),Tag("[pass type]"),..pass.Select(value => new PvfToken(0,value))];
        var resources = new Dictionary<string,PvfToken[]>(StringComparer.Ordinal)
        {
            [root] = [Tag("[tile]"),Name("a.til"),Name("b.til"),Name("c.til"),Name("source.til"),
                Tag("[extended tile]"),Tag("[tile files]"),new(0,0),Name("missing.til"),Tag("[tile map]"),new(0,0),new(0,0),new(0,0),new(0,0),Tag("[/extended tile]")],
            ["map/test/source.til"] = tile, ["map/test/a.til"] = tile, ["map/test/b.til"] = tile, ["map/test/c.til"] = tile
        };
        string Resolve(int id) => config.ResolveString(id,index => pool[index]);
        IReadOnlyDictionary<string,byte[]> Build() => MapGeometryCapabilities.BuildCore(config,[],path => resources.ContainsKey(path) || config.Aliases.ContainsKey(path),
            path => config.Apply(path,resources[config.Aliases.GetValueOrDefault(path,path)],index => pool[index]),Resolve);
        That(Build().ContainsKey("map/test/displaycompat/empty.til"), "真实缺失扩展只以现有空图全阻挡别名修复，完整签名覆盖");
        resources["map/test/missing.til"] = tile;
        Reject<InvalidDataException>(() => Build(), "补回真实源时旧empty修复必须失效");
        resources.Remove("map/test/missing.til");
        resources["map/test/source.til"] = [tile[0],Name("unexpected.img"),..tile.Skip(2)];
        Reject<InvalidDataException>(() => Build(), "空别名源出现可绘制图时拒绝，即使pass签名未变");
        resources["map/test/source.til"] = tile;
        That(MapGeometryConfig.EmptyExtendedTile(tile,Resolve), "空IMAGE/frame0全阻挡源的扩展语义明确");
        foreach (PvfToken[] bad in new[] { tile[..^14], tile.Select((token,index) => index == 2 ? new PvfToken(0,1) : token).ToArray(), tile.Select((token,index) => index == tile.Length-1 ? new PvfToken(0,0) : token).ToArray() })
            That(!MapGeometryConfig.EmptyExtendedTile(bad,Resolve), "空扩展不得带帧、缺行或可走格");
        fileRule["kind"] = "dungeon_ext_tilefiles";
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(GeometryDocument(rules,aliases)), "普通扩展追加器不能替换原始定义");
    }
}
