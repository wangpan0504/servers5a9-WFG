using System.Collections.Concurrent;

namespace DfoServer.GameWorld.Items;

internal sealed partial class ItemCatalog
{
    readonly ConcurrentDictionary<int, Lazy<NpcFavorItemDefinition?>> npcFavorItems = new();
    public NpcFavorItemDefinition? NpcFavorItem(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return npcFavorItems.GetOrAdd(id, key => new(() => NpcFavorItemDefinition.Parse(key, "stackable/" + path,
            pvf.ReadTokens("stackable/" + path), pvf.ResolveString, Get(key)))).Value;
    }
}
