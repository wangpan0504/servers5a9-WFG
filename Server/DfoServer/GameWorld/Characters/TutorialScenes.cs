using System.Collections.Concurrent;
using System.Collections.Immutable;
using DfoServer.GameWorld.Dungeons;
using PvfLib;

namespace DfoServer.GameWorld.Characters;

internal sealed record TutorialCinematicEvidence(int Id, string Path, ImmutableHashSet<int> MonsterActors, ImmutableHashSet<int> DestroyTargets);
internal sealed record TutorialSceneEvidence(string MapPath, ImmutableHashSet<int> ScriptedMonsterIds,
    ImmutableArray<TutorialCinematicEvidence> Cinematics);

internal sealed class TutorialScenes
{
    readonly PvfArchive pvf;
    readonly IReadOnlyDictionary<int, string> cinematics;
    readonly ImmutableHashSet<int> maps;
    readonly ConcurrentDictionary<int, Lazy<TutorialSceneEvidence>> evidence = new();
    public TutorialScenes(PvfArchive archive, TutorialCatalog tutorials, DungeonCatalog dungeons)
    {
        pvf = archive;
        cinematics = PvfScript.List(pvf.ReadText("cinematic/cinematic.lst"));
        maps = tutorials.Flows.SelectMany(flow => dungeons.Get(checked((int)flow.DungeonId)).Mazes)
            .SelectMany(maze => maze.MapSpecification ?? []).Select(map => map.Id).ToImmutableHashSet();
    }
    public DungeonRoomDefinition Apply(DungeonRoomDefinition room)
    {
        var rules = Get(room.Map);
        return room with { TutorialCinematics = rules.Cinematics, Monsters = room.Monsters.Select(monster => monster with
            { ScriptedRetirement = rules.ScriptedMonsterIds.Contains(monster.MonsterId) }).ToImmutableArray() };
    }
    public TutorialSceneEvidence Get(DungeonMap map)
    {
        if (!maps.Contains(map.Id)) throw new InvalidDataException("训练动作查询不能使用未登记的地图");
        var result = evidence.GetOrAdd(map.Id, _ => new(() => Read(map))).Value;
        if (result.MapPath != map.Path) throw new InvalidDataException("训练地图ID的路径发生变化");
        return result;
    }
    TutorialSceneEvidence Read(DungeonMap map)
    {
        string? action = TutorialSceneFile.BasicAction(pvf.ReadTokens(map.Path), pvf.ResolveString);
        if (string.IsNullOrEmpty(action)) return new(map.Path, [], []);
        string path = PvfArchive.NormalizePath(action.StartsWith("map/", StringComparison.OrdinalIgnoreCase) ? action :
            map.Path[..(map.Path.LastIndexOf('/') + 1)] + action);
        var declared = TutorialSceneFile.Action(pvf.ReadTokens(path), pvf.ResolveString);
        var movies = ImmutableArray.CreateBuilder<TutorialCinematicEvidence>();
        foreach (int id in declared.Cinematics)
        {
            if (!cinematics.TryGetValue(id, out var listed)) throw new InvalidDataException("训练引用的CMT不存在: " + id);
            string file = PvfArchive.NormalizePath("cinematic/" + listed);
            var movie = TutorialSceneFile.Cinematic(pvf.ReadTokens(file), pvf.ResolveString);
            if (movie.MapId != map.Id) continue;
            movies.Add(new(id, file, movie.MonsterActors.ToImmutableHashSet(), movie.DestroyTargets.ToImmutableHashSet()));
        }
        return new(map.Path, declared.DestroySelectedMonsters.ToImmutableHashSet(), movies.ToImmutable());
    }
}
