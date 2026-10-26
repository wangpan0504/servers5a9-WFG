using System.Collections.Concurrent;
using System.Collections.Immutable;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed record NpcFavorTarget(int OwnerId, int Maximum);

// 好感度的共享对象与上限来自本版NPC资源，不按NPC名字或道具编号猜测。
internal sealed class NpcFavorCatalog
{
    readonly Func<int, NpcFavorTarget?> read;
    readonly ConcurrentDictionary<int, Lazy<NpcFavorTarget?>> targets = new();

    public NpcFavorCatalog(PvfArchive archive)
    {
        var paths = PvfScript.List(archive.ReadText("npc/npc.lst"));
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
                return new(owner, maximum);
            }
            return null;
        };
    }

    internal NpcFavorCatalog(Func<int, NpcFavorTarget?> read) => this.read = read;
    public NpcFavorTarget? Get(int id) => targets.GetOrAdd(id, key => new(() => read(key))).Value;
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
