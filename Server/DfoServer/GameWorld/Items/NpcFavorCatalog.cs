using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record NpcFavorTarget(int OwnerId, int Maximum, int Friendly = 10000, int Intimate = 30000)
{
    public ImmutableHashSet<uint> DungeonExceptions { get; init; } = [];
    public ImmutableArray<(int Quest, int Npc)> Shapes { get; init; } = [];
    public int Appearance(IReadOnlySet<int> completed) => Shapes.LastOrDefault(shape => completed.Contains(shape.Quest)) is { Npc: > 0 } shape
        ? shape.Npc : OwnerId;
}

// 好感度的共享对象与上限来自本版NPC资源，不按NPC名字或道具编号猜测。
internal sealed class NpcFavorCatalog
{
    readonly Func<int, NpcFavorTarget?> read;
    readonly Func<int, bool> exists;
    readonly Lazy<NpcFavorRules?> rules;
    readonly ConcurrentDictionary<int, Lazy<NpcFavorTarget?>> targets = new();

    public NpcFavorCatalog(PvfArchive archive)
    {
        var paths = PvfScript.List(archive.ReadText("npc/npc.lst"));
        exists = paths.ContainsKey;
        rules = new(() => archive.Contains("etc/npcfavorsystem.etc") ? NpcFavorRules.Parse(
            new(archive.ReadTokens("etc/npcfavorsystem.etc"), archive.ResolveString)) : null);
        read = id =>
        {
            var seen = new HashSet<int>();
            int owner = id;
            while (seen.Add(owner) && paths.TryGetValue(owner, out var path))
            {
                var document = new PvfDocument(archive.ReadText("npc/" + path));
                if (document.Has("favor sharing"))
                {
                    if (!document.TryGetInt32("favor sharing", out owner) || owner <= 0) return null;
                    continue;
                }
                if (!document.Has("favorable relationship 2")) return null;
                var fields = document.Block("favor level point").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || !int.TryParse(fields[0], out int first) || !int.TryParse(fields[1], out int second) ||
                    !int.TryParse(fields[2], out int maximum) || first <= 0 || second <= first || maximum <= second) return null;
                int[] exceptions = PvfScript.Integers(document.Block("appear dungeon exception"));
                if (exceptions.Any(id => id <= 0)) return null;
                var shapes = ImmutableArray.CreateBuilder<(int Quest, int Npc)>();
                if (document.Has("favor shape transform"))
                {
                    var structured = new PvfTokenDocument(archive.ReadTokens("npc/" + path), archive.ResolveString);
                    var favor = structured.Sections.Single(field => field.Name == "favorable relationship 2");
                    foreach (var shape in favor.Children.Where(field => field.Name == "favor shape transform"))
                    {
                        int Value(string name) => shape.Children.Where(field => field.Name == name).ToArray() is
                            [{ Children.Length: 0, Values: [{ Type: 0, Value: > 0 } value] }] ? value.Value :
                            throw new InvalidDataException("NPC好感外观转换字段无效: " + name);
                        shapes.Add((Value("trigger quest"), Value("child npc img")));
                    }
                }
                return new(owner, maximum, first, second) { DungeonExceptions = exceptions.Select(id => (uint)id).ToImmutableHashSet(), Shapes = shapes.ToImmutable() };
            }
            return null;
        };
    }

    internal NpcFavorCatalog(Func<int, NpcFavorTarget?> read, NpcFavorRules? rules = null)
    {
        this.read = read;
        exists = _ => true;
        this.rules = new(() => rules);
    }
    public NpcFavorRules? Rules => rules.Value;
    // 未知客户端编号不进入缓存，容量至多为本份LST的NPC数量。
    public NpcFavorTarget? Get(int id) => id <= 0 || !exists(id) ? null : targets.GetOrAdd(id, key => new(() => read(key))).Value;
}

internal sealed record NpcFavorItemDefinition(ActionConsumableDefinition Source, ImmutableHashSet<int> Targets)
{
    public const uint Action = 162;

    public static NpcFavorItemDefinition? Parse(int id, string path, PvfToken[] tokens, Func<int, string> resolve, ItemTemplate item)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        var file = RecoveryConsumableFile.Parse(id, path, tokens, resolve);
        if (ActionConsumableDefinition.Parse(file, item) is not { Label: "[use favor point]", Parameters: [> 0] } source ||
            file.Stackable.TypeDeclaration is not { Label: "[waste]", Parameters: [1] } ||
            document.Diagnostics.Length != 0 || document.Sections.GroupBy(field => field.Name).Any(group => group.Count() != 1) ||
            document.Sections.Any(field => field.Name is not ("name" or "explain" or "flavor text" or "grade" or "rarity" or
                "usable job" or "attach type" or "minimum level" or "maximum level" or "icon" or "icon mark" or
                "stackable type" or "move wav" or "expiration date" or "action type" or "stack limit" or
                "action usable place" or "usable period" or "use action packet" or "npc select category" or
                "cool time" or "cooltime group" or "cooltime maintenance" or "total usable count"))) return null;
        var categories = document.Sections.Where(field => field.Name == "npc select category").ToArray();
        if (categories is not [{ IsBlock: true, Children.Length: 0, Values.Length: > 0 } category] ||
            category.Values.Any(value => value.Type != 0 || value.Value <= 0)) return null;
        var allowed = category.Values.Select(value => value.Value).ToImmutableHashSet();
        return allowed.Count == category.Values.Length ? new(source, allowed) : null;
    }
}
