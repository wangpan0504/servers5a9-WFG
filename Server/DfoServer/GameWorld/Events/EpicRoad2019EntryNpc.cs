using DfoServer.GameWorld.Towns;
using System.Globalization;
using System.Text.RegularExpressions;

namespace DfoServer.GameWorld.Events;

// 身份及535/165位置沿用MapGeometryConfig.ValidateEpicRoadNpc与客户端同一官方恢复白名单；不改变地图几何。
internal static class EpicRoad2019EntryNpc
{
    internal const uint Id = 400001102;
    internal const string Resource = "npc/chn_tayberrs_road_arthur1.npc";

    internal static string RestoreText(string path, string text, bool enabled, Func<string, bool> resourceExists)
    {
        if (!enabled || path is not ("map/common/gate_seria.map" or "map/common/(r)gate_seria.map")) return text;
        var sections = Regex.Matches(text, @"\[npc\](.*?)\[/npc\]", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (sections.Count != 1) throw new InvalidDataException("星河原入口NPC段缺失或重复");
        var body = sections[0].Groups[1];
        const string row = @"(-?\d+)\s+`\[(left|right)\]`\s+(-?\d+)\s+(-?\d+)\s+(-?\d+)";
        var rows = Regex.Matches(body.Value, row, RegexOptions.IgnoreCase);
        if (Regex.Replace(body.Value, row, "", RegexOptions.IgnoreCase).Trim().Length != 0 || rows.Count == 0)
            throw new InvalidDataException("星河原入口NPC记录形状无效");
        foreach (Match value in rows)
            for (int n = 3; n <= 5; n++)
                if (int.Parse(value.Groups[n].Value, CultureInfo.InvariantCulture) is < -1048576 or > 1048576)
                    throw new InvalidDataException("星河原入口NPC坐标超出受限恢复合同");
        var source = new TownMap(path, [], [], true, rows.Select(value => new TownNpc(
            checked((uint)int.Parse(value.Groups[1].Value, CultureInfo.InvariantCulture)),
            int.Parse(value.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(value.Groups[4].Value, CultureInfo.InvariantCulture))).ToArray());
        if (ReferenceEquals(source, Restore(source, true, resourceExists))) return text;
        // 只在原闭合标签之前追加；既有NPC前缀、其它标签及所有几何文本逐字符保留。
        return text.Insert(body.Index + body.Length, "\n400001102 `[right]` 535 165 0\n");
    }

    internal static TownMap Restore(TownMap map, bool enabled, Func<string, bool> resourceExists)
    {
        if (!enabled || map.Path is not ("map/common/gate_seria.map" or "map/common/(r)gate_seria.map")) return map;
        if (map.Error is not null || !map.IsRoom || map.Npcs.Length == 0)
            throw new InvalidDataException("星河原入口地图身份或原NPC列表无效");
        int existing = map.Npcs.Count(npc => npc.Id == Id);
        if (existing > 1) throw new InvalidDataException("星河原入口NPC重复");
        if (existing == 1) return map;
        if (!resourceExists(Resource)) throw new FileNotFoundException("星河原入口NPC资源缺失", Resource);
        return map with { Npcs = [.. map.Npcs, new(Id, 535, 165)] };
    }
}
