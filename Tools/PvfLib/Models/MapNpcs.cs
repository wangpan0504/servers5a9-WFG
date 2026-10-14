using System.Collections.Immutable;

namespace PvfLib;

public sealed record PvfMapNpc(int Id, string Direction, int X, int Y, int Z);

public static class MapNpcs
{
    // 只读取地图自身的完整 NPC 块；坐标、方向与编号保持原值，不授予业务会面资格。
    public static ImmutableArray<PvfMapNpc> Read(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        int starts = 0, ends = 0;
        foreach (var token in tokens)
        {
            if (token.Type != 3) continue;
            string tag = resolve(token.Value);
            if (tag.Equals("[NPC]", StringComparison.OrdinalIgnoreCase)) starts++;
            if (tag.Equals("[/NPC]", StringComparison.OrdinalIgnoreCase)) ends++;
        }
        if (starts == 0 && ends == 0) return [];
        if (starts != 1 || ends != 1) throw new InvalidDataException("地图 NPC 块重复或缺少闭合标签");
        // 条件块内的同名声明不能被提取成无条件地图演员；复用现有有界声明树辨别归属。
        var sections = new PvfTokenDocument(tokens, resolve, preserveExtraEndTags: true).Sections.Where(row => row.Name == "npc").ToArray();
        if (sections.Length != 1 || !sections[0].Children.IsEmpty)
            throw new InvalidDataException("地图 NPC 来源包含尚未解析的嵌套条件");
        var values = sections[0].Values;
        if (values.Length % 5 != 0) throw new InvalidDataException("地图 NPC 行被截断");
        var rows = ImmutableArray.CreateBuilder<PvfMapNpc>();
        for (int at = 0; at < values.Length; at += 5)
        {
            if (values[at].Type != 0 || values[at + 1].Type != 6 || values[at + 2].Type != 0 || values[at + 3].Type != 0 || values[at + 4].Type != 0)
                throw new InvalidDataException("地图 NPC 行不是完整的编号、方向和三坐标");
            rows.Add(new(values[at].Value, resolve(values[at + 1].Value), values[at + 2].Value, values[at + 3].Value, values[at + 4].Value));
        }
        return rows.ToImmutable();
    }
}
