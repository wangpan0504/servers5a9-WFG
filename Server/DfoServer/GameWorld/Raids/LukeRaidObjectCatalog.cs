using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer.GameWorld.Raids;

internal readonly record struct LukeObjectRoom(int Dungeon, int MazeOrdinal, string Kind, DungeonPoint Position, int Map, string Path);
internal sealed record LukeObjectBinding(LukeObjectRoom Room, int Ordinal, int ObjectId, int Symbol, int ActionOrdinal, string ActionPath);

// 仅登记已核验的静态潜在出现；不是命中、销毁或symbol写入授权。空数组表示已审无对象，null表示未覆盖。
internal sealed class LukeRaidObjectCatalog(ImmutableDictionary<LukeObjectRoom, ImmutableArray<LukeObjectBinding>> rooms,
    ImmutableArray<string> issues)
{
    public ImmutableArray<string> Issues { get; } = issues;
    public ImmutableArray<LukeObjectBinding>? Find(LukeObjectRoom room) => rooms.TryGetValue(room, out var rows) ? rows : null;
    public int RoomCount => rooms.Count;
    public int BindingCount => rooms.Values.Sum(rows => rows.Length);

    static readonly (int Dungeon, int Object, int Symbol, int Action, string SymbolName, string File)[] Supported =
    [
        (5054, 64249, 117, 0, "[birth sanctum object]", "luke_mainmchine_destroy_raid.act"),
        (5055, 64251, 118, 0, "[extinction sanctum object]", "mainmchine_destroy_raid.act"),
        (5056, 64252, 119, 1, "[spread sanctum object]", "censor_machine_broken_front_raid.act")
    ];

    public static LukeRaidObjectCatalog Load(PvfArchive pvf, RaidDungeonCatalog catalog, DungeonRoomCatalog source,
        IReadOnlyList<(int Dungeon, int MazeOrdinal)> choices)
    {
        var result = ImmutableDictionary.CreateBuilder<LukeObjectRoom, ImmutableArray<LukeObjectBinding>>();
        var issues = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<LukeObjectRoom>();
        if (catalog.Type != 1 || choices.Count > 32 || choices.Distinct().Count() != choices.Count)
            return new(result.ToImmutable(), ["luke-object-scope-invalid"]);
        foreach (var choice in choices.Where(row => Supported.Any(rule => rule.Dungeon == row.Dungeon)))
        {
            var rule = Supported.Single(row => row.Dungeon == choice.Dungeon);
            if (!catalog.Script.Symbols.TryGetValue(rule.SymbolName, out int symbol) || symbol != rule.Symbol)
            { issues.Add($"luke-object-symbol-unavailable:{choice.Dungeon}"); continue; }
            var maze = catalog.Find(choice.Dungeon)?.Mazes.SingleOrDefault(row => row.SourceOrdinal == choice.MazeOrdinal);
            if (maze is null || maze.QuestConnectionTokens.Count != 0 || maze.MapSpecification is null ||
                maze.MapSpecification.Count + maze.MapAlternatives.Count > 64)
            { issues.Add($"luke-object-maze-unavailable:{choice.Dungeon}:{choice.MazeOrdinal}"); continue; }
            foreach (var map in maze.MapSpecification.Concat(maze.MapAlternatives))
            {
                var key = new LukeObjectRoom(choice.Dungeon, choice.MazeOrdinal, map.Kind, map.Position, map.Id, map.Path);
                if (!seen.Add(key)) { result.Remove(key); issues.Add($"luke-object-duplicate-room:{map.Id}"); continue; }
                try
                {
                    if (map.IsConditional || map.LayerIndex >= 0 || map.StoryStageIndex >= 0 || map.DummyStageIndex >= 0 ||
                        map.CandidateIds.Count > 1 || !RaidObjectEvidence.UnconditionalPassiveObjects(pvf.ReadTokens(map.Path), pvf.ResolveString))
                        throw new InvalidDataException("尚未支持的对象房间来源");
                    var room = source.Get(map, requirePassiveDefinitions: false, allowUnresolvedSpecialMetadata: true);
                    if (room.PassiveObjects.Count > 128) throw new InvalidDataException("静态对象行超过上限");
                    var rows = ImmutableArray.CreateBuilder<LukeObjectBinding>();
                    for (int ordinal = 0; ordinal < room.PassiveObjects.Count; ordinal++)
                    {
                        var obj = room.PassiveObjects[ordinal];
                        bool companion = choice.Dungeon == 5056 && obj.ObjectId == 64287;
                        if (obj.ObjectId != rule.Object && !companion) continue;
                        if (obj.Path is null || !obj.Path.StartsWith("passiveobject/actionobject/map/luke_raid/phase1/", StringComparison.Ordinal))
                            throw new InvalidDataException("登记对象路径不匹配");
                        var actions = RaidObjectEvidence.EtcActions(pvf.ReadTokens(obj.Path), pvf.ResolveString);
                        string directory = obj.Path[..(obj.Path.LastIndexOf('/') + 1)];
                        for (int action = 0; action < actions.Length; action++)
                        {
                            string reference = PvfArchive.NormalizePath(directory + actions[action]);
                            if (!reference.StartsWith(directory, StringComparison.Ordinal) || !pvf.Contains(reference))
                                issues.Add($"luke-object-action-reference-missing:{map.Id}:{obj.ObjectId}:{action}");
                        }
                        // 后部只核对已声明引用缺口，不授予119写入或销毁资格。
                        if (companion) continue;
                        if (actions.Length <= rule.Action) throw new InvalidDataException("登记ETC动作缺失");
                        string path = PvfArchive.NormalizePath(obj.Path[..(obj.Path.LastIndexOf('/') + 1)] + actions[rule.Action]);
                        if (!path.StartsWith(obj.Path[..(obj.Path.LastIndexOf('/') + 1)], StringComparison.Ordinal) ||
                            !path.EndsWith("/action/" + rule.File, StringComparison.Ordinal) || !pvf.Contains(path) ||
                            RaidObjectEvidence.SetActionIncrement(pvf.ReadTokens(path), pvf.ResolveString) != rule.Symbol)
                            throw new InvalidDataException("直接动作触发或符号声明不匹配");
                        rows.Add(new(key, ordinal, obj.ObjectId, rule.Symbol, rule.Action, path));
                    }
                    if (rows.Count > 1) throw new InvalidDataException("同房间存在多个同符号候选");
                    result.Add(key, rows.ToImmutable());
                }
                catch (Exception error) when (error is InvalidDataException or NotSupportedException or FileNotFoundException)
                { issues.Add($"luke-object-room-unavailable:{map.Id}:{error.GetType().Name}"); }
            }
        }
        return new(result.ToImmutable(), issues.ToImmutable());
    }
}
