using System.Text.Json;
using DfoServer.GameWorld.Maps;
using DfoServer.GameWorld.Towns;
using PvfLib;
using static DfoServer.Testing.TestCheck;

namespace DfoServer;

internal static class EpicRoad2019NpcRestoreTests
{
    const string Path = "map/common/(r)gate_seria.map";

    internal static string Profile(PvfToken[] source, Func<int, string> resolve)
    {
        int begin = Array.FindIndex(source, token => token.Type == 3 && resolve(token.Value).Equals("[npc]", StringComparison.OrdinalIgnoreCase));
        if (begin < 0) throw new InvalidDataException("入口地图NPC原段缺失");
        int end = Array.FindIndex(source, begin + 1, token => token.Type == 3);
        if (begin < 0 || end < 0 || !resolve(source[end].Value).Equals("[/npc]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("入口地图NPC原段缺失");
        object[][] before = source[(begin + 1)..end].Select(token => token.Type == 6
            ? new object[] { 6, resolve(token.Value) } : new object[] { 0, token.Value }).ToArray();
        object[][] after = [..before, [0, 400001102], [6, "[right]"], [0, 535], [0, 165], [0, 0]];
        object Endpoints(object[][] values) => new Dictionary<string, object> { ["800"] = values, ["1067"] = values, ["960"] = values };
        return JsonSerializer.Serialize(new { schema = 1, layer = "world", rules = new[] { Path, "map/common/gate_seria.map" }.Select((path, index) =>
            new { id = "epic-road-2019-entry-" + index, enabled = true, map = path, kind = "epic_road_npc_restore", tag = "[npc]",
                occurrence = 0, first = true, last = true, left = Array.Empty<int>(), right = Array.Empty<int>(),
                from = Endpoints(before), to = Endpoints(after) }) });
    }

    internal static MapGeometryConfig Geometry(PvfArchive pvf)
    {
        var raw = pvf.ReadTokens(Path);
        return raw.Any(token => token.Type == 0 && token.Value == 400001102) ? MapGeometryConfig.Empty
            : MapGeometryConfig.Parse(Profile(raw, pvf.ResolveString));
    }

    public static void RunCore()
    {
        int checks = Count;
        string[] pool = ["[NPC]", "[left]", "[/NPC]", "[next]"];
        PvfToken[] raw = [new(3, 0), new(0, 2), new(6, 1), new(0, 443), new(0, 125), new(0, 0), new(3, 2), new(3, 3), new(0, 74)];
        string profile = Profile(raw, id => pool[id]);
        var config = MapGeometryConfig.Parse(profile);
        var output = config.Apply(Path, raw, id => pool[id]);
        That(config.Apply("map/common/gate_seria.map", raw, id => pool[id]).SequenceEqual(output),
            "原生规范路径和保留叶路径使用相同原段及原摆放");
        That(output.Length == raw.Length + 5 && output[..6].SequenceEqual(raw[..6]) && output[^3..].SequenceEqual(raw[^3..]) &&
            output[6] == new PvfToken(0, 400001102) && config.ResolveString(output[7].Value, id => pool[id]) == "[right]" &&
            output[8] == new PvfToken(0, 535) && output[9] == new PvfToken(0, 165) && output[10] == new PvfToken(0, 0),
            "只恢复原亚瑟摆放，完整保留旧NPC、关闭标签和后续字段");
        That(config.Apply(Path, output, id => config.ResolveString(id, index => pool[index])).SequenceEqual(output) &&
            config.SignatureKind(Path) == "DCG1", "恢复端点幂等且保持城镇DCG1签名合同");
        var capabilities = MapGeometryCapabilities.BuildCore(config, [Path], path => path == Path,
            path => config.Apply(path, raw, id => pool[id]), id => config.ResolveString(id, index => pool[index]));
        That(capabilities.Count == 2 && capabilities[Path].SequenceEqual(MapGeometrySignature.Compute(Path, output,
            id => config.ResolveString(id, index => pool[index]))) &&
            capabilities["map/common/gate_seria.map"].SequenceEqual(MapGeometrySignature.Compute("map/common/gate_seria.map", output,
                id => config.ResolveString(id, index => pool[index]))),
            "规范入口缺文件时沿受限同源叶资源生成两份各自路径签名");
        Reject<InvalidDataException>(() => MapGeometryCapabilities.BuildCore(config, ["map/common/unrelated.map"],
            path => path == Path, path => config.Apply(path, raw, id => pool[id]),
            id => config.ResolveString(id, index => pool[index])), "其它缺图不受原入口同源读取影响");
        foreach (string bad in new[] { profile.Replace("400001102", "400001103", StringComparison.Ordinal),
            profile.Replace("535", "536", StringComparison.Ordinal), profile.Replace("[right]", "[left]", StringComparison.Ordinal),
            profile.Replace("(r)gate_seria", "another_map", StringComparison.Ordinal) })
            Reject<InvalidDataException>(() => MapGeometryConfig.Parse(bad), "原NPC恢复不能更换身份、坐标、方向或目标地图");
        var changed = raw.ToArray(); changed[3] = new(0, 444);
        Reject<InvalidDataException>(() => config.Apply(Path, changed, id => pool[id]), "旧NPC有任意变化则拒绝整段恢复");
        Reject<InvalidDataException>(() => config.Apply(Path, raw[..6], id => pool[id]), "原NPC块缺少闭合不能恢复");
        Console.WriteLine($"PASS {Count - checks} 星河原入口NPC受限恢复断言");
    }

    public static void RunResources(PvfArchive pvf)
    {
        var towns = TownCatalog.Load(pvf, Geometry(pvf), epicRoad2019Enabled: true);
        That(towns.Areas[(38, 1)].Default.Npcs.Count(npc => npc.Id == 400001102 && npc.X == 535 && npc.Y == 165) == 1 &&
            towns.NpcMoves.TryGetValue(400001102, out var targets) && targets.Contains(400001103u),
            "当前资源以派生配置恢复唯一原入口和原传送角色，不改PVF");
        Console.WriteLine("PASS 当前资源原入口NPC派生装配");
    }
}
