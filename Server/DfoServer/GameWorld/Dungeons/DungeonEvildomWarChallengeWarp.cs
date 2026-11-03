using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 只冻结本版英雄封印往返的资源来源，不把客户端阶段符号当成服务端战斗事实。
internal sealed record DungeonEvildomWarChallengeWarp(int MapId, string MapPath, int MonsterOrdinal, int MonsterId,
    string MonsterPath, string ActionPath, DungeonPoint Destination);

internal static class DungeonEvildomWarChallengeWarpRules
{
    internal const string Root = "monster/newmonsters/gcontents/200114_theevildomwar_challenge/boss/sarpoza/";

    internal static DungeonEvildomWarChallengeWarp? Read(DungeonRoomDefinition room,
        Func<string, IReadOnlyList<PvfToken>> read, Func<int, string> resolve)
    {
        bool outward = room.Map.Id == 400000524 && room.Map.Position == new DungeonPoint(12, 0);
        bool inward = room.Map.Id == 400000525 && room.Map.Position == new DungeonPoint(12, 1);
        if (!outward && !inward) return null;
        int id = outward ? 407000556 : 407000558;
        var rows = room.Monsters.Select((monster, ordinal) => (monster, ordinal)).Where(row => row.monster.MonsterId == id).ToArray();
        string monsterPath = Root + (outward ? "sarpoza.mob" : "sarpoza_seal_dummy1.mob");
        if (rows is not [var actor] || actor.monster.Path != monsterPath || room.Map.Path != MapPath(outward)) return null;
        string action = Root + (outward ? "action/seal_start.act" : "action/proc_0.act");
        var mob = read(actor.monster.Path);
        string tag = outward ? "attack action" : "proc action";
        bool linked = false;
        for (int at = 0; at + 1 < mob.Count; at++)
        {
            if (Tag(mob[at]) != tag) continue;
            for (int index = at + 1; index < mob.Count && mob[index].Type == 6; index++)
            {
                string path = PvfArchive.NormalizePath(Root + resolve(mob[index].Value));
                if (path == action) linked = true;
            }
        }
        if (!linked) return null;
        var tokens = read(action);
        var behaviors = Blocks(tokens, "behavior");
        var triggers = Blocks(tokens, "trigger");
        bool valid = outward
            ? behaviors.Count > 6 && triggers.Any(block => Match(block, "[FRAME]", 58, 58, "[WHICH]", "[PLAYER]",
                "[DO BEHAVIOR]", "[CHECKUP OBJECT]", 6)) &&
                Contains(behaviors[6], "[DUNGEON SYMBOL SET]", "Phase2_Seal_Start", 1,
                    "[DUNGEON SYMBOL SET]", "Phase2_Seal_IN_Kill", 0,
                    "[MOVE MAP]", 12, 1, 440, 251, 30, "[IS STATE CHECK]", 0, "[/MOVE MAP]") &&
                tokens.Count(token => Tag(token) == "move map") == 1
            : behaviors.Count > 5 &&
                triggers.Any(block => Match(block, "[ENABLE]", "[ON]", "[DUNGEON SYMBOL COMPARE]", "Phase2_Seal_Fail",
                    "[=]", 1, "[DO BEHAVIOR]", "[ME]", 3, "[DO BEHAVIOR]", "[ME]", 5)) &&
                triggers.Any(block => Match(block, "[ENABLE]", "[ON]", "[DUNGEON SYMBOL COMPARE]", "Phase2_Seal_Fail",
                    "[=]", 2, "[DO BEHAVIOR]", "[ME]", 4, "[DO BEHAVIOR]", "[ME]", 5)) &&
                Match(behaviors[3], "[TRIGGER]", 7, "[OFF]", "[SET ACTION]", "[ATTACK]", 1, "[NOW]",
                    "[KICK OUT MAP CHARACTER]", 12, 0, 785, 269, 300, "[INCLUDE DEAD]", 1, "[/KICK OUT MAP CHARACTER]") &&
                Match(behaviors[4], "[TRIGGER]", 7, "[OFF]", "[SET ACTION]", "[ATTACK]", 1, "[NOW]",
                    "[KICK OUT MAP CHARACTER]", 12, 0, 785, 269, 80, "[INCLUDE DEAD]", 1, "[/KICK OUT MAP CHARACTER]") &&
                tokens.Count(token => Tag(token) == "kick out map character") == 2;
        return valid ? new(room.Map.Id, room.Map.Path, actor.ordinal, id, actor.monster.Path, action,
            new DungeonPoint(12, outward ? 1 : 0)) : null;

        string Tag(PvfToken token) => token.Type == 3 ? resolve(token.Value).Trim('[', ']').ToLowerInvariant() : "";
        bool Match(IReadOnlyList<PvfToken> block, params object[] shape) =>
            DungeonLostLandWarpRules.Match(block, resolve, shape);
        bool Contains(IReadOnlyList<PvfToken> block, params object[] shape) => Enumerable.Range(0, Math.Max(0, block.Count - shape.Length + 1))
            .Any(at => Match(block.Skip(at).Take(shape.Length).ToArray(), shape));
        List<IReadOnlyList<PvfToken>> Blocks(IReadOnlyList<PvfToken> source, string section)
        {
            var result = new List<IReadOnlyList<PvfToken>>();
            string outer = "";
            int start = 0;
            for (int at = 0; at < source.Count; at++)
            {
                string current = Tag(source[at]);
                if (outer.Length == 0 && current is "motion" or "trigger" or "behavior") { outer = current; start = at + 1; }
                else if (current == "/" + outer)
                {
                    if (outer == section) result.Add(source.Skip(start).Take(at - start).ToArray());
                    outer = "";
                }
            }
            return result;
        }
    }

    internal static string MapPath(bool boss) => "map/gcontents/200114_theevildomwar_challenge/eastharlem_a/" +
        (boss ? "400000105_400000524_(12,0)_eastharlem.map" : "400000105_400000525_(12,1)_eastharlem.map");
}
