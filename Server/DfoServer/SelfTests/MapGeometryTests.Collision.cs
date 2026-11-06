using System.Text.Json.Nodes;
using DfoServer.GameWorld.Maps;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static partial class MapGeometryTests
{
    private static void RunCollisionCore()
    {
        const string path = "map/collision/test.map";
        byte[] grid = [2, 1, 0, 6, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2];
        var planRow = new JsonObject { ["map"] = path, ["policy"] = 1, ["source_columns"] = 14, ["rows"] = 1,
            ["target_columns"] = 70, ["source_grid"] = new JsonArray(grid.Select(value => JsonValue.Create(value)).Cast<JsonNode?>().ToArray()) };
        var doc = new JsonObject { ["schema"] = 1, ["layer"] = "world", ["rules"] = new JsonArray(), ["collision_plans"] = new JsonArray(planRow) };
        var config = MapGeometryConfig.Parse(doc.ToJsonString());
        That(config.Paths.SequenceEqual([path]) && config.SignatureKind(path) == "DCG4", "只有碰撞计划的MAP也进入根审批");
        var plan = config.CollisionPlans[path];
        That(plan.Target.Length == 70 && plan.Target[1] == 1 && plan.Target[2] == 0 && plan.Target[59] == 6 && plan.Target[60] == 2,
            "纯碰撞扩域保留原条件并移动尾部阻挡");
        That(plan.Source.SequenceEqual(grid), "不改原格网");
        string Resolve(int id) => id switch { 0 => "[tile]", 1 => "tile.til", 2 => "[force map width]", 3 => "[pass type]", _ => throw new InvalidDataException() };
        PvfToken[] map = [new(3,0),new(6,1),new(3,2),new(0,1120)];
        PvfToken[] tile = [new(3,3),..grid.Select(value => new PvfToken(0,value))];
        PvfToken[] Read(string key) => key == path ? map : key == "map/collision/tile.til" ? tile : throw new InvalidDataException();
        var capabilities = MapGeometryCapabilities.BuildCore(config,[],key => key is path or "map/collision/tile.til",Read,Resolve);
        That(capabilities.Count == 2 && capabilities.ContainsKey(path) && capabilities.ContainsKey("map/collision/tile.til"), "DCG4审批包括原始TIL闭包");
        var native = MapGeometryConfig.Parse("{\"schema\":1,\"layer\":\"world\",\"rules\":[]}");
        That(native.CollisionPlans.Count == 0 && native.SignatureKind(path) == "DCG1", "原生配置不启用碰撞计划");
        tile[2] = new(0,0);
        Reject<InvalidDataException>(() => MapGeometryCapabilities.BuildCore(config,[],_ => true,Read,Resolve), "原始TIL条件变化必须拒绝而不是沿用JSON声称的格网");
        tile[2] = new(0,1); map[^1] = new(0,896);
        Reject<InvalidDataException>(() => MapGeometryCapabilities.BuildCore(config,[],_ => true,Read,Resolve), "只扩碰撞而没有实际目标世界宽度拒绝");
        planRow["policy"] = 2;
        Reject<InvalidDataException>(() => MapGeometryConfig.Parse(doc.ToJsonString()), "未知碰撞配方不静默降级");
    }
}
