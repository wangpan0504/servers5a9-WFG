using PvfLib;

namespace DfoServer.GameWorld.Towns;

// 创建攻坚队菜单的传送关系来自团本头，不是NPC的普通move town角色。
internal sealed record RaidNpcRoute(byte Type, uint RecruitNpc, uint WaitingNpc,
    Location WaitingRoom, Location? SingleWaitingRoom)
{
    internal Location Room(bool guide) => guide ? SingleWaitingRoom ?? WaitingRoom : WaitingRoom;

    internal static RaidNpcRoute[] Load(PvfArchive pvf)
    {
        if (!pvf.Contains(Raids.RaidCatalog.ListPath)) return [];
        var routes = new List<RaidNpcRoute>();
        foreach (var (type, path) in PvfScript.List(pvf.ReadText(Raids.RaidCatalog.ListPath)))
        {
            if (type is < 0 or > 3 || !pvf.Contains(path)) continue;
            var fields = Raids.RaidScriptParser.Fields(pvf.ReadTokens(path), pvf.ResolveString, (_, _, _) => { });
            int[] Pair(string tag)
            {
                var rows = fields.Where(field => field.Tag == tag).ToArray();
                return rows is [var row] && row.Values is [{ Type: 0, Number: >= 0 } a, { Type: 0, Number: >= 0 } b]
                    ? [a.Number, b.Number] : [];
            }
            Location? Room(string tag) => Pair(tag) is [<= byte.MaxValue and var town, <= byte.MaxValue and var area]
                ? new((byte)town, (byte)area, 0, 0) : null;
            if (Pair("[waiting room npc]") is [> 0 and var recruit, > 0 and var waiting] &&
                recruit != waiting && Room("[waiting room]") is { } room)
                routes.Add(new((byte)type, (uint)recruit, (uint)waiting, room, Room("[single waiting room]")));
        }
        return routes.ToArray();
    }
}
