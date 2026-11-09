using System.Collections.Frozen;
using PvfLib;

namespace DfoServer.GameWorld.Dungeons;

internal sealed class StackableDungeonLimitCatalog
{
    internal const string ResourcePath = "etc/stackabledungeonlimit.etc";
    internal FrozenSet<int> Potions { get; }
    internal FrozenDictionary<int, int> Dungeons { get; }

    internal StackableDungeonLimitCatalog(PvfArchive archive) : this(archive.ReadTokens(ResourcePath), archive.ResolveString) { }

    internal StackableDungeonLimitCatalog(IReadOnlyList<PvfToken> tokens, Func<int, string> resolve)
    {
        var document = new PvfTokenDocument(tokens, resolve);
        if (!document.Diagnostics.IsEmpty || document.Sections.Any(row => !row.Children.IsEmpty || row.Name is not ("potion list" or "dungeon list")))
            throw new InvalidDataException("地下城药品限额专表结构异常");
        var potions = Values("potion list");
        if (potions.Length == 0 || potions.Any(id => id <= 0) || potions.Distinct().Count() != potions.Length)
            throw new InvalidDataException("地下城受限药品编号为空、无效或重复");
        Potions = potions.ToFrozenSet();
        var values = Values("dungeon list");
        if (values.Length == 0 || values.Length % 2 != 0)
            throw new InvalidDataException("地下城药品限额须为副本与次数二列");
        var limits = new Dictionary<int, int>();
        for (int index = 0; index < values.Length; index += 2)
            if (values[index] <= 0 || values[index + 1] < 0 || !limits.TryAdd(values[index], values[index + 1]))
                throw new InvalidDataException("地下城药品限额编号或次数无效、重复");
        Dungeons = limits.ToFrozenDictionary();

        int[] Values(string name)
        {
            var sections = document.Sections.Where(row => row.Name == name).ToArray();
            if (sections is not [var section] || section.Values.Any(token => token.Type != 0))
                throw new InvalidDataException("地下城药品限额字段缺失、重复或不是整数：" + name);
            return section.Values.Select(token => token.Value).ToArray();
        }
    }
}
