using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed partial class ItemCatalog
{
    readonly ConcurrentDictionary<int, Lazy<AvatarSocketOpenerDefinition?>> avatarSocketOpeners = new();

    public AvatarSocketOpenerDefinition? AvatarSocketOpener(int id)
    {
        if (!stackable.TryGetValue(id, out var path)) return null;
        return avatarSocketOpeners.GetOrAdd(id, key => new(() => AvatarSocketOpenerDefinition.Parse(key,
            "stackable/" + path, pvf.ReadTokens("stackable/" + path), pvf.ResolveString, Get(key)))).Value;
    }
}
