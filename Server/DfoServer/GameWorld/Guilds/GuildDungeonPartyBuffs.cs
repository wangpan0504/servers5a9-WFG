using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Guilds;

internal static class GuildDungeonPartyBuffs
{
    internal static FrozenSet<int> Load(PvfArchive pvf)
    {
        var document = new PvfTokenDocument(pvf.ReadTokens("etc/variousbufflist.etc"), pvf.ResolveString);
        bool Text(PvfTokenSection row, string name, string expected) =>
            row.Children.Where(field => field.Name == name).ToArray() is [{ Values: [{ Type: 6 } value] }] &&
            pvf.ResolveString(value.Value) == expected;
        return document.Sections.Where(row => row.Name == "index" && row.Values is [{ Type: 0 }] &&
                Text(row, "system group", "Guild_Party") &&
                row.Children.Where(field => field.Name == "apply type").ToArray() is [var apply] &&
                Text(apply, "type", "dungeon_one_match") &&
                apply.Children.Where(field => field.Name == "param").ToArray() is [{ Values: [{ Type: 0, Value: 1 }, { Type: 0, Value: 0 }] }])
            .GroupBy(row => row.Values[0].Value).Where(group => group.Count() == 1).Select(group => group.Key).ToFrozenSet();
    }

    internal static uint Resolve(int declared, IReadOnlySet<int> supported)
    {
        // 当前正式副本目录仍为旧索引；本服显式映射到同份资源中的公会组队场次效果。
        int mapped = supported.Contains(declared) ? declared : declared switch { 205 => 281, 206 => 282, 207 => 283, _ => 0 };
        return supported.Contains(mapped) ? checked((uint)mapped) : 0;
    }
}
