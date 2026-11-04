using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed record DungeonRandomizedObjectGroup(int SelectCount, bool Regenerate, int? MinimapIcon,
    ImmutableArray<DungeonRandomizedObject> Objects);

internal static class DungeonRandomizedObjectGroups
{
    // 分组边界、显式对象类型及回访政策一起保留；不再因select小于候选数丢弃整个块。
    public static IReadOnlyList<DungeonRandomizedObjectGroup> Parse(IReadOnlyList<DungeonRawSection> sections,
        Func<int, string> resolve)
    {
        var groups = new List<DungeonRandomizedObjectGroup>();
        var rows = ImmutableArray.CreateBuilder<DungeonRandomizedObject>();
        bool inside = false, inObject = false;
        int select = 0, regenerate = 0;
        int? icon = null, id = null, mx = null, my = null, px = null, py = null;
        string? team = null, type = null;
        var tags = new HashSet<string>();
        foreach (var section in sections)
        {
            string tag = section.Tag;
            if (tag == "randomized object creation")
            {
                if (inside || section.Tokens.Count != 0) return [];
                inside = true; select = regenerate = 0; icon = null; rows.Clear(); tags.Clear();
                continue;
            }
            if (!inside) continue;
            if (tag == "/randomized object creation")
            {
                if (inObject || section.Tokens.Count != 0) return [];
                groups.Add(new(select, regenerate != 0, icon, rows.ToImmutable()));
                inside = false;
                continue;
            }
            if (tag == "object")
            {
                if (inObject || section.Tokens.Count != 0) return [];
                inObject = true; id = mx = my = px = py = null; team = type = null;
                continue;
            }
            if (tag == "/object")
            {
                if (!inObject || section.Tokens.Count != 0 || id is not > 0 ||
                    mx is not (>= 0 and <= 255) || my is not (>= 0 and <= 255) || px is not >= 0 || py is not >= 0 ||
                    team is not ("neutral" or "monster" or "character") ||
                    type is not (null or "passive object" or "ridable object")) return [];
                rows.Add(new(new(mx.Value, my.Value), id.Value, team, px.Value, py.Value)
                { SpawnMode = type == "passive object" ? 1 : 0 });
                inObject = false;
                continue;
            }
            if (!inObject)
            {
                if (!tags.Add(tag) || section.Tokens is not [{ Type: 0, Value: >= 0 } value]) return [];
                switch (tag)
                {
                    case "select": select = value.Value; break;
                    case "regenerate" when value.Value <= 1: regenerate = value.Value; break;
                    case "minimap icon": icon = value.Value; break;
                    default: return [];
                }
                continue;
            }
            switch (tag)
            {
                case "object type" when type is null && section.Tokens is [{ Type: 3 or 6 } typeToken]:
                    type = resolve(typeToken.Value); break;
                case "map" when mx is null && Pair(section.Tokens, out int mapX, out int mapY): mx = mapX; my = mapY; break;
                case "pos" when px is null && Pair(section.Tokens, out int posX, out int posY): px = posX; py = posY; break;
                case "index" when id is null && section.Tokens is [{ Type: 0, Value: > 0 } idToken]: id = idToken.Value; break;
                case "team" when section.Tokens.Count == 0: break;
                case "neutral" or "monster" or "character" when team is null && section.Tokens.Count == 0: team = tag; break;
                default: return [];
            }
        }
        return inside || inObject ? [] : groups.AsReadOnly();
    }

    static bool Pair(IReadOnlyList<PvfToken> tokens, out int x, out int y)
    {
        x = y = 0;
        if (tokens is not [{ Type: 0 } first, { Type: 0 } second]) return false;
        x = first.Value; y = second.Value; return true;
    }
}
