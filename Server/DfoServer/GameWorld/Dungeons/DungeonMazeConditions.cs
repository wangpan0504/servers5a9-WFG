using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonEntranceHunt(int TargetId, int Opaque, int Count);
internal sealed record DungeonBossEntrance(int TimeMode, int TimeValue, IReadOnlyList<DungeonEntranceHunt> Targets);
internal sealed record DungeonWarpMap(DungeonPoint Source, DungeonPoint Destination);
internal sealed record DungeonClearCondition(string Type, int TargetId, int Count);

internal static class DungeonMazeConditions
{
    public static IReadOnlyList<DungeonClearCondition> Clear(IReadOnlyList<DungeonRawSection> sections, Func<int,string> _)
    {
        var result = new List<DungeonClearCondition>();
        bool inside = false;
        foreach (var section in sections)
        {
            if (section.Tag == "clear condition")
            {
                if (inside || section.Tokens.Count != 0) throw new InvalidDataException("通关条件块无效");
                inside = true;
            }
            else if (section.Tag == "/clear condition")
            {
                if (!inside) throw new InvalidDataException("通关条件块起点缺失");
                inside = false;
            }
            else if (inside)
            {
                if (section.Tag is not ("hunt monster" or "hunt boss" or "destroy object" or "hunt apc" or "seeking"))
                {
                    // 未确认的声明保留占位，不能将混合条件误认为单目标猎杀。
                    result.Add(new(section.Tag,0,0));
                    continue;
                }
                if (section.Tokens.Count == 0 || section.Tokens.Count % 2 != 0 ||
                    section.Tokens.Any(token => token.Type != 0 || token.Value <= 0))
                    throw new InvalidDataException("通关条件目标和数量无效");
                for (int at = 0; at < section.Tokens.Count; at += 2)
                    result.Add(new(section.Tag,section.Tokens[at].Value,section.Tokens[at+1].Value));
            }
        }
        if (inside) throw new InvalidDataException("通关条件块未结束");
        return result.AsReadOnly();
    }

    public static DungeonBossEntrance? Entrance(IReadOnlyList<DungeonRawSection> sections, Func<int,string> resolve)
    {
        var rows = sections.Where(section => section.Tag == "boss room entrance condition").ToArray();
        if (rows.Length == 0) return null;
        if (rows.Length != 1) throw new InvalidDataException("重复Boss入口条件");
        var tokens = rows[0].Tokens;
        if (tokens.Count < 9 || !Tag(0,"[clear action]") || !Tag(1,"[time condition]") || !Tag(4,"[hunt monster]") ||
            tokens[2].Type != 0 || tokens[3].Type != 0 || tokens[5] is not { Type: 0, Value: > 0 } count ||
            (long)count.Value * 3 != tokens.Count - 6) throw new NotSupportedException("Boss入口条件声明未支持");
        var targets = new List<DungeonEntranceHunt>();
        var ids = new HashSet<int>();
        for (int at = 6; at < tokens.Count; at += 3)
        {
            if (tokens[at] is not { Type: 0, Value: > 0 } id || tokens[at+1].Type != 0 ||
                tokens[at+2] is not { Type: 0, Value: > 0 } amount || !ids.Add(id.Value))
                throw new InvalidDataException("Boss入口猎杀目标无效或重复");
            targets.Add(new(id.Value,tokens[at+1].Value,amount.Value));
        }
        return new(tokens[2].Value,tokens[3].Value,targets.AsReadOnly());
        bool Tag(int at,string value) => tokens[at].Type is 3 or 6 && resolve(tokens[at].Value) == value;
    }

    public static IReadOnlyList<DungeonWarpMap> Warps(IReadOnlyList<DungeonRawSection> sections)
    {
        var result = new List<DungeonWarpMap>();
        bool inside = false;
        DungeonPoint? source = null;
        foreach (var section in sections)
        {
            if (section.Tag == "warp map condition")
            {
                if (inside || section.Tokens.Count != 0) throw new InvalidDataException("条件传送块无效");
                inside = true;
            }
            else if (section.Tag == "/warp map condition")
            {
                if (!inside || source is not null) throw new InvalidDataException("条件传送缺少完整坐标对");
                inside = false;
            }
            else if (inside)
            {
                if (section.Tag is not ("source grid pos" or "dest grid pos") ||
                    section.Tokens is not [{ Type: 0 } x,{ Type: 0 } y]) throw new NotSupportedException("条件传送坐标未支持");
                var point = new DungeonPoint(x.Value,y.Value);
                if (section.Tag == "source grid pos")
                {
                    if (source is not null) throw new InvalidDataException("条件传送起点缺少目标");
                    source = point;
                }
                else
                {
                    if (source is null || result.Any(row => row.Source == source.Value))
                        throw new InvalidDataException("条件传送起点缺失或不唯一");
                    result.Add(new(source.Value,point)); source = null;
                }
            }
        }
        if (inside || source is not null) throw new InvalidDataException("条件传送块未结束");
        return result.AsReadOnly();
    }
}
