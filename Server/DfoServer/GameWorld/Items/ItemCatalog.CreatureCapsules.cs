using System.Collections.Concurrent;
using PvfLib;

namespace DfoServer.GameWorld.Items;

internal sealed partial class ItemCatalog
{
    readonly ConcurrentDictionary<bool, Lazy<IReadOnlyDictionary<int, int>>> creatureCapsules = new();

    IReadOnlyDictionary<int, int> CreatureCapsules(bool special) => creatureCapsules.GetOrAdd(special, key => new(() =>
    {
        string path = key ? "etc/chn_creaturecapsuleforpackage.etc" : "etc/chn_creaturecapsule.etc";
        return pvf.Contains(path) ? ReadCreatureCapsules(pvf.ReadText(path)) : new Dictionary<int, int>();
    })).Value;

    internal static IReadOnlyDictionary<int, int> ReadCreatureCapsules(string text)
    {
        int[] values = PvfScript.Integers(new PvfDocument(text).Section("creature capsule"));
        if (values.Length == 0 || values.Length % 2 != 0 || values.Any(value => value <= 0))
            throw new InvalidDataException("宠物胶囊映射无效");
        var result = new Dictionary<int, int>();
        for (int i = 0; i < values.Length; i += 2)
            if (!result.TryAdd(values[i], values[i + 1])) throw new InvalidDataException("宠物胶囊映射重复");
        return result;
    }

    public int CreatureCapsule(int toolId, int creatureId) => toolId is 2683240 or 2683241 or 2683804
        ? CreatureCapsules(toolId == 2683804).GetValueOrDefault(creatureId) : 0;

    bool IsCreatureCapsule(int id) => CreatureCapsules(true).Values.Contains(id) || CreatureCapsules(false).Values.Contains(id);
}
