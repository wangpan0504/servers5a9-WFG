using PvfLib;
using static DfoServer.GameWorld.Dungeons.DungeonLostLandWarpRules;

namespace DfoServer.GameWorld.Dungeons;

// 公主与铁笼均须由当前MAP实际引用；救援条件留在客户端ACT，只恢复完整链声明的返程导航。
internal static class DungeonLostLandRescue
{
    public static DungeonLostLandWarp? Read(int ordinal, DungeonRoomDefinition room,
        IReadOnlyDictionary<int, string> objects, Func<string, IReadOnlyList<PvfToken>> read, Func<int, string> resolve)
    {
        if ((uint)ordinal >= room.PassiveObjects.Count) return null;
        var source = room.PassiveObjects[ordinal];
        if (source.ObjectId != 37073 || source.RawValues.Count != 4 ||
            source.Path is not { Length: > 0 } root || objects.GetValueOrDefault(37073) != root ||
            room.PassiveObjects.Count(candidate => candidate.ObjectId == 37073) != 1) return null;
        var cages = room.PassiveObjects.Where(candidate => candidate.ObjectId == 37080).ToArray();
        if (cages is not [var cage] || cage.RawValues.Count != 4 || cage.Path is not { Length: > 0 } cagePath ||
            objects.GetValueOrDefault(37080) != cagePath) return null;
        var chain = new List<string>();
        if (Load(cagePath).Count == 0) return null;
        var npc = Load(root);
        var actions = DungeonScriptedLayerReturnRules.OtherActions(npc, resolve);
        string? basic = Action(root, DungeonCinematicLayerReturnRules.BasicAction(npc, resolve));
        if (actions is not [var rescueAction] || basic is null) return null;
        string? rescue = Action(root, rescueAction);
        if (rescue is null || rescue == basic || !Match(Load(basic), resolve,
            "[motion]", "[base ani]", null, "[speech]", null, 0, 50, "[/speech]", "[/motion]",
            "[trigger]", "[limit]", 1, "[frame]", 1, 1, "[do behavior]", "[me]", 0, "[/trigger]",
            "[behavior]", "[dialog]", null, 1500, "[/behavior]",
            "[trigger]", "[which]", "[passive]", "[checkup]", "[is index]", 37080, "[/is index]",
            "[distance]", "[<=]", 2000, "[/checkup]", "[checked no]", "[<=]", 0,
            "[do behavior]", "[me]", 1, "[/trigger]",
            "[behavior]", "[set action]", "[custom]", 0, "[now]", "[/behavior]",
            "[trigger]", "[frame]", 2, 2, "[do behavior]", "[me]", 2, "[/trigger]",
            "[behavior]", "[say speech]", null, 0, 0, "[/behavior]") ||
            Move(Load(rescue), resolve) is not { } move) return null;
        return new(ordinal, source.ObjectId, root, rescue, move.Destination, move.PlayerX, move.PlayerY)
        {
            MoveParameter = 0,
            SourceChain = Array.AsReadOnly(chain.Distinct(StringComparer.Ordinal).ToArray())
        };

        IReadOnlyList<PvfToken> Load(string path)
        {
            chain.Add(path);
            return read(path);
        }
    }

    internal static (DungeonPoint Destination, int PlayerX, int PlayerY)? Move(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        if (tokens.Count != 93 || tokens[35] is not { Type: 0, Value: >= 0 and <= byte.MaxValue } ||
            tokens[36] is not { Type: 0, Value: >= 0 and <= byte.MaxValue } ||
            tokens[37] is not { Type: 0, Value: >= 0 and <= ushort.MaxValue } ||
            tokens[38] is not { Type: 0, Value: >= 0 and <= ushort.MaxValue } ||
            !Match(tokens, resolve,
                "[motion]", "[base ani]", null, "[speech]", null, 1, 100, "[/speech]", "[/motion]",
                "[trigger]", "[frame]", 3, 3, "[do behavior]", "[me]", 0, "[/trigger]",
                "[behavior]", "[dungeon symbol set]", "Event", 1, "[dialog]", null, 30000, "[/behavior]",
                "[trigger]", "[frame]", 26, 26, "[do behavior]", "[me]", 1, "[/trigger]",
                "[behavior]", "[move map]", tokens[35].Value, tokens[36].Value, tokens[37].Value, tokens[38].Value, 0,
                "[is state check]", 0, "[/move map]", "[/behavior]",
                "[trigger]", "[frame]", 27, 27, "[do behavior]", "[me]", 2, "[/trigger]",
                "[behavior]", "[destroy]", "[/behavior]",
                "[trigger]", "[limit]", 1, "[frame]", 20, 20, "[which]", "[player]",
                "[do behavior]", "[checkup object]", 3, "[/trigger]",
                "[trigger]", "[limit]", 1, "[frame]", 21, 21, "[which]", "[player]",
                "[do behavior]", "[checkup object]", 3, "[/trigger]",
                "[behavior]", "[create passiveobject]", "[index]", 37253, "[particle filename]", "", "[level]", 1,
                "[pos]", 0, 0, 130, "[/create passiveobject]", "[/behavior]")) return null;
        return (new(tokens[35].Value, tokens[36].Value), tokens[37].Value, tokens[38].Value);
    }
}
