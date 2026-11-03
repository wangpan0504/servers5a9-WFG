using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

// 仅接纳英雄原MOB直接ACT声明的两条召唤；不从编号前缀推导生成资格。
internal sealed record DungeonEvildomWarChallengeSpawn(int ParentOrdinal, int ParentId, string ParentPath,
    int ChildId, string ChildPath, string ActionPath, bool SingleActive);

internal static class DungeonEvildomWarChallengeSpawnRules
{
    internal static DungeonEvildomWarChallengeSpawn? Read(DungeonRoomDefinition room,
        Func<string, IReadOnlyList<PvfToken>> read, Func<int, string> resolve, Func<int, string?> monsterPath)
    {
        bool boss = room.Map.Id == 400000524 && room.Map.Position == new DungeonPoint(12, 0);
        bool seal = room.Map.Id == 400000525 && room.Map.Position == new DungeonPoint(12, 1);
        if ((!boss && !seal) || room.Map.Path != DungeonEvildomWarChallengeWarpRules.MapPath(boss)) return null;
        string root = DungeonEvildomWarChallengeWarpRules.Root;
        int parent = boss ? 407000556 : 407000558, child = boss ? 407000559 : 407000557;
        string parentPath = root + (boss ? "sarpoza.mob" : "sarpoza_seal_dummy1.mob");
        string childPath = root + (boss ? "sarpoza_seal_dummy2.mob" : "sarpoza_seal.mob");
        string action = root + (boss ? "action/seal.act" : "action/proc_0.act");
        var actors = room.Monsters.Select((row, ordinal) => (row, ordinal)).Where(row => row.row.MonsterId == parent).ToArray();
        if (actors is not [var actor] || actor.row.Path != parentPath || monsterPath(parent) != parentPath ||
            monsterPath(child) != childPath || read(childPath).Count == 0) return null;
        var mob = read(parentPath);
        bool linked = false;
        for (int at = 0; at < mob.Count; at++)
        {
            if (Tag(mob[at]) != (boss ? "attack action" : "proc action")) continue;
            for (int index = at + 1; index < mob.Count && mob[index].Type == 6; index++)
                if (PvfArchive.NormalizePath(root + resolve(mob[index].Value)) == action) linked = true;
        }
        if (!linked) return null;
        var tokens = read(action);
        var behaviors = Blocks("behavior"); var triggers = Blocks("trigger");
        object[] summon = ["[SUMMON MONSTER]", "[INDEX]", child, "[LEVEL]", -1, "[POS]", 0, boss ? -1 : 0, 0,
            "[NO EFFECT]", "[IGNORE GHOSTMODE]", "[SUMMON MASTER COPY]", "[NONE]", "[USE MY BASEPOS]",
            "[USE OBJECT ZPOS]", "[SUMMON MARK]", 0, "[/SUMMON MONSTER]"];
        bool valid = boss
            ? behaviors.Count > 0 && triggers.Any(block => Match(block, "[ON SET ACTION]", "[DO BEHAVIOR]", "[ME]", 0)) &&
                Contains(behaviors[0], summon) && tokens.Count(token => Tag(token) == "summon monster") == 1
            : behaviors.Count > 7 && Contains(behaviors[6], summon) && Contains(behaviors[7], summon) &&
                tokens.Count(token => Tag(token) == "summon monster") == 2 && new[] { 6, 7 }.All(behavior =>
                    triggers.Any(block => Contains(block, "[WHICH]", "[MONSTER]", "[CHECKUP]", "[IS INDEX]", child,
                        "[/IS INDEX]", "[/CHECKUP]", "[CHECKED NO]", "[=]", 0, "[DO BEHAVIOR]", "[ME]", behavior)));
        return valid ? new(actor.ordinal, parent, parentPath, child, childPath, action, !boss) : null;

        string Tag(PvfToken token) => token.Type == 3 ? resolve(token.Value).Trim('[', ']').ToLowerInvariant() : "";
        bool Match(IReadOnlyList<PvfToken> block, params object[] shape) => DungeonLostLandWarpRules.Match(block, resolve, shape);
        bool Contains(IReadOnlyList<PvfToken> block, params object[] shape) =>
            Enumerable.Range(0, Math.Max(0, block.Count - shape.Length + 1)).Any(at => Match(block.Skip(at).Take(shape.Length).ToArray(), shape));
        List<IReadOnlyList<PvfToken>> Blocks(string section)
        {
            var result = new List<IReadOnlyList<PvfToken>>(); string outer = ""; int start = 0;
            for (int at = 0; at < tokens.Count; at++)
            {
                string tag = Tag(tokens[at]);
                if (outer.Length == 0 && tag is "motion" or "trigger" or "behavior") { outer = tag; start = at + 1; }
                else if (tag == "/" + outer)
                {
                    if (outer == section) result.Add(tokens.Skip(start).Take(at - start).ToArray());
                    outer = "";
                }
            }
            return result;
        }
    }
}
