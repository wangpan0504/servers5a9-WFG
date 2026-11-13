using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using PvfLib;

internal static class SupportMapEligibilityExport
{
internal static void Write(string serverPath, string pvfPath, string output)
{
if (File.Exists(output)) throw new IOException("Output already exists: " + output);
var assembly = Assembly.LoadFrom(serverPath);
var type = assembly.GetType("DfoServer.GameWorld.Dungeons.DungeonCatalog", true)!;
using var archive = PvfArchive.Open(pvfPath);
var extra = assembly.GetType("DfoServer.GameWorld.Dungeons.EpicRoadResourcePaths", true)!;
var flags = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
var extraDungeons = extra.GetProperty("Dungeons",flags)!.GetValue(null);
var extraMaps = extra.GetProperty("Maps",flags)!.GetValue(null);
var catalog = type.GetMethod("Load")!.Invoke(null, [archive, extraDungeons, extraMaps])!;
object? Property(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value);
var definitions = new List<object>();
var refs = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
var blockedRefs = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
var errors = new List<object>();
var registry = archive.ReadTokens("dungeon/dungeon.lst");
var idsToScan = registry.Where((_,i)=>i%2==0).Select(t=>t.Value).Concat(new[]{400000097,400000098}).Distinct();
foreach(int id in idsToScan)
{
    try
    {
        var definition = type.GetMethod("Get")!.Invoke(catalog, [id])!;
        bool allowed = (bool)Property(definition, "AllowsCompanionAi")!;
        string path = (string)Property(definition, "Path")!;
        // 与正式入场计划一致，排除专用禁止模式。
        bool Mode(string name) => (bool)assembly.GetType("DfoServer.GameWorld.Dungeons." + name, true)!
            .GetMethod("Supports")!.Invoke(null, [definition])!;
        bool extraBlocked = id == 4000 || string.Equals((string?)Property(definition,"DungeonType"), "lost land", StringComparison.OrdinalIgnoreCase) ||
            Mode("TagEvildomEntryPolicy") || Mode("ImperialArenaEntryPolicy");
        allowed &= !extraBlocked;
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var mazes = ((IEnumerable)Property(definition,"Mazes")!).Cast<object>().ToList();
        if(id is >=5335 and <=5339)
            for(byte difficulty=0;difficulty<3;difficulty++)
                mazes.Add(type.GetMethod("RevisedBeastMaze")!.Invoke(catalog,[id,difficulty])!);
        foreach (object maze in mazes)
        {
            foreach (string field in new[] { "MapSpecification", "LayeredMaps", "MapAlternatives", "ConditionalMaps" })
                if (Property(maze, field) is IEnumerable maps)
                    foreach (object map in maps) paths.Add((string)Property(map, "Path")!);
            foreach (string field in new[] { "HellPartyPath", "HellLegendPath" })
                if (Property(maze, field) is string map) paths.Add(map);
        }
        foreach (string map in paths)
        {
            var target = allowed ? refs : blockedRefs;
            if (!target.TryGetValue(map, out var ids)) target.Add(map, ids = []);
            ids.Add(id);
        }
        definitions.Add(new { id, path, allowed, maps = paths.Count });
    }
    catch (TargetInvocationException ex) { errors.Add(new { id, error = ex.InnerException?.Message ?? ex.Message }); }
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(new { pvf = Path.GetFullPath(pvfPath), server = Path.GetFullPath(serverPath), definitions,
    allowed = refs.ToDictionary(p => p.Key, p => p.Value.Order().ToArray()),
    blocked = blockedRefs.ToDictionary(p => p.Key, p => p.Value.Order().ToArray()), errors }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"MAP_PROBE definitions={definitions.Count} allowedMaps={refs.Count} blockedMaps={blockedRefs.Count} errors={errors.Count}; no PVF writes");

}
}
